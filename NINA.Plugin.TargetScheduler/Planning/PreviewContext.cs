using NINA.Plugin.TargetScheduler.Planning.Exposures;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;

namespace NINA.Plugin.TargetScheduler.Planning {

    /// <summary>
    /// Marks the current thread as running a plan PREVIEW (Plan Preview UI, TS API
    /// /preview endpoint) so that simulation NEVER mutates live sequencing state.
    ///
    /// A preview simulates the night by driving the same exposure selectors as live
    /// planning, including ExposureTaken/TargetReset — which mutate three pieces of
    /// state shared with the live engine:
    ///   1. DitherManagerCache (static)          → dither cadence corrupted
    ///   2. SmartExposureRotateCache (static)    → filter rotation corrupted
    ///   3. FilterCadence rows via UpdateFilterCadences → DATABASE corrupted (durable)
    ///
    /// Observed in the field: a plugin polling the TS API /preview endpoint every two
    /// minutes suppressed dithering for an entire session and previously caused
    /// single-filter repetition. While the context is active, both caches redirect to
    /// thread-local scratch storage and filter-cadence DB writes are skipped.
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
            TSLogger.Info("PREVIEW-ISOLATION: entered preview context — live dither/rotation/cadence state protected");
        }

        public static void Exit() {
            DitherManagerCache.ExitPreviewContext();
            SmartExposureRotateCache.ExitPreviewContext();
            active = false;
            TSLogger.Info("PREVIEW-ISOLATION: exited preview context");
        }
    }
}
