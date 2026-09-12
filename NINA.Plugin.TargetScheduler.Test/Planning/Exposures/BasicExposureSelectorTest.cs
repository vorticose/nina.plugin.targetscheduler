using FluentAssertions;
using Moq;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Exposures;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Test.Astrometry;
using NUnit.Framework;
using System;

namespace NINA.Plugin.TargetScheduler.Test.Planning.Exposures {

    [TestFixture]
    public class BasicExposureSelectorTest {

        // These tests exercise dither cadence through the shared static DitherManagerCache
        // (keyed by target DatabaseId, here 0). Without clearing it between tests, cadence state
        // leaks across tests and makes pass/fail depend on execution order. Clear per test so the
        // dither assertions are deterministic. (SmartExposureSelectorTest does the same.)
        [SetUp]
        public void Setup() {
            DitherManagerCache.Clear();
            SmartExposureRotateCache.Clear();
            ExposureRatioWalkCache.Clear();
        }

        [TearDown]
        public void TearDown() {
            DitherManagerCache.Clear();
            SmartExposureRotateCache.Clear();
            ExposureRatioWalkCache.Clear();
        }

        [Test]
        public void testBasicExposureSelector() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.FilterSwitchFrequency, 2);
            pp.SetupProperty(p => p.DitherEvery, 1);
            pp.SetupProperty(p => p.SmartExposureOrder, false);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);

            BasicExposureSelector sut = new BasicExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("R");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("R");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("G");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("G");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("B");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("B");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeFalse();
        }

        [Test]
        public void testForOverDither() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.FilterSwitchFrequency, 2);
            pp.SetupProperty(p => p.DitherEvery, 1);
            pp.SetupProperty(p => p.SmartExposureOrder, false);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);

            BasicExposureSelector sut = new BasicExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("R");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("R");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("G");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("G");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("B");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("B");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            pt.Object.ExposurePlans.ForEach(e => { if (e.FilterName != "L") e.Rejected = true; });
            sut.TargetReset();

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeTrue();
        }

        [Test]
        public void testAllExposurePlansRejected() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.FilterSwitchFrequency, 2);
            pp.SetupProperty(p => p.DitherEvery, 1);
            pp.SetupProperty(p => p.SmartExposureOrder, false);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);
            pt.Object.ExposurePlans.ForEach(e => { e.Rejected = true; });

            BasicExposureSelector sut = new BasicExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);
            sut.Select(new DateTime(2024, 12, 1), pp.Object, pt.Object).Should().BeNull();
        }

        [Test]
        public void testBasicExposureRatioSelection() {
            // With MaintainExposureRatio enabled, the most-behind filter should be selected
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.FilterSwitchFrequency, 2);
            pp.SetupProperty(p => p.DitherEvery, 0);
            pp.SetupProperty(p => p.SmartExposureOrder, false);
            pp.SetupProperty(p => p.MaintainExposureRatio, true);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);

            // B is behind: L=5/10=50%, R=5/10=50%, G=5/10=50%, B=0/10=0%
            pt.Object.ExposurePlans[0].Accepted = 5;  // L
            pt.Object.ExposurePlans[1].Accepted = 5;  // R
            pt.Object.ExposurePlans[2].Accepted = 5;  // G
            pt.Object.ExposurePlans[3].Accepted = 0;  // B

            BasicExposureSelector sut = new BasicExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            // B should be selected because it's most behind
            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("B");
        }

        [Test]
        public void testBasicExposureRatioDeadBandFallback() {
            // With all ratios within dead band, should fall back to cadence
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.FilterSwitchFrequency, 2);
            pp.SetupProperty(p => p.DitherEvery, 0);
            pp.SetupProperty(p => p.SmartExposureOrder, false);
            pp.SetupProperty(p => p.MaintainExposureRatio, true);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);

            // All equal: 50%
            pt.Object.ExposurePlans[0].Accepted = 5;
            pt.Object.ExposurePlans[1].Accepted = 5;
            pt.Object.ExposurePlans[2].Accepted = 5;
            pt.Object.ExposurePlans[3].Accepted = 5;

            BasicExposureSelector sut = new BasicExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            // Should fall back to cadence, which starts with L
            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
        }

        [Test]
        public void testBasicExposureRatioDisabled() {
            // With MaintainExposureRatio disabled, should use normal cadence
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.FilterSwitchFrequency, 2);
            pp.SetupProperty(p => p.DitherEvery, 0);
            pp.SetupProperty(p => p.SmartExposureOrder, false);
            pp.SetupProperty(p => p.MaintainExposureRatio, false);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);

            // B is behind but ratio is disabled
            pt.Object.ExposurePlans[0].Accepted = 5;
            pt.Object.ExposurePlans[1].Accepted = 5;
            pt.Object.ExposurePlans[2].Accepted = 5;
            pt.Object.ExposurePlans[3].Accepted = 0;

            BasicExposureSelector sut = new BasicExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            // Should follow cadence order (L first), not ratio
            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
        }

        private void SetEPs(Mock<ITarget> pt) {
            Mock<IExposure> Lpf = PlanMocks.GetMockPlanExposure("L", 10, 0);
            Mock<IExposure> Rpf = PlanMocks.GetMockPlanExposure("R", 10, 0);
            Mock<IExposure> Gpf = PlanMocks.GetMockPlanExposure("G", 10, 0);
            Mock<IExposure> Bpf = PlanMocks.GetMockPlanExposure("B", 10, 0);

            PlanMocks.AddMockPlanFilter(pt, Lpf);
            PlanMocks.AddMockPlanFilter(pt, Rpf);
            PlanMocks.AddMockPlanFilter(pt, Gpf);
            PlanMocks.AddMockPlanFilter(pt, Bpf);
        }
    }
}