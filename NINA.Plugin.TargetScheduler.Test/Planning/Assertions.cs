using FluentAssertions;
using NUnit.Framework;
using System;

namespace NINA.Plugin.TargetScheduler.Test.Planning {

    public class Assertions {

        /// <summary>
        /// Assert an astrometry time against an expectation recorded as US Eastern wall clock.  The expected
        /// hours/minutes/seconds are reinterpreted into the running machine's zone (see TestTimeZone), so
        /// these tests pass anywhere rather than only where they were written.
        /// </summary>
        public static void AssertTime(DateTime? actual, DateTime expected, int hours, int minutes, int seconds) {
            actual.Should().NotBeNull();

            DateTime edt = TestTimeZone.Et(expected.Date.AddHours(hours).AddMinutes(minutes).AddSeconds(seconds));
            DateTime adt = new DateTime(((DateTime)actual).Year, ((DateTime)actual).Month, ((DateTime)actual).Day,
                ((DateTime)actual).Hour, ((DateTime)actual).Minute, ((DateTime)actual).Second);

            bool cond = (edt == adt);
            if (!cond) {
                TestContext.WriteLine($"AssertTime failed:");
                TestContext.WriteLine($"  expected: {edt}");
                TestContext.WriteLine($"  actual:   {adt}");
            }

            cond.Should().BeTrue();
        }
    }
}