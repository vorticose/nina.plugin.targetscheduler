using NINA.Astrometry;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin.TargetScheduler.Astrometry;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using NINA.Plugin.TargetScheduler.Util;
using NINA.Profile.Interfaces;
using System;
using System.Linq;

namespace NINA.Plugin.TargetScheduler.Planning {

    /// <summary>
    /// Core methods to determine the suitability of a target and target exposures for imaging.  Note that
    /// these methods cause side-effects on the provided target and exposures, for example setting
    /// rejection details.
    /// </summary>
    public class TargetImagingExpert {
        private const int TARGET_VISIBILITY_SAMPLE_INTERVAL = 10;
        private const int TARGET_FUTURE_TEST_SAMPLE_INTERVAL = 60;

        private IProfile profile;
        private ObserverInfo observerInfo;
        private int targetVisibilitySampleInterval = TARGET_VISIBILITY_SAMPLE_INTERVAL;
        private int targetFutureTestSampleInterval = TARGET_FUTURE_TEST_SAMPLE_INTERVAL;

        public TargetImagingExpert(IProfile activeProfile, ProfilePreference profilePreferences, bool isPreview) {
            this.profile = activeProfile;
            this.observerInfo = new ObserverInfo {
                Latitude = activeProfile.AstrometrySettings.Latitude,
                Longitude = activeProfile.AstrometrySettings.Longitude,
                Elevation = activeProfile.AstrometrySettings.Elevation,
            };

            if (isPreview) {
                targetVisibilitySampleInterval *= 3;
                targetFutureTestSampleInterval *= 3;
            }
        }

        public bool Visibility(DateTime atTime, ITarget target, bool requireMinimumTime = true) {
            TwilightCircumstances twilightCircumstances = TwilightCircumstances.AdjustTwilightCircumstances(observerInfo, atTime);
            TargetVisibility targetVisibility = new(target, observerInfo,
                twilightCircumstances.OnDate, twilightCircumstances.Sunset, twilightCircumstances.Sunrise, targetVisibilitySampleInterval);

            return Visibility(atTime, target, twilightCircumstances, targetVisibility, requireMinimumTime);
        }

