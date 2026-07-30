using FluentAssertions;
using Moq;
using NINA.Plugin.TargetScheduler.Astrometry;
using NINA.Plugin.TargetScheduler.Planning;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Test.Astrometry;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace NINA.Plugin.TargetScheduler.Test.Planning {

    [TestFixture]
    public class MoonAvoidanceDetailTest {
        private static readonly DateTime AT_TIME = new DateTime(2024, 1, 17, 20, 0, 0);

        [Test]
        public void testDisabled() {
            IExposure exposure = GetExposure(false, 120, 14, 0, 5, -15, false);
            MoonAvoidanceDetail detail = MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, 20, 14, 20);

            detail.Outcome.Should().Be(MoonAvoidanceOutcome.Disabled);
            detail.Rejected.Should().BeFalse();
            detail.Evaluated.Should().BeFalse();
            detail.MaximumPassingSeparationSetting.Should().BeNull();
            detail.StatusText.Should().Be("avoidance off");
        }

        [Test]
        public void testBlocked() {
            IExposure exposure = GetExposure(true, 120, 14, 0, 5, -15, false);
            MoonAvoidanceDetail detail = MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, 20, 14, 20);

            detail.Outcome.Should().Be(MoonAvoidanceOutcome.Blocked);
            detail.Rejected.Should().BeTrue();
            detail.AtTime.Should().Be(AT_TIME);
            detail.MoonAltitude.Should().Be(20);
            detail.MoonAge.Should().Be(14);
            detail.MoonSeparation.Should().Be(20);
            detail.RelaxationApplied.Should().BeFalse();
            detail.BaseSeparationParameter.Should().Be(120);
            detail.SeparationParameter.Should().Be(120);
            detail.BaseWidthParameter.Should().Be(14);
            detail.WidthParameter.Should().Be(14);

            double expectedRequired = AstrometryUtils.GetMoonAvoidanceLorentzianSeparation(14, 120, 14);
            detail.RequiredSeparation.Should().BeApproximately(expectedRequired, 0.001);
            detail.Margin.Should().BeApproximately(20 - expectedRequired, 0.001);
        }

        [Test]
        public void testClear() {
            IExposure exposure = GetExposure(true, 120, 14, 0, 5, -15, false);
            MoonAvoidanceDetail detail = MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, 20, 14, 130);

            detail.Outcome.Should().Be(MoonAvoidanceOutcome.Clear);
            detail.Rejected.Should().BeFalse();
            detail.Evaluated.Should().BeTrue();
            detail.Margin.Should().BeGreaterThan(0);

            // No separation change is needed when it's already clear
            detail.MaximumPassingSeparationSetting.Should().BeNull();
        }

        [Test]
        public void testMoonDownBlocked() {
            IExposure exposure = GetExposure(true, 120, 14, 2, 5, -15, true);
            MoonAvoidanceDetail detail = MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, 6, 14, 170);

            detail.Outcome.Should().Be(MoonAvoidanceOutcome.MoonDownBlocked);
            detail.Rejected.Should().BeTrue();

            // Separation is irrelevant when Moon Must Be Down did the rejecting
            detail.MaximumPassingSeparationSetting.Should().BeNull();
            detail.StatusText.Should().Be("blocked: moon must be down");
        }

        [Test]
        public void testRelaxedOff() {
            IExposure exposure = GetExposure(true, 120, 14, 2, 5, -15, false);
            MoonAvoidanceDetail detail = MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, -16, 14, 5);

            detail.Outcome.Should().Be(MoonAvoidanceOutcome.RelaxedOff);
            detail.Rejected.Should().BeFalse();
            detail.RelaxationApplied.Should().BeTrue();
        }

        [Test]
        public void testRelaxedToZero() {
            // relaxScale of 20 with the moon 10 degrees below relax max drives separation to 120 - 200
            IExposure exposure = GetExposure(true, 120, 14, 20, 5, -15, false);
            MoonAvoidanceDetail detail = MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, -5, 14, 5);

            detail.Outcome.Should().Be(MoonAvoidanceOutcome.RelaxedToZero);
            detail.Rejected.Should().BeFalse();
            detail.SeparationParameter.Should().BeLessThan(0);
        }

        [Test]
        public void testRelaxationModulatesParameters() {
            IExposure exposure = GetExposure(true, 120, 14, 2, 5, -15, false);
            MoonAvoidanceDetail detail = MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, 0, 14, 112);

            detail.RelaxationApplied.Should().BeTrue();
            detail.BaseSeparationParameter.Should().Be(120);
            detail.SeparationParameter.Should().BeApproximately(120 + (2 * (0 - 5)), 0.001);
            detail.BaseWidthParameter.Should().Be(14);
            detail.WidthParameter.Should().BeApproximately(14 * ((0 - -15) / (5.0 - -15)), 0.001);
            detail.Outcome.Should().Be(MoonAvoidanceOutcome.Clear);
        }

        /// <summary>
        /// The suggested separation setting is the whole point of the analysis: setting the exposure template to
        /// that value must actually unblock the exposure, and anything above it must still be blocked.
        /// </summary>
        [Test]
        [TestCase(120, 14, 0.0, 14.0, 40.0)]
        [TestCase(120, 14, 0.0, 10.0, 40.0)]
        [TestCase(90, 7, 0.0, 14.8, 25.0)]
        [TestCase(180, 14, 0.0, 14.0, 5.0)]
        public void testSuggestedSeparationRoundTripsWithoutRelaxation(double separation, int width, double relaxScale,
            double moonAge, double moonSeparation) {
            IExposure exposure = GetExposure(true, separation, width, relaxScale, 5, -15, false);
            MoonAvoidanceDetail detail = MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, 20, moonAge, moonSeparation);

            detail.Outcome.Should().Be(MoonAvoidanceOutcome.Blocked);
            double suggested = (double)detail.MaximumPassingSeparationSetting;
            suggested.Should().BeLessThan(separation);

            AssertRoundTrip(exposure, suggested, 20, moonAge, moonSeparation);
        }

        [Test]
        [TestCase(120, 14, 2.0, 14.0, 40.0)]
        [TestCase(120, 14, 5.0, 12.0, 60.0)]
        [TestCase(150, 10, 1.5, 14.8, 30.0)]
        public void testSuggestedSeparationRoundTripsWithRelaxation(double separation, int width, double relaxScale,
            double moonAge, double moonSeparation) {
            // Moon altitude of 0 is inside the relaxation zone (relax max is 5)
            IExposure exposure = GetExposure(true, separation, width, relaxScale, 5, -15, false);
            MoonAvoidanceDetail detail = MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, 0, moonAge, moonSeparation);

            detail.Outcome.Should().Be(MoonAvoidanceOutcome.Blocked);
            detail.RelaxationApplied.Should().BeTrue();

            double suggested = (double)detail.MaximumPassingSeparationSetting;
            AssertRoundTrip(exposure, suggested, 0, moonAge, moonSeparation);
        }

        /// <summary>
        /// Applying the suggested value clears the exposure; nudging it up again re-blocks it.
        /// </summary>
        private void AssertRoundTrip(IExposure exposure, double suggested, double moonAltitude, double moonAge,
            double moonSeparation) {
            exposure.MoonAvoidanceSeparation = suggested;
            MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, moonAltitude, moonAge, moonSeparation)
                .Rejected.Should().BeFalse("separation of {0} should clear", suggested);

            exposure.MoonAvoidanceSeparation = suggested + 1;
            MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, moonAltitude, moonAge, moonSeparation)
                .Rejected.Should().BeTrue("separation of {0} should still be blocked", suggested + 1);
        }

        [Test]
        public void testIsRejectedRecordsDetailOnExposure() {
            Mock<ITarget> target = new Mock<ITarget>();
            target.SetupAllProperties();
            target.SetupProperty(m => m.Name, "T1");

            IExposure exposure = GetExposure(true, 120, 14, 0, 5, -15, false);
            MoonAvoidanceExpert sut = new MoonAvoidanceExpertMock(TestData.North_Mid_Lat) { Altitude = 20, MoonAge = 14, SeparationAngle = 20 };

            sut.IsRejected(AT_TIME, target.Object, exposure).Should().BeTrue();

            exposure.MoonAvoidanceDetail.Should().NotBeNull();
            exposure.MoonAvoidanceDetail.Outcome.Should().Be(MoonAvoidanceOutcome.Blocked);
            exposure.MoonAvoidanceDetail.MoonAltitude.Should().Be(20);
            exposure.MoonAvoidanceDetail.MoonAge.Should().Be(14);
            exposure.MoonAvoidanceDetail.MoonSeparation.Should().Be(20);
        }

        [Test]
        public void testIsRejectedRecordsDisabledDetail() {
            Mock<ITarget> target = new Mock<ITarget>();
            target.SetupAllProperties();

            IExposure exposure = GetExposure(false, 120, 14, 0, 5, -15, false);
            MoonAvoidanceExpert sut = new MoonAvoidanceExpertMock(TestData.North_Mid_Lat) { Altitude = 20, MoonAge = 14, SeparationAngle = 20 };

            sut.IsRejected(AT_TIME, target.Object, exposure).Should().BeFalse();
            exposure.MoonAvoidanceDetail.Outcome.Should().Be(MoonAvoidanceOutcome.Disabled);
        }

        [Test]
        public void testApproximateMoonIllumination() {
            IExposure exposure = GetExposure(true, 120, 14, 0, 5, -15, false);

            MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, 20, 0, 20)
                .ApproximateMoonIllumination.Should().BeApproximately(0, 0.001);
            MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, 20, AstrometryUtils.DAYS_IN_LUNAR_CYCLE / 2, 20)
                .ApproximateMoonIllumination.Should().BeApproximately(1, 0.001);
            MoonAvoidanceExpert.Evaluate(AT_TIME, exposure, 20, AstrometryUtils.DAYS_IN_LUNAR_CYCLE / 4, 20)
                .ApproximateMoonIllumination.Should().BeApproximately(0.5, 0.001);
        }

        [Test]
        [TestCase(0, "New")]
        [TestCase(7.4, "First Quarter")]
        [TestCase(14.8, "Full")]
        [TestCase(22.1, "Last Quarter")]
        [TestCase(29.5, "New")]
        public void testMoonPhaseName(double moonAge, string expected) {
            AstrometryUtils.GetMoonPhaseName(moonAge).Should().Be(expected);
        }

        private IExposure GetExposure(bool avoidanceEnabled, double separation, int width, double relaxScale,
            double relaxMaxAlt, double relaxMinAlt, bool moonDownEnabled) {
            Mock<IExposure> pe = new Mock<IExposure>();
            pe.SetupAllProperties();
            pe.SetupProperty(m => m.MoonAvoidanceEnabled, avoidanceEnabled);
            pe.SetupProperty(m => m.MoonAvoidanceSeparation, separation);
            pe.SetupProperty(m => m.MoonAvoidanceWidth, width);
            pe.SetupProperty(m => m.MoonRelaxScale, relaxScale);
            pe.SetupProperty(m => m.MoonRelaxMaxAltitude, relaxMaxAlt);
            pe.SetupProperty(m => m.MoonRelaxMinAltitude, relaxMinAlt);
            pe.SetupProperty(m => m.MoonDownEnabled, moonDownEnabled);
            pe.SetupProperty(m => m.FilterName, "FLT");
            return pe.Object;
        }
    }

    /// <summary>
    /// Moon-free time is the number that decides what can be shot broadband, so it gets direct coverage of
    /// each way the moon can straddle astronomical night.
    /// </summary>
    [TestFixture]
    public class MoonFreeTimeTest {
        private static readonly DateTime DUSK = new DateTime(2026, 7, 30, 22, 0, 0);
        private static readonly DateTime DAWN = new DateTime(2026, 7, 31, 4, 0, 0);

        private TimeInterval AstroNight => new TimeInterval(DUSK, DAWN);

        [Test]
        public void testMoonDownAllNight() {
            MoonAvoidanceAnalyzer.CalculateMoonFreeTime(AstroNight, false, new List<MoonCrossing>())
                .Should().Be(TimeSpan.FromHours(6));
        }

        [Test]
        public void testMoonUpAllNight() {
            MoonAvoidanceAnalyzer.CalculateMoonFreeTime(AstroNight, true, new List<MoonCrossing>())
                .Should().Be(TimeSpan.Zero);
        }

        [Test]
        public void testMoonSetsDuringNight() {
            // Up at dusk, sets at 01:00 -> 3 hours moon-free before dawn
            List<MoonCrossing> crossings = new List<MoonCrossing> {
                new MoonCrossing(new DateTime(2026, 7, 31, 1, 0, 0), false)
            };
            MoonAvoidanceAnalyzer.CalculateMoonFreeTime(AstroNight, true, crossings)
                .Should().Be(TimeSpan.FromHours(3));
        }

        [Test]
        public void testMoonRisesDuringNight() {
            // Down at dusk, rises at 00:30 -> 2.5 hours moon-free at the start
            List<MoonCrossing> crossings = new List<MoonCrossing> {
                new MoonCrossing(new DateTime(2026, 7, 31, 0, 30, 0), true)
            };
            MoonAvoidanceAnalyzer.CalculateMoonFreeTime(AstroNight, false, crossings)
                .Should().Be(TimeSpan.FromHours(2.5));
        }

        [Test]
        public void testMoonSetsThenRisesWithinNight() {
            // Up at dusk, sets 23:00, rises 02:00 -> 3 hours moon-free in the middle
            List<MoonCrossing> crossings = new List<MoonCrossing> {
                new MoonCrossing(new DateTime(2026, 7, 30, 23, 0, 0), false),
                new MoonCrossing(new DateTime(2026, 7, 31, 2, 0, 0), true)
            };
            MoonAvoidanceAnalyzer.CalculateMoonFreeTime(AstroNight, true, crossings)
                .Should().Be(TimeSpan.FromHours(3));
        }

        [Test]
        public void testCrossingsOutsideSpanIgnored() {
            // A set before dusk and a rise after dawn must not affect the total
            List<MoonCrossing> crossings = new List<MoonCrossing> {
                new MoonCrossing(new DateTime(2026, 7, 30, 20, 0, 0), false),
                new MoonCrossing(new DateTime(2026, 7, 31, 6, 0, 0), true)
            };
            MoonAvoidanceAnalyzer.CalculateMoonFreeTime(AstroNight, false, crossings)
                .Should().Be(TimeSpan.FromHours(6));
        }

        [Test]
        public void testUnorderedCrossingsAreSorted() {
            List<MoonCrossing> crossings = new List<MoonCrossing> {
                new MoonCrossing(new DateTime(2026, 7, 31, 2, 0, 0), true),
                new MoonCrossing(new DateTime(2026, 7, 30, 23, 0, 0), false)
            };
            MoonAvoidanceAnalyzer.CalculateMoonFreeTime(AstroNight, true, crossings)
                .Should().Be(TimeSpan.FromHours(3));
        }

        [Test]
        public void testNoAstroNightIsZero() {
            MoonAvoidanceAnalyzer.CalculateMoonFreeTime(null, false, new List<MoonCrossing>())
                .Should().Be(TimeSpan.Zero);
        }

        [Test]
        public void testNullCrossingsTreatedAsNone() {
            MoonAvoidanceAnalyzer.CalculateMoonFreeTime(AstroNight, false, null)
                .Should().Be(TimeSpan.FromHours(6));
        }
    }
}
