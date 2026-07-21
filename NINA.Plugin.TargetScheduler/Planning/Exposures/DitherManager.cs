using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;

namespace NINA.Plugin.TargetScheduler.Planning.Exposures {

    /// <summary>
    /// Manage the dither state for a target.  We maintain a stack of exposures since the last
    /// dither.  When a new exposure is selected, we need to dither if the count of that
    /// exposure filter in the stack is equal to the 'dither every' value.  The stack should be
    /// reset (cleared) after a successful dither/exposure.
    ///
    /// Note that the comparison is on the filter name - not the exposure plan itself.  Since you
    /// could have different exposure plans using the same filter, we want to ensure that when that
    /// filter is selected again, we dither appropriately.
    ///
    /// Also, we don't reset the stack here if a dither is required since we don't know at that
    /// point whether the exposure will actually be taken and pre-dither performed.
    /// </summary>
    public class DitherManager {
        private Stack<IExposure> exposureStack;
        private int ditherEvery;

        // True if this manager was created while a plan preview was running. A LIVE manager
        // (false) must NEVER be mutated while a preview is active, and a PREVIEW manager (true)
        // must never be mutated during live execution — either means preview/live isolation has
        // broken. CheckIsolation turns that (historically silent) corruption into a loud log line.
        private readonly bool createdInPreview;

        public DitherManager(int ditherEvery) {
            this.ditherEvery = ditherEvery;
            exposureStack = new Stack<IExposure>();
            createdInPreview = PreviewContext.IsActive;
            TSLogger.Info($"DITHER-DIAG: new DitherManager created with ditherEvery={ditherEvery} preview={createdInPreview} (hash={GetHashCode()})");
        }

        public void AddExposure(IExposure exposure) {
            CheckIsolation("AddExposure");
            exposureStack.Push(exposure);
            TSLogger.Info($"DITHER-DIAG: AddExposure filter={exposure.FilterName}; stack now depth={exposureStack.Count} " +
                $"[{string.Join(",", exposureStack.Select(e => e.FilterName))}] (hash={GetHashCode()})");
        }

        public bool DitherRequired(IExposure nextExposure) {
            CheckIsolation("DitherRequired");
            int? ditherOverride = GetExposureDitherOverride(nextExposure);
            int dither = ditherOverride.HasValue ? ditherOverride.Value : ditherEvery;

            int count = exposureStack.Count(item => item.FilterName == nextExposure.FilterName);
            bool required = dither != 0 && count >= dither;
            TSLogger.Info($"DITHER-DIAG: DitherRequired? filter={nextExposure.FilterName} exposure.DitherEvery={nextExposure.DitherEvery} " +
                $"override={(ditherOverride.HasValue ? ditherOverride.Value.ToString() : "none")} effectiveDither={dither} " +
                $"sameFilterCount={count} stackDepth={exposureStack.Count} => {required} (hash={GetHashCode()})");
            if (dither == 0) { return false; }
            return count >= dither;
        }

        public void Reset() {
            CheckIsolation("Reset");
            TSLogger.Info($"DITHER-DIAG: Reset() clearing stack (was depth={exposureStack.Count}) (hash={GetHashCode()})");
            exposureStack.Clear();
        }

        /// <summary>
        /// True if this manager was created during a plan preview (i.e. it lives in the thread-local
        /// scratch cache).  Exposed so isolation regression tests can assert live-vs-preview routing.
        /// </summary>
        public bool CreatedInPreview => createdInPreview;

        /// <summary>
        /// Loud tripwire for the "preview mutates live dither state" bug class (this is its 3rd
        /// occurrence). If a manager is mutated while the preview context does not match the context
        /// it was created in, isolation has broken — log it at Warning so it surfaces on the first
        /// offending sub instead of being discovered as a whole night of missing dithers.
        /// </summary>
        private void CheckIsolation(string op) {
            if (createdInPreview != PreviewContext.IsActive) {
                TSLogger.Warning($"DITHER-LEAK: {op} on a {(createdInPreview ? "PREVIEW" : "LIVE")} DitherManager " +
                    $"while PreviewContext.IsActive={PreviewContext.IsActive} (hash={GetHashCode()}) — " +
                    "live/preview dither isolation has broken; a preview is corrupting live dither state (or vice versa)");
            }
        }

