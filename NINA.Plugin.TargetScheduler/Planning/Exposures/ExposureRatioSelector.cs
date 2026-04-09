using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NINA.Plugin.TargetScheduler.Planning.Exposures {

    /// <summary>
    /// Maintains proportional filter balance by adjusting the weighted rotation cycle
    /// to account for current frame deficits. Filters that are behind their ideal
    /// allocation get extra frames blended into the rotation, naturally closing gaps
    /// without a separate catch-up mode or oscillation-prone thresholds.
    ///
    /// For L:300, R:100, G:100, B:100 with B 3 frames behind:
    ///   Normal weights:   [3, 1, 1, 1] → L,L,L,R,G,B
    ///   Adjusted weights: [3, 1, 1, 4] → L,L,L,R,G,B,B,B,B
    ///
    /// When FilterSwitchFrequency > 1, each weight slot becomes a block.
    ///
    /// Returns null for single candidates or equal desired counts (defers to stock
    /// SmartExposureRotateManager).
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
        /// Select the next exposure using deficit-adjusted weighted rotation.
        /// Returns null only for degenerate cases (0-1 candidates) or when all
        /// candidates have equal desired counts and are balanced.
        /// </summary>
        public IExposure Select(List<IExposure> candidates) {
            List<IExposure> eligible = candidates.Where(e => !e.Rejected && e.Desired > 0).ToList();

            if (eligible.Count <= 1) {
                TSLogger.Debug($"ratio selector: {eligible.Count} eligible candidate(s), skipping (need >1)");
                return null;
            }

            // Calculate ratios and frame deficits
            var sb = new StringBuilder();
            sb.Append("ratio selector candidates: ");

            int totalFrames = 0;
            int totalDesired = 0;

            foreach (IExposure exposure in eligible) {
                totalFrames += CompletionCount(exposure);
                totalDesired += exposure.Desired;
            }

            double minRatio = double.MaxValue;
            double maxRatio = double.MinValue;
            double[] deficits = new double[eligible.Count];

            for (int i = 0; i < eligible.Count; i++) {
                IExposure exposure = eligible[i];
                double ratio = CompletionRatio(exposure);
                double idealCount = totalDesired > 0 ? (double)totalFrames * exposure.Desired / totalDesired : 0;
                deficits[i] = idealCount - CompletionCount(exposure);

                bool isProvisional = completionHelper != null && completionHelper.IsProvisionalPercentComplete(exposure);
                sb.Append($"{exposure.FilterName}={ratio:F3} ({exposure.Accepted}a/{exposure.Acquired}q/{exposure.Desired}d{(isProvisional ? " provisional" : "")}, deficit={deficits[i]:F1}), ");

                if (ratio < minRatio) minRatio = ratio;
                if (ratio > maxRatio) maxRatio = ratio;
            }

            double spread = maxRatio - minRatio;
            sb.Append($"spread={spread:F3}");
            TSLogger.Debug(sb.ToString());

            // --- Equal desired counts: use percentage-based dead band ---
            if (AllDesiredEqual(eligible)) {
                if (spread >= 0.05) {
                    IExposure mostBehind = eligible[Array.IndexOf(deficits, deficits.Max())];
                    TSLogger.Info($"ratio selector: {mostBehind.FilterName} is most behind (ratio={minRatio:F3}, spread={spread:F3}), prioritizing over default selection");
                    return mostBehind;
                }
                TSLogger.Debug($"ratio selector: equal desired counts, within dead band ({spread:F3}), deferring to default selector");
                return null;
            }

            // --- Deficit-adjusted weighted rotation ---
            IExposure selected = WeightedRotationSelect(eligible, deficits);
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
        /// Checks if all eligible candidates have the same Desired count.
        /// </summary>
        private bool AllDesiredEqual(List<IExposure> eligible) {
            int first = eligible[0].Desired;
            return eligible.All(e => e.Desired == first);
        }

        /// <summary>
        /// Selects the next filter from a deficit-adjusted weighted rotation cycle.
        /// Base weights come from desired counts (normalized by GCD). Filters that are
        /// behind their ideal allocation get extra weight equal to their deficit (rounded up),
        /// blending catch-up into the rotation pattern.
        ///
        /// For L:300, R:100, G:100, B:100 with B 3.2 frames behind:
        ///   Base weights:     [3, 1, 1, 1]
        ///   Deficit adjust:   [0, 0, 0, +4]  (ceil(3.2) = 4)
        ///   Adjusted weights: [3, 1, 1, 5]
        ///   Cycle: L,L,L,R,G,B,B,B,B,B (length 10)
        ///
        /// When FilterSwitchFrequency > 1, each weight is scaled by FSF for block shooting.
        /// </summary>
        internal IExposure WeightedRotationSelect(List<IExposure> eligible, double[] deficits) {
            // Detect candidate set changes and reset cycle
            var fingerprint = eligible.Select(e => e.FilterName).ToList();
            bool candidatesChanged = _lastCandidateFingerprint == null || !fingerprint.SequenceEqual(_lastCandidateFingerprint);
            if (candidatesChanged) {
                _lastCandidateFingerprint = fingerprint;
            }

            // Build base weights normalized by GCD
            int[] desiredCounts = eligible.Select(e => e.Desired).ToArray();
            int gcd = desiredCounts.Aggregate(GCD);
            if (gcd == 0) gcd = 1;
            int[] baseWeights = desiredCounts.Select(w => w / gcd).ToArray();

            // Cap base cycle length
            int baseCycleLength = baseWeights.Sum();
            if (baseCycleLength > MAX_CYCLE_LENGTH) {
                int divisor = (baseCycleLength + MAX_CYCLE_LENGTH - 1) / MAX_CYCLE_LENGTH;
                baseWeights = baseWeights.Select(w => Math.Max(1, w / divisor)).ToArray();
            }

            // Add deficit adjustment: filters behind ideal get extra weight
            int[] adjustedWeights = new int[eligible.Count];
            bool hasDeficit = false;
            for (int i = 0; i < eligible.Count; i++) {
                int extra = deficits[i] > 0 ? (int)Math.Ceiling(deficits[i]) : 0;
                adjustedWeights[i] = baseWeights[i] + extra;
                if (extra > 0) hasDeficit = true;
            }

            // Scale by FilterSwitchFrequency for block shooting
            int[] blockWeights = adjustedWeights.Select(w => w * filterSwitchFrequency).ToArray();
            int cycleLength = blockWeights.Sum();

            // Reset index on candidate set change
            if (candidatesChanged) {
                _weightedRotationIndex = FindStartIndex(eligible, deficits, blockWeights);
            }

            // Map index to filter using cumulative sums
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

            if (hasDeficit) {
                // Log adjusted weights when deficit correction is active
                string weightsStr = string.Join(",", eligible.Select((e, i) => $"{e.FilterName}={adjustedWeights[i]}"));
                TSLogger.Info($"ratio selector: deficit-adjusted rotation [{weightsStr}] -> {selected.FilterName}");
            } else {
                TSLogger.Debug($"ratio selector: weighted rotation -> {selected.FilterName}");
            }

            return selected;
        }

        /// <summary>
        /// Finds the starting index in the weighted rotation cycle for the filter
        /// with the largest frame deficit. Ensures the rotation begins with the
        /// filter that needs frames most, rather than an arbitrary position.
        /// </summary>
        private int FindStartIndex(List<IExposure> eligible, double[] deficits, int[] blockWeights) {
            // Find which filter has the largest deficit
            double worstDeficit = double.MinValue;
            int worstIndex = 0;
            for (int i = 0; i < eligible.Count; i++) {
                if (deficits[i] > worstDeficit) {
                    worstDeficit = deficits[i];
                    worstIndex = i;
                }
            }

            // If no filter is behind, start at 0
            if (worstDeficit <= 0) return 0;

            // Map the filter index to its block start position in the cycle
            int startPos = 0;
            for (int i = 0; i < worstIndex; i++) {
                startPos += blockWeights[i];
            }
            return startPos;
        }

        internal static int GCD(int a, int b) {
            a = Math.Abs(a);
            b = Math.Abs(b);
            while (b != 0) { int t = b; b = a % b; a = t; }
            return a;
        }
    }
}
