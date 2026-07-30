using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;

namespace NINA.Plugin.TargetScheduler.Planning.Explain {

    /*
     * **CUSTOM FORK** Planning insight / scoring transparency feature.
     *
     * Orchestrates a full PlanExplanation: runs the decision-capturing PlanExplainer and the eligibility/
     * altitude TimelineSweep on independent fresh project loads (both mutate per-run state), assembles the
     * timeline axis bounds, and cross-references the planner's actual picks onto each target's lane.
     */

    public class PlanExplanationBuilder {

        /// <param name="loadProjects">
        /// Factory returning a FRESH, independent project graph each call (the explainer and sweep both
        /// mutate rejection/scoring/count state, so they must not share instances).
        /// </param>
        public PlanExplanation Build(DateTime atTime, string profileName, IProfileService profileService,
                ProfilePreference profilePreferences, Func<List<IProject>> loadProjects) {
            PlanExplanation explanation = new PlanExplanation {
                GeneratedAt = DateTime.Now,
                ProfileName = profileName,
                PreviewStart = atTime
            };

            // Authoritative decisions from the real planner chain. Restrict to targets the scheduler can
            // actually consider — active project + enabled target (already enforced by the loader) AND with
            // remaining exposures. Complete targets are immediately rejected by the planner and only clutter
            // the view, so they are excluded from the timeline (and listed separately as "complete").
            List<string> completed = new List<string>();
            List<IProject> forExplain = FilterToConsiderable(loadProjects(), completed);
            ExplainerResult explainerResult = new PlanExplainer().Explain(atTime, profileService, profilePreferences, forExplain);
            explanation.Decisions = explainerResult.Decisions;
            explanation.CompletedTargets = completed;

            // Eligibility + altitude from an independent fresh copy (same considerable-target filter).
            List<IProject> forSweep = FilterToConsiderable(loadProjects(), null);
            SweepResult sweep = new TimelineSweep(profileService.ActiveProfile, profilePreferences).Sweep(atTime, forSweep);
            explanation.Targets = sweep.Targets;
            explanation.Twilight = sweep.Twilight;

            // Axis bounds: civil dusk -> dawn, widened to include the preview start and every decision span.
            DateTime nightStart = sweep.NightStart;
            DateTime nightEnd = sweep.NightEnd;
            if (atTime < nightStart) { nightStart = atTime; }
            foreach (DecisionSnapshot d in explanation.Decisions) {
                if (d.StartTime < nightStart) { nightStart = d.StartTime; }
                DateTime end = d.EndTime ?? d.WaitUntil ?? d.StartTime;
                if (end > nightEnd) { nightEnd = end; }
            }
            explanation.NightStart = nightStart;
            explanation.NightEnd = nightEnd;

            explanation.Phases = ComputePhases(explanation.Decisions);
            AttachScheduledBlocks(explanation);
            return explanation;
        }

        /// <summary>
        /// Keep only targets the scheduler can actually consider: enabled (already enforced upstream by the
        /// loader, which builds PlanningTargets only for enabled targets) AND with remaining exposures.
        /// Projects left with no considerable targets are dropped. Names of excluded complete targets are
        /// collected (when a collector is provided) so they can be reported separately rather than cluttering
        /// the timeline.
        /// </summary>
        private List<IProject> FilterToConsiderable(List<IProject> projects, List<string> completedCollector) {
            List<IProject> result = new List<IProject>();
            if (projects == null) { return result; }

            foreach (IProject project in projects) {
                List<ITarget> keep = new List<ITarget>();
                foreach (ITarget target in project.Targets) {
                    if (TargetHasRemainingExposures(target)) {
                        keep.Add(target);
                    } else {
                        completedCollector?.Add($"{project.Name} / {target.Name}");
                    }
                }

                if (keep.Count > 0) {
                    project.Targets = keep;
                    result.Add(project);
                }
            }

            return result;
        }

        // Mirrors the planner's incomplete check (Planner.ProjectIsInComplete): workable if any enabled
        // exposure plan still needs exposures.
        private bool TargetHasRemainingExposures(ITarget target) {
            if (target.ExposurePlans == null) { return false; }
            foreach (IExposure exposure in target.ExposurePlans) {
                if (exposure.NeededExposures() > 0) { return true; }
            }
            return false;
        }

        /// <summary>
        /// Collapse the per-exposure decision stream into phases: consecutive decisions for the same target
        /// become one phase, each wait its own. The first decision of a target run is kept as the phase's
        /// initiating decision (the scoring contest that chose it).
        /// </summary>
        public static List<SchedulePhase> ComputePhases(List<DecisionSnapshot> decisions) {
            List<SchedulePhase> phases = new List<SchedulePhase>();
            SchedulePhase current = null;

            foreach (DecisionSnapshot d in decisions) {
                if (d.Type == DecisionType.Wait) {
                    if (current != null) { phases.Add(current); current = null; }
                    phases.Add(new SchedulePhase {
                        Start = d.StartTime,
                        End = d.WaitUntil ?? d.StartTime,
                        IsWait = true,
                        WaitUntil = d.WaitUntil,
                        InitiatingDecision = d
                    });
                    continue;
                }

                if (current == null || current.TargetDatabaseId != d.SelectedTargetDatabaseId) {
                    if (current != null) { phases.Add(current); }
                    current = new SchedulePhase {
                        Start = d.StartTime,
                        End = d.EndTime ?? d.StartTime,
                        ProjectName = d.SelectedProjectName,
                        TargetName = d.SelectedTargetName,
                        TargetDatabaseId = d.SelectedTargetDatabaseId,
                        InitiatingDecision = d
                    };
                    AddFilter(current, d.SelectedExposureFilter);
                } else {
                    current.End = d.EndTime ?? current.End;
                    AddFilter(current, d.SelectedExposureFilter);
                }
            }

            if (current != null) { phases.Add(current); }
            for (int i = 0; i < phases.Count; i++) { phases[i].Index = i; }
            return phases;
        }

        private static void AddFilter(SchedulePhase phase, string filter) {
            if (!string.IsNullOrEmpty(filter) && !phase.Filters.Contains(filter)) {
                phase.Filters.Add(filter);
            }
        }

        private void AttachScheduledBlocks(PlanExplanation explanation) {
            Dictionary<int, TargetTimeline> byId = new Dictionary<int, TargetTimeline>();
            foreach (TargetTimeline t in explanation.Targets) {
                if (!byId.ContainsKey(t.TargetDatabaseId)) { byId[t.TargetDatabaseId] = t; }
            }

            foreach (SchedulePhase phase in explanation.Phases) {
                if (phase.IsWait) { continue; }
                if (byId.TryGetValue(phase.TargetDatabaseId, out TargetTimeline tl)) {
                    tl.ScheduledBlocks.Add(new ScheduledBlock {
                        Start = phase.Start,
                        End = phase.End,
                        Filter = string.Join("+", phase.Filters),
                        DecisionIndex = phase.Index
                    });
                }
            }
        }
    }
}
