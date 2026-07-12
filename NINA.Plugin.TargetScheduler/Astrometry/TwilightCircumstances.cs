using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using NINA.Astrometry;
using NINA.Plugin.TargetScheduler.Planning;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.Caching;
using System.Text;

namespace NINA.Plugin.TargetScheduler.Astrometry {

    [JsonConverter(typeof(StringEnumConverter))]
    public enum TwilightLevel {
        Nighttime, Astronomical, Nautical, Civil
    };

    [JsonConverter(typeof(StringEnumConverter))]
    public enum TwilightStage {
        Dusk, Dawn
    };

    /// <summary>
    /// Determine the nightly twilight circumstances for the provided date.  The dusk (start) times will be on the provided date
    /// while the dawn (end) times will be on the following day (in general) - determining the potential imaging time span for
    /// a single 'night'.
    ///
    /// This code leverages the NINA Astrometry sun rise/set/altitude time determination code in NINA.Astrometry.RiseAndSet.  Note
    /// that this code works up to latitude 70° - above that, it doesn't return reliable twilight times.
    /// </summary>
    public class TwilightCircumstances {
        public const double DayEndAltitude = 0; // refraction adjustment not needed here
        public const double CivilEndSunAltitude = -6;
        public const double NauticalEndSunAltitude = -12;
        public const double AstronomicalEndSunAltitude = -18;

        public DateTime? CivilTwilightStart { get; protected set; }
        public DateTime? CivilTwilightEnd { get; protected set; }
        public DateTime? NauticalTwilightStart { get; protected set; }
        public DateTime? NauticalTwilightEnd { get; protected set; }
        public DateTime? AstronomicalTwilightStart { get; protected set; }
        public DateTime? AstronomicalTwilightEnd { get; protected set; }
        public DateTime? NighttimeStart { get; protected set; }
        public DateTime? NighttimeEnd { get; protected set; }
        public DateTime? Sunset { get => CivilTwilightStart; }
        public DateTime? Sunrise { get => CivilTwilightEnd; }

        public DateTime OnDate { get; protected set; }
        private ObserverInfo observerInfo;

        public TwilightCircumstances(ObserverInfo observerInfo, DateTime atTime) {
            this.observerInfo = observerInfo;
            OnDate = atTime.Date.AddHours(12); // fix to noon on date

            string cacheKey = GetCacheKey();
            //TSLogger.Trace($"TwilightCircumstances cache key: {cacheKey}");

            TwilightCircumstances cached = TwilightCircumstancesCache.Get(cacheKey);
            if (cached == null) {
                Calculate();
                TwilightCircumstancesCache.Put(this, cacheKey);
            } else {
                CivilTwilightStart = cached.CivilTwilightStart;
                CivilTwilightEnd = cached.CivilTwilightEnd;
                NauticalTwilightStart = cached.NauticalTwilightStart;
                NauticalTwilightEnd = cached.NauticalTwilightEnd;
                AstronomicalTwilightStart = cached.AstronomicalTwilightStart;
                AstronomicalTwilightEnd = cached.AstronomicalTwilightEnd;
                NighttimeStart = cached.NighttimeStart;
                NighttimeEnd = cached.NighttimeEnd;
            }
        }

        public bool HasNighttime() {
            return NighttimeStart != null;
        }

        public bool HasAstronomicalTwilight() {
            return AstronomicalTwilightStart != null;
        }

        public bool HasNauticalTwilight() {
            return NauticalTwilightStart != null;
        }

        public bool HasCivilTwilight() {
            return CivilTwilightStart != null;
        }

        public TimeInterval GetTwilightSpan(TwilightLevel twilightLevel) {
            switch (twilightLevel) {
                case TwilightLevel.Nighttime: return SafeTwilightSpan(NighttimeStart, NighttimeEnd);
                case TwilightLevel.Astronomical: return SafeTwilightSpan(AstronomicalTwilightStart, AstronomicalTwilightEnd);
                case TwilightLevel.Nautical: return SafeTwilightSpan(NauticalTwilightStart, NauticalTwilightEnd);
                case TwilightLevel.Civil: return SafeTwilightSpan(CivilTwilightStart, CivilTwilightEnd);
                default:
                    throw new ArgumentException($"unknown twilight level: {twilightLevel}");
            }
        }

