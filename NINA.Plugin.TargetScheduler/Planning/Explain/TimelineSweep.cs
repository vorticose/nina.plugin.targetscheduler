using NINA.Astrometry;
using NINA.Plugin.TargetScheduler.Astrometry;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugin.TargetScheduler.Planning.Explain {

    /*
     * **CUSTOM FORK** Planning insight / scoring transparency feature.
     *
     * Chain-independent sweep of the night that produces, per target:
     *   - eligibility bands (would the target pass the planner's filters at time T, and if not, why)
     *   - an altitude curve (from the cached TargetVisibility samples)
     *
     * Eligibility (visibility / altitude / meridian / twilight / moon) does NOT depend on what was being
     * imaged before, so this reconstruction is faithful to the planner without needing the chain. Only
     * scoring is chain-dependent, and that is intentionally sourced from PlanExplainer's decision snapshots
     * (never reconstructed here) to avoid teaching a model the planner doesn't actually use.
     *
     * Humidity is skipped: it is a live-only concern (the preview has no weather forecast).
     */

    public class SweepResult {
        public List<TargetTimeline> Targets { get; set; } = new List<TargetTimeline>();
        public TwilightWindows Twilight { get; set; } = new TwilightWindows();
        public DateTime NightStart { get; set; }
        public DateTime NightEnd { get; set; }
    }

    public class TimelineSweep {
        private readonly IProfile profile;
        private readonly ProfilePreference profilePreferences;
        private readonly int stepMinutes;

        public TimelineSweep(IProfile profile, ProfilePreference profilePreferences, int stepMinutes = 10) {
            this.profile = profile;
            this.profilePreferences = profilePreferences;
            this.stepMinutes = Math.Max(1, stepMinutes);
        }

        public SweepResult Sweep(DateTime atTime, List<IProject> projects) {
            ObserverInfo observerInfo = new ObserverInfo {
                Latitude = profile.AstrometrySettings.Latitude,
                Longitude = profile.AstrometrySettings.Longitude,
                Elevation = profile.AstrometrySettings.Elevation,
            };

            TwilightCircumstances twilight = TwilightCircumstances.AdjustTwilightCircumstances(observerInfo, atTime);
            DateTime nightStart = twilight.CivilTwilightStart ?? atTime;
            DateTime nightEnd = twilight.CivilTwilightEnd ?? atTime.AddHours(12);
            if (nightEnd <= nightStart) { nightEnd = nightStart.AddHours(12); }

            TargetImagingExpert expert = new TargetImagingExpert(profile, profilePreferences, true);
            IMoonAvoidanceExpert moonExpert = new MoonAvoidanceExpert(observerInfo);

            SweepResult result = new SweepResult {
                Twilight = BuildTwilightWindows(twilight),
                NightStart = nightStart,
                NightEnd = nightEnd
            };

            try {
                foreach (IProject project in projects) {
                    foreach (ITarget target in project.Targets) {
                        result.Targets.Add(BuildTimeline(target, project, observerInfo, twilight, nightStart, nightEnd, expert, moonExpert));
                    }
                }
            } catch (Exception ex) {
                TSLogger.Error($"exception during timeline sweep: {ex.Message}\n{ex.StackTrace}");
            }

            return result;
        }

        private TargetTimeline BuildTimeline(ITarget target, IProject project, ObserverInfo observerInfo,
                TwilightCircumstances twilight, DateTime nightStart, DateTime nightEnd,
                TargetImagingExpert expert, IMoonAvoidanceExpert moonExpert) {
            TargetTimeline tl = new TargetTimeline {
                ProjectName = project.Name,
                TargetName = target.Name,
                TargetDatabaseId = target.DatabaseId,
                MinimumAltitude = project.MinimumAltitude,
                MaximumAltitude = project.MaximumAltitude,
                UsesCustomHorizon = project.UseCustomHorizon
            };

            // Altitude curve from the cached visibility samples.
            TargetVisibility visibility = new TargetVisibility(target, observerInfo, twilight.OnDate, twilight.Sunset, twilight.Sunrise);
            tl.ImagingPossible = visibility.ImagingPossible;

            double peak = double.MinValue;
            for (DateTime t = nightStart; t <= nightEnd; t = t.AddMinutes(stepMinutes)) {
                double alt = visibility.GetAltitude(t);
                if (alt == double.MinValue) { continue; }
                tl.AltitudeSamples.Add(new AltitudeSample { Time = t, Altitude = alt });
                if (alt > peak) { peak = alt; }
            }
            tl.PeakAltitude = (peak == double.MinValue) ? 0 : peak;

            // Eligibility bands: evaluate the planner's filters at each step and coalesce equal runs.
            EligibilitySegment current = null;
            for (DateTime t = nightStart; t <= nightEnd; t = t.AddMinutes(stepMinutes)) {
                EligibilityState state = EvaluateAt(t, target, expert, moonExpert, twilight);
                if (current == null || current.Eligible != state.Eligible || current.Reason != state.Reason) {
                    if (current != null) {
                        current.End = t;
                        tl.EligibilitySegments.Add(current);
                    }
                    current = new EligibilitySegment { Start = t, Eligible = state.Eligible, Reason = state.Reason };
                }
            }
            if (current != null) {
                current.End = nightEnd;
                tl.EligibilitySegments.Add(current);
            }

            return tl;
        }

        private struct EligibilityState {
            public bool Eligible;
            public string Reason;
        }

        /// <summary>
        /// Faithful per-instant eligibility using the same experts the planner uses. Visibility covers
        /// rise/altitude/meridian; then the twilight + moon-avoidance exposure filters are applied and the
        /// target is ineligible if every exposure plan is rejected.
        ///
        /// MinimumTime is not required here. The planner uses it to decide whether it can *start a new
        /// block*; applying that to the band paints "not yet visible" holes while the target is still up
        /// (leftover in the current window shorter than MinimumTime). The band answers "could this
        /// target be imaged at T," matching altitude/twilight/moon, not "would GetPlan open a new 30-min
        /// commitment at T."
        /// </summary>
        private EligibilityState EvaluateAt(DateTime atTime, ITarget target, TargetImagingExpert expert,
                IMoonAvoidanceExpert moonExpert, TwilightCircumstances twilight) {
            expert.ClearRejections(target);
            expert.Visibility(atTime, target, requireMinimumTime: false);
            if (target.Rejected) {
                return new EligibilityState { Eligible = false, Reason = target.RejectedReason };
            }

            TwilightLevel? level = twilight.GetCurrentTwilightLevel(atTime);
            expert.TwilightFilter(target, atTime, twilight, level);
            expert.MoonAvoidanceFilter(atTime, target, moonExpert);

            if (target.ExposurePlans.Count > 0 && target.ExposurePlans.All(ep => ep.Rejected)) {
                return new EligibilityState { Eligible = false, Reason = DeriveExposureReason(target) };
            }

            return new EligibilityState { Eligible = true, Reason = null };
        }

        // Mirrors Planner.GetTargetExposureRejectReason so timeline reasons match the planner's vocabulary.
        private string DeriveExposureReason(ITarget target) {
            List<IExposure> eps = target.ExposurePlans;
            if (eps.All(ep => ep.RejectedReason == Reasons.FilterComplete)) { return Reasons.TargetComplete; }
            if (eps.All(ep => ep.RejectedReason == Reasons.FilterTwilight)) { return Reasons.TargetTwilight; }
            if (eps.All(ep => ep.RejectedReason == Reasons.FilterMoonAvoidance)) { return Reasons.TargetMoonAvoidance; }
            if (eps.All(ep => ep.RejectedReason == Reasons.FilterHumidity)) { return Reasons.TargetHumidity; }
            return Reasons.TargetAllExposurePlans;
        }

        private TwilightWindows BuildTwilightWindows(TwilightCircumstances tc) {
            return new TwilightWindows {
                CivilStart = tc.CivilTwilightStart,
                CivilEnd = tc.CivilTwilightEnd,
                NauticalStart = tc.NauticalTwilightStart,
                NauticalEnd = tc.NauticalTwilightEnd,
                AstronomicalStart = tc.AstronomicalTwilightStart,
                AstronomicalEnd = tc.AstronomicalTwilightEnd,
                NighttimeStart = tc.NighttimeStart,
                NighttimeEnd = tc.NighttimeEnd
            };
        }
    }
}
