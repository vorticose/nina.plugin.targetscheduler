using FluentAssertions;
using LinqKit;
using Moq;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Exposures;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Test.Astrometry;
using NUnit.Framework;
using System.Collections.Generic;

namespace NINA.Plugin.TargetScheduler.Test.Planning.Exposures {

    [TestFixture]
    public class SmartExposureRotateManagerTest {

        [SetUp]
        public void Setup() {
            SmartExposureRotateCache.Clear();
        }

        [TearDown]
        public void TearDown() {
            SmartExposureRotateCache.Clear();
        }

        [Test]
        public void testAllActive() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            ExposureCompletionHelper helper = new ExposureCompletionHelper(true, 0, 125);

            pp.SetupAllProperties();
            pp.SetupProperty(p => p.DitherEvery, 1);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            pp.SetupProperty(p => p.FilterSwitchFrequency, 2);
            pp.SetupProperty(p => p.ExposureCompletionHelper, helper);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            pt.SetupProperty(t => t.DatabaseId, 1);
            SetEPs(pt);

            string[] expected = { "L", "L", "R", "R", "G", "G", "B", "B",
                                  "L", "L", "R", "R", "G", "G", "B", "B",
                                  "L", "L", "R", "R", "G", "G", "B", "B" };
            SmartExposureRotateManager sut = new SmartExposureRotateManager(pt.Object, 2);

            expected.ForEach(e => {
                IExposure selected = sut.Select(pt.Object.ExposurePlans);
                selected.FilterName.Should().Be(e);
                sut.ExposureTaken(selected);
            });
        }

        [Test]
        public void testSomeActive() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            ExposureCompletionHelper helper = new ExposureCompletionHelper(true, 0, 125);

            pp.SetupAllProperties();
            pp.SetupProperty(p => p.DitherEvery, 1);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            pp.SetupProperty(p => p.FilterSwitchFrequency, 2);
            pp.SetupProperty(p => p.ExposureCompletionHelper, helper);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            pt.SetupProperty(t => t.DatabaseId, 1);
            SetEPs(pt);

            string[] expected1 = { "L", "L", "G", "G", "L", "L", "G", "G", "L", "L", "G", "G" };
            List<IExposure> actives = new List<IExposure> { pt.Object.ExposurePlans[0], pt.Object.ExposurePlans[2] };
            SmartExposureRotateManager sut = new SmartExposureRotateManager(pt.Object, 2);

            expected1.ForEach(e => {
                IExposure selected = sut.Select(actives);
                selected.FilterName.Should().Be(e);
                sut.ExposureTaken(selected);
            });

            string[] expected2 = { "R", "R", "B", "B", "R", "R", "B", "B", "R", "R", "B", "B" };
            actives = new List<IExposure> { pt.Object.ExposurePlans[1], pt.Object.ExposurePlans[3] };

            expected2.ForEach(e => {
                IExposure selected = sut.Select(actives);
                selected.FilterName.Should().Be(e);
                sut.ExposureTaken(selected);
            });
        }

        [Test]
        public void testPreviewDoesNotCorruptLiveRotation() {
            // **CUSTOM FORK** Regression for the S,S,S filter jam observed on the rig 2026-06-28: running the
            // preview/explain emulation (IsPreview targets) must NOT mutate the live rotation state. Live and
            // preview share the same DatabaseId but must use separate rotation cache entries.
            Mock<ITarget> live = GetSHTarget(isPreview: false);
            Mock<ITarget> preview = GetSHTarget(isPreview: true);

            SmartExposureRotateManager liveMgr = new SmartExposureRotateManager(live.Object, 1);
            SmartExposureRotateManager previewMgr = new SmartExposureRotateManager(preview.Object, 1);

            // Live takes its first frame: S (first plan in rotation order).
            IExposure first = liveMgr.Select(live.Object.ExposurePlans);
            first.FilterName.Should().Be("S");
            liveMgr.ExposureTaken(first);

            // Preview emulates a full night, hammering ExposureTaken many times against the same DatabaseId.
            for (int i = 0; i < 25; i++) {
                IExposure pe = previewMgr.Select(preview.Object.ExposurePlans);
                previewMgr.ExposureTaken(pe);
            }

            // Live must still alternate: the next live frame is H, not another S.
            IExposure second = liveMgr.Select(live.Object.ExposurePlans);
            second.FilterName.Should().Be("H");
            liveMgr.ExposureTaken(second);

            liveMgr.Select(live.Object.ExposurePlans).FilterName.Should().Be("S");
        }

        private Mock<ITarget> GetSHTarget(bool isPreview) {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            ExposureCompletionHelper helper = new ExposureCompletionHelper(true, 0, 125);

            pp.SetupAllProperties();
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            pp.SetupProperty(p => p.FilterSwitchFrequency, 1);
            pp.SetupProperty(p => p.ExposureCompletionHelper, helper);

            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            pt.SetupProperty(t => t.DatabaseId, 1);
            pt.SetupProperty(t => t.IsPreview, isPreview);

            Mock<IExposure> Spf = PlanMocks.GetMockPlanExposure("S", 10, 0);
            Mock<IExposure> Hpf = PlanMocks.GetMockPlanExposure("H", 10, 0);
            Spf.SetupProperty(e => e.DatabaseId, 1);
            Hpf.SetupProperty(e => e.DatabaseId, 2);
            PlanMocks.AddMockPlanFilter(pt, Spf);
            PlanMocks.AddMockPlanFilter(pt, Hpf);
            return pt;
        }

        private void SetEPs(Mock<ITarget> pt) {
            Mock<IExposure> Lpf = PlanMocks.GetMockPlanExposure("L", 10, 0);
            Mock<IExposure> Rpf = PlanMocks.GetMockPlanExposure("R", 10, 0);
            Mock<IExposure> Gpf = PlanMocks.GetMockPlanExposure("G", 10, 0);
            Mock<IExposure> Bpf = PlanMocks.GetMockPlanExposure("B", 10, 0);

            Lpf.SetupProperty(e => e.DatabaseId, 1);
            Rpf.SetupProperty(e => e.DatabaseId, 2);
            Gpf.SetupProperty(e => e.DatabaseId, 3);
            Bpf.SetupProperty(e => e.DatabaseId, 4);

            PlanMocks.AddMockPlanFilter(pt, Lpf);
            PlanMocks.AddMockPlanFilter(pt, Rpf);
            PlanMocks.AddMockPlanFilter(pt, Gpf);
            PlanMocks.AddMockPlanFilter(pt, Bpf);
        }
    }
}