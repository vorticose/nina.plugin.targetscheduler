using FluentAssertions;
using Moq;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning;
using NINA.Plugin.TargetScheduler.Planning.Exposures;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Test.Astrometry;
using NUnit.Framework;
using System;
using System.Collections.Generic;
using System.Linq;

namespace NINA.Plugin.TargetScheduler.Test.Planning.Exposures {

    /// <summary>
    /// Lifecycle coverage for the exposure-ratio leftover walk. ExposureRatioSelectorTest drives
    /// one selector against one state, which is the one lifecycle the planner never gives it:
    /// Planner.GetPlan reloads every target on every run and each PlanningTarget builds a fresh
    /// selector. On 2026-09-11 the walk state lived in selector fields, so every live pick was
    /// step one of a new walk ("largest leftover wins") and a 100/50/50/50 target shot 57 L
    /// against 3 each of R, G and B. These tests rebuild the selector per pick, the way the
    /// planner does, run the previous-target continue probe before the real pick, and check that
    /// plan previews cannot touch the live walk.
    /// </summary>
    [TestFixture]
    public class ExposureRatioWalkLifecycleTest {

        private static readonly ExposureCompletionHelper NoGrading = new ExposureCompletionHelper(false, 0, 100);

        [SetUp]
        public void Setup() {
            ExposureRatioWalkCache.Clear();
            DitherManagerCache.Clear();
            SmartExposureRotateCache.Clear();
        }

        [TearDown]
        public void TearDown() {
            if (PreviewContext.IsActive) { PreviewContext.Exit(); }
            ExposureRatioWalkCache.Clear();
            DitherManagerCache.Clear();
            SmartExposureRotateCache.Clear();
        }

        /// <summary>
        /// A brand-new selector for every pick, resolving its state through the cache, must
        /// produce exactly the sequence a single selector holding one state produces.
        /// </summary>
        [TestCaseSource(typeof(ExposureRatioSelectorTest), nameof(ExposureRatioSelectorTest.DrainCases))]
        public void testFreshSelectorPerPickMatchesSingleState(ExposureRatioSelectorTest.DrainCase c) {
            List<IExposure> reference = c.Start.Select(row => MakeExposure(row.Filter, row.Desired, row.Accepted)).ToList();
            List<string> expected = DrainWithExplicitState(reference, NoGrading);
            expected.Should().NotBeEmpty();

            List<IExposure> candidates = c.Start.Select(row => MakeExposure(row.Filter, row.Desired, row.Accepted)).ToList();
            ITarget target = MockTarget(60);
            List<string> actual = new List<string>();
            for (int i = 0; i < expected.Count; i++) {
                ExposureRatioSelector fresh = new ExposureRatioSelector(target, NoGrading);
                IExposure picked = fresh.Select(candidates);
                picked.Should().NotBeNull($"{c.Name}: pick {i}");
                fresh.ExposureTaken(picked);
                picked.Accepted++;
                picked.Acquired++;
                actual.Add(picked.FilterName);
            }

            actual.Should().Equal(expected, $"{c.Name}: a fresh selector per pick must continue the cached walk");
        }

        /// <summary>
        /// The field bug: 100/50/50/50 from zero, fresh selector per pick. Before the cache this
        /// was L, R, G, B and then L for the rest of the night. With the walk persisted it is the
        /// designed sequence: L, then the 3-frame empty-filter seed of R, G and B, then a 2:1:1:1
        /// interleave.
        /// </summary>
        [Test]
        public void testFieldBugFreshSelectorPerPickInterleaves() {
            List<IExposure> candidates = Gecko();
            ITarget target = MockTarget(60);

            List<string> picks = new List<string>();
            for (int i = 0; i < 40; i++) {
                ExposureRatioSelector fresh = new ExposureRatioSelector(target, NoGrading);
                IExposure picked = fresh.Select(candidates);
                picked.Should().NotBeNull();
                fresh.ExposureTaken(picked);
                picked.Accepted++;
                picked.Acquired++;
                picks.Add(picked.FilterName);
            }

            AssertColdStartGeckoShape(picks);
        }

