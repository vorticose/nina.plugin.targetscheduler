using FluentAssertions;
using NINA.Plugin.TargetScheduler.Planning.Explain;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace NINA.Plugin.TargetScheduler.Test.Planning.Explain {

    /*
     * **CUSTOM FORK** Tests for the planning-insight pure logic (phase coalescing + digest/JSON shape).
     * These operate on plain DTOs and need no database or astrometry, so they run fast and deterministically.
     * The eligibility sweep and decision capture are validated by the human in-app (they exercise the real
     * planner/astrometry).
     */

    [TestFixture]
    public class PlanExplanationTest {
        private static readonly DateTime Base = new(2024, 12, 1, 21, 0, 0);

        [Test]
        public void ComputePhases_collapses_consecutive_same_target_and_splits_on_wait() {
            List<DecisionSnapshot> decisions = new List<DecisionSnapshot> {
                Sel(0, Base, Base.AddMinutes(20), 1, "M31", "L"),
                Sel(1, Base.AddMinutes(20), Base.AddMinutes(40), 1, "M31", "L"),
                Sel(2, Base.AddMinutes(40), Base.AddMinutes(60), 1, "M31", "R"),
                WaitDec(3, Base.AddMinutes(60), Base.AddMinutes(90)),
                Sel(4, Base.AddMinutes(90), Base.AddMinutes(110), 2, "Rosette", "Ha"),
                Sel(5, Base.AddMinutes(110), Base.AddMinutes(130), 1, "M31", "L"),
            };

            List<SchedulePhase> phases = PlanExplanationBuilder.ComputePhases(decisions);

            phases.Count.Should().Be(4);

            phases[0].IsWait.Should().BeFalse();
            phases[0].TargetDatabaseId.Should().Be(1);
            phases[0].Start.Should().Be(Base);
            phases[0].End.Should().Be(Base.AddMinutes(60));
            phases[0].Filters.Should().Equal("L", "R"); // distinct, in first-use order
            phases[0].InitiatingDecision.Index.Should().Be(0);

            phases[1].IsWait.Should().BeTrue();
            phases[1].WaitUntil.Should().Be(Base.AddMinutes(90));

            phases[2].TargetDatabaseId.Should().Be(2);
            phases[2].TargetName.Should().Be("Rosette");

            phases[3].TargetDatabaseId.Should().Be(1); // M31 again is a new phase after the switch
            phases[3].Start.Should().Be(Base.AddMinutes(110));

            for (int i = 0; i < phases.Count; i++) {
                phases[i].Index.Should().Be(i);
            }
        }

        [Test]
        public void Digest_and_Json_carry_schedule_reasons_and_unscheduled_targets() {
            PlanExplanation ex = new PlanExplanation {
                GeneratedAt = Base,
                ProfileName = "Test",
                PreviewStart = Base,
                NightStart = Base,
                NightEnd = Base.AddHours(6),
            };

            DecisionSnapshot d0 = Sel(0, Base, Base.AddHours(2), 1, "M31", "L");
            d0.Candidates = new List<TargetEvaluation> {
                new TargetEvaluation {
                    ProjectName = "P", TargetName = "M31", TargetDatabaseId = 1,
                    Selected = true, Scored = true, TotalScore = 0.80,
                    RuleContributions = new List<RuleContribution> {
                        new RuleContribution { RuleName = "ProjectPriority", Enabled = true, RawScore = 0.8, Weight = 0.5, WeightPercent = 50, Contribution = 0.40 }
                    }
                },
                new TargetEvaluation {
                    ProjectName = "P", TargetName = "Veil", TargetDatabaseId = 3,
                    Rejected = true, RejectedReason = "max altitude"
                }
            };

            ex.Decisions = new List<DecisionSnapshot> { d0 };
            ex.Phases = PlanExplanationBuilder.ComputePhases(ex.Decisions);
            ex.Targets = new List<TargetTimeline> {
                new TargetTimeline { ProjectName = "P", TargetName = "M31", TargetDatabaseId = 1, ImagingPossible = true, MinimumAltitude = 30, PeakAltitude = 65 },
                new TargetTimeline { ProjectName = "P", TargetName = "NGC7000", TargetDatabaseId = 2, ImagingPossible = false, MinimumAltitude = 30, PeakAltitude = 12 },
            };

            string digest = ex.ToDigest();
            digest.Should().Contain("M31");
            digest.Should().Contain("ProjectPriority");          // score driver surfaced
            digest.Should().Contain("chosen over");              // comparison surfaced
            digest.Should().Contain("Veil");                     // the rejected rival
            digest.Should().Contain("max altitude");             // its reason
            digest.Should().Contain("TARGETS NOT SCHEDULED");
            digest.Should().Contain("NGC7000");                  // the unscheduled target
            digest.Should().NotContain("\nM31:");                // M31 was scheduled, so not in the unscheduled list

            string json = ex.ToJson();
            json.Should().Contain("\"phases\"");
            json.Should().Contain("\"targets\"");
            json.Should().Contain("M31");
            json.Should().Contain("disclaimer");
        }

        private static DecisionSnapshot Sel(int index, DateTime start, DateTime end, int dbId, string name, string filter) {
            return new DecisionSnapshot {
                Index = index,
                AtTime = start,
                Type = DecisionType.Selection,
                StartTime = start,
                EndTime = end,
                SelectedProjectName = "P",
                SelectedTargetName = name,
                SelectedTargetDatabaseId = dbId,
                SelectedExposureFilter = filter
            };
        }

        private static DecisionSnapshot WaitDec(int index, DateTime start, DateTime until) {
            return new DecisionSnapshot {
                Index = index,
                AtTime = start,
                Type = DecisionType.Wait,
                StartTime = start,
                WaitUntil = until
            };
        }
    }
}