        private int? GetExposureDitherOverride(IExposure exposure) {
            return exposure.DitherEvery >= 0 ? exposure.DitherEvery : null;
        }
    }

    /// <summary>
    /// Support an in-memory cache of DitherManagers.  This is needed so that some exposure
    /// selectors can maintain dither state over the course of an imaging session.
    ///
    /// Note that when the planner determines that it is switching from a previous target to
    /// a new one, it will remove the cache entry for the previous so it starts fresh if
    /// selected again (in which case it will necessarily have a slew/center to begin).
    /// </summary>
    public class DitherManagerCache {
        private static readonly TimeSpan ITEM_TIMEOUT = TimeSpan.FromHours(18);
        private static MemoryCache _cache = Create();
        private static object lockObj = new object();

        // Preview isolation: plan previews (TS API /preview endpoint, Plan Preview UI,
        // Plan Explainer) simulate the night by calling ExposureTaken/TargetReset on the
        // exposure selectors — which read DitherManagers from THIS shared cache. Without
        // isolation a background preview (e.g. another plugin polling the TS API) mutates
        // the LIVE dither stacks mid-session, corrupting dither cadence (observed as a
        // full night of same-filter subs with zero dithers). While a thread is inside a
        // preview context, all cache operations are redirected to a thread-local scratch
        // cache; the live cache is untouched. Preview runs are synchronous on one thread,
        // so [ThreadStatic] is sufficient.
        [ThreadStatic] private static Dictionary<string, DitherManager> previewCache;

        public static bool IsPreviewContext => previewCache != null;

        public static void EnterPreviewContext() {
            previewCache = new Dictionary<string, DitherManager>();
            TSLogger.Info("DITHER-DIAG: entered PREVIEW dither-cache context (live cache isolated)");
        }

        public static void ExitPreviewContext() {
            previewCache = null;
            TSLogger.Info("DITHER-DIAG: exited PREVIEW dither-cache context");
        }

        public static string GetCacheKey(Target target) {
            return $"{target.Id}";
        }

        public static string GetCacheKey(ITarget target) {
            return $"{target.DatabaseId}";
        }

        public static DitherManager Get(string cacheKey) {
            if (previewCache != null) {
                return previewCache.TryGetValue(cacheKey, out var pm) ? pm : null;
            }
            lock (lockObj) {
                return (DitherManager)_cache.Get(cacheKey);
            }
        }

        public static void Put(DitherManager ditherManager, string cacheKey) {
            if (previewCache != null) {
                previewCache[cacheKey] = ditherManager;
                return;
            }
            lock (lockObj) {
                _cache.Add(cacheKey, ditherManager, DateTime.Now.Add(ITEM_TIMEOUT));
            }
        }

        public static void Remove(ITarget target) {
            Remove(GetCacheKey(target));
        }

        public static void Remove(Target target) {
            Remove(GetCacheKey(target));
        }

        public static void Remove(List<Target> targets) {
            if (Common.IsEmpty(targets)) return;
            foreach (Target target in targets) {
                Remove(target);
            }
        }

        public static void Remove(string cacheKey) {
            if (previewCache != null) {
                previewCache.Remove(cacheKey);
                return;
            }
            TSLogger.Info($"DITHER-DIAG: LIVE dither cache Remove key={cacheKey}");
            lock (lockObj) {
                _cache.Remove(cacheKey);
            }
        }

        public static void Clear() {
            if (previewCache != null) {
                previewCache.Clear();
                return;
            }
            TSLogger.Info("DITHER-DIAG: LIVE dither cache Clear (all dither state wiped)");
            lock (lockObj) {
                _cache.Dispose();
                _cache = Create();
            }
        }

        private static MemoryCache Create() {
            return new MemoryCache("Scheduler DitherManager");
        }

        private DitherManagerCache() {
        }
    }
}