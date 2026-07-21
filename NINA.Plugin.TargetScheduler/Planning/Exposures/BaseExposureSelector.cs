using LinqKit;
using NINA.Plugin.TargetScheduler.Database;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System.Collections.Generic;

namespace NINA.Plugin.TargetScheduler.Planning.Exposures {

    public abstract class BaseExposureSelector {
        protected IProject Project;
        protected ITarget Target;
        protected FilterCadence FilterCadence;

        /// <summary>
        /// The DitherManager is resolved LAZILY on every access (never cached in a field) so that
        /// the preview/live distinction — which is only established AFTER the selector is constructed
        /// (MarkForPreview sets ITarget.IsPreview; PreviewPlanner enters the PreviewContext) — is
        /// always honored at the point of use.
        ///
        /// This is the root-cause fix for the third occurrence of the preview-corrupts-live class of
        /// bug. Previously each selector captured GetDitherManager(...) in its constructor, grabbing a
        /// reference to the LIVE DitherManager before any preview context existed. The thread-local
        /// scratch redirect in DitherManagerCache could then never intercept it, so a background plan
        /// preview (TS API /preview, Plan Preview UI) drove ExposureTaken/Reset straight into live
        /// dither state and suppressed dithering for the rest of the session. The smart-rotation cache
        /// never had this bug precisely because it re-resolves from the cache on every operation — this
        /// makes dither behave the same way. See PreviewContext.
        /// </summary>
        protected DitherManager DitherManager => GetDitherManager(Project, Target);

        public BaseExposureSelector(IProject project, ITarget target) {
            Project = project;
            Target = target;
        }

        /// <summary>
        /// Some exposure selectors need to remember the previous dither state - typically those
        /// that don't rely on a persisted FilterCadence.  Always resolved through the cache (never
        /// held in a field) so preview runs transparently hit the thread-local scratch cache and
        /// live runs hit the shared live cache.
        /// </summary>
        /// <param name="project"></param>
        /// <param name="target"></param>
        /// <returns></returns>
        public DitherManager GetDitherManager(IProject project, ITarget target) {
            string cacheKey = DitherManagerCache.GetCacheKey(target);
            DitherManager dm = DitherManagerCache.Get(cacheKey);
            if (dm != null) {
                TSLogger.Debug($"DITHER-DIAG: GetDitherManager cache HIT key={cacheKey} preview={PreviewContext.IsActive} project.DitherEvery={project.DitherEvery} (hash={dm.GetHashCode()})");
                return dm;
            } else {
                TSLogger.Info($"DITHER-DIAG: GetDitherManager cache MISS key={cacheKey} preview={PreviewContext.IsActive} -> creating new with project.DitherEvery={project.DitherEvery}");
                dm = new DitherManager(project.DitherEvery);
                DitherManagerCache.Put(dm, cacheKey);
                return dm;
            }
        }

        /// <summary>
        /// Return true if all exposure plans were rejected, otherwise false.
        /// </summary>
        /// <param name="target"></param>
        /// <returns></returns>
        public bool AllExposurePlansRejected(ITarget target) {
            bool atLeastOneAccepted = false;
            target.ExposurePlans.ForEach(e => { if (!e.Rejected) atLeastOneAccepted = true; });
            return !atLeastOneAccepted;
        }

        /// <summary>
        /// Update the target's filter cadence list, typically after an exposure is taken
        /// and the cadence is advanced.
        /// </summary>
        /// <param name="filterCadence"></param>
        public void UpdateFilterCadences(FilterCadence filterCadence) {
            // Previews simulate ExposureTaken, which advances the cadence — persisting
            // that would durably corrupt the LIVE filter order in the database. Skip.
            if (PreviewContext.IsActive) {
                TSLogger.Debug("PREVIEW-ISOLATION: skipping filter-cadence DB write during preview");
                return;
            }

            List<FilterCadenceItem> items = new List<FilterCadenceItem>(filterCadence.Count);
            filterCadence.List.ForEach(fci => {
                items.Add(new FilterCadenceItem {
                    TargetId = Target.DatabaseId,
                    Order = fci.Order,
                    Next = fci.Next,
                    Action = fci.Action,
                    ReferenceIdx = fci.ReferenceIdx,
                });
            });

            using (var context = GetSchedulerDatabaseContext()) {
                context.ReplaceFilterCadences(Target.DatabaseId, items, false);
            }
        }

        public virtual ISchedulerDatabaseContext GetSchedulerDatabaseContext() {
            return new SchedulerDatabaseInteraction().GetContext();
        }
    }
}