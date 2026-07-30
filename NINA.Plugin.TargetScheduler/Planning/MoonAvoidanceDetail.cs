using NINA.Plugin.TargetScheduler.Astrometry;
using System;

namespace NINA.Plugin.TargetScheduler.Planning {

    /// <summary>
    /// How a moon avoidance evaluation was decided.  The order of these matches the order of the checks
    /// in MoonAvoidanceExpert.Evaluate().
    /// </summary>
    public enum MoonAvoidanceOutcome {

        /// Moon avoidance is not enabled on the exposure template.
        Disabled,

        /// The moon is below the relax minimum altitude and relaxation applies: avoidance is off.
        RelaxedOff,

        /// The moon is up and Moon Must Be Down is enabled: rejected regardless of phase or separation.
        MoonDownBlocked,

        /// Relaxation drove the separation parameter to zero or below: avoidance is off.
        RelaxedToZero,

        /// Target-moon separation is less than the required avoidance separation: rejected.
        Blocked,

        /// Target-moon separation meets or exceeds the required avoidance separation: accepted.
        Clear
    }

    /// <summary>
    /// A full record of one moon avoidance evaluation for a single exposure plan at a single point in time.
    /// This captures both the inputs (moon altitude/age, target-moon separation, the avoidance parameters
    /// actually in force after any relaxation) and the outcome, so the decision can be explained rather than
    /// just applied.
    ///
    /// Instances are created by MoonAvoidanceExpert and are immutable.
    /// </summary>
    public class MoonAvoidanceDetail {
        public DateTime AtTime { get; private set; }
        public MoonAvoidanceOutcome Outcome { get; private set; }

        /// Moon altitude in degrees at AtTime.
        public double MoonAltitude { get; private set; }

        /// Moon age in days (0 and 29.53 are new, ~14.8 is full).
        public double MoonAge { get; private set; }

        /// Actual angular separation in degrees between the target and the moon.
        public double MoonSeparation { get; private set; }

        /// The separation in degrees the target must clear to be accepted (the Lorentzian result).
        public double RequiredSeparation { get; private set; }

        /// The separation/width parameters from the exposure template, before relaxation.
        public double BaseSeparationParameter { get; private set; }

        public double BaseWidthParameter { get; private set; }

        /// The separation/width parameters actually used, after any relaxation.
        public double SeparationParameter { get; private set; }

        public double WidthParameter { get; private set; }

        /// True if the moon was low enough for the relaxation zone to modulate the parameters.
        public bool RelaxationApplied { get; private set; }

        public bool MoonDownEnabled { get; private set; }

        public bool Rejected => Outcome == MoonAvoidanceOutcome.Blocked || Outcome == MoonAvoidanceOutcome.MoonDownBlocked;

        public bool Evaluated => Outcome != MoonAvoidanceOutcome.Disabled;

        /// <summary>
        /// How much room to spare (positive) or how far short (negative) the target is, in degrees.  Only
        /// meaningful when the Lorentzian comparison actually decided the outcome.
        /// </summary>
        public double Margin => MoonSeparation - RequiredSeparation;

        /// <summary>
        /// Illumination of the moon as a fraction 0-1, approximated from the moon age.  This is intentionally
        /// derived rather than calculated from ephemerides: it's for display only and the exact value isn't
        /// used in any avoidance decision.
        /// </summary>
        public double ApproximateMoonIllumination =>
            (1 - Math.Cos(2 * Math.PI * MoonAge / AstrometryUtils.DAYS_IN_LUNAR_CYCLE)) / 2;

        /// <summary>
        /// The largest value the exposure template's Moon Avoidance Separation could be set to and still allow
        /// this exposure at this time.  Null when no separation setting would help - either because the exposure
        /// isn't blocked by the Lorentzian comparison at all, or because Moon Must Be Down is what rejected it.
        ///
        /// The returned value is in template terms (i.e. relaxation is added back in), so it can be typed
        /// directly into the Moon Avoidance Separation field.  It's floored to a whole degree: the exact
        /// inverse lands precisely on the accept/reject boundary, where both floating point rounding and
        /// rounding the number for display can tip it back to rejected.
        /// </summary>
        public double? MaximumPassingSeparationSetting {
            get {
                if (Outcome != MoonAvoidanceOutcome.Blocked) { return null; }
                if (WidthParameter == 0) { return null; }

                double k = (0.5 - (MoonAge / AstrometryUtils.DAYS_IN_LUNAR_CYCLE)) / (WidthParameter / AstrometryUtils.DAYS_IN_LUNAR_CYCLE);
                double maximumRelaxedSeparation = MoonSeparation * (1 + (k * k));

                // Back out the relaxation offset so the answer is in terms of the template setting
                double relaxationOffset = SeparationParameter - BaseSeparationParameter;
                double setting = Math.Floor(maximumRelaxedSeparation - relaxationOffset);

                return setting > 0 ? setting : 0;
            }
        }

        private MoonAvoidanceDetail() {
        }

        public static MoonAvoidanceDetail Disabled(DateTime atTime) {
            return new MoonAvoidanceDetail {
                AtTime = atTime,
                Outcome = MoonAvoidanceOutcome.Disabled
            };
        }

        public static MoonAvoidanceDetail Create(DateTime atTime, MoonAvoidanceOutcome outcome, double moonAltitude,
            double moonAge, double moonSeparation, double requiredSeparation, double baseSeparationParameter,
            double baseWidthParameter, double separationParameter, double widthParameter, bool relaxationApplied,
            bool moonDownEnabled) {
            return new MoonAvoidanceDetail {
                AtTime = atTime,
                Outcome = outcome,
                MoonAltitude = moonAltitude,
                MoonAge = moonAge,
                MoonSeparation = moonSeparation,
                RequiredSeparation = requiredSeparation,
                BaseSeparationParameter = baseSeparationParameter,
                BaseWidthParameter = baseWidthParameter,
                SeparationParameter = separationParameter,
                WidthParameter = widthParameter,
                RelaxationApplied = relaxationApplied,
                MoonDownEnabled = moonDownEnabled
            };
        }

        /// <summary>
        /// A short phrase explaining the outcome, suitable for a table cell or a log line.
        /// </summary>
        public string StatusText {
            get {
                switch (Outcome) {
                    case MoonAvoidanceOutcome.Disabled: return "avoidance off";
                    case MoonAvoidanceOutcome.RelaxedOff: return "moon down (relaxed off)";
                    case MoonAvoidanceOutcome.MoonDownBlocked: return "blocked: moon must be down";
                    case MoonAvoidanceOutcome.RelaxedToZero: return "relaxed below zero";
                    case MoonAvoidanceOutcome.Blocked: return $"blocked by {Math.Abs(Margin):F1}°";
                    case MoonAvoidanceOutcome.Clear: return $"clear by {Margin:F1}°";
                    default: return Outcome.ToString();
                }
            }
        }

        public override string ToString() {
            if (Outcome == MoonAvoidanceOutcome.Disabled) { return "moon avoidance disabled"; }

            string relaxed = RelaxationApplied
                ? $", relaxed to {SeparationParameter:F1}°/{WidthParameter:F1}d"
                : string.Empty;

            return $"{StatusText}: separation {MoonSeparation:F1}° vs required {RequiredSeparation:F1}°, " +
                   $"moon alt {MoonAltitude:F1}°, age {MoonAge:F1}d, " +
                   $"settings {BaseSeparationParameter:F1}°/{BaseWidthParameter:F1}d{relaxed}";
        }
    }
}
