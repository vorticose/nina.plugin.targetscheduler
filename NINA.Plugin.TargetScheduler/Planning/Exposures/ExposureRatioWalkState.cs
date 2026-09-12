using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;
using System.Collections.Generic;
using System.Runtime.Caching;

namespace NINA.Plugin.TargetScheduler.Planning.Exposures {

    /// <summary>
    /// Per-target memory of the exposure-ratio leftover walk: the Bresenham error accumulators,
    /// the empty-filter seed bookkeeping and the FilterSwitchFrequency run in progress.
    ///
    /// This state MUST outlive the selector instance that uses it. The planner reloads every
    /// project and target from the database on each planning run and each PlanningTarget builds
    /// a fresh exposure selector, so anything kept in selector fields is discarded after a single
    /// pick. When the walk state lived in selector fields (night of 2026-09-11) every live pick was
    /// step one of a brand-new walk, which degenerates to "largest leftover wins": a 100/50/50/50
    /// LRGB target shot 57 L against 3 each of R, G and B. The state now lives in
    /// ExposureRatioWalkCache keyed by target id, exactly like SmartExposureRotateCache, and the
    /// selector resolves it at the point of use (never in a field) so preview isolation holds.
    ///
    /// Select is a pure function of this state plus the current exposure counts; the state only
    /// advances in ExposureTaken. That matters because the planner evaluates the selector twice per
    /// run (once in the previous-target continue check, once in the full plan) and only one exposure
    /// is taken. A Select that advanced the walk would burn a step on every full planning run.
    /// </summary>
    public class ExposureRatioWalkState {
        internal List<string> WalkNames = null;
        internal int[] Remaining = null;
        internal int[] Errors = null;
        internal int TotalRemaining = 0;

        internal readonly HashSet<string> SeededFilterNames = new HashSet<string>();
        internal string SeedFilterName = null;
        internal int SeedLeft = 0;

        internal string RunFilterName = null;
        internal int RunLeft = 0;

        /// <summary>
        /// What the most recent Select proposed. ExposureTaken commits the recorded transition
        /// when the exposure actually taken matches the proposal, and leaves the walk untouched
        /// otherwise (the host took something the walk did not choose).
        /// </summary>
        internal WalkProposal LastProposal = null;

        // Same live-vs-preview tripwire as DitherManager.createdInPreview and
        // ExposureRotateStatus.createdInPreview: a state born in a preview must never be
        // touched by live planning and vice versa.
        private readonly bool createdInPreview;

        public ExposureRatioWalkState() {
            createdInPreview = PreviewContext.IsActive;
        }

        /// <summary>True if this state was created during a plan preview (scratch cache).</summary>
        public bool CreatedInPreview => createdInPreview;

        internal void CheckIsolation(string op) {
            if (createdInPreview != PreviewContext.IsActive) {
                TSLogger.Warning($"RATIO-LEAK: {op} on a {(createdInPreview ? "PREVIEW" : "LIVE")} exposure-ratio walk state " +
                    $"while PreviewContext.IsActive={PreviewContext.IsActive} - " +
                    "live/preview ratio-walk isolation has broken; a preview is corrupting live ratio state (or vice versa)");
            }
        }
    }

    internal enum WalkProposalKind {
        SeedStart,
        SeedContinue,
        RunContinue,
        Walk,
    }

    /// <summary>
    /// The transition a Select would apply if its pick is taken. Carries the leftover snapshot
    /// the pick was computed from so the commit does not depend on whether the exposure counts
    /// have already been incremented when ExposureTaken runs.
    /// </summary>
    internal class WalkProposal {
        public string FilterName;
        public WalkProposalKind Kind;
        public int SeedCount;
        public bool InitWalk;
        public List<string> WalkNames;
        public int[] Remaining;
        public int TotalRemaining;
    }

    /// <summary>
    /// In-memory cache of ExposureRatioWalkState keyed by target id, so the leftover walk survives
    /// the planner rebuilding targets and selectors on every run. Mirrors SmartExposureRotateCache,
    /// including the thread-local scratch redirect while a plan preview is active (see PreviewContext).
    /// </summary>
    public class ExposureRatioWalkCache {
        private static readonly TimeSpan ITEM_TIMEOUT = TimeSpan.FromHours(18);
        private static MemoryCache _cache = Create();
        private static object lockObj = new object();

        [ThreadStatic] private static Dictionary<string, ExposureRatioWalkState> previewCache;

        public static void EnterPreviewContext() {
            previewCache = new Dictionary<string, ExposureRatioWalkState>();
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

        public static ExposureRatioWalkState Get(ITarget target) {
            if (previewCache != null) {
                return previewCache.TryGetValue(GetCacheKey(target), out var ps) ? ps : null;
            }
            lock (lockObj) {
                return (ExposureRatioWalkState)_cache.Get(GetCacheKey(target));
            }
        }

        public static ExposureRatioWalkState GetOrCreate(ITarget target) {
            ExposureRatioWalkState state = Get(target);
            if (state == null) {
                state = new ExposureRatioWalkState();
                Put(target, state);
                TSLogger.Debug($"ratio selector: new walk state for target key={GetCacheKey(target)} preview={PreviewContext.IsActive}");
            }
            return state;
        }

        public static void Put(ITarget target, ExposureRatioWalkState state) {
            if (previewCache != null) {
                previewCache[GetCacheKey(target)] = state;
                return;
            }
            lock (lockObj) {
                _cache.Set(GetCacheKey(target), state, DateTime.Now.Add(ITEM_TIMEOUT));
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
            return new MemoryCache("Scheduler ExposureRatioWalkCache");
        }

        private ExposureRatioWalkCache() {
        }
    }
}