        /// <summary>
        /// PreviousTargetExpert.CanContinue calls Select on the retained previous target, then
        /// the full plan calls Select again on a fresh target, and one exposure is taken. Two
        /// Selects per exposure must not advance the walk twice.
        /// </summary>
        [Test]
        public void testProbeSelectBeforePlanDoesNotConsumeWalkStep() {
            List<IExposure> a = Iris();
            ExposureRatioWalkState stateA = new ExposureRatioWalkState();
            ExposureRatioSelector sutA = new ExposureRatioSelector(NoGrading);
            List<string> single = new List<string>();
            for (int i = 0; i < 25; i++) {
                IExposure picked = sutA.Select(a, stateA);
                sutA.ExposureTaken(picked, stateA);
                single.Add(picked.FilterName);
            }

            List<IExposure> b = Iris();
            ExposureRatioWalkState stateB = new ExposureRatioWalkState();
            ExposureRatioSelector sutB = new ExposureRatioSelector(NoGrading);
            List<string> probed = new List<string>();
            for (int i = 0; i < 25; i++) {
                IExposure probe = sutB.Select(b, stateB);      // continue-check probe, result discarded
                IExposure picked = sutB.Select(b, stateB);     // the real plan
                picked.FilterName.Should().Be(probe.FilterName, "Select is a pure function of state and counts");
                sutB.ExposureTaken(picked, stateB);
                probed.Add(picked.FilterName);
            }

            probed.Should().Equal(single);
        }

        /// <summary>
        /// If the host takes an exposure the walk did not propose (equal-desired path, stock
        /// rotation fallback, twilight override) the walk must not move.
        /// </summary>
        [Test]
        public void testExposureTakenOfUnproposedFilterLeavesWalkUntouched() {
            List<IExposure> candidates = Iris();
            ExposureRatioWalkState state = new ExposureRatioWalkState();
            ExposureRatioSelector sut = new ExposureRatioSelector(NoGrading);

            IExposure first = sut.Select(candidates, state);
            IExposure other = candidates.First(e => e.FilterName != first.FilterName);
            sut.ExposureTaken(other, state);

            sut.Select(candidates, state).FilterName.Should().Be(first.FilterName);
        }

        /// <summary>
        /// A selector with no target bound cannot resolve state from the cache. Failing loudly
        /// here is what stops a future caller from silently getting per-instance state back.
        /// </summary>
        [Test]
        public void testCacheResolvedSelectRequiresTarget() {
            ExposureRatioSelector sut = new ExposureRatioSelector(NoGrading);
            Action act = () => sut.Select(Iris());
            act.Should().Throw<InvalidOperationException>();
        }

        /// <summary>Same scratch-cache guarantee as the dither and rotation caches.</summary>
        [Test]
        public void testWalkCacheIsolatesPreviewFromLive() {
            ITarget target = MockTarget(42);

            ExposureRatioWalkState live = ExposureRatioWalkCache.GetOrCreate(target);
            live.CreatedInPreview.Should().BeFalse();

            PreviewContext.Enter();
            try {
                ExposureRatioWalkCache.Get(target).Should().BeNull("a preview must start from an empty scratch cache");
                ExposureRatioWalkState preview = ExposureRatioWalkCache.GetOrCreate(target);
                preview.CreatedInPreview.Should().BeTrue();
                ExposureRatioWalkCache.Get(target).Should().BeSameAs(preview);
            } finally {
                PreviewContext.Exit();
            }

            ExposureRatioWalkCache.Get(target).Should().BeSameAs(live, "live walk state must survive the preview untouched");
        }

