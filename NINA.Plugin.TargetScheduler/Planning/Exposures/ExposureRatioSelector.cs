using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NINA.Plugin.TargetScheduler.Planning.Exposures {

    /// <summary>
    /// Maintains proportional filter balance using three modes:
    ///
    /// 1. CATCH-UP: when any filter's frame deficit exceeds the catch-up threshold,
    ///    forces the most-behind filter. The threshold is derived from the weighted
    ///    rotation cycle length, so normal rotation drift doesn't trigger catch-up.
    ///
    /// 2. WEIGHTED ROTATION: when balanced, rotates proportional to desired counts
    ///    (e.g. L,L,L,R,G,B for 3:1:1:1 ratio). When FilterSwitchFrequency > 1,
    ///    each slot becomes a block (e.g. L×10,L×10,L×10,R×10,G×10,B×10 for FSF=10).
    ///
    /// 3. DEFER: returns null for single candidates or equal desired counts,
    ///    letting the stock SmartExposureRotateManager handle selection.
    /// </summary>
    public class ExposureRatioSelector {
        private const int MAX_CYCLE_LENGTH = 50;

        private ExposureCompletionHelper completionHelper;
        private int filterSwitchFrequency;
        private int _weightedRotationIndex = 0;
        private List<string> _lastCandidateFingerprint = null;

        public ExposureRatioSelector(ExposureCompletionHelper completionHelper, int filterSwitchFrequency = 1) {
            this.completionHelper = completionHelper;
            this.filterSwitchFrequency = Math.Max(1, filterSwitchFrequency);
        }

        /// <summary>
        /// Select the next exposure using frame-deficit catch-up or weighted rotation.
        /// Returns null only for degenerate cases (0-1 candidates) or when all
        /// candidates have equal desired counts and are balanced.
        /// </summary>
        public IExposure Select(List<IExposure> candidates) {
            List<IExposure> eligible = candidates.Where(e => !e.Rejected && e.Desired > 0).ToList();

            if (eligible.Count <= 1) {
                TSLogger.Debug($"ratio selector: {eligible.Count} eligible candidate(s), skipping (need >1)");
                return null;
            }

            // Calculate ratios, frame deficits, and find most-behind filter
            var sb = new StringBuilder();
            sb.Append("ratio selector candidates: ");

            int totalFrames = 0;
            int totalDesired = 0;
            double minRatio = double.MaxValue;
            double maxRatio = double.MinValue;
            IExposure mostBehind = null;
            double worstDeficit = double.MinValue;

            foreach (IExposure exposure in eligible) {
                totalFrames += CompletionCount(exposure);
                totalDesired += exposure.Desired;
            }

            foreach (IExposure exposure in eligible) {
                double ratio = CompletionRatio(exposure);
                double idealCount = totalDesired > 0 ? (double)totalFrames * exposure.Desired / totalDesired : 0;
                double deficit = idealCount - CompletionCount(exposure);

                bool isProvisional = completionHelper != null && completionHelper.IsProvisionalPercentComplete(exposure);
                sb.Append($"{exposure.FilterName}={ratio:F3} ({exposure.Accepted}a/{exposure.Acquired}q/{exposure.Desired}d{(isProvisional ? " provisional" : "")}, deficit={deficit:F1}), ");

                if (ratio < minRatio) {
                    minRatio = ratio;
                }
                if (ratio > maxRatio) {
                    maxRatio = ratio;
                }
                if (deficit > worstDeficit) {
                    worstDeficit = deficit;
                    mostBehind = exposure;
                }
            }

            double spread = maxRatio - minRatio;

            // Calculate catch-up threshold from the weighted rotation cycle length.
            // The cycle length accounts for FilterSwitchFrequency (block size), so
            // normal block rotation drift doesn't trigger catch-up.
            int cycleLength = GetCycleLength(eligible);
            sb.Append($"spread={spread:F3}, maxDeficit={worstDeficit:F1}, catchUpThreshold={cycleLength}");
            TSLogger.Debug(sb.ToString());

            // --- Equal desired counts: use percentage-based dead band ---
            if (AllDesiredEqual(eligible)) {
                if (spread >= 0.05) {
                    TSLogger.Info($"ratio selector: {mostBehind.FilterName} is most behind (ratio={minRatio:F3}, spread={spread:F3}), prioritizing over default selection");
                    return mostBehind;
                }
                TSLogger.Debug($"ratio selector: equal desired counts, within dead band ({spread:F3}), deferring to default selector");
                return null;
            }

            // --- Catch-up (unequal desired counts) ---
            // Force the most-behind filter when its frame deficit exceeds the cycle length
            if (worstDeficit >= cycleLength) {
                TSLogger.Info($"ratio selector: catch-up {mostBehind.FilterName} ({worstDeficit:F1} frames behind ideal, threshold={cycleLength})");
                return mostBehind;
            }

            // --- Weighted rotation ---
            IExposure selected = WeightedRotationSelect(eligible);
            TSLogger.Debug($"ratio selector: weighted rotation -> {selected.FilterName}");
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

        /// <summary>
        /// Calculate how many frames behind ideal a specific filter is.
        /// Returns positive when behind, negative when ahead.
        /// </summary>
        internal double GetFrameDeficit(IExposure exposure, List<IExposure> eligible) {
            int totalFrames = eligible.Sum(e => CompletionCount(e));
            int totalDesired = eligible.Sum(e => e.Desired);
            if (totalDesired == 0) return 0;

            double idealCount = (double)totalFrames * exposure.Desired / totalDesired;
            return idealCount - CompletionCount(exposure);
        }

        /// <summary>
        /// Calculates the weighted rotation cycle length for the given candidates,
        /// scaled by FilterSwitchFrequency for block shooting.
        /// For L:300, R:100, G:100, B:100 with FSF=1 → cycle length 6.
        /// For L:300, R:100, G:100, B:100 with FSF=10 → cycle length 60.
        /// </summary>
        internal int GetCycleLength(List<IExposure> eligible) {
            int[] weights = eligible.Select(e => e.Desired).ToArray();
            int gcd = weights.Aggregate(GCD);
            if (gcd == 0) gcd = 1;
            int[] normalized = weights.Select(w => w / gcd).ToArray();

            int baseCycleLength = normalized.Sum();
            if (baseCycleLength > MAX_CYCLE_LENGTH) {
                int divisor = (baseCycleLength + MAX_CYCLE_LENGTH - 1) / MAX_CYCLE_LENGTH;
                normalized = normalized.Select(w => Math.Max(1, w / divisor)).ToArray();
                baseCycleLength = normalized.Sum();
            }
            return baseCycleLength * filterSwitchFrequency;
        }

        /// <summary>
        /// Checks if all eligible candidates have the same Desired count.
        /// </summary>
        private bool AllDesiredEqual(List<IExposure> eligible) {
            int first = eligible[0].Desired;
            return eligible.All(e => e.Desired == first);
        }

        /// <summary>
        /// Selects the next filter from a weighted rotation cycle proportional to desired counts.
        /// When FilterSwitchFrequency > 1, each weight slot is repeated FSF times to produce
        /// blocks. For L:300, R:100, G:100, B:100 with FSF=10:
        ///   cycle = L×10, L×10, L×10, R×10, G×10, B×10 (length 60)
        /// </summary>
        internal IExposure WeightedRotationSelect(List<IExposure> eligible) {
            // Detect candidate set changes and reset cycle
            var fingerprint = eligible.Select(e => e.FilterName).ToList();
            if (_lastCandidateFingerprint == null || !fingerprint.SequenceEqual(_lastCandidateFingerprint)) {
                _lastCandidateFingerprint = fingerprint;
                _weightedRotationIndex = 0;
            }

            // Build weights normalized by GCD, scaled by FilterSwitchFrequency
            int[] weights = eligible.Select(e => e.Desired).ToArray();
            int gcd = weights.Aggregate(GCD);
            if (gcd == 0) gcd = 1;
            int[] normalized = weights.Select(w => w / gcd).ToArray();

            // Cap base cycle length before scaling by FSF
            int baseCycleLength = normalized.Sum();
            if (baseCycleLength > MAX_CYCLE_LENGTH) {
                int divisor = (baseCycleLength + MAX_CYCLE_LENGTH - 1) / MAX_CYCLE_LENGTH;
                normalized = normalized.Select(w => Math.Max(1, w / divisor)).ToArray();
                baseCycleLength = normalized.Sum();
            }

            // Scale each weight by FSF to produce blocks
            int[] blockWeights = normalized.Select(w => w * filterSwitchFrequency).ToArray();
            int cycleLength = blockWeights.Sum();

            // Map index to filter using cumulative sums over block weights
            int pos = _weightedRotationIndex % cycleLength;
            int cumulative = 0;
            IExposure selected = eligible.Last();
            for (int i = 0; i < eligible.Count; i++) {
                cumulative += blockWeights[i];
                if (pos < cumulative) {
                    selected = eligible[i];
                    break;
                }
            }

            _weightedRotationIndex = (_weightedRotationIndex + 1) % cycleLength;
            return selected;
        }

        internal static int GCD(int a, int b) {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0) { int t = b; b = a % b; a = t; }
            return a;
        }
    }
}
