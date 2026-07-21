using Accord.Collections;
using LinqKit;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;

namespace NINA.Plugin.TargetScheduler.Planning.Exposures {

    /// <summary>
    /// With the smart exposure selector, it's not uncommon to have multiple exposure plans with the same (or very close)
    /// moon avoidance score - for example all broadband filters for a target.  In this case, a 'rotate count' can be
    /// provided which defines the number of exposures of each to take before moving to the next.
    /// </summary>
    public class SmartExposureRotateManager {
        private ITarget target;
        private int rotateCount;

        public SmartExposureRotateManager(ITarget target, int rotateCount) {
            this.target = target;
            this.rotateCount = rotateCount;
        }

        public IExposure Select(List<IExposure> candidates) {
            ExposureRotateStatus exposureRotateStatus = GetRotateStatus();
            return exposureRotateStatus.Select(candidates, rotateCount);
        }

        public void ExposureTaken(IExposure exposure) {
            ExposureRotateStatus exposureRotateStatus = GetRotateStatus();
            exposureRotateStatus.ExposureTaken(exposure, rotateCount);
        }

        public void Reset() {
            SmartExposureRotateCache.Remove(target);
        }

        private ExposureRotateStatus GetRotateStatus() {
            ExposureRotateStatus exposureRotateStatus = SmartExposureRotateCache.Get(target);
            if (exposureRotateStatus == null) {
                exposureRotateStatus = new ExposureRotateStatus(target);
                SmartExposureRotateCache.Put(target, exposureRotateStatus);
            }

            return exposureRotateStatus;
        }
    }

    public class ExposureRotateStatus {
        private OrderedDictionary<int, ExposureCountState> exposureCountState;

        // See DitherManager.createdInPreview — same live-vs-preview isolation tripwire, applied to
        // smart filter-rotation state (the sibling of the dither bug in this same class of defect).
        private readonly bool createdInPreview;

        public ExposureRotateStatus(ITarget target) {
            exposureCountState = new OrderedDictionary<int, ExposureCountState>(target.ExposurePlans.Count);
            target.ExposurePlans.ForEach(ep => {
                exposureCountState.Add(ep.DatabaseId, new ExposureCountState());
            });
            createdInPreview = PreviewContext.IsActive;
        }

        /// <summary>True if this rotation state was created during a plan preview (scratch cache).</summary>
        public bool CreatedInPreview => createdInPreview;

        private void CheckIsolation(string op) {
            if (createdInPreview != PreviewContext.IsActive) {
                TSLogger.Warning($"ROTATE-LEAK: {op} on a {(createdInPreview ? "PREVIEW" : "LIVE")} rotation state " +
                    $"while PreviewContext.IsActive={PreviewContext.IsActive} — " +
                    "live/preview filter-rotation isolation has broken; a preview is corrupting live rotation state (or vice versa)");
            }
        }

        internal IExposure Select(List<IExposure> candidates, int rotateCount) {
            CheckIsolation("Select");
            ResetForSelect(candidates, rotateCount);

            foreach (var item in exposureCountState) {
                if (item.Value.Active && item.Value.Count < rotateCount) {
                    IExposure exposure = candidates.Where(ep => ep.DatabaseId == item.Key).FirstOrDefault();
                    if (exposure != null) { return exposure; }
                }
            }

            return null;
        }

        internal void ExposureTaken(IExposure exposure, int rotateCount) {
            CheckIsolation("ExposureTaken");
            var item = exposureCountState.FirstOrDefault(ec => ec.Key == exposure.DatabaseId);
            ExposureCountState state = item.Value;
            state.Count = state.Count == rotateCount ? rotateCount : state.Count + 1;
            exposureCountState[item.Key] = state;
        }

        private void ResetForSelect(List<IExposure> candidates, int rotateCount) {
            var ids = candidates.Select(ep => ep.DatabaseId);
            bool cycleComplete = true;

            exposureCountState.ForEach(ec => {
                ExposureCountState state = ec.Value;
                state.Active = ids.Contains(ec.Key);
                exposureCountState[ec.Key] = state;
                if (state.Active && state.Count < rotateCount)
                    cycleComplete = false;
            });

            if (cycleComplete) {
                exposureCountState.ForEach(ec => {
                    ExposureCountState state = ec.Value;
                    if (state.Active) {
                        state.Count = 0;
                        exposureCountState[ec.Key] = state;
                    }
                });
            }
        }
    }

    public struct ExposureCountState {
        public bool Active { get; set; }
        public int Count { get; set; }

        public ExposureCountState() {
            Active = false;
            Count = 0;
        }
    }

    /// <summary>
    /// Support an in-memory cache to remember the state of smart exposure rotation.
    /// </summary>
    public class SmartExposureRotateCache {
        private static readonly TimeSpan ITEM_TIMEOUT = TimeSpan.FromHours(18);
        private static MemoryCache _cache = Create();
        private static object lockObj = new object();

        // Preview isolation: plan previews drive ExposureTaken/Reset on the same
        // selectors as live planning, which mutate this cache's rotation counts.
        // Without isolation a background preview (TS API /preview, Plan Preview UI)
        // corrupts live filter rotation mid-session (observed as one filter repeating
        // instead of rotating). Inside a preview context all operations are redirected
        // to a thread-local scratch cache. See PreviewContext.
        [ThreadStatic] private static Dictionary<string, ExposureRotateStatus> previewCache;

        public static void EnterPreviewContext() {
            previewCache = new Dictionary<string, ExposureRotateStatus>();
        }

        public static void ExitPreviewContext() {
            previewCache = null;
        }

        public static string GetCacheKey(Target target) {
            return target.Id.ToString();
        }

        public static string GetCacheKey(ITarget target) {
            return target.DatabaseId.ToString();
        }

        public static ExposureRotateStatus Get(ITarget target) {
            if (previewCache != null) {
                return previewCache.TryGetValue(GetCacheKey(target), out var ps) ? ps : null;
            }
            lock (lockObj) {
                return (ExposureRotateStatus)_cache.Get(GetCacheKey(target));
            }
        }

        public static void Put(ITarget target, ExposureRotateStatus exposureRotateStatus) {
            if (previewCache != null) {
                previewCache[GetCacheKey(target)] = exposureRotateStatus;
                return;
            }
            lock (lockObj) {
                _cache.Add(GetCacheKey(target), exposureRotateStatus, DateTime.Now.Add(ITEM_TIMEOUT));
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
            lock (lockObj) {
                _cache.Remove(cacheKey);
            }
        }

        public static void Clear() {
            if (previewCache != null) {
                previewCache.Clear();
                return;
            }
            lock (lockObj) {
                _cache.Dispose();
                _cache = Create();
            }
        }

        private static MemoryCache Create() {
            return new MemoryCache("Scheduler SmartExposureRotateCache");
        }

        private SmartExposureRotateCache() {
        }
    }
}