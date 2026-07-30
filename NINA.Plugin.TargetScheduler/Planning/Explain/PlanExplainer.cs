using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Exposures;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Planning.Scoring;
using NINA.Plugin.TargetScheduler.Planning.Scoring.Rules;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using NINA.Profile.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugin.TargetScheduler.Planning.Explain {

    /*
     * **CUSTOM FORK** Planning insight / scoring transparency feature.
     *
     * Mirrors PreviewPlanner's perfect-plan loop, but captures a structured DecisionSnapshot at each
     * planner decision BEFORE the per-run state wipe discards the scoring/rejection detail. This keeps
     * upstream PreviewPlanner untouched (clean upstream merges) at the cost of replicating its small loop
     * and the private PrepForNextRun bookkeeping.
     *
     * The captured data IS the real planner's own output, so it cannot teach the user a different model
     * than the one that actually schedules.
     */

    public class ExplainerResult {
        public List<SchedulerPlan> Plans { get; set; } = new List<SchedulerPlan>();
        public List<DecisionSnapshot> Decisions { get; set; } = new List<DecisionSnapshot>();
    }

    public class PlanExplainer {
        // Safety backstop against a runaway loop in a prototype; a real night completes well under this.
        private const int MaxIterations = 3000;

        private ITarget previousTarget;

        public ExplainerResult Explain(DateTime atTime, IProfileService profileService, ProfilePreference profilePreferences, List<IProject> projects) {
            TSLogger.Info("-- BEGIN PLAN EXPLAIN ----------------------------------------------------------");
            DitherManagerCache.Clear();
            SmartExposureRotateCache.ClearPreview(projects); // **CUSTOM FORK** isolate preview rotation from live

            ExplainerResult result = new ExplainerResult();
            IWeatherDataMediator weatherData = new DisconnectedWeatherDataMediator();
            DateTime currentTime = atTime;
            previousTarget = null;
            int index = 0;

            try {
                SchedulerPlan plan;
                while ((plan = new Planner(currentTime, profileService.ActiveProfile, profilePreferences, weatherData, false, true, projects).GetPlan(previousTarget)) != null) {
                    result.Plans.Add(plan);
                    result.Decisions.Add(CaptureDecision(index++, currentTime, plan, projects));

                    currentTime = plan.IsWait ? (DateTime)plan.WaitForNextTargetTime : plan.EndTime;
                    PrepForNextRun(projects, plan);

                    if (index >= MaxIterations) {
                        TSLogger.Warning($"plan explain stopped at {MaxIterations} iterations (safety backstop)");
                        break;
                    }
                }

                return result;
            } catch (Exception ex) {
                TSLogger.Error($"exception during plan explain: {ex.Message}\n{ex.StackTrace}");
                return result;
            } finally {
                TSLogger.Info("-- END PLAN EXPLAIN ------------------------------------------------------------");
            }
        }

        private DecisionSnapshot CaptureDecision(int index, DateTime atTime, SchedulerPlan plan, List<IProject> projects) {
            DecisionSnapshot d = new DecisionSnapshot {
                Index = index,
                AtTime = atTime,
                StartTime = plan.StartTime
            };

            if (plan.IsWait) {
                d.Type = DecisionType.Wait;
                d.WaitUntil = plan.WaitForNextTargetTime;
            } else {
                d.EndTime = plan.EndTime;
                d.SelectedProjectName = plan.PlanTarget?.Project?.Name;
                d.SelectedTargetName = plan.PlanTarget?.Name;
                d.SelectedTargetDatabaseId = plan.PlanTarget?.DatabaseId ?? -1;
                d.SelectedExposureFilter = plan.PlanTarget?.SelectedExposure?.FilterName;

                // Continuation: the previous target carried on within its minimum-time span, so no filtering
                // or scoring ran this cycle (every target is still clear of rejections/scores).
                bool evaluated = projects.Any(p => p.Targets.Any(t => t.ScoringResults != null || t.Rejected));
                d.Type = (!evaluated && previousTarget != null && ReferenceEquals(plan.PlanTarget, previousTarget))
                    ? DecisionType.Continuation
                    : DecisionType.Selection;
            }

            foreach (IProject project in projects) {
                foreach (ITarget target in project.Targets) {
                    d.Candidates.Add(BuildEvaluation(project, target, plan));
                }
            }

            return d;
        }

        private TargetEvaluation BuildEvaluation(IProject project, ITarget target, SchedulerPlan plan) {
            TargetEvaluation e = new TargetEvaluation {
                ProjectName = project.Name,
                TargetName = target.Name,
                TargetDatabaseId = target.DatabaseId,
                Selected = !plan.IsWait && ReferenceEquals(target, plan.PlanTarget),
                Rejected = target.Rejected,
                RejectedReason = target.Rejected ? target.RejectedReason : null,
                SelectedExposureFilter = target.SelectedExposure?.FilterName
            };

            if (target.ScoringResults != null) {
                e.Scored = true;
                e.TotalScore = target.ScoringResults.TotalScore;
                e.RuleContributions = BuildContributions(target);
            }

            return e;
        }

        private List<RuleContribution> BuildContributions(ITarget target) {
            List<RuleContribution> contributions = new List<RuleContribution>();
            HashSet<string> scored = new HashSet<string>();

            foreach (RuleResult rr in target.ScoringResults.Results) {
                contributions.Add(new RuleContribution {
                    RuleName = rr.ScoringRule.Name,
                    Enabled = true,
                    RawScore = rr.Score,
                    Weight = rr.Weight,
                    WeightPercent = rr.Weight * ScoringRule.WEIGHT_SCALE,
                    Contribution = rr.Score * rr.Weight
                });
                scored.Add(rr.ScoringRule.Name);
            }

            // Include rules that were disabled (project weight 0) so the user can see they exist and are off.
            Dictionary<string, double> weights = target.Project?.RuleWeights;
            foreach (KeyValuePair<string, IScoringRule> entry in ScoringRule.GetAllScoringRules()) {
                if (scored.Contains(entry.Key)) { continue; }
                double weightPercent = (weights != null && weights.ContainsKey(entry.Key)) ? weights[entry.Key] : 0;
                contributions.Add(new RuleContribution {
                    RuleName = entry.Key,
                    Enabled = false,
                    RawScore = 0,
                    Weight = 0,
                    WeightPercent = weightPercent,
                    Contribution = 0
                });
            }

            return contributions.OrderByDescending(c => c.Enabled).ThenByDescending(c => c.Contribution).ToList();
        }

        /// <summary>
        /// Replicates PreviewPlanner.PrepForNextRun exactly: advances the perfect-plan bookkeeping (counts,
        /// exposure selector) for the chosen target, then clears all per-run rejection/scoring state so the
        /// next planner cycle starts fresh. Kept in sync with PreviewPlanner intentionally.
        /// </summary>
        private void PrepForNextRun(List<IProject> projects, SchedulerPlan plan) {
            if (!plan.IsWait) {
                if (previousTarget != null && plan.PlanTarget != previousTarget) {
                    plan.PlanTarget.ExposureSelector.TargetReset();
                }

                plan.PlanTarget.ExposureSelector.ExposureTaken(plan.PlanTarget.SelectedExposure);

                plan.PlanTarget.SelectedExposure.Acquired++;
                if (plan.PlanTarget.Project.EnableGrader) {
                    plan.PlanTarget.SelectedExposure.Accepted++;
                }

                previousTarget = plan.PlanTarget;
            } else {
                previousTarget = null;
            }

            foreach (IProject project in projects) {
                project.Rejected = false;
                project.RejectedReason = null;
                foreach (ITarget target in project.Targets) {
                    target.ScoringResults = null;
                    target.Rejected = false;
                    target.RejectedReason = null;

                    foreach (IExposure exposure in target.ExposurePlans) {
                        exposure.Rejected = false;
                        exposure.RejectedReason = null;
                        exposure.MoonAvoidanceScore = MoonAvoidanceExpert.SCORE_OFF;
                    }
                }
            }
        }
    }
}
