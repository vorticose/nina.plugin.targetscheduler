using NINA.Astrometry;
using NINA.Plugin.TargetScheduler.Astrometry;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NINA.Plugin.TargetScheduler.Planning {

    /// <summary>
    /// Sweep a whole night and report, for every active target and exposure plan, how moon avoidance is actually
    /// going to behave: the target-moon separation, the separation the Lorentzian will demand, when (if ever) the
    /// exposure is clear to run, and what the avoidance separation setting would have to be relaxed to in order
    /// for a blocked exposure to image at all.
    ///
    /// This deliberately covers every active target rather than just the ones the planner selects.  A target that
    /// moon avoidance rejects outright never appears in a scheduler preview, which is precisely the target you
    /// need to see when you're trying to work out what the moon is costing you.
    /// </summary>
    public class MoonAvoidanceAnalyzer {

        /// Sampling resolution for the night sweep.  Clear window times are only accurate to this interval.
        public const int SampleIntervalMinutes = 10;

        private readonly ObserverInfo observerInfo;

        public MoonAvoidanceAnalyzer(ObserverInfo observerInfo) {
            this.observerInfo = observerInfo;
        }

        /// <summary>
        /// Analyze moon avoidance for the night containing atTime.
        /// </summary>
        /// <param name="atTime"></param>
        /// <param name="projects"></param>
        /// <returns>the analysis, or null if there's no night at this date/location</returns>
        public MoonAvoidanceAnalysis Analyze(DateTime atTime, List<IProject> projects) {
            TwilightCircumstances twilight = TwilightCircumstances.AdjustTwilightCircumstances(observerInfo, atTime);
            TimeInterval night = twilight.GetTwilightSpan(TwilightLevel.Civil);

            if (night == null) {
                TSLogger.Warning($"moon avoidance analysis: no civil twilight span for {atTime} at this location");
                return null;
            }

            List<MoonSample> samples = GetMoonSamples(night);
            MoonAvoidanceAnalysis analysis = new MoonAvoidanceAnalysis(night, samples);

            if (Common.IsEmpty(projects)) { return analysis; }

            foreach (IProject project in projects) {
                foreach (ITarget target in project.Targets) {
                    List<double> separations = GetTargetSeparations(target, samples);
                    List<bool> visible = GetTargetVisibility(target, samples);

                    foreach (IExposure exposure in target.ExposurePlans) {
                        TimeInterval twilightSpan = GetExposureTwilightSpan(twilight, exposure);
                        analysis.Rows.Add(AnalyzeExposure(target, exposure, samples, separations, visible, twilightSpan));
                    }
                }
            }

            return analysis;
        }

        private MoonAvoidanceAnalysisRow AnalyzeExposure(ITarget target, IExposure exposure, List<MoonSample> samples,
            List<double> separations, List<bool> visible, TimeInterval twilightSpan) {
            MoonAvoidanceAnalysisRow row = new MoonAvoidanceAnalysisRow(target, exposure);

            MoonAvoidanceDetail bestClear = null;
            MoonAvoidanceDetail bestBlocked = null;
            double bestClearScore = double.MinValue;
            double bestBlockedScore = double.MinValue;
            double bestClearSeparation = 0;
            double bestBlockedSeparation = 0;
            double? bestSeparationSetting = null;
            int runStart = -1;

            for (int i = 0; i < samples.Count; i++) {
                MoonSample sample = samples[i];
                bool imagable = visible[i] && twilightSpan != null && twilightSpan.Contains(sample.AtTime);

                if (!imagable) {
                    CloseRun(row, samples, ref runStart, i);
                    continue;
                }

                row.ImagableSampleCount++;

                MoonAvoidanceDetail detail = MoonAvoidanceExpert.Evaluate(sample.AtTime, exposure,
                    sample.MoonAltitude, sample.MoonAge, separations[i]);

                // With avoidance enabled the most favorable moment is the one with the most margin.  With it
                // disabled every moment is equally acceptable, so fall back to the greatest moon distance -
                // which is still worth reporting even when nothing acts on it.
                double score = detail.Evaluated ? detail.Margin : separations[i];

                if (detail.Rejected) {
                    CloseRun(row, samples, ref runStart, i);

                    if (score > bestBlockedScore) {
                        bestBlockedScore = score;
                        bestBlocked = detail;
                        bestBlockedSeparation = separations[i];
                    }

                    double? setting = detail.MaximumPassingSeparationSetting;
                    if (setting != null && (bestSeparationSetting == null || setting > bestSeparationSetting)) {
                        bestSeparationSetting = setting;
                    }
                } else {
                    if (runStart == -1) { runStart = i; }

                    if (score > bestClearScore) {
                        bestClearScore = score;
                        bestClear = detail;
                        bestClearSeparation = separations[i];
                    }
                }
            }

            CloseRun(row, samples, ref runStart, samples.Count);

            row.BestDetail = bestClear ?? bestBlocked;
            row.BestMoonSeparation = row.BestDetail == null
                ? (double?)null
                : (bestClear != null ? bestClearSeparation : bestBlockedSeparation);
            row.SuggestedSeparationSetting = row.ClearWindows.Count == 0 ? bestSeparationSetting : null;
            return row;
        }

        /// <summary>
        /// Close an open run of clear samples, ending at (but not including) endIndex.
        /// </summary>
        private void CloseRun(MoonAvoidanceAnalysisRow row, List<MoonSample> samples, ref int runStart, int endIndex) {
            if (runStart == -1) { return; }

            DateTime start = samples[runStart].AtTime;
            DateTime end = endIndex < samples.Count
                ? samples[endIndex].AtTime
                : samples[samples.Count - 1].AtTime.AddMinutes(SampleIntervalMinutes);

            row.ClearWindows.Add(new TimeInterval(start, end));
            runStart = -1;
        }

        /// <summary>
        /// The span the exposure is allowed to image in, per its twilight level and minutes offset.  This mirrors
        /// TwilightCircumstances.CheckTwilightWithOffset().
        /// </summary>
        private TimeInterval GetExposureTwilightSpan(TwilightCircumstances twilight, IExposure exposure) {
            TimeInterval span = twilight.GetTwilightSpan(exposure.TwilightLevel);
            if (span == null) { return null; }
            if (exposure.MinutesOffset == 0) { return span; }

            DateTime start = span.StartTime.AddMinutes(exposure.MinutesOffset);
            DateTime end = span.EndTime.AddMinutes(-exposure.MinutesOffset);
            return start < end ? new TimeInterval(start, end) : null;
        }

        private List<MoonSample> GetMoonSamples(TimeInterval night) {
            List<MoonSample> samples = new List<MoonSample>();
            DateTime sampleTime = night.StartTime;

            while (sampleTime <= night.EndTime) {
                samples.Add(new MoonSample(
                    sampleTime,
                    AstroUtil.GetMoonAltitude(sampleTime, observerInfo),
                    AstrometryUtils.GetMoonAge(sampleTime),
                    AstrometryUtils.GetMoonPosition(observerInfo, sampleTime)));
                sampleTime = sampleTime.AddMinutes(SampleIntervalMinutes);
            }

            return samples;
        }

        private List<double> GetTargetSeparations(ITarget target, List<MoonSample> samples) {
            return samples
                .Select(s => AstrometryUtils.GetMoonSeparationAngle(s.MoonPosition, target.Coordinates))
                .ToList();
        }

        /// <summary>
        /// Whether the target is above the project horizon (and below the project maximum altitude, if set) at
        /// each sample time.
        /// </summary>
        private List<bool> GetTargetVisibility(ITarget target, List<MoonSample> samples) {
            IProject project = target.Project;
            HorizonDefinition horizon = project.HorizonDefinition;
            double maximumAltitude = project.MaximumAltitude;

            List<bool> visible = new List<bool>(samples.Count);
            foreach (MoonSample sample in samples) {
                HorizontalCoordinate hc = AstrometryUtils.GetHorizontalCoordinates(observerInfo, target.Coordinates, sample.AtTime);
                bool aboveHorizon = hc.Altitude > horizon.GetTargetAltitude(hc.Azimuth);
                bool belowMaximum = maximumAltitude <= 0 || hc.Altitude <= maximumAltitude;
                visible.Add(aboveHorizon && belowMaximum);
            }

            return visible;
        }
    }

    /// <summary>
    /// The moon's circumstances at one instant, independent of any target.
    /// </summary>
    public class MoonSample {
        public DateTime AtTime { get; private set; }
        public double MoonAltitude { get; private set; }
        public double MoonAge { get; private set; }
        public NOVAS.SkyPosition MoonPosition { get; private set; }

        public MoonSample(DateTime atTime, double moonAltitude, double moonAge, NOVAS.SkyPosition moonPosition) {
            AtTime = atTime;
            MoonAltitude = moonAltitude;
            MoonAge = moonAge;
            MoonPosition = moonPosition;
        }
    }

    public class MoonAvoidanceAnalysis {
        public TimeInterval Night { get; private set; }
        public List<MoonSample> Samples { get; private set; }
        public List<MoonAvoidanceAnalysisRow> Rows { get; private set; }

        public double MoonAge { get; private set; }
        public double MoonIllumination { get; private set; }
        public string MoonPhaseName { get; private set; }
        public double MoonMaximumAltitude { get; private set; }

        /// Times the moon crosses the horizon during the night, if it does.  Accurate to the sample interval.
        public DateTime? MoonRise { get; private set; }

        public DateTime? MoonSet { get; private set; }

        /// True if the moon is above the horizon for the entire night.
        public bool MoonUpAllNight { get; private set; }

        /// True if the moon never rises during the night.
        public bool MoonDownAllNight { get; private set; }

        public MoonAvoidanceAnalysis(TimeInterval night, List<MoonSample> samples) {
            Night = night;
            Samples = samples;
            Rows = new List<MoonAvoidanceAnalysisRow>();

            DateTime midpoint = night.StartTime.AddSeconds((night.EndTime - night.StartTime).TotalSeconds / 2);
            MoonAge = AstrometryUtils.GetMoonAge(midpoint);
            MoonIllumination = AstrometryUtils.GetMoonIllumination(midpoint);
            MoonPhaseName = AstrometryUtils.GetMoonPhaseName(MoonAge);

            DetermineMoonRiseSet();
        }

        private void DetermineMoonRiseSet() {
            MoonMaximumAltitude = double.MinValue;
            bool everUp = false;
            bool everDown = false;

            for (int i = 0; i < Samples.Count; i++) {
                double altitude = Samples[i].MoonAltitude;
                if (altitude > MoonMaximumAltitude) { MoonMaximumAltitude = altitude; }

                bool up = altitude > 0;
                everUp |= up;
                everDown |= !up;

                if (i == 0) { continue; }

                bool wasUp = Samples[i - 1].MoonAltitude > 0;
                if (!wasUp && up && MoonRise == null) { MoonRise = Samples[i].AtTime; }
                if (wasUp && !up && MoonSet == null) { MoonSet = Samples[i].AtTime; }
            }

            MoonUpAllNight = everUp && !everDown;
            MoonDownAllNight = everDown && !everUp;
        }

        public int BlockedRowCount => Rows.Count(r => r.IsBlockedAllNight);

        public int EvaluatedRowCount => Rows.Count(r => r.AvoidanceEnabled);

        /// <summary>
        /// A short human-readable description of the night's moon circumstances.
        /// </summary>
        public string MoonSummary {
            get {
                StringBuilder sb = new StringBuilder();
                sb.Append($"{MoonPhaseName} moon, {MoonIllumination * 100:F0}% illuminated, age {MoonAge:F1} days.");

                if (MoonUpAllNight) {
                    sb.Append($"  Up all night, peaking at {MoonMaximumAltitude:F0}°.");
                } else if (MoonDownAllNight) {
                    sb.Append("  Below the horizon all night.");
                } else {
                    if (MoonSet != null) { sb.Append($"  Sets {((DateTime)MoonSet):HH:mm}."); }
                    if (MoonRise != null) { sb.Append($"  Rises {((DateTime)MoonRise):HH:mm}."); }
                    sb.Append($"  Peaks at {MoonMaximumAltitude:F0}°.");
                }

                return sb.ToString();
            }
        }

        public string NightSummary =>
            $"Night of {Night.StartTime:yyyy-MM-dd}: {Night.StartTime:HH:mm} to {Night.EndTime:HH:mm} (sunset to sunrise).";

        public string CoverageSummary {
            get {
                if (Rows.Count == 0) { return "No active exposure plans to evaluate."; }

                int blocked = BlockedRowCount;
                string plans = Rows.Count == 1 ? "exposure plan" : "exposure plans";
                return blocked == 0
                    ? $"All {Rows.Count} active {plans} have time available tonight."
                    : $"{blocked} of {Rows.Count} active {plans} are blocked by moon avoidance for the whole night.";
            }
        }
    }

    /// <summary>
    /// The result of sweeping one night for one exposure plan of one target.
    /// </summary>
    public class MoonAvoidanceAnalysisRow {
        public string ProjectName { get; private set; }
        public string TargetName { get; private set; }
        public string FilterName { get; private set; }
        public IExposure Exposure { get; private set; }

        /// The most favorable evaluation over the night: the clear sample with the most margin if there is one,
        /// otherwise the blocked sample that came closest.  Null if the target was never imagable.
        public MoonAvoidanceDetail BestDetail { get; set; }

        /// Target-moon angular separation at that same moment.  Reported even when avoidance is disabled.
        public double? BestMoonSeparation { get; set; }

        /// Spans where the target is imagable and moon avoidance permits it.
        public List<TimeInterval> ClearWindows { get; private set; }

        /// Number of samples where the target was above the horizon and within its twilight span, moon aside.
        public int ImagableSampleCount { get; set; }

        /// What the exposure template's Moon Avoidance Separation would have to be lowered to for this exposure
        /// to get any time tonight.  Null when it already has time, or when nothing but disabling Moon Must Be
        /// Down would help.
        public double? SuggestedSeparationSetting { get; set; }

        public MoonAvoidanceAnalysisRow(ITarget target, IExposure exposure) {
            ProjectName = target.Project.Name;
            TargetName = target.Name;
            FilterName = exposure.FilterName;
            Exposure = exposure;
            ClearWindows = new List<TimeInterval>();
        }

        public bool AvoidanceEnabled => Exposure.MoonAvoidanceEnabled;

        public bool NeverImagable => ImagableSampleCount == 0;

        public bool IsBlockedAllNight => !NeverImagable && ClearWindows.Count == 0;

        public TimeSpan ClearDuration =>
            TimeSpan.FromSeconds(ClearWindows.Sum(w => w.Duration));

        public string TargetLabel => $"{ProjectName} / {TargetName}";

        /// <summary>
        /// The avoidance configuration in force, as it reads in the exposure template.
        /// </summary>
        public string AvoidanceSettings {
            get {
                if (!Exposure.MoonAvoidanceEnabled) { return "off"; }

                StringBuilder sb = new StringBuilder();
                sb.Append($"{Exposure.MoonAvoidanceSeparation:F0}° / {Exposure.MoonAvoidanceWidth}d");
                if (Exposure.MoonRelaxScale > 0) { sb.Append($", relax {Exposure.MoonRelaxScale:F1}"); }
                if (Exposure.MoonDownEnabled) { sb.Append(", moon down"); }
                return sb.ToString();
            }
        }

        public string MoonSeparationText => BestMoonSeparation == null
            ? "-"
            : $"{BestMoonSeparation:F1}°";

        public string RequiredSeparationText => BestDetail == null || !BestDetail.Evaluated
            ? "-"
            : $"{BestDetail.RequiredSeparation:F1}°";

        public string MarginText {
            get {
                if (BestDetail == null || !BestDetail.Evaluated) { return "-"; }
                double margin = BestDetail.Margin;
                return margin >= 0 ? $"+{margin:F1}°" : $"{margin:F1}°";
            }
        }

        public string ClearWindowText {
            get {
                if (NeverImagable) { return "target not imagable tonight"; }
                if (ClearWindows.Count == 0) { return "blocked all night"; }

                string spans = string.Join(", ", ClearWindows.Select(w => $"{w.StartTime:HH:mm}-{w.EndTime:HH:mm}"));
                TimeSpan total = ClearDuration;
                return $"{spans}  ({(int)total.TotalHours}h{total.Minutes:00}m)";
            }
        }

        /// <summary>
        /// The concrete change that would unblock this exposure, when there is one.
        /// </summary>
        public string SuggestionText {
            get {
                if (NeverImagable) { return string.Empty; }
                if (ClearWindows.Count > 0) { return string.Empty; }

                if (SuggestedSeparationSetting != null) {
                    return $"separation ≤ {SuggestedSeparationSetting:F0}°";
                }

                if (BestDetail != null && BestDetail.Outcome == MoonAvoidanceOutcome.MoonDownBlocked) {
                    return "turn off Moon Must Be Down";
                }

                return string.Empty;
            }
        }

        /// <summary>
        /// Best-moment status, or the reason there is no best moment.
        /// </summary>
        public string StatusText {
            get {
                if (NeverImagable) { return "not imagable"; }
                if (!AvoidanceEnabled) { return "avoidance off"; }
                return BestDetail == null ? "-" : BestDetail.StatusText;
            }
        }

        public override string ToString() {
            return $"{TargetLabel} [{FilterName}] {StatusText}: {ClearWindowText}";
        }
    }
}