        /// <summary>
        /// A plan preview simulating a whole night on the same target id, with fresh selectors
        /// built outside the preview context as LoadActiveProjects builds them, must not move
        /// the live walk: live picks after the preview continue exactly where they left off.
        /// </summary>
        [Test]
        public void testPreviewDoesNotAdvanceLiveWalk() {
            List<string> expected = new List<string>();
            {
                List<IExposure> reference = Gecko();
                ExposureRatioWalkState state = new ExposureRatioWalkState();
                ExposureRatioSelector sut = new ExposureRatioSelector(NoGrading);
                for (int i = 0; i < 9; i++) {
                    IExposure picked = sut.Select(reference, state);
                    sut.ExposureTaken(picked, state);
                    picked.Accepted++;
                    picked.Acquired++;
                    expected.Add(picked.FilterName);
                }
            }

            List<IExposure> live = Gecko();
            ITarget liveTarget = MockTarget(60);
            List<string> actual = new List<string>();
            for (int i = 0; i < 3; i++) {
                actual.Add(TakeOne(liveTarget, live));
            }

            List<IExposure> previewPlans = Gecko();
            foreach (IExposure e in previewPlans) { e.Accepted = 20; e.Acquired = 20; }
            Mock<ITarget> previewTarget = PlanMocks.GetMockPlanTarget("Preview", TestData.M31);
            previewTarget.SetupProperty(t => t.DatabaseId, 60);
            previewTarget.SetupProperty(t => t.IsPreview, true);
            ExposureRatioSelector builtBeforeContext = new ExposureRatioSelector(previewTarget.Object, NoGrading);

            PreviewContext.Enter();
            try {
                for (int i = 0; i < 10; i++) {
                    IExposure picked = builtBeforeContext.Select(previewPlans);
                    picked.Should().NotBeNull();
                    builtBeforeContext.ExposureTaken(picked);
                    picked.Accepted++;
                    picked.Acquired++;
                }
                ExposureRatioWalkCache.Get(previewTarget.Object).CreatedInPreview.Should().BeTrue();
            } finally {
                PreviewContext.Exit();
            }

            for (int i = 3; i < 9; i++) {
                actual.Add(TakeOne(liveTarget, live));
            }

            actual.Should().Equal(expected, "a plan preview must not advance the live leftover walk");
            ExposureRatioWalkCache.Get(liveTarget).CreatedInPreview.Should().BeFalse();
        }

        /// <summary>
        /// End to end through SmartExposureSelector the way the live planner drives it: a new
        /// selector per run, the previous run's selector probed first (CanContinue), one
        /// ExposureTaken per run. 100/50/50/50 with tied moon scores must seed and then interleave.
        /// </summary>
        [Test]
        public void testSmartSelectorFreshPerRunWithContinueProbeInterleaves() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupProperty(p => p.DitherEvery, 0);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            pp.SetupProperty(p => p.MaintainExposureRatio, true);
            pp.SetupProperty(p => p.FilterSwitchFrequency, 1);
            pp.SetupProperty(p => p.EnableGrader, false);
            pp.SetupProperty(p => p.ExposureCompletionHelper, NoGrading);

            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("Gecko", TestData.M31);
            pt.SetupProperty(t => t.DatabaseId, 60);
            pt.SetupProperty(t => t.Project, pp.Object);
            (string filter, int desired, int id)[] plans = new[] { ("L", 100, 1), ("R", 50, 2), ("G", 50, 3), ("B", 50, 4) };
            foreach (var p in plans) {
                Mock<IExposure> ep = PlanMocks.GetMockPlanExposure(p.filter, p.desired, 0, p.id);
                PlanMocks.AddMockPlanFilter(pt, ep);
                ep.Object.MoonAvoidanceScore = 1.0;
            }

