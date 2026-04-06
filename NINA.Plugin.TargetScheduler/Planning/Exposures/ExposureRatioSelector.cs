using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NINA.Plugin.TargetScheduler.Planning.Exposures {

    /// <summary>
    /// Selects the exposure that is furthest behind its target ratio (Accepted/Desired).
    /// Uses Acquired instead of Accepted when grading is delayed and the threshold has not been reached.
    /// Returns null if all candidates are within the dead band (roughly balanced),
    /// signaling the caller to fall back to its default selection behavior.
    /// </summary>
    public class ExposureRatioSelector {
        public const double DEAD_BAND = 0.05;

        private ExposureCompletionHelper completionHelper;

        public ExposureRatioSelector(ExposureCompletionHelper completionHelper) {
            this.completionHelper = completionHelper;
        }

        /// <summary>
        /// Select the exposure with the lowest completion ratio among the candidates.
        /// Returns null if all candidates are within the dead band or there is only one candidate.
        /// </summary>
        public IExposure Select(List<IExposure> candidates) {
            List<IExposure> eligible = candidates.Where(e => !e.Rejected && e.Desired > 0).ToList();

            if (eligible.Count <= 1) {
                TSLogger.Debug($"ratio selector: {eligible.Count} eligible candidate(s), skipping (need >1)");
                return null;
            }

            // Log all ratios for visibility
            var sb = new StringBuilder();
            sb.Append("ratio selector candidates: ");

            double minRatio = double.MaxValue;
            double maxRatio = double.MinValue;
            IExposure mostBehind = null;

            foreach (IExposure exposure in eligible) {
                double ratio = CompletionRatio(exposure);
                bool isProvisional = completionHelper != null && completionHelper.IsProvisionalPercentComplete(exposure);
                sb.Append($"{exposure.FilterName}={ratio:F3} ({exposure.Accepted}a/{exposure.Acquired}q/{exposure.Desired}d{(isProvisional ? " provisional" : "")}), ");

                if (ratio < minRatio) {
                    minRatio = ratio;
                    mostBehind = exposure;
                }
                if (ratio > maxRatio) {
                    maxRatio = ratio;
                }
            }

            double spread = maxRatio - minRatio;
            sb.Append($"spread={spread:F3}, deadBand={DEAD_BAND}");
            TSLogger.Debug(sb.ToString());

            if (spread < DEAD_BAND) {
                TSLogger.Debug($"ratio selector: within dead band ({spread:F3} < {DEAD_BAND}), deferring to default selector");
                return null;
            }

            TSLogger.Info($"ratio selector: {mostBehind.FilterName} is most behind (ratio={minRatio:F3}), prioritizing over default selection");
            return mostBehind;
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
    }
}