        /// <summary>
        /// Determine visibility for the target at the provided time.
        /// </summary>
        /// <param name="atTime"></param>
        /// <param name="target"></param>
        /// <param name="twilightCircumstances"></param>
        /// <param name="targetVisibility"></param>
        /// <param name="requireMinimumTime">
        /// When true (planner default), the next interval must be at least project MinimumTime long.
        /// Short leftovers are skipped and the next later window is used, which rejects as
        /// "not yet visible" even if the target is currently above the horizon. Insight eligibility
        /// passes false so the band answers "is it up now" rather than "can I start a new block."
        /// </param>
        /// <returns></returns>
        public bool Visibility(DateTime atTime, ITarget target, TwilightCircumstances twilightCircumstances, TargetVisibility targetVisibility, bool requireMinimumTime = true) {
            if (target.Rejected) { return false; }
            IProject project = target.Project;

            if (!AstrometryUtils.RisesAtLocation(observerInfo, target.Coordinates)) {
                TSLogger.Warning($"target {project.Name}/{target.Name} never rises at location - skipping");
                SetRejected(target, Reasons.TargetNeverRises);
                return false;
            }

            // Get the most inclusive twilight over all incomplete exposure plans
            TimeInterval twilightSpan = GetTwilightSpan(twilightCircumstances, target);

            // At high latitudes near the summer solsice, you can lose nighttime completely (even below the polar circle)
            if (twilightSpan == null) {
                TSLogger.Warning($"No twilight span for target {project.Name}/{target.Name} on {Utils.FormatDateTimeFull(atTime)} at latitude {observerInfo.Latitude}");
                SetRejected(target, Reasons.TargetAllExposurePlans);
                return false;
            }

            if (!targetVisibility.ImagingPossible) {
                TSLogger.Warning($"Target not visible at all {project.Name}/{target.Name} on {Utils.FormatDateTimeFull(atTime)} at latitude {observerInfo.Latitude}");
                SetRejected(target, Reasons.TargetNotVisible);
                return false;
            }

            // Planner: next interval must fit MinimumTime. Insight: any current interval is enough.
            VisibilityDetermination viz = requireMinimumTime
                ? targetVisibility.NextVisibleInterval(atTime, twilightSpan, project.HorizonDefinition, project.MinimumTime * 60)
                : targetVisibility.NextVisibleInterval(atTime, twilightSpan, project.HorizonDefinition);
            if (!viz.IsVisible) {
                LogVisibility("no-interval", atTime, target, twilightSpan, targetVisibility, viz, requireMinimumTime, Reasons.TargetNotVisible);
                SetRejected(target, Reasons.TargetNotVisible);
                return false;
            }

            DateTime targetStartTime = viz.StartTime;
            DateTime targetTransitTime = targetVisibility.TransitTime;
            DateTime targetEndTime = viz.StopTime;

            // Clip time span to optional meridian window
            TimeInterval meridianClippedSpan = null;
            if (project.MeridianWindow > 0) {
                TSLogger.Trace($"checking meridian window for {project.Name}/{target.Name}");
                meridianClippedSpan = new MeridianWindowClipper().Clip(targetStartTime, targetTransitTime, targetEndTime, project.MeridianWindow);

                if (meridianClippedSpan == null) {
                    SetRejected(target, Reasons.TargetMeridianWindowClipped);
                    return false;
                }

                target.MeridianWindow = meridianClippedSpan;
                targetStartTime = meridianClippedSpan.StartTime;
                targetEndTime = meridianClippedSpan.EndTime;
            }

            // Recheck minimum time after potential meridian clip
            if (project.MeridianWindow > 0 && target.MeridianWindow.Duration < project.MinimumTime * 60) {
                TSLogger.Debug($"Target not visible for min time after meridian window clip: {project.Name}/{target.Name} on {Utils.FormatDateTimeFull(atTime)} at latitude {observerInfo.Latitude}");
                SetRejected(target, Reasons.TargetNotVisible);
                return false;
            }

            // Check for maximum altitude exceeded
            if (project.MaximumAltitude != 0) {
                MaximumAltitudeClipper maxAltitudeClipper = targetVisibility.MaxAltitudeClipper;
                if (maxAltitudeClipper.IsClipped) {
                    TimeInterval exceedsMaxInterval = maxAltitudeClipper.ClipInterval;

                    // See if imaging the minimum time is possible before max is exceeded
                    if (targetStartTime.AddMinutes(project.MinimumTime) < exceedsMaxInterval.StartTime) {
                        // There is time now before the max is exceeded
                        targetEndTime = exceedsMaxInterval.StartTime;
                    } else {
                        // No time now but might be later
                        TSLogger.Debug($"Target not visible for min time after max altitude clip: {project.Name}/{target.Name} on {Utils.FormatDateTimeFull(atTime)} at latitude {observerInfo.Latitude}, max alt is {project.MaximumAltitude}");
                        SetRejected(target, Reasons.TargetMaxAltitude);
                        return false;
                    }
                }
            }

            // Special handling when profile specifies a pause before MF.  If pause > 0 and that pause would occur in the visibility
            // span, then adjust the target start or end times to avoid the MF safety zone.
            TimeInterval meridianFlipClippedSpan = null;
            if (profile.MeridianFlipSettings?.PauseTimeBeforeMeridian > 0) {
                MeridianFlipClipper mfClipper = new MeridianFlipClipper(profile, atTime, target, targetStartTime, targetTransitTime, targetEndTime);
                meridianFlipClippedSpan = mfClipper.Clip();
                if (meridianFlipClippedSpan == null) {
                    TSLogger.Debug($"Target meridian flip clip {project.Name}/{target.Name} on {Utils.FormatDateTimeFull(atTime)}");
                    SetRejected(target, Reasons.TargetMeridianFlipClipped);
                    return false;
                }

                // Recheck minimum time after potential meridian flip clip
                if (meridianFlipClippedSpan.Duration < project.MinimumTime * 60) {
                    TSLogger.Debug($"Target not visible for min time before meridian flip, potentially visible later: {project.Name}/{target.Name} on {Utils.FormatDateTimeFull(atTime)} at latitude {observerInfo.Latitude}");
                    targetStartTime = mfClipper.GetSafeAfterTime();
                } else {
                    targetStartTime = meridianFlipClippedSpan.StartTime;
                    targetEndTime = meridianFlipClippedSpan.EndTime;
                }
            }

            // If the start time is in the future, reject ... for now
            DateTime actualStart = atTime > targetStartTime ? atTime : targetStartTime;
            if (actualStart > atTime) {
                target.StartTime = actualStart;
                target.EndTime = targetEndTime;
                string reason = Reasons.TargetNotYetVisible;
                if (meridianClippedSpan != null || meridianFlipClippedSpan != null) {
                    reason = meridianClippedSpan != null ? Reasons.TargetBeforeMeridianWindow : Reasons.TargetMeridianFlipClipped;
                }

                LogVisibility($"future-start reason={reason} actualStart={Utils.FormatDateTimeFull(actualStart)}",
                    atTime, target, twilightSpan, targetVisibility, viz, requireMinimumTime, reason);
                SetRejected(target, reason);
                return false;
            }

            // Otherwise the target is a candidate
            target.StartTime = targetStartTime;
            target.EndTime = targetEndTime;
            target.MinimumTimeSpanEnd = atTime.AddSeconds(project.MinimumTime * 60);
            target.BonusTimeSpanEnd = target.MinimumTimeSpanEnd;
            target.CulminationTime = targetTransitTime;
            LogVisibility("ok", atTime, target, twilightSpan, targetVisibility, viz, requireMinimumTime, null);
            return true;
        }

