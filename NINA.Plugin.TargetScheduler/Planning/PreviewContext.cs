using NINA.Plugin.TargetScheduler.Astrometry;
using NINA.Plugin.TargetScheduler.Planning.Exposures;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;

namespace NINA.Plugin.TargetScheduler.Planning {

    /// <summary>
    /// Marks the current thread as running a plan PREVIEW (Plan Preview UI, TS API
    /// /preview endpoint) so that simulation NEVER mutates live sequencing state.
    ///
    /// A preview simulates the night by driving the same exposure selectors as live
    /// planning, including ExposureTaken/TargetReset — which mutate shared
    /// state used by the live engine:
    ///   1. DitherManagerCache (static)          → dither cadence corrupted
    ///   2. SmartExposureRotateCache (static)    → filter rotation corrupted
    ///   3. FilterCadence rows via UpdateFilterCadences → DATABASE corrupted (durable)
    ///   4. TwilightCircumstancesCache (static)  → twilight boundary corrupted for 12h
    ///   5. TargetVisibilityCache (static)       → altitude samples / StopTime corrupted for 12h
    ///
    /// Observed in the field: a plugin polling the TS API /preview endpoint every two
    /// minutes suppressed dithering for an entire session and previously caused
    /// single-filter repetition. The same class of bug hit TwilightCircumstancesCache:
    /// a preview walking synthetic future timestamps can win the race to seed (or
    /// re-seed after the 12h TTL expires) the live night's twilight-boundary cache
    /// entry, pushing the actual imaging start later than the true twilight boundary
    /// for the rest of the session. TargetVisibilityCache has the same first-fill-wins
    /// shape: a preview can seed a short sample span and later Visibility calls invent
    /// mid-night "not yet visible" holes. While the context is active, all five caches
    /// redirect to thread-local scratch storage and filter-cadence DB writes are
    /// skipped. The live key also includes sunset/sunrise so a wrong span cannot be
    /// reused even if isolation is missed.
    ///
    /// Previews run synchronously on one thread, so [ThreadStatic] is sufficient.
    /// Always call in try/finally: Enter() ... finally Exit().
    /// </summary>
    public static class PreviewContext {

        [ThreadStatic] private static bool active;

        /// <summary>True when the current thread is running a plan preview.</summary>
        public static bool IsActive => active;

        public static void Enter() {
            active = true;
            DitherManagerCache.EnterPreviewContext();
            SmartExposureRotateCache.EnterPreviewContext();
            TwilightCircumstancesCache.EnterPreviewContext();
            TargetVisibilityCache.EnterPreviewContext();
            TSLogger.Info("PREVIEW-ISOLATION: entered preview context — live dither/rotation/cadence/twilight/visibility state protected");
        }

        public static void Exit() {
            DitherManagerCache.ExitPreviewContext();
            SmartExposureRotateCache.ExitPreviewContext();
            TwilightCircumstancesCache.ExitPreviewContext();
            TargetVisibilityCache.ExitPreviewContext();
            active = false;
            TSLogger.Info("PREVIEW-ISOLATION: exited preview context");
        }
    }
}
