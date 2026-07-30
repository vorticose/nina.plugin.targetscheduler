using NINA.Astrometry;
using NINA.Plugin.TargetScheduler.Astrometry;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using NINA.Profile.Interfaces;
using System;

namespace NINA.Plugin.TargetScheduler.Planning {

    /// <summary>
    /// Determine moon avoidance for a target and exposure plan, supporting both classic, relaxed, and absolute (moon above relax max).
    ///
    /// We also calculate an avoidance score that can be used to sort exposures for automatic exposure plan selection.  If a plan was
    /// rejected, the score is zero.  Otherwise the score is:
    /// * 1 if moon down enabled is true
    /// * (required avoidance separation angle) / 180 otherwise
    ///
    /// This will sort exposure plans by maximum aversion to moonlight.  Since any comparison of scores is only for the same target
    /// (and therefore independent of actual target-moon separation), the score can simply use the calculated required avoidance
    /// separation which will always be > 0 and < 180.
    /// </summary>
    public class MoonAvoidanceExpert : IMoonAvoidanceExpert {
        public const double SCORE_OFF = 0;
        public const double SCORE_MAX = 1;

        private ObserverInfo observerInfo;

        public MoonAvoidanceExpert(ObserverInfo observerInfo) {
            this.observerInfo = observerInfo;
        }

        public MoonAvoidanceExpert(IProfile activeProfile) {
            this.observerInfo = new ObserverInfo {
                Latitude = activeProfile.AstrometrySettings.Latitude,
                Longitude = activeProfile.AstrometrySettings.Longitude,
                Elevation = activeProfile.AstrometrySettings.Elevation,
            };
        }

        public bool IsRejected(DateTime atTime, ITarget target, IExposure exposure) {
            if (!exposure.MoonAvoidanceEnabled) {
                exposure.MoonAvoidanceScore = SCORE_OFF;
                exposure.MoonAvoidanceDetail = MoonAvoidanceDetail.Disabled(atTime);
                return false;
            }

            DateTime evaluationTime = atTime;
            double moonAltitude = GetRelaxationMoonAltitude(evaluationTime);
            double moonAge = GetMoonAge(evaluationTime);
            double moonSeparation = GetMoonSeparationAngle(observerInfo, evaluationTime, target.Coordinates);
            exposure.MoonAvoidanceScore = GetAvoidanceScore(exposure);

            MoonAvoidanceDetail detail = Evaluate(evaluationTime, exposure, moonAltitude, moonAge, moonSeparation);
            exposure.MoonAvoidanceDetail = detail;

            TSLogger.Trace($"moon avoidance {target.Name}/{exposure.FilterName}: {detail}");
            return detail.Rejected;
        }

