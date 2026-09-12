using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NINA.Plugin.TargetScheduler.Planning.Exposures {

    /// <summary>
    /// Maintains proportional filter balance by walking leftover weights so prefixes
    /// stay mixed (not greedy blocks of the most-behind filter).
    ///
    /// leftover_i = max(0, Desired_i - CompletionCount_i). A Bresenham / DDA
    /// error-diffusion step (initial error = leftover/2) picks the next filter.
    /// Leftovers 1 L, 24 R, 36 G, 64 B yield B,G,B,R,B,G,B... (about 0:2:3:5),
    /// not a long B run.
    ///
    /// Empty-filter seed: if some eligible filters already have frames and another
    /// still has CompletionCount == 0, emit that empty filter for
    /// min(EMPTY_FILTER_SEED, Desired) picks (SEED=3), then resume the walk.
    /// Cold start (all CompletionCount == 0) skips the seed and walks immediately.
    ///
    /// FilterSwitchFrequency is a minimum run length: FSF==1 switches every pick,
    /// FSF&gt;1 repeats the walk's choice that many times.
    ///
    /// Equal desired counts keep the existing percentage dead band (defer to stock
    /// rotation when spread is small). Unequal desired always uses the leftover walk.
    ///
    /// Returns null when 0 or 1 eligible candidates remain, or every leftover is 0.
    ///
    /// STATE: the walk's memory lives in an ExposureRatioWalkState held in
    /// ExposureRatioWalkCache keyed by target id, resolved at the point of use, never in
    /// a field of this object. The planner builds a fresh selector for every target on
    /// every run; state kept here would reset after each pick and the walk would collapse
    /// to "largest leftover wins" (the 2026-09-11 all-L night). Select only PROPOSES and is
    /// safe to call any number of times; ExposureTaken COMMITS the proposal. The planner
    /// calls Select twice per run (previous-target continue check, then the full plan) and
    /// takes one exposure, so a Select that advanced the walk would skip a step every run.
    /// </summary>
    public class ExposureRatioSelector {

        /// <summary>
        /// Frames to take on a never-shot eligible filter before mixing it into the
        /// leftover walk. Applies only when at least one other eligible filter already
        /// has CompletionCount &gt; 0.
        /// </summary>
        public const int EMPTY_FILTER_SEED = 3;

        private readonly ITarget target;
        private ExposureCompletionHelper completionHelper;
        private int filterSwitchFrequency;

        /// <summary>
        /// Unit-test constructor: no target is bound, so callers must pass an
        /// ExposureRatioWalkState explicitly to Select/ExposureTaken. The cache-resolving
        /// overloads throw, so a production caller cannot silently fall back to per-instance state.
        /// </summary>
        public ExposureRatioSelector(ExposureCompletionHelper completionHelper, int filterSwitchFrequency = 1)
            : this(null, completionHelper, filterSwitchFrequency) {
        }

        public ExposureRatioSelector(ITarget target, ExposureCompletionHelper completionHelper, int filterSwitchFrequency = 1) {
            this.target = target;
            this.completionHelper = completionHelper;
            this.filterSwitchFrequency = Math.Max(1, filterSwitchFrequency);
        }

        /// <summary>
        /// Select the next exposure using the walk state cached for this selector's target.
        /// Pure with respect to the walk: call it as often as you like, commit with ExposureTaken.
        /// </summary>
        public IExposure Select(List<IExposure> candidates) {
            return Select(candidates, ResolveState("Select"));
        }

        /// <summary>
        /// Advance the cached walk state for this selector's target after an exposure was taken.
        /// </summary>
        public void ExposureTaken(IExposure exposure) {
            ExposureTaken(exposure, ResolveState("ExposureTaken"));
        }

        /// <summary>
        /// Forget the walk state for this selector's target (target reset / switch).
        /// </summary>
        public void Reset() {
            if (target != null) {
                ExposureRatioWalkCache.Remove(target);
            }
        }

        /// <summary>
        /// Select the next exposure from leftover weights (or the equal-desired dead
        /// band) against an explicit walk state. Returns null for degenerate cases
        /// (0-1 candidates, all complete). Does not advance the state; it records a
        /// proposal that ExposureTaken commits.
        /// </summary>
        public IExposure Select(List<IExposure> candidates, ExposureRatioWalkState state) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            state.CheckIsolation("Select");
            state.LastProposal = null;

            List<IExposure> eligible = candidates.Where(e => !e.Rejected && e.Desired > 0).ToList();

            if (eligible.Count <= 1) {
                TSLogger.Debug($"ratio selector: {eligible.Count} eligible candidate(s), skipping (need >1)");
                return null;
            }

            LogCandidates(eligible);

            if (AllDesiredEqual(eligible)) {
                return SelectEqualDesired(eligible);
            }

            if (eligible.All(e => Remaining(e) == 0)) {
                TSLogger.Debug("ratio selector: all leftovers 0, skipping");
                return null;
            }

            IExposure seeded = ProposeSeed(eligible, state);
            if (seeded != null) {
                return seeded;
            }

            WalkView view = ViewWalk(eligible, state);

            IExposure continued = ProposeRunContinue(eligible, state, view);
            if (continued != null) {
                return continued;
            }

            return ProposeWalkPick(eligible, state, view);
        }

        /// <summary>
        /// Commit the transition proposed by the most recent Select against an explicit walk
        /// state. If the exposure taken is not what the walk proposed (equal-desired path,
        /// stock rotation fallback, twilight override) the walk is left untouched: leftovers
        /// re-sync from the real counts on the next Select.
        /// </summary>
        public void ExposureTaken(IExposure exposure, ExposureRatioWalkState state) {
            if (state == null) throw new ArgumentNullException(nameof(state));
            state.CheckIsolation("ExposureTaken");

            WalkProposal proposal = state.LastProposal;
            state.LastProposal = null;
            if (exposure == null) return;

            string filterName = exposure.FilterName;
            if (proposal == null || proposal.FilterName != filterName) {
                TSLogger.Debug($"ratio selector: exposure taken ({filterName}) was not the walk's proposal ({proposal?.FilterName ?? "none"}), walk state unchanged");
                return;
            }

            switch (proposal.Kind) {
                case WalkProposalKind.SeedStart:
                    state.SeededFilterNames.Add(filterName);
                    state.SeedFilterName = filterName;
                    state.SeedLeft = proposal.SeedCount - 1;
                    if (state.SeedLeft <= 0) {
                        state.SeedLeft = 0;
                        state.SeedFilterName = null;
                    }
                    break;

                case WalkProposalKind.SeedContinue:
                    state.SeedLeft = Math.Max(0, state.SeedLeft - 1);
                    if (state.SeedLeft == 0) {
                        state.SeedFilterName = null;
                    }
                    break;

                case WalkProposalKind.RunContinue:
                    state.RunLeft = Math.Max(0, state.RunLeft - 1);
                    if (state.RunLeft == 0) {
                        state.RunFilterName = null;
                    }
                    break;

                case WalkProposalKind.Walk:
                    CommitWalk(state, proposal, filterName);
                    break;
            }

            TSLogger.Debug($"ratio selector: committed {proposal.Kind} -> {filterName}");
        }

        /// <summary>
        /// Calculate the completion ratio for an exposure plan.
        /// Uses Acquired/Desired when grading is disabled or when grading is delayed
        /// and the threshold has not been reached yet. Uses Accepted/Desired otherwise.
        /// </summary>
        public double CompletionRatio(IExposure exposure) {
            if (exposure.Desired == 0) return 1.0;

            if (completionHelper != null &&
                (!completionHelper.ImageGradingEnabled || completionHelper.IsProvisionalPercentComplete(exposure))) {
                return (double)exposure.Acquired / (double)exposure.Desired;
            }

            return (double)exposure.Accepted / (double)exposure.Desired;
        }

        /// <summary>
        /// Returns the raw frame count used for ratio calculation (Acquired or Accepted
        /// depending on grading mode).
        /// </summary>
        internal int CompletionCount(IExposure exposure) {
            if (completionHelper != null &&
                (!completionHelper.ImageGradingEnabled || completionHelper.IsProvisionalPercentComplete(exposure))) {
                return exposure.Acquired;
            }
            return exposure.Accepted;
        }

        internal int Remaining(IExposure exposure) {
            return Math.Max(0, exposure.Desired - CompletionCount(exposure));
        }

        private ExposureRatioWalkState ResolveState(string op) {
            if (target == null) {
                throw new InvalidOperationException($"ExposureRatioSelector.{op}: no target bound; construct with a target (production) or pass the walk state explicitly (tests)");
            }
            return ExposureRatioWalkCache.GetOrCreate(target);
        }

        private IExposure SelectEqualDesired(List<IExposure> eligible) {
            double minRatio = double.MaxValue;
            double maxRatio = double.MinValue;
            IExposure mostBehind = eligible[0];

            foreach (IExposure exposure in eligible) {
                double ratio = CompletionRatio(exposure);
                if (ratio < minRatio) {
                    minRatio = ratio;
                    mostBehind = exposure;
                }
                if (ratio > maxRatio) maxRatio = ratio;
            }

            double spread = maxRatio - minRatio;
            if (spread >= 0.05) {
                TSLogger.Info($"ratio selector: {mostBehind.FilterName} is most behind (ratio={minRatio:F3}, spread={spread:F3}), prioritizing over default selection");
                return mostBehind;
            }

            TSLogger.Debug($"ratio selector: equal desired counts, within dead band ({spread:F3}), deferring to default selector");
            return null;
        }

        private IExposure ProposeSeed(List<IExposure> eligible, ExposureRatioWalkState state) {
            if (state.SeedLeft > 0 && state.SeedFilterName != null) {
                IExposure current = FindByName(eligible, state.SeedFilterName);
                if (current != null && Remaining(current) > 0) {
                    state.LastProposal = new WalkProposal { FilterName = current.FilterName, Kind = WalkProposalKind.SeedContinue };
                    TSLogger.Debug($"ratio selector: empty seed continue -> {current.FilterName} ({state.SeedLeft - 1} left)");
                    return current;
                }
                // Seed filter no longer eligible or complete: the seed is over. The commit of
                // whatever is proposed next clears it.
            }

            bool anyEmpty = eligible.Any(e => CompletionCount(e) == 0);
            bool anyStarted = eligible.Any(e => CompletionCount(e) > 0);
            if (!anyEmpty || !anyStarted) {
                return null;
            }

            foreach (IExposure exposure in eligible) {
                if (CompletionCount(exposure) != 0) continue;
                if (state.SeededFilterNames.Contains(exposure.FilterName)) continue;

                int seedCount = Math.Min(EMPTY_FILTER_SEED, exposure.Desired);
                if (seedCount <= 0) continue;

                state.LastProposal = new WalkProposal { FilterName = exposure.FilterName, Kind = WalkProposalKind.SeedStart, SeedCount = seedCount };
                TSLogger.Info($"ratio selector: empty seed -> {exposure.FilterName} x{seedCount}");
                return exposure;
            }

            return null;
        }

        private struct WalkView {
            public List<string> Names;
            public int[] Remaining;
            public int[] Errors;
            public int Total;
            public bool Init;
        }

        /// <summary>
        /// Read-only view of the walk for this candidate set: current leftovers plus either
        /// the persisted error accumulators or, if the candidate set changed, fresh ones.
        /// </summary>
        private WalkView ViewWalk(List<IExposure> eligible, ExposureRatioWalkState state) {
            List<string> names = eligible.Select(e => e.FilterName).ToList();
            int[] remaining = eligible.Select(e => Remaining(e)).ToArray();
            bool init = state.WalkNames == null || state.Errors == null || !names.SequenceEqual(state.WalkNames);
            int[] errors = init ? remaining.Select(r => r / 2).ToArray() : state.Errors;
            return new WalkView { Names = names, Remaining = remaining, Errors = errors, Total = remaining.Sum(), Init = init };
        }

        private IExposure ProposeRunContinue(List<IExposure> eligible, ExposureRatioWalkState state, WalkView view) {
            if (view.Init || state.RunLeft <= 0 || state.RunFilterName == null) {
                return null;
            }

            IExposure current = FindByName(eligible, state.RunFilterName);
            if (current == null || Remaining(current) <= 0) {
                return null;
            }

            state.LastProposal = new WalkProposal { FilterName = current.FilterName, Kind = WalkProposalKind.RunContinue };
            TSLogger.Debug($"ratio selector: FSF run continue -> {current.FilterName} ({state.RunLeft - 1} left)");
            return current;
        }

        private IExposure ProposeWalkPick(List<IExposure> eligible, ExposureRatioWalkState state, WalkView view) {
            if (view.Total <= 0) {
                TSLogger.Debug("ratio selector: leftover walk has no remaining frames");
                return null;
            }

            int bestIndex = -1;
            int bestError = int.MinValue;

            for (int i = 0; i < view.Remaining.Length; i++) {
                if (view.Remaining[i] <= 0) continue;
                int error = view.Errors[i] + view.Remaining[i];
                if (error > bestError) {
                    bestError = error;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) {
                TSLogger.Debug("ratio selector: leftover walk found no positive remaining");
                return null;
            }

            IExposure selected = FindByName(eligible, view.Names[bestIndex]);
            state.LastProposal = new WalkProposal {
                FilterName = view.Names[bestIndex],
                Kind = WalkProposalKind.Walk,
                InitWalk = view.Init,
                WalkNames = view.Names,
                Remaining = view.Remaining,
                TotalRemaining = view.Total,
            };
            TSLogger.Debug($"ratio selector: leftover walk -> {selected?.FilterName}");
            return selected;
        }

        private void CommitWalk(ExposureRatioWalkState state, WalkProposal proposal, string filterName) {
            int[] remaining = (int[])proposal.Remaining.Clone();
            if (proposal.InitWalk) {
                state.WalkNames = proposal.WalkNames;
                state.Errors = remaining.Select(r => r / 2).ToArray();
            }
            state.Remaining = remaining;
            state.TotalRemaining = proposal.TotalRemaining;

            for (int i = 0; i < remaining.Length; i++) {
                if (remaining[i] > 0) {
                    state.Errors[i] += remaining[i];
                }
            }
            int index = state.WalkNames.IndexOf(filterName);
            if (index >= 0) {
                state.Errors[index] -= state.TotalRemaining;
            }

            // A walk pick means no seed was active (or it was stale): clear it.
            state.SeedLeft = 0;
            state.SeedFilterName = null;

            if (filterSwitchFrequency > 1) {
                state.RunFilterName = filterName;
                state.RunLeft = filterSwitchFrequency - 1;
            } else {
                state.RunFilterName = null;
                state.RunLeft = 0;
            }
        }

        private bool AllDesiredEqual(List<IExposure> eligible) {
            int first = eligible[0].Desired;
            return eligible.All(e => e.Desired == first);
        }

        private static IExposure FindByName(List<IExposure> eligible, string filterName) {
            if (filterName == null) return null;
            return eligible.FirstOrDefault(e => e.FilterName == filterName);
        }

        private void LogCandidates(List<IExposure> eligible) {
            var sb = new StringBuilder();
            sb.Append("ratio selector candidates: ");
            foreach (IExposure exposure in eligible) {
                bool isProvisional = completionHelper != null && completionHelper.IsProvisionalPercentComplete(exposure);
                int leftover = Remaining(exposure);
                sb.Append($"{exposure.FilterName}={CompletionRatio(exposure):F3} ({exposure.Accepted}a/{exposure.Acquired}q/{exposure.Desired}d leftover={leftover}{(isProvisional ? " provisional" : "")}), ");
            }
            TSLogger.Debug(sb.ToString());
        }
    }
}