            List<string> picks = new List<string>();
            SmartExposureSelector previous = null;
            for (int run = 0; run < 40; run++) {
                if (previous != null) {
                    previous.Select(DateTime.Now, pp.Object, pt.Object);   // PreviousTargetExpert.CanContinue probe
                }
                SmartExposureSelector fresh = new SmartExposureSelector(pp.Object, pt.Object, new Target());
                IExposure picked = fresh.Select(DateTime.Now, pp.Object, pt.Object);
                picked.Should().NotBeNull($"run {run}");
                fresh.ExposureTaken(picked);
                picked.Acquired++;
                picked.Accepted++;
                picks.Add(picked.FilterName);
                previous = fresh;
            }

            AssertColdStartGeckoShape(picks);
        }

        // ---------------------------------------------------------------------

        /// <summary>
        /// 100/50/50/50 from zero: pick 0 is L (largest leftover), picks 1-9 are the 3-frame
        /// empty-filter seed of R, G and B, and the 30 walk picks after that split about 2:1:1:1
        /// with no filter repeated more than three times in a row.
        /// </summary>
        private static void AssertColdStartGeckoShape(List<string> picks) {
            picks.Should().HaveCount(40);
            picks[0].Should().Be("L", "cold start walks and L has the largest leftover");
            List<string> seed = picks.Take(10).ToList();
            CountOf(seed, "L").Should().Be(1);
            CountOf(seed, "R").Should().Be(3, "a never-shot filter gets a 3-frame seed once another filter has started");
            CountOf(seed, "G").Should().Be(3);
            CountOf(seed, "B").Should().Be(3);
            List<string> walk = picks.Skip(10).ToList();
            CountOf(walk, "L").Should().BeInRange(10, 14, "2:1:1:1 over the walk picks after the seed");
            CountOf(walk, "R").Should().BeGreaterOrEqualTo(5);
            CountOf(walk, "G").Should().BeGreaterOrEqualTo(5);
            CountOf(walk, "B").Should().BeGreaterOrEqualTo(5);
            MaxConsecutive(picks).Should().BeLessOrEqualTo(3);
        }

        private static string TakeOne(ITarget target, List<IExposure> candidates) {
            ExposureRatioSelector fresh = new ExposureRatioSelector(target, NoGrading);
            IExposure picked = fresh.Select(candidates);
            picked.Should().NotBeNull();
            fresh.ExposureTaken(picked);
            picked.Accepted++;
            picked.Acquired++;
            return picked.FilterName;
        }

        private static List<string> DrainWithExplicitState(List<IExposure> candidates, ExposureCompletionHelper helper) {
            ExposureRatioWalkState state = new ExposureRatioWalkState();
            ExposureRatioSelector sut = new ExposureRatioSelector(helper);
            List<string> picks = new List<string>();
            int needed = candidates.Sum(e => Math.Max(0, e.Desired - e.Accepted));
            for (int i = 0; i < needed + 5; i++) {
                IExposure picked = sut.Select(candidates, state);
                if (picked == null) break;
                sut.ExposureTaken(picked, state);
                picked.Accepted++;
                picked.Acquired++;
                picks.Add(picked.FilterName);
            }
            return picks;
        }

        private static List<IExposure> Gecko() {
            return new List<IExposure> {
                MakeExposure("L", 100, 0),
                MakeExposure("R", 50, 0),
                MakeExposure("G", 50, 0),
                MakeExposure("B", 50, 0),
            };
        }

        private static List<IExposure> Iris() {
            return new List<IExposure> {
                MakeExposure("L", 80, 79),
                MakeExposure("R", 50, 26),
                MakeExposure("G", 62, 26),
                MakeExposure("B", 90, 26),
            };
        }

        private static ITarget MockTarget(int databaseId) {
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T" + databaseId, TestData.M31);
            pt.SetupProperty(t => t.DatabaseId, databaseId);
            return pt.Object;
        }

        private static IExposure MakeExposure(string filterName, int desired, int accepted) {
            Mock<IExposure> pe = PlanMocks.GetMockPlanExposure(filterName, desired, accepted);
            pe.SetupProperty(m => m.Acquired, accepted);
            return pe.Object;
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
    }
}