        private void LogVisibility(string outcome, DateTime atTime, ITarget target, TimeInterval twilightSpan,
                TargetVisibility targetVisibility, VisibilityDetermination viz, bool requireMinimumTime, string rejectReason) {
            IProject project = target.Project;
            double altNow = targetVisibility.GetAltitude(atTime);
            double altStop = (viz != null && viz.IsVisible) ? targetVisibility.GetAltitude(viz.StopTime) : double.MinValue;
            double durMin = (viz != null && viz.IsVisible) ? (viz.StopTime - viz.StartTime).TotalMinutes : 0;
            string filters = string.Join(",", target.ExposurePlans.Select(e =>
                $"{e.FilterName}:{e.TwilightLevel}/off={e.MinutesOffset}/rej={(e.Rejected ? e.RejectedReason : "ok")}"));
            TSLogger.Debug(
                $"VIS-DIAG {outcome} {project.Name}/{target.Name} at={Utils.FormatDateTimeFull(atTime)} " +
                $"minTime={(requireMinimumTime ? project.MinimumTime : 0)}m horizon={project.HorizonDefinition} " +
                $"twilight=[{Utils.FormatDateTimeFull(twilightSpan?.StartTime)}..{Utils.FormatDateTimeFull(twilightSpan?.EndTime)}] " +
                $"viz=[{(viz != null && viz.IsVisible ? Utils.FormatDateTimeFull(viz.StartTime) : "n/a")}..{(viz != null && viz.IsVisible ? Utils.FormatDateTimeFull(viz.StopTime) : "n/a")}] " +
                $"dur={durMin:0.0}m altNow={altNow:0.0} altStop={altStop:0.0} transit={Utils.FormatDateTimeFull(targetVisibility.TransitTime)} " +
                $"reject={rejectReason ?? "none"} filters=[{filters}]");
        }

        private static string FormatFilterRejections(ITarget target) {
            return "filters=[" + string.Join(",", target.ExposurePlans.Select(e =>
                $"{e.FilterName}:{(e.Rejected ? e.RejectedReason : "ok")}")) + "]";
        }

        /// <summary>
        /// Return the span of time for the exposure with the most 'permissive' (brightest) twilight, taking
        /// into account an optional twilight offset.
        /// </summary>
        /// <param name="twilightCircumstances"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        /// <exception cref="Exception"></exception>
        public TimeInterval GetTwilightSpan(TwilightCircumstances twilightCircumstances, ITarget target) {
            DateTime start = DateTime.MaxValue;
            DateTime end = DateTime.MinValue;

            foreach (IExposure exposure in target.ExposurePlans) {
                if (!exposure.Rejected && exposure.IsIncomplete()) {
                    TimeInterval span = twilightCircumstances.GetTwilightSpan(exposure.TwilightLevel);
                    if (span != null) {
                        DateTime d = span.StartTime.AddMinutes(exposure.MinutesOffset);
                        if (start > d) start = d;
                        d = span.EndTime.AddMinutes(-1 * exposure.MinutesOffset);
                        if (end < d) end = d;
                    }
                }
            }

            return (start == DateTime.MaxValue || end == DateTime.MinValue) ? null : new TimeInterval(start, end);
        }

        /// <summary>
        /// Reject targets that are currently above the project's max altitude setting.
        /// </summary>
        /// <param name="atTime"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        public bool CheckMaximumAltitude(DateTime atTime, ITarget target, TargetVisibility targetVisibility) {
            if (target.Rejected || target.Project.MaximumAltitude == 0) { return true; }

            double altitude = targetVisibility.GetAltitude(atTime);
            if (altitude > target.Project.MaximumAltitude) {
                SetRejected(target, Reasons.TargetMaxAltitude);
                return false;
            }

