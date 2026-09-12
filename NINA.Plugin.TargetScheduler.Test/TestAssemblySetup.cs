using FluentAssertions;
using NINA.Core.Utility;
using NINA.Plugin.TargetScheduler.Shared.Utility;
using NUnit.Framework;
using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace NINA.Plugin.TargetScheduler.Test {

    /// <summary>
    /// Moves NINA's application home out of the real %LOCALAPPDATA%\NINA before any test runs.
    ///
    /// NINA.Core's Logger writes to CoreUtil.APPLICATIONTEMPPATH\Logs from its static constructor
    /// and runs a retention cleanup on that folder, and Common.PLUGIN_HOME (the TS database and TS
    /// logs) is derived from the same path. Constructing almost any plugin type triggers both, so a
    /// test run on a machine with a live NINA install used to drop fixture logs into the production
    /// Logs folder (they then rode the nightly rig-log bundle) and could touch the live scheduler
    /// database. A [ModuleInitializer] runs before any type in this assembly is used, so the redirect
    /// is in place before the first Logger call or the first read of Common.PLUGIN_HOME.
    ///
    /// Set TS_TEST_NINA_HOME to choose the sandbox (the rig test runner does); otherwise a folder
    /// under the user's temp directory is used.
    /// </summary>
    public static class TestAssemblySetup {
        public static string TestNinaHome { get; private set; }

        [ModuleInitializer]
        public static void Initialize() {
            string home = Environment.GetEnvironmentVariable("TS_TEST_NINA_HOME");
            if (string.IsNullOrWhiteSpace(home)) {
                home = Path.Combine(Path.GetTempPath(), "ts_test_nina_home");
            }
            Directory.CreateDirectory(home);
            CoreUtil.APPLICATIONTEMPPATH = home;
            TestNinaHome = home;
        }
    }

    [TestFixture]
    public class TestAssemblySetupTest {

        [Test]
        public void testNinaHomeIsIsolatedFromProduction() {
            string production = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NINA");

            TestAssemblySetup.TestNinaHome.Should().NotBeNullOrEmpty();
            CoreUtil.APPLICATIONTEMPPATH.Should().Be(TestAssemblySetup.TestNinaHome, "the module initializer must own NINA's home for the whole run");
            CoreUtil.APPLICATIONTEMPPATH.Should().NotStartWith(production, "tests must never touch the live NINA folder");
            Common.PLUGIN_HOME.Should().StartWith(TestAssemblySetup.TestNinaHome, "the TS plugin home (database, TS logs) must follow the redirect");

            Logger.Info("TS test suite: logger isolation check");
            Directory.Exists(Path.Combine(TestAssemblySetup.TestNinaHome, "Logs")).Should().BeTrue("the NINA logger must write under the test home");
        }
    }
}
