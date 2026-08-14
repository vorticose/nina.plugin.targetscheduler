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
    /// </summary>
    public class ExposureRatioSelector {

        /// <summary>
        /// Frames to take on a never-shot eligible filter before mixing it into the
        /// leftover walk. Applies only when at least one other eligible filter already
        /// has CompletionCount &gt; 0.
        /// </summary>
        public const int EMPTY_FILTER_SEED = 3;

        private ExposureCompletionHelper completionHelper;
        private int filterSwitchFrequency;

        private List<string> walkNames = null;
        private int[] remaining = null;
        private int[] errors = null;
        private int totalRemaining = 0;

        private readonly HashSet<string> seededFilterNames = new HashSet<string>();
        private string seedFilterName = null;
        private int seedLeft = 0;

        private string runFilterName = null;
        private int runLeft = 0;

        public ExposureRatioSelector(ExposureCompletionHelper completionHelper, int filterSwitchFrequency = 1) {
            this.completionHelper = completionHelper;
            this.filterSwitchFrequency = Math.Max(1, filterSwitchFrequency);
        }

        /// <summary>
        /// Select the next exposure from leftover weights (or the equal-desired dead
        /// band). Returns null for degenerate cases (0-1 candidates, all complete).
        /// </summary>
        public IExposure Select(List<IExposure> candidates) {
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

            IExposure seeded = TrySeed(eligible);
            if (seeded != null) {
                return seeded;
            }

            SyncWalk(eligible);

            IExposure continued = ContinueRun(eligible);
            if (continued != null) {
                return continued;
            }

            IExposure selected = WalkPick(eligible);
            if (selected != null && filterSwitchFrequency > 1) {
                runFilterName = selected.FilterName;
                runLeft = filterSwitchFrequency - 1;
            }
            return selected;
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

        private IExposure TrySeed(List<IExposure> eligible) {
            if (seedLeft > 0) {
                IExposure current = FindByName(eligible, seedFilterName);
                if (current != null && Remaining(current) > 0) {
                    seedLeft--;
                    TSLogger.Debug($"ratio selector: empty seed continue -> {current.FilterName} ({seedLeft} left)");
                    return current;
                }
                seedLeft = 0;
                seedFilterName = null;
            }

            bool anyEmpty = eligible.Any(e => CompletionCount(e) == 0);
            bool anyStarted = eligible.Any(e => CompletionCount(e) > 0);
            if (!anyEmpty || !anyStarted) {
                return null;
            }

            foreach (IExposure exposure in eligible) {
                if (CompletionCount(exposure) != 0) continue;
                if (seededFilterNames.Contains(exposure.FilterName)) continue;

                int seedCount = Math.Min(EMPTY_FILTER_SEED, exposure.Desired);
                if (seedCount <= 0) continue;

                seededFilterNames.Add(exposure.FilterName);
                seedFilterName = exposure.FilterName;
                seedLeft = seedCount - 1;
                TSLogger.Info($"ratio selector: empty seed -> {exposure.FilterName} x{seedCount}");
                return exposure;
            }

            return null;
        }

        private IExposure ContinueRun(List<IExposure> eligible) {
            if (runLeft <= 0 || runFilterName == null) {
                return null;
            }

            IExposure current = FindByName(eligible, runFilterName);
            if (current == null || Remaining(current) <= 0) {
                runLeft = 0;
                runFilterName = null;
                return null;
            }

            runLeft--;
            TSLogger.Debug($"ratio selector: FSF run continue -> {current.FilterName} ({runLeft} left)");
            return current;
        }

        private void SyncWalk(List<IExposure> eligible) {
            List<string> names = eligible.Select(e => e.FilterName).ToList();
            if (walkNames == null || !names.SequenceEqual(walkNames)) {
                walkNames = names;
                remaining = eligible.Select(e => Remaining(e)).ToArray();
                errors = remaining.Select(r => r / 2).ToArray();
                totalRemaining = remaining.Sum();
                runFilterName = null;
                runLeft = 0;
                return;
            }

            for (int i = 0; i < eligible.Count; i++) {
                remaining[i] = Remaining(eligible[i]);
            }
            totalRemaining = remaining.Sum();
        }

        private IExposure WalkPick(List<IExposure> eligible) {
            if (totalRemaining <= 0 || walkNames == null) {
                TSLogger.Debug("ratio selector: leftover walk has no remaining frames");
                return null;
            }

            int bestIndex = -1;
            int bestError = int.MinValue;

            for (int i = 0; i < remaining.Length; i++) {
                if (remaining[i] <= 0) continue;
                errors[i] += remaining[i];
                if (errors[i] > bestError) {
                    bestError = errors[i];
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) {
                TSLogger.Debug("ratio selector: leftover walk found no positive remaining");
                return null;
            }

            errors[bestIndex] -= totalRemaining;
            IExposure selected = FindByName(eligible, walkNames[bestIndex]);
            TSLogger.Debug($"ratio selector: leftover walk -> {selected?.FilterName}");
            return selected;
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