            return true;
        }

        /// <summary>
        /// Reject target exposures that are not suitable for the current level of twilight.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="currentTwilightLevel"></param>
        public void TwilightFilter(ITarget target, DateTime atTime, TwilightCircumstances twilightCircumstances, TwilightLevel? currentTwilightLevel) {
            if (target.Rejected) { return; }

            foreach (IExposure exposure in target.ExposurePlans) {
                if (!exposure.Rejected && exposure.IsIncomplete()) {
                    if (currentTwilightLevel.HasValue) {
                        ExposureTwilightFilter(exposure, atTime, twilightCircumstances, (TwilightLevel)currentTwilightLevel);
                    } else {
                        SetRejected(exposure, Reasons.FilterTwilight);
                    }
                }
            }
        }

        public void ExposureTwilightFilter(IExposure exposure, DateTime atTime, TwilightCircumstances twilightCircumstances, TwilightLevel currentTwilightLevel) {
            int offset = exposure.MinutesOffset;

            if (offset == 0 && currentTwilightLevel > exposure.TwilightLevel) {
                SetRejected(exposure, Reasons.FilterTwilight);
            }

            if (offset != 0 && !twilightCircumstances.CheckTwilightWithOffset(atTime, exposure.TwilightLevel, offset)) {
                SetRejected(exposure, Reasons.FilterTwilight);
            }
        }

        /// <summary>
        /// Reject target exposures that are not suitable for the current level of humidity.
        /// </summary>
        /// <param name="target"></param>
        /// <param name="weatherDataMediator"></param>
        public void HumidityFilter(ITarget target, IWeatherDataMediator weatherDataMediator) {
            if (target.Rejected) { return; }

            double currentHumidity = double.MinValue;
            foreach (IExposure exposure in target.ExposurePlans) {
                if (!exposure.Rejected && exposure.IsIncomplete() && exposure.MaximumHumidity > 0) {
                    currentHumidity = GetHumidity(currentHumidity, weatherDataMediator);
                    if (currentHumidity != double.MinValue && currentHumidity > 0 && currentHumidity > exposure.MaximumHumidity) {
                        SetRejected(exposure, Reasons.FilterHumidity);
                    }
                }
            }
        }