        /// <summary>
        /// Decide moon avoidance from already-determined circumstances.  This is a pure function of the exposure's
        /// avoidance parameters plus the three moon values, which lets callers that need many evaluations (such as
        /// sweeping a whole night) compute the expensive astrometry once per point in time and reuse it across
        /// every target and exposure plan.
        ///
        /// The order of the checks below is the definition of avoidance behavior - don't reorder them.
        /// </summary>
        /// <param name="atTime"></param>
        /// <param name="exposure"></param>
        /// <param name="moonAltitude">moon altitude in degrees</param>
        /// <param name="moonAge">moon age in days</param>
        /// <param name="moonSeparation">target-moon angular separation in degrees</param>
        /// <returns>the full evaluation record</returns>
        public static MoonAvoidanceDetail Evaluate(DateTime atTime, IExposure exposure, double moonAltitude,
            double moonAge, double moonSeparation) {
            if (!exposure.MoonAvoidanceEnabled) {
                return MoonAvoidanceDetail.Disabled(atTime);
            }

            double baseSeparationParameter = exposure.MoonAvoidanceSeparation;
            double baseWidthParameter = exposure.MoonAvoidanceWidth;
            double moonSeparationParameter = baseSeparationParameter;
            double moonWidthParameter = baseWidthParameter;
            bool relaxationApplied = false;

            // If moon altitude is in the relaxation zone, then modulate the separation and width parameters
            if (moonAltitude <= exposure.MoonRelaxMaxAltitude && exposure.MoonRelaxScale > 0) {
                relaxationApplied = true;
                moonSeparationParameter = moonSeparationParameter + (exposure.MoonRelaxScale * (moonAltitude - exposure.MoonRelaxMaxAltitude));
                moonWidthParameter = moonWidthParameter * ((moonAltitude - exposure.MoonRelaxMinAltitude) / (exposure.MoonRelaxMaxAltitude - exposure.MoonRelaxMinAltitude));
            }

            double moonAvoidanceSeparation = AstrometryUtils.GetMoonAvoidanceLorentzianSeparation(moonAge,
                moonSeparationParameter, moonWidthParameter);

            MoonAvoidanceOutcome outcome;

            // Avoidance is completely off if the moon is below the relaxation min altitude and relaxation applies
            if (moonAltitude <= exposure.MoonRelaxMinAltitude && exposure.MoonRelaxScale > 0) {
                outcome = MoonAvoidanceOutcome.RelaxedOff;
            }

            // Avoidance is absolute regardless of moon phase or separation if Moon Must Be Down is enabled
            else if (moonAltitude >= exposure.MoonRelaxMaxAltitude && exposure.MoonDownEnabled) {
                outcome = MoonAvoidanceOutcome.MoonDownBlocked;
            }

            // If the separation was relaxed into oblivion, avoidance is off
            else if (moonSeparationParameter <= 0) {
                outcome = MoonAvoidanceOutcome.RelaxedToZero;
            } else {
                outcome = moonSeparation < moonAvoidanceSeparation
                    ? MoonAvoidanceOutcome.Blocked
                    : MoonAvoidanceOutcome.Clear;
            }

            return MoonAvoidanceDetail.Create(atTime, outcome, moonAltitude, moonAge, moonSeparation,
                moonAvoidanceSeparation, baseSeparationParameter, baseWidthParameter, moonSeparationParameter,
                moonWidthParameter, relaxationApplied, exposure.MoonDownEnabled);
        }

        /// <summary>
        /// The moon avoidance/aversion score is statically determined from the avoidance parameters and is
        /// independent of actual target-moon separation or moon altitude.
        /// </summary>
        /// <param name="exposure"></param>
        /// <returns></returns>
        public double GetAvoidanceScore(IExposure exposure) {
            if (!exposure.MoonAvoidanceEnabled) return SCORE_OFF;
            if (exposure.MoonDownEnabled) return SCORE_MAX;

            return (exposure.MoonAvoidanceSeparation * exposure.MoonAvoidanceWidth) / (180 * 14);

            /*
            return rejected
                ? SCORE_OFF
                : planExposure.MoonDownEnabled || moonAvoidanceSeparation < 0
                    ? SCORE_MAX
                    : moonAvoidanceSeparation / 180;
            */
        }

        public virtual double GetRelaxationMoonAltitude(DateTime evaluationTime) {
            return AstroUtil.GetMoonAltitude(evaluationTime, observerInfo);
        }

        public virtual double GetMoonAge(DateTime atTime) {
            return AstrometryUtils.GetMoonAge(atTime);
        }

        public virtual double GetMoonSeparationAngle(ObserverInfo location, DateTime atTime, Coordinates coordinates) {
            return AstrometryUtils.GetMoonSeparationAngle(observerInfo, atTime, coordinates);
        }
    }

    public interface IMoonAvoidanceExpert {

        bool IsRejected(DateTime atTime, ITarget planTarget, IExposure planExposure);

        double GetRelaxationMoonAltitude(DateTime evaluationTime);

        double GetMoonAge(DateTime atTime);

        double GetMoonSeparationAngle(ObserverInfo location, DateTime atTime, Coordinates coordinates);
    }
}