        public TwilightLevel? GetCurrentTwilightLevel(DateTime atTime) {
            if (HasNighttime() && NighttimeStart < atTime && atTime <= NighttimeEnd) return TwilightLevel.Nighttime;
            if (HasAstronomicalTwilight() && AstronomicalTwilightStart < atTime && atTime <= AstronomicalTwilightEnd) return TwilightLevel.Astronomical;
            if (HasNauticalTwilight() && NauticalTwilightStart < atTime && atTime <= NauticalTwilightEnd) return TwilightLevel.Nautical;
            if (HasCivilTwilight() && CivilTwilightStart < atTime && atTime <= CivilTwilightEnd) return TwilightLevel.Civil;
            return null;
        }

        public bool CheckTwilightWithOffset(DateTime atTime, TwilightLevel? acceptableTwilightLevel, int offset) {
            if (acceptableTwilightLevel.HasValue) {
                TimeInterval span = GetTwilightSpan((TwilightLevel)acceptableTwilightLevel);
                return atTime >= span.StartTime.AddMinutes(offset) && atTime <= span.EndTime.AddMinutes(-offset);
            }

            throw new ArgumentException("current twilight level cannot be null for offsetting");
        }

        public static TwilightCircumstances AdjustTwilightCircumstances(ObserverInfo observerInfo, DateTime atTime) {
            TwilightCircumstances twilightCircumstances = new TwilightCircumstances(observerInfo, atTime);
            DateTime? CivilTwilightStart = twilightCircumstances.CivilTwilightStart;
            DateTime? CivilTwilightEnd = twilightCircumstances.CivilTwilightEnd;
            DateTime noon = atTime.Date.AddHours(12);
            DateTime midnight = atTime.Date.AddHours(24);

            if (!CivilTwilightStart.HasValue || !CivilTwilightEnd.HasValue) {
                throw new ArgumentException("Oops!  Need to fix AdjustTwilightCircumstances!");
            }

            // If atTime is between noon and civil start, return next dusk/following dawn
            if (noon <= atTime && atTime < CivilTwilightStart) {
                return twilightCircumstances;
            }

            // If atTime is between civil start/end and atTime is before midnight, return next dusk/following dawn
            if (CivilTwilightStart <= atTime && atTime < CivilTwilightEnd && atTime < midnight) {
                return twilightCircumstances;
            }

            // If atTime is after the previous dawn and before noon, return next dusk/following dawn
            TwilightCircumstances previous = new TwilightCircumstances(observerInfo, atTime.AddDays(-1));
            CivilTwilightEnd = previous.CivilTwilightEnd;
            if (CivilTwilightEnd <= atTime && atTime < noon) {
                return twilightCircumstances;
            }

            // Otherwise, we want NighttimeCircumstances for the previous day
            return previous;
        }

        private void Calculate() {
            var sun = AstroUtil.GetSunRiseAndSet(OnDate, observerInfo.Latitude, observerInfo.Longitude, observerInfo.Elevation);
            var civil = AstroUtil.GetCivilNightTimes(OnDate, observerInfo.Latitude, observerInfo.Longitude, observerInfo.Elevation);
            var nautical = AstroUtil.GetNauticalNightTimes(OnDate, observerInfo.Latitude, observerInfo.Longitude, observerInfo.Elevation);
            var astro = AstroUtil.GetNightTimes(OnDate, observerInfo.Latitude, observerInfo.Longitude, observerInfo.Elevation);

            CivilTwilightStart = sun.Set;
            CivilTwilightEnd = sun.Rise;
            NauticalTwilightStart = civil.Set;
            NauticalTwilightEnd = civil.Rise;
            AstronomicalTwilightStart = nautical.Set;
            AstronomicalTwilightEnd = nautical.Rise;
            NighttimeStart = astro.Set;
            NighttimeEnd = astro.Rise;

            // TWILIGHT-DIAG: log every fresh (non-cached) calculation so a bad/stale
            // recompute (e.g. after the 12h cache entry expires mid-session) is visible
            // in the log instead of only showing up as an unexplained scheduling delay.
            TSLogger.Debug($"TWILIGHT-DIAG: calculated for OnDate={OnDate:yyyy-MM-dd} preview={PreviewContext.IsActive} " +
                $"lat={observerInfo.Latitude.ToString("0.000000", CultureInfo.InvariantCulture)} " +
                $"lon={observerInfo.Longitude.ToString("0.000000", CultureInfo.InvariantCulture)} " +
                $"nighttimeStart={NighttimeStart:yyyy-MM-dd HH:mm:ss} nighttimeEnd={NighttimeEnd:yyyy-MM-dd HH:mm:ss}");
        }