        private double GetHumidity(double currentHumidity, IWeatherDataMediator weatherDataMediator) {
            if (currentHumidity != double.MinValue) { return currentHumidity; }

            if (!weatherDataMediator.GetInfo().Connected) {
                TSLogger.Warning("exposure specifies a maximum humidity but no weather device is connected");
                return double.MinValue;
            }

            double humidity = weatherDataMediator.GetInfo().Humidity;
            TSLogger.Debug($"current humidity for exposure check: {Utils.FormatDbl(humidity, "{0:0.##}")}");
            return humidity;
        }

        /// <summary>
        /// Reject target exposures that are not suitable based on moon avoidance settings.
        /// </summary>
        /// <param name="atTime"></param>
        /// <param name="target"></param>
        /// <param name="moonExpert"></param>
        public void MoonAvoidanceFilter(DateTime atTime, ITarget target, IMoonAvoidanceExpert moonExpert) {
            if (target.Rejected && target.RejectedReason != Reasons.TargetNotYetVisible) { return; }

            foreach (IExposure exposure in target.ExposurePlans) {
                if (exposure.IsIncomplete()) {
                    if (moonExpert.IsRejected(atTime, target, exposure)) {
                        SetRejected(exposure, Reasons.FilterMoonAvoidance);
                    }
                }
            }
        }

        /// <summary>
        /// Determine if the target is ready to image at the provided time, taking the visibility sampling
        /// interval into account.
        /// </summary>
        /// <param name="atTime"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        public bool ReadyNow(DateTime atTime, ITarget target, bool rejectRejected = true) {
            if (rejectRejected && target.Rejected) { return false; }

            TimeSpan diff = atTime - target.StartTime;
            return Math.Abs(diff.TotalSeconds) <= targetVisibilitySampleInterval * 2;
        }

        /// <summary>
        /// Find the next possible time that a target could be imaged, taking into account target
        /// visibility and the suitability of exposures given the state of circumstances (like
        /// moon avoidance) at that time.
        ///
        /// If future imaging is possible, the target's start time will be advanced to that time.
        /// Otherwise, the target will be marked rejected.
        /// </summary>
        /// <param name="target"></param>
        public void CheckFuture(ITarget target, IMoonAvoidanceExpert moonExpert) {
            DateTime atTime = target.StartTime;
            TSLogger.Debug($"VIS-FUTURE begin {target.Project.Name}/{target.Name} start={Utils.FormatDateTimeFull(atTime)} rejected={target.Rejected}/{target.RejectedReason}");
            TwilightCircumstances twilightCircumstances = TwilightCircumstances.AdjustTwilightCircumstances(observerInfo, atTime);
            TargetVisibility targetVisibility = new(target, observerInfo,
                twilightCircumstances.OnDate, twilightCircumstances.Sunset, twilightCircumstances.Sunrise, targetVisibilitySampleInterval);

            // Advance time if currently exceeding a max altitude constraint
            MaximumAltitudeClipper maxAltClipper = targetVisibility.MaxAltitudeClipper;
            if (maxAltClipper.IsClipped) {
                if (maxAltClipper.ClipInterval.Contains(atTime)) {
                    atTime = maxAltClipper.ClipInterval.EndTime;
                }
            }

            while (true) {
                // Check target for moon avoidance, and twilight at this time

                if (!target.Rejected) {
                    MoonAvoidanceFilter(atTime, target, moonExpert);
                    if (AllExposurePlansRejected(target)) {
                        SetRejected(target, Reasons.TargetMoonAvoidance);
                        TSLogger.Debug($"VIS-FUTURE moon-reject {target.Name} at={Utils.FormatDateTimeFull(atTime)} {FormatFilterRejections(target)}");
                    }
                }

                if (!target.Rejected) {
                    TwilightFilter(target, atTime, twilightCircumstances, twilightCircumstances.GetCurrentTwilightLevel(atTime));
                    if (AllExposurePlansRejected(target)) {
                        SetRejected(target, Reasons.FilterTwilight);
                        TSLogger.Debug($"VIS-FUTURE twilight-reject {target.Name} at={Utils.FormatDateTimeFull(atTime)} level={twilightCircumstances.GetCurrentTwilightLevel(atTime)} {FormatFilterRejections(target)}");
                    }
                }

                // If not rejected, we've found a future time at which the target could be imaged
                if (!target.Rejected) {
                    target.StartTime = atTime;
                    TSLogger.Debug($"VIS-FUTURE found {target.Name} at={Utils.FormatDateTimeFull(atTime)} end={Utils.FormatDateTimeFull(target.EndTime)}");
                    return;
                }

                // Otherwise, advance time and check target visibility at the new time (which also rechecks max altitude)
                DateTime before = atTime;
                atTime = atTime.AddSeconds(targetFutureTestSampleInterval);
                ClearRejections(target);
                if (Visibility(atTime, target, twilightCircumstances, targetVisibility)) {
                    atTime = target.StartTime;
                    TSLogger.Debug($"VIS-FUTURE step {target.Name} {Utils.FormatDateTimeFull(before)} -> viz-ok {Utils.FormatDateTimeFull(atTime)}");
                } else if (VisibleLater(target)) {
                    atTime = target.StartTime;
                    TSLogger.Debug($"VIS-FUTURE step {target.Name} {Utils.FormatDateTimeFull(before)} -> later {Utils.FormatDateTimeFull(atTime)} reason={target.RejectedReason}");
                } else {
                    TSLogger.Debug($"VIS-FUTURE exhausted {target.Name} after {Utils.FormatDateTimeFull(before)} reason={target.RejectedReason}");
                    return; // no more visibility this night
                }
            }
        }

        public bool VisibleLater(ITarget target) {
            return target.Rejected &&
                  (target.RejectedReason == Reasons.TargetNotYetVisible
                || target.RejectedReason == Reasons.TargetBeforeMeridianWindow
                || target.RejectedReason == Reasons.TargetMeridianFlipClipped
                || target.RejectedReason == Reasons.TargetMaxAltitude);
        }

        public bool AllExposurePlansRejected(ITarget target) {
            foreach (IExposure exposure in target.ExposurePlans) {
                if (!exposure.Rejected) {
                    return false;
                }
            }

            return true;
        }

        public void ClearRejections(ITarget target) {
            target.Rejected = false;
            target.RejectedReason = null;
            target.ExposurePlans.ForEach(e => { e.Rejected = false; e.RejectedReason = null; });
        }

        private void SetRejected(ITarget target, string reason) {
            target.Rejected = true;
            target.RejectedReason = reason;
        }

        private void SetRejected(IExposure exposure, string reason) {
            exposure.Rejected = true;
            exposure.RejectedReason = reason;
        }
    }
}