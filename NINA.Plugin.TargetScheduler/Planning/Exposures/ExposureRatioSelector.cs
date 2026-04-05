using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using System.Collections.Generic;
using System.Linq;

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
                return null;
            }

            double minRatio = double.MaxValue;
            double maxRatio = double.MinValue;
            IExposure mostBehind = null;

            foreach (IExposure exposure in eligible) {
                double ratio = CompletionRatio(exposure);
                if (ratio < minRatio) {
                    minRatio = ratio;
                    mostBehind = exposure;
                }
                if (ratio > maxRatio) {
                    maxRatio = ratio;
                }
            }

            if (maxRatio - minRatio < DEAD_BAND) {
                return null;
            }

            return mostBehind;
        }

        /// <summary>
        /// Calculate the completion ratio for an exposure plan.
        /// Uses Accepted/Desired normally, but falls back to Acquired/Desired when
        /// grading is delayed and the delay threshold has not been reached yet.
        /// </summary>
        public double CompletionRatio(IExposure exposure) {
            if (exposure.Desired == 0) return 1.0;

            if (completionHelper != null && completionHelper.IsProvisionalPercentComplete(exposure)) {
                return (double)exposure.Acquired / (double)exposure.Desired;
            }

            return (double)exposure.Accepted / (double)exposure.Desired;
        }
    }
}