        public override string ToString() {
            StringBuilder sb = new StringBuilder();
            sb.Append($"Civil start:        {CivilTwilightStart}\n");
            sb.Append($"Nautical start:     {NauticalTwilightStart}\n");
            sb.Append($"Astronomical start: {AstronomicalTwilightStart}\n");
            sb.Append($"Night start:        {NighttimeStart}\n");
            sb.Append($"Night end:          {NighttimeEnd}\n");
            sb.Append($"Astronomical end:   {AstronomicalTwilightEnd}\n");
            sb.Append($"Nautical end:       {NauticalTwilightEnd}\n");
            sb.Append($"Civil end:          {CivilTwilightEnd}\n");
            return sb.ToString();
        }

        private string GetCacheKey() {
            StringBuilder sb = new StringBuilder();
            sb.Append($"{OnDate:yyyy-MM-dd-HH-mm-ss}_");
            sb.Append($"{observerInfo.Latitude.ToString("0.000000", CultureInfo.InvariantCulture)}_");
            sb.Append($"{observerInfo.Longitude.ToString("0.000000", CultureInfo.InvariantCulture)}");
            return sb.ToString();
        }

        private TimeInterval SafeTwilightSpan(DateTime? t1, DateTime? t2) {
            if (t1 == null || t2 == null) {
                return null;
            }

            return new TimeInterval((DateTime)t1, (DateTime)t2);
        }
    }

    internal class TwilightCircumstancesCache {
        private static readonly TimeSpan ITEM_TIMEOUT = TimeSpan.FromHours(12);
        private static readonly MemoryCache _cache = new MemoryCache("Scheduler TwilightCircumstances");
        private static readonly object lockObj = new object();

        // Preview isolation: like DitherManagerCache/SmartExposureRotateCache, this is a
        // static cache shared by every caller on the process - including plan previews
        // (TS API /preview endpoint polled by other plugins, Plan Preview UI, Plan
        // Explainer). A preview walks through many synthetic 'atTime' values spanning
        // hours/days ahead on a background/API thread, concurrently with the live
        // sequencer thread computing the SAME night's circumstances. MemoryCache.Add()
        // only writes when the key is absent, so this isn't a last-write-wins overwrite
        // race - but a preview run computing the live night's entry FIRST (or racing the
        // live thread's first calculation right as the previous 12h entry expires) can
        // still be the one that wins the Add() and permanently seeds the live cache for
        // the next 12 hours. Route preview-thread reads/writes to thread-local scratch
        // storage instead, exactly like the other two caches, so a preview can never
        // seed or clobber the entry the live sequencer depends on.
        [ThreadStatic] private static Dictionary<string, TwilightCircumstances> previewCache;

        public static bool IsPreviewContext => previewCache != null;

        public static void EnterPreviewContext() {
            previewCache = new Dictionary<string, TwilightCircumstances>();
            TSLogger.Info("TWILIGHT-DIAG: entered PREVIEW twilight-cache context (live cache isolated)");
        }

        public static void ExitPreviewContext() {
            previewCache = null;
            TSLogger.Info("TWILIGHT-DIAG: exited PREVIEW twilight-cache context");
        }

        public static TwilightCircumstances Get(string cacheKey) {
            if (previewCache != null) {
                return previewCache.TryGetValue(cacheKey, out var tc) ? tc : null;
            }
            lock (lockObj) {
                return (TwilightCircumstances)_cache.Get(cacheKey);
            }
        }

        public static void Put(TwilightCircumstances nighttimeCircumstances, string cacheKey) {
            if (previewCache != null) {
                previewCache[cacheKey] = nighttimeCircumstances;
                return;
            }
            lock (lockObj) {
                _cache.Add(cacheKey, nighttimeCircumstances, DateTime.Now.Add(ITEM_TIMEOUT));
            }
        }

        private TwilightCircumstancesCache() {
        }
    }
}