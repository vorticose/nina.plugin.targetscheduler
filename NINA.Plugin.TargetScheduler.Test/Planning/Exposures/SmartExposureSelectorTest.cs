using FluentAssertions;
using Moq;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Exposures;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Test.Astrometry;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace NINA.Plugin.TargetScheduler.Test.Planning.Exposures {

    [TestFixture]
    public class SmartExposureSelectorTest {

        [SetUp]
        public void Setup() {
            DitherManagerCache.Clear();
            SmartExposureRotateCache.Clear();
        }

        [TearDown]
        public void TearDown() {
            DitherManagerCache.Clear();
            SmartExposureRotateCache.Clear();
        }

        [Test]
        public void testSmartExposureSelector() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.DitherEvery, 1);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            pp.SetupProperty(p => p.SmartExposureOrder, false);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);
            pt.Object.ExposurePlans[0].MoonAvoidanceScore = .1;
            pt.Object.ExposurePlans[1].MoonAvoidanceScore = .2;
            pt.Object.ExposurePlans[2].MoonAvoidanceScore = .3;
            pt.Object.ExposurePlans[3].MoonAvoidanceScore = .4;

            SmartExposureSelector sut = new SmartExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("B");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("B");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);
            e.Rejected = true;

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("G");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("G");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);
            e.Rejected = true;

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("R");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("R");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);
            e.Rejected = true;

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);
        }

        [Test]
        public void testRememberDither() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.DitherEvery, 2);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);

            SetAllScores(pt.Object.ExposurePlans, 0);
            pt.Object.ExposurePlans[0].MoonAvoidanceScore = 1;

            SmartExposureSelector sut = new SmartExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeFalse();
            sut.ExposureTaken(e);

            // A new selector can't forget the dither state: need LLdL
            sut = new SmartExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            e.PreDither.Should().BeTrue();
            sut.ExposureTaken(e);
        }

        private void SetAllScores(List<IExposure> plans, double score) {
            plans.ForEach(e => e.MoonAvoidanceScore = score);
        }

        [Test]
        public void testSmartExposureRotate() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            ExposureCompletionHelper helper = new ExposureCompletionHelper(true, 0, 125);

            pp.SetupAllProperties();
            pp.SetupProperty(p => p.DitherEvery, 1);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            pp.SetupProperty(p => p.FilterSwitchFrequency, 2);
            pp.SetupProperty(p => p.ExposureCompletionHelper, helper);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);
            pt.Object.ExposurePlans[0].MoonAvoidanceScore = .10; // L
            pt.Object.ExposurePlans[1].MoonAvoidanceScore = .41; // R
            pt.Object.ExposurePlans[2].MoonAvoidanceScore = .40; // G
            pt.Object.ExposurePlans[3].MoonAvoidanceScore = .40; // B

            SmartExposureSelector sut = new SmartExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("R");
            sut.ExposureTaken(e);
            e.Acquired++;
            e.Accepted++;

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("R");
            sut.ExposureTaken(e);
            e.Acquired++;
            e.Accepted++;

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("G");
            sut.ExposureTaken(e);
            e.Acquired++;
            e.Accepted++;

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("G");
            sut.ExposureTaken(e);
            e.Acquired++;
            e.Accepted++;

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("B");
            sut.ExposureTaken(e);
            e.Acquired++;
            e.Accepted++;

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("B");
            sut.ExposureTaken(e);
            e.Acquired++;
            e.Accepted++;

            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("R");
        }

        [Test]
        public void testAllExposurePlansRejected() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.DitherEvery, 1);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);
            pt.Object.ExposurePlans.ForEach(e => { e.Rejected = true; });

            SmartExposureSelector sut = new SmartExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);
            sut.Select(new DateTime(2024, 12, 1), pp.Object, pt.Object).Should().BeNull();
        }

        [Test]
        public void testSmartExposureRatioSelection() {
            // With MaintainExposureRatio enabled and tied moon scores, the most-behind filter should be selected
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.DitherEvery, 0);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            pp.SetupProperty(p => p.MaintainExposureRatio, true);
            pp.SetupProperty(p => p.FilterSwitchFrequency, 1);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);

            // All broadband filters have the same moon score (tied)
            SetAllScores(pt.Object.ExposurePlans, 0.5);

            // L is behind: 0/10 = 0%, R = 5/10 = 50%, G = 5/10 = 50%, B = 5/10 = 50%
            pt.Object.ExposurePlans[0].Accepted = 0;  // L
            pt.Object.ExposurePlans[1].Accepted = 5;  // R
            pt.Object.ExposurePlans[2].Accepted = 5;  // G
            pt.Object.ExposurePlans[3].Accepted = 5;  // B

            SmartExposureSelector sut = new SmartExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            // L should be selected because it's most behind
            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
        }

        [Test]
        public void testSmartExposureRatioDeadBandFallback() {
            // With MaintainExposureRatio enabled but all ratios within dead band,
            // should fall back to rotate manager behavior
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.DitherEvery, 0);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            pp.SetupProperty(p => p.MaintainExposureRatio, true);
            pp.SetupProperty(p => p.FilterSwitchFrequency, 1);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);

            SetAllScores(pt.Object.ExposurePlans, 0.5);

            // All within dead band: all at 50%
            pt.Object.ExposurePlans[0].Accepted = 5;  // L 50%
            pt.Object.ExposurePlans[1].Accepted = 5;  // R 50%
            pt.Object.ExposurePlans[2].Accepted = 5;  // G 50%
            pt.Object.ExposurePlans[3].Accepted = 5;  // B 50%

            SmartExposureSelector sut = new SmartExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            // Should fall back to rotate manager, which picks first in order (L)
            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.Should().NotBeNull();
            // With equal ratios, falls back to rotate manager which selects L (first)
            e.FilterName.Should().Be("L");
        }

        [Test]
        public void testSmartExposureRatioMoonPriority() {
            // Moon score should still win over ratio - higher moon score filter selected even if behind
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.DitherEvery, 0);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            pp.SetupProperty(p => p.MaintainExposureRatio, true);
            pp.SetupProperty(p => p.FilterSwitchFrequency, 1);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);

            // L has low score, R/G/B have high (tied) scores
            pt.Object.ExposurePlans[0].MoonAvoidanceScore = 0.1;  // L
            pt.Object.ExposurePlans[1].MoonAvoidanceScore = 0.5;  // R
            pt.Object.ExposurePlans[2].MoonAvoidanceScore = 0.5;  // G
            pt.Object.ExposurePlans[3].MoonAvoidanceScore = 0.5;  // B

            // L is most behind but has low moon score
            pt.Object.ExposurePlans[0].Accepted = 0;  // L 0%
            pt.Object.ExposurePlans[1].Accepted = 5;  // R 50%
            pt.Object.ExposurePlans[2].Accepted = 5;  // G 50%
            pt.Object.ExposurePlans[3].Accepted = 0;  // B 0%

            SmartExposureSelector sut = new SmartExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            // R/G/B are tied with higher moon score; among those, B is most behind (0%)
            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("B");
        }

        [Test]
        public void testSmartExposureRatioDisabled() {
            // With MaintainExposureRatio disabled, should use normal rotate behavior
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupAllProperties();
            pp.SetupProperty(p => p.DitherEvery, 0);
            pp.SetupProperty(p => p.SmartExposureOrder, true);
            pp.SetupProperty(p => p.MaintainExposureRatio, false);
            pp.SetupProperty(p => p.FilterSwitchFrequency, 1);
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.Project, pp.Object);
            SetEPs(pt);

            SetAllScores(pt.Object.ExposurePlans, 0.5);

            // L is way behind but ratio is disabled
            pt.Object.ExposurePlans[0].Accepted = 0;  // L 0%
            pt.Object.ExposurePlans[1].Accepted = 5;  // R 50%
            pt.Object.ExposurePlans[2].Accepted = 5;  // G 50%
            pt.Object.ExposurePlans[3].Accepted = 5;  // B 50%

            SmartExposureSelector sut = new SmartExposureSelector(pp.Object, pt.Object, new Target());
            pt.SetupProperty(t => t.ExposureSelector, sut);

            // Should use rotate manager (L first since it's first in order), NOT ratio-based
            IExposure e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("L");
            sut.ExposureTaken(e);

            // Rotate manager should continue to R (not stay on L for catch-up)
            e = sut.Select(DateTime.Now, pp.Object, pt.Object);
            e.FilterName.Should().Be("R");
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