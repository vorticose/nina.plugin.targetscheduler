using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugin.TargetScheduler.Planning.Exposures {

    /// <summary>
    /// Select the next exposure based on the persisted filter cadence for this target.  Add a dither before the
    /// exposure if appropriate.
    /// </summary>
    public class BasicExposureSelector : BaseExposureSelector, IExposureSelector {
        private ExposureRatioSelector ExposureRatioSelector = null;
        private bool lastSelectionByRatio = false;

        public BasicExposureSelector(IProject project, ITarget target, Target databaseTarget) : base(target) {
            FilterCadence = new FilterCadenceFactory().Generate(project, target, databaseTarget);
            DitherManager = GetDitherManager(project, target);
            if (project.MaintainExposureRatio) {
                ExposureRatioSelector = new ExposureRatioSelector(project.ExposureCompletionHelper);
            }
        }

        public IExposure Select(DateTime atTime, IProject project, ITarget target) {
            if (AllExposurePlansRejected(target)) {
                TSLogger.Warning($"unexpected: all exposure plans were rejected at exposure selection time for target '{target.Name}' at time {atTime}");
                return null;
            }

            // If ratio maintenance is enabled, try ratio-based selection first
            if (ExposureRatioSelector != null) {
                List<IExposure> candidates = target.ExposurePlans.Where(ep => !ep.Rejected).ToList();
                IExposure ratioSelected = ExposureRatioSelector.Select(candidates);
                if (ratioSelected != null) {
                    TSLogger.Debug($"basic selector: ratio override selected {ratioSelected.FilterName} (skipping cadence)");
                    ratioSelected.PreDither = DitherManager.DitherRequired(ratioSelected);
                    lastSelectionByRatio = true;
                    return ratioSelected;
                } else {
                    TSLogger.Debug("basic selector: ratio within dead band, falling back to cadence");
                }
            }

            lastSelectionByRatio = false;

            try {
                foreach (IFilterCadenceItem item in FilterCadence) {
                    IExposure exposure = target.AllExposurePlans[item.ReferenceIdx];
                    if (exposure.Rejected) { continue; }

                    exposure.PreDither = DitherManager.DitherRequired(exposure);
                    FilterCadence.SetLastSelected(item);
                    return exposure;
                }
            } catch (Exception ex) {
                TSLogger.Warning($"exception in basic exposure selector for target '{target.Name}' at time {atTime}: {ex.Message}, aborting target");
                return null;
            }

            TSLogger.Warning($"no acceptable exposure plan in basic exposure selector for target '{target.Name}' at time {atTime}");
            return null;
        }

        public void ExposureTaken(IExposure exposure) {
            if (!lastSelectionByRatio) {
                FilterCadence.Advance();
                UpdateFilterCadences(FilterCadence);
            }

            if (exposure.PreDither) DitherManager.Reset();
            DitherManager.AddExposure(exposure);
        }

        public void TargetReset() {
            DitherManager.Reset();
        }
    }
}