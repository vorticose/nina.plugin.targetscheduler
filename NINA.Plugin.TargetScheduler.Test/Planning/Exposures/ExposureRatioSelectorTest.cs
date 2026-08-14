using FluentAssertions;
using Moq;
using NINA.Plugin.TargetScheduler.Planning.Exposures;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Test.Planning;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugin.TargetScheduler.Test.Planning.Exposures {

    [TestFixture]
    public class ExposureRatioSelectorTest {

        [Test]
        public void testSingleCandidateReturnsNull() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 20, 5));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            sut.Select(candidates).Should().BeNull();
        }

        [Test]
        public void testDesiredZeroTreatedAsComplete() {
            // L has Desired=0 (dropped), R has 0/10. Only one eligible candidate, returns null.
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 0, 0));
            candidates.Add(MakeExposure("R", 10, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            sut.Select(candidates).Should().BeNull();
        }

        [Test]
        public void testRejectedCandidatesExcluded() {
            List<IExposure> candidates = new List<IExposure>();
            IExposure l = MakeExposure("L", 20, 0);
            l.Rejected = true;
            candidates.Add(l);
            candidates.Add(MakeExposure("R", 10, 3));
            candidates.Add(MakeExposure("G", 10, 8));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("R");
        }

        [Test]
        public void testCompletionRatioCalculation() {
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            IExposure zero = MakeExposure("L", 10, 0);
            sut.CompletionRatio(zero).Should().BeApproximately(0.0, 0.0001);

            IExposure half = MakeExposure("R", 10, 5);
            sut.CompletionRatio(half).Should().BeApproximately(0.5, 0.0001);

            IExposure done = MakeExposure("G", 10, 10);
            sut.CompletionRatio(done).Should().BeApproximately(1.0, 0.0001);

            IExposure noDesired = MakeExposure("B", 0, 0);
            sut.CompletionRatio(noDesired).Should().BeApproximately(1.0, 0.0001);
        }

        [Test]
        public void testDelayedGradingUsesAcquired() {
            // With delayed grading enabled (threshold 80%), before threshold is reached
            // the ratio should use Acquired instead of Accepted
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(true, 80, 100));

            List<IExposure> candidates = new List<IExposure>();
            Mock<IExposure> lMock = PlanMocks.GetMockPlanExposure("L", 10, 0);
            lMock.SetupProperty(m => m.Acquired, 2);
            candidates.Add(lMock.Object);

            Mock<IExposure> rMock = PlanMocks.GetMockPlanExposure("R", 10, 0);
            rMock.SetupProperty(m => m.Acquired, 5);
            candidates.Add(rMock.Object);

            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("L");
        }

        [Test]
        public void testGradingDisabledUsesAcquired() {
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            IExposure l = MakeExposure("L", 20, 10, acquired: 2);
            sut.CompletionRatio(l).Should().BeApproximately(0.10, 0.0001);

            IExposure r = MakeExposure("R", 20, 1, acquired: 8);
            sut.CompletionRatio(r).Should().BeApproximately(0.40, 0.0001);

            List<IExposure> candidates = new List<IExposure> { l, r };
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("L");
        }

        [Test]
        public void testGradingEnabledUsesAccepted() {
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(true, 0, 100));

            IExposure l = MakeExposure("L", 20, 2, acquired: 10);
            sut.CompletionRatio(l).Should().BeApproximately(0.10, 0.0001);

            IExposure r = MakeExposure("R", 20, 8, acquired: 1);
            sut.CompletionRatio(r).Should().BeApproximately(0.40, 0.0001);

            List<IExposure> candidates = new List<IExposure> { l, r };
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("L");
        }

        [Test]
        public void testEqualDesiredDeadBandReturnsNull() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 100, 50));
            candidates.Add(MakeExposure("R", 100, 50));
            candidates.Add(MakeExposure("G", 100, 52));
            candidates.Add(MakeExposure("B", 100, 48));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            sut.Select(candidates).Should().BeNull();
        }

        [Test]
        public void testEqualDesiredDeadBandBoundaryJustOutside() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 1000, 500));
            candidates.Add(MakeExposure("B", 1000, 551));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("L");
        }

        [Test]
        public void testEqualDesiredDeadBandBoundaryJustInside() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 1000, 500));
            candidates.Add(MakeExposure("B", 1000, 549));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            sut.Select(candidates).Should().BeNull();
        }

        [Test]
        public void testEqualDesiredMostBehind() {
            // Equal desired: OIII is far behind, dead band does not apply
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("Ha", 20, 15));
            candidates.Add(MakeExposure("SII", 20, 14));
            candidates.Add(MakeExposure("OIII", 20, 5));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("OIII");
        }

        [Test]
        public void testEqualDesiredTieFirstInListWins() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 10, 0));
            candidates.Add(MakeExposure("R", 10, 0));
            candidates.Add(MakeExposure("G", 10, 5));
            candidates.Add(MakeExposure("B", 10, 5));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("L");
        }

        [Test]
        public void testUnequalDesiredAlwaysWalks() {
            // All at 50% but Desired is unequal: leftover walk decides, does not defer
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 150));
            candidates.Add(MakeExposure("R", 100, 50));
            candidates.Add(MakeExposure("G", 100, 50));
            candidates.Add(MakeExposure("B", 100, 50));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            List<string> picks = CollectPicks(sut, candidates, 6);
            picks.Should().OnlyContain(name => name == "L" || name == "R" || name == "G" || name == "B");
            CountOf(picks, "L").Should().Be(3);
            CountOf(picks, "R").Should().Be(1);
            CountOf(picks, "G").Should().Be(1);
            CountOf(picks, "B").Should().Be(1);
        }

        [Test]
        public void testIrisMidProjectMix() {
            // Desired L80 R50 G62 B90, accepted L79 R26 G26 B26
            // leftovers 1, 24, 36, 64. First 20 must mix R/G/B (B most often), L<=1,
            // max consecutive <= 3. Must not be ~20 B in a row.
            List<IExposure> candidates = IrisMidProject();
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            List<string> picks = CollectPicks(sut, candidates, 20);
            picks.Should().HaveCount(20);
            picks.Should().OnlyContain(name => name == "L" || name == "R" || name == "G" || name == "B");

            CountOf(picks, "L").Should().BeLessOrEqualTo(1);
            CountOf(picks, "B").Should().BeGreaterThan(CountOf(picks, "G"));
            CountOf(picks, "G").Should().BeGreaterThan(CountOf(picks, "R"));
            CountOf(picks, "R").Should().BeGreaterThan(0);
            picks.Should().Contain("G");
            MaxConsecutive(picks).Should().BeLessOrEqualTo(3);
            CountOf(picks, "B").Should().BeLessThan(15);
        }

        [Test]
        public void testColdStartThreeToOne() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 0));
            candidates.Add(MakeExposure("R", 100, 0));
            candidates.Add(MakeExposure("G", 100, 0));
            candidates.Add(MakeExposure("B", 100, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            List<string> first6 = CollectPicks(sut, candidates, 6);
            CountOf(first6, "L").Should().BeInRange(2, 4);
            CountOf(first6, "R").Should().BeInRange(0, 2);
            CountOf(first6, "G").Should().BeInRange(0, 2);
            CountOf(first6, "B").Should().BeInRange(0, 2);
            first6.Distinct().Should().HaveCount(4);

            List<string> first12 = first6.Concat(CollectPicks(sut, candidates, 6)).ToList();
            CountOf(first12, "L").Should().Be(6);
            CountOf(first12, "R").Should().Be(2);
            CountOf(first12, "G").Should().Be(2);
            CountOf(first12, "B").Should().Be(2);
            MaxConsecutive(first12).Should().BeLessOrEqualTo(3);
        }

        [Test]
        public void testCompleteLNeverPicked() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 80, 80));
            candidates.Add(MakeExposure("R", 50, 26));
            candidates.Add(MakeExposure("G", 62, 26));
            candidates.Add(MakeExposure("B", 90, 26));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            List<string> picks = CollectPicks(sut, candidates, 30);
            picks.Should().NotContain("L");
            picks.Should().OnlyContain(name => name == "R" || name == "G" || name == "B");
        }

        [Test]
        public void testRejectedBOnlyRG() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 80, 80));
            candidates.Add(MakeExposure("R", 50, 26));
            candidates.Add(MakeExposure("G", 62, 26));
            IExposure b = MakeExposure("B", 90, 26);
            b.Rejected = true;
            candidates.Add(b);

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            List<string> picks = CollectPicks(sut, candidates, 20);
            picks.Should().OnlyContain(name => name == "R" || name == "G");
            CountOf(picks, "G").Should().BeGreaterThan(CountOf(picks, "R"));
        }

        [Test]
        public void testEmptyFilterSeed() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("R", 50, 26));
            candidates.Add(MakeExposure("G", 62, 26));
            candidates.Add(MakeExposure("B", 90, 26));
            candidates.Add(MakeExposure("O", 30, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            List<string> picks = CollectPicks(sut, candidates, 12);

            picks.Take(3).Should().OnlyContain(name => name == "O");
            picks.Skip(3).Should().Contain(name => name != "O");
            picks.Skip(3).Distinct().Count().Should().BeGreaterThan(1);
        }

        [Test]
        public void testEmptyFilterSeedCapsAtDesired() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("R", 50, 26));
            candidates.Add(MakeExposure("G", 62, 26));
            candidates.Add(MakeExposure("O", 2, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            List<string> picks = CollectPicks(sut, candidates, 6);
            picks.Take(2).Should().Equal("O", "O");
        }

        [Test]
        public void testAllCompleteReturnsNull() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 80, 80));
            candidates.Add(MakeExposure("R", 50, 50));
            candidates.Add(MakeExposure("G", 62, 62));
            candidates.Add(MakeExposure("B", 90, 90));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            sut.Select(candidates).Should().BeNull();
        }

        [Test]
        public void testStatefulWalkDeterministicNoLongBlock() {
            List<IExposure> candidates = IrisMidProject();
            ExposureRatioSelector a = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            ExposureRatioSelector b = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            List<string> picksA = CollectPicks(a, candidates, 40);
            List<string> picksB = CollectPicks(b, candidates, 40);

            picksA.Should().Equal(picksB);
            MaxConsecutive(picksA).Should().BeLessThan(15);
            CountOf(picksA, "B").Should().BeGreaterThan(CountOf(picksA, "G"));
            CountOf(picksA, "L").Should().BeLessOrEqualTo(1);
        }

        [Test]
        public void testFilterSwitchFrequencyMinRunLength() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 0));
            candidates.Add(MakeExposure("R", 100, 0));
            candidates.Add(MakeExposure("G", 100, 0));
            candidates.Add(MakeExposure("B", 100, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100), filterSwitchFrequency: 2);
            List<string> picks = CollectPicks(sut, candidates, 12);

            for (int i = 0; i < picks.Count; i += 2) {
                picks[i].Should().Be(picks[i + 1], $"FSF=2 should emit pairs, mismatch at {i}");
            }
            CountOf(picks, "L").Should().Be(6);
            CountOf(picks, "R").Should().Be(2);
            CountOf(picks, "G").Should().Be(2);
            CountOf(picks, "B").Should().Be(2);
        }

        [Test]
        public void testFilterSwitchFrequency1SwitchesEveryPick() {
            List<IExposure> candidates = IrisMidProject();
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100), filterSwitchFrequency: 1);
            List<string> picks = CollectPicks(sut, candidates, 12);
            MaxConsecutive(picks).Should().BeLessOrEqualTo(2);
        }

        [Test]
        public void testLeftoverCycleExactCounts() {
            // Frozen leftovers 7:3 must produce exactly those counts over one full cycle
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 7, 0));
            candidates.Add(MakeExposure("R", 3, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            List<string> picks = CollectPicks(sut, candidates, 10);
            CountOf(picks, "L").Should().Be(7);
            CountOf(picks, "R").Should().Be(3);
            MaxConsecutive(picks).Should().BeLessOrEqualTo(3);
        }

        // ---------------------------------------------------------------------
        // Drain simulations: increment Accepted after each pick so leftovers
        // shrink the way a real night does. Frozen CollectPicks tests cannot
        // see a filter drop out after its last needed frame.
        // ---------------------------------------------------------------------

        public class DrainCase {
            public string Name { get; set; }
            public (string Filter, int Desired, int Accepted)[] Start { get; set; }
            public int MaxMixedRun { get; set; }

            public override string ToString() {
                return Name;
            }
        }

        public static IEnumerable<DrainCase> DrainCases() {
            yield return new DrainCase {
                Name = "iris mid-project 80/50/62/90 from 79/26/26/26",
                Start = new[] { ("L", 80, 79), ("R", 50, 26), ("G", 62, 26), ("B", 90, 26) },
                MaxMixedRun = 3,
            };
            yield return new DrainCase {
                Name = "cold start 3:1:1:1 (30/10/10/10)",
                Start = new[] { ("L", 30, 0), ("R", 10, 0), ("G", 10, 0), ("B", 10, 0) },
                MaxMixedRun = 3,
            };
            yield return new DrainCase {
                Name = "classic 3:1:1:1 partway, B behind",
                Start = new[] { ("L", 30, 15), ("R", 10, 5), ("G", 10, 5), ("B", 10, 2) },
                MaxMixedRun = 3,
            };
            yield return new DrainCase {
                Name = "SHO 1:1:3 O behind",
                Start = new[] { ("Ha", 20, 10), ("S", 20, 10), ("O", 60, 5) },
                MaxMixedRun = 5,
            };
            yield return new DrainCase {
                Name = "lopsided 10:1",
                Start = new[] { ("L", 20, 0), ("R", 2, 0) },
                MaxMixedRun = 10,
            };
            yield return new DrainCase {
                Name = "L overshot desired, RGB short",
                Start = new[] { ("L", 80, 90), ("R", 50, 26), ("G", 62, 26), ("B", 90, 26) },
                MaxMixedRun = 3,
            };
            yield return new DrainCase {
                Name = "only B leftover after even RGB (raised B desired)",
                Start = new[] { ("L", 30, 30), ("R", 30, 30), ("G", 30, 30), ("B", 90, 30) },
                MaxMixedRun = 60,
            };
        }

        [TestCaseSource(nameof(DrainCases))]
        public void testDrainHitsDesiredNoOvershoot(DrainCase c) {
            List<IExposure> candidates = c.Start
                .Select(row => MakeExposure(row.Filter, row.Desired, row.Accepted))
                .ToList();
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            DrainResult drain = Drain(sut, candidates);

            foreach (var row in c.Start) {
                IExposure exposure = candidates.Single(e => e.FilterName == row.Filter);
                int expected = Math.Max(row.Accepted, row.Desired);
                exposure.Accepted.Should().Be(expected,
                    $"{c.Name}: {exposure.FilterName} should land on max(start, Desired)={expected}");
                if (row.Accepted <= row.Desired) {
                    exposure.Accepted.Should().BeLessOrEqualTo(exposure.Desired,
                        $"{c.Name}: {exposure.FilterName} overshot Desired");
                }
            }
            drain.Overshoot.Should().BeFalse($"{c.Name}: a pick exceeded Desired");
            drain.Picks.Should().NotBeEmpty($"{c.Name}: expected leftover work");
            drain.MaxMixedRun.Should().BeLessOrEqualTo(c.MaxMixedRun,
                $"{c.Name}: mixed-phase run of {drain.MaxMixedRun} (tail of last filter is ignored)");
        }

        [Test]
        public void testIrisDrainFirstPicksAreMixed() {
            List<IExposure> candidates = IrisMidProject();
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            DrainResult drain = Drain(sut, candidates);

            drain.Picks.Take(20).Should().Contain("R").And.Contain("G").And.Contain("B");
            CountOf(drain.Picks.Take(20).ToList(), "L").Should().BeLessOrEqualTo(1);
            CountOf(drain.Picks.Take(20).ToList(), "B")
                .Should().BeGreaterThan(CountOf(drain.Picks.Take(20).ToList(), "R"));
            candidates.Single(e => e.FilterName == "L").Accepted.Should().Be(80);
        }

        [Test]
        public void testRaiseBDesiredMidDrain() {
            // Session starts as even RGB; after 12 frames we raise B 10 -> 40.
            // Remaining work should absorb the extra B without a 30-B binge
            // while R/G still have leftovers.
            List<IExposure> candidates = new List<IExposure> {
                MakeExposure("R", 10, 0),
                MakeExposure("G", 10, 0),
                MakeExposure("B", 10, 0),
            };
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            List<string> before = Drain(sut, candidates, stopAfter: 12).Picks;
            before.Should().HaveCount(12);

            IExposure b = candidates.Single(e => e.FilterName == "B");
            b.Desired = 40;

            DrainResult after = Drain(sut, candidates);
            after.Picks.Should().NotBeEmpty();
            candidates.Single(e => e.FilterName == "R").Accepted.Should().Be(10);
            candidates.Single(e => e.FilterName == "G").Accepted.Should().Be(10);
            b.Accepted.Should().Be(40);
            after.MaxMixedRun.Should().BeLessOrEqualTo(6, "extra B should interleave while R/G remain");
        }

        [Test]
        public void testDrainEmptySeedThenMix() {
            List<IExposure> candidates = new List<IExposure> {
                MakeExposure("R", 20, 10),
                MakeExposure("G", 20, 10),
                MakeExposure("O", 12, 0),
            };
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            DrainResult drain = Drain(sut, candidates);

            drain.Picks.Take(3).Should().OnlyContain(name => name == "O");
            drain.Picks.Skip(3).Should().Contain("R").And.Contain("G");
            candidates.Single(e => e.FilterName == "O").Accepted.Should().Be(12);
            candidates.Single(e => e.FilterName == "R").Accepted.Should().Be(20);
            candidates.Single(e => e.FilterName == "G").Accepted.Should().Be(20);
        }

        private class DrainResult {
            public List<string> Picks { get; set; } = new List<string>();
            public int MaxMixedRun { get; set; }
            public bool Overshoot { get; set; }
        }

        /// <summary>
        /// Pick until every filter is at Desired (or stopAfter picks). Increments
        /// Accepted and Acquired on the chosen mock so leftovers shrink.
        /// MaxMixedRun ignores the tail once only one filter still needs frames.
        /// </summary>
        private static DrainResult Drain(ExposureRatioSelector sut, List<IExposure> candidates, int stopAfter = 0) {
            DrainResult result = new DrainResult();
            int remaining() => candidates.Sum(e => Math.Max(0, e.Desired - e.Accepted));
            int stillNeeded = remaining();
            int cap = stopAfter > 0 ? stopAfter : stillNeeded + 5;
            int mixedRun = 0;
            string prev = null;

            for (int i = 0; i < cap && remaining() > 0; i++) {
                IExposure picked = sut.Select(candidates);
                if (picked == null) {
                    break;
                }
                if (picked.Accepted >= picked.Desired) {
                    result.Overshoot = true;
                    break;
                }
                picked.Accepted++;
                picked.Acquired++;
                result.Picks.Add(picked.FilterName);

                int othersLeft = candidates
                    .Where(e => e.FilterName != picked.FilterName)
                    .Sum(e => Math.Max(0, e.Desired - e.Accepted));
                if (picked.FilterName == prev) mixedRun++;
                else mixedRun = 1;
                prev = picked.FilterName;
                if (othersLeft > 0 && mixedRun > result.MaxMixedRun) {
                    result.MaxMixedRun = mixedRun;
                }
            }
            return result;
        }

        private static List<IExposure> IrisMidProject() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 80, 79));
            candidates.Add(MakeExposure("R", 50, 26));
            candidates.Add(MakeExposure("G", 62, 26));
            candidates.Add(MakeExposure("B", 90, 26));
            return candidates;
        }

        private static List<string> CollectPicks(ExposureRatioSelector sut, List<IExposure> candidates, int count) {
            List<string> picks = new List<string>(count);
            for (int i = 0; i < count; i++) {
                IExposure result = sut.Select(candidates);
                result.Should().NotBeNull($"pick {i}");
                picks.Add(result.FilterName);
            }
            return picks;
        }

        private static int CountOf(List<string> picks, string filterName) {
            return picks.Count(name => name == filterName);
        }

        private static int MaxConsecutive(List<string> picks) {
            int max = 0;
            int current = 0;
            string previous = null;
            foreach (string name in picks) {
                if (name == previous) {
                    current++;
                } else {
                    current = 1;
                    previous = name;
                }
                if (current > max) max = current;
            }
            return max;
        }

        /// <summary>
        /// Creates a mock exposure for ratio testing.
        /// Sets both Acquired and Accepted so the ratio works regardless of grading mode.
        /// </summary>
        private static IExposure MakeExposure(string filterName, int desired, int accepted, int acquired = -1) {
            Mock<IExposure> pe = PlanMocks.GetMockPlanExposure(filterName, desired, accepted);
            pe.SetupProperty(m => m.Acquired, acquired >= 0 ? acquired : accepted);
            return pe.Object;
        }
    }
}
