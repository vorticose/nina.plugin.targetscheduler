using System;

namespace NINA.Plugin.TargetScheduler.Test {

    /// <summary>
    /// Astrometry tests are written against observing sites on the US east coast (longitude -79/-80) and
    /// their expected times were recorded as US Eastern wall clock.  NINA's astrometry returns times in the
    /// *machine's* local zone, so on any machine outside Eastern every one of those expectations is wrong by
    /// the offset difference - 36 tests failed on a Central-time machine purely for that reason.
    ///
    /// The events themselves happen at fixed absolute instants, so the fix is to stop treating the recorded
    /// numbers as machine-local. Et() reinterprets an Eastern wall clock as the same instant expressed in
    /// whatever zone the test happens to be running in.  On an Eastern machine it is the identity, so the
    /// original expectations are preserved exactly.
    ///
    /// Wrap BOTH the inputs and the expectations of a scenario, so the whole scenario describes one set of
    /// absolute instants.  Shifting only the expectations leaves the test asserting against a physically
    /// different night.
    ///
    /// Caveat: a wrapped midday value that a test then takes .Date of is only safe while the shift stays
    /// inside the same calendar day, which holds for the US zones but not for far-eastern ones.  Such date
    /// anchors select which night is under test and do not need wrapping at all; if these tests are ever run
    /// somewhere like Australia, unwrap the anchors and leave only the expectations converted.
    /// </summary>
    public static class TestTimeZone {
        private static readonly TimeZoneInfo Eastern = FindEastern();

        /// <summary>
        /// Reinterpret an Eastern wall clock as the equivalent instant in the local zone.
        /// </summary>
        public static DateTime Et(int year, int month, int day, int hour = 0, int minute = 0, int second = 0) {
            return Et(new DateTime(year, month, day, hour, minute, second));
        }

        /// <summary>
        /// Reinterpret an Eastern wall clock as the equivalent instant in the local zone.
        /// </summary>
        public static DateTime Et(DateTime easternWallClock) {
            if (Eastern.Equals(TimeZoneInfo.Local)) { return easternWallClock; }

            DateTime unspecified = DateTime.SpecifyKind(easternWallClock, DateTimeKind.Unspecified);
            DateTime utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, Eastern);
            return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local), DateTimeKind.Unspecified);
        }

        /// <summary>
        /// The UTC offset Eastern is on at the given Eastern wall clock, as the plugin would format it.
        /// </summary>
        public static TimeSpan LocalOffsetAt(DateTime easternWallClock) {
            return TimeZoneInfo.Local.GetUtcOffset(Et(easternWallClock));
        }

        private static TimeZoneInfo FindEastern() {
            // .NET 6+ accepts IANA ids on Windows too, but fall back to the Windows id if ICU data is absent.
            foreach (string id in new[] { "America/New_York", "Eastern Standard Time" }) {
                try {
                    return TimeZoneInfo.FindSystemTimeZoneById(id);
                } catch (TimeZoneNotFoundException) {
                } catch (InvalidTimeZoneException) {
                }
            }

            throw new InvalidOperationException(
                "cannot resolve the US Eastern time zone, which the astrometry test expectations are recorded in");
        }
    }
}
