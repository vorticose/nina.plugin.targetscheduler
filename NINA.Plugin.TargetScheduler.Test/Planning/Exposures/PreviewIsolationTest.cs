using FluentAssertions;
using Moq;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Astrometry;
using NINA.Plugin.TargetScheduler.Planning;
using NINA.Plugin.TargetScheduler.Planning.Exposures;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NINA.Plugin.TargetScheduler.Test.Astrometry;
using NUnit.Framework;
using System;
using System.Collections.Generic;

namespace NINA.Plugin.TargetScheduler.Test.Planning.Exposures {

    /// <summary>
    /// Regression coverage for the "plan preview corrupts live sequencing state" class of bug — its
    /// third occurrence (dither) after two filter-rotation occurrences. A background plan preview
    /// (TS API /preview endpoint, Plan Preview UI) drives the same exposure selectors as the live
    /// engine; those must NEVER mutate the live dither / rotation caches. See PreviewContext.
    /// </summary>
    [TestFixture]
    public class PreviewIsolationTest {

        [SetUp]
        public void Setup() {
            DitherManagerCache.Clear();
            SmartExposureRotateCache.Clear();
            TargetVisibilityCache.Clear();
        }

        [TearDown]
        public void TearDown() {
            // Defensive: never let a failed assertion leave the thread stuck in a preview context.
            if (PreviewContext.IsActive) { PreviewContext.Exit(); }
            DitherManagerCache.Clear();
            SmartExposureRotateCache.Clear();
            TargetVisibilityCache.Clear();
        }

        /// <summary>
        /// A DitherManager/rotation state remembers whether it was born in a preview so the isolation
        /// tripwire (CheckIsolation) can detect a live object being mutated during a preview.
        /// </summary>
        [Test]
        public void testCreatedInPreviewFlag() {
            new DitherManager(1).CreatedInPreview.Should().BeFalse();

            PreviewContext.Enter();
            try {
                new DitherManager(1).CreatedInPreview.Should().BeTrue();
            } finally {
                PreviewContext.Exit();
            }

            new DitherManager(1).CreatedInPreview.Should().BeFalse();
        }

        /// <summary>
        /// The dither cache must hand previews a thread-local scratch view: a preview can neither see
        /// nor overwrite the live entry, and the live entry survives the preview byte-for-byte.
        /// </summary>
        [Test]
        public void testDitherCacheIsolatesPreviewFromLive() {
            DitherManager liveDm = new DitherManager(1);
            DitherManagerCache.Put(liveDm, "42");

            PreviewContext.Enter();
            try {
                DitherManagerCache.Get("42").Should().BeNull("a preview must start from an empty scratch cache, not see live managers");

                DitherManager previewDm = new DitherManager(1);
                DitherManagerCache.Put(previewDm, "42");
                DitherManagerCache.Get("42").Should().BeSameAs(previewDm, "within a preview, gets/puts hit only the scratch cache");
            } finally {
                PreviewContext.Exit();
            }

            DitherManagerCache.Get("42").Should().BeSameAs(liveDm, "the live dither manager must survive the preview untouched");
        }

        /// <summary>
        /// Visibility sample cache must not let a preview seed the live night. First-fill-wins
        /// plus a key that omitted sunset/sunrise produced mid-night "not yet visible" holes
        /// after a long-lived NINA process had run many previews.
        /// </summary>
        [Test]
        public void testVisibilityCacheIsolatesPreviewFromLive() {
            DateTime date = new DateTime(2024, 12, 1, 13, 0, 0);
            DateTime sunset = new DateTime(2024, 12, 1, 19, 0, 0);
            DateTime sunrise = new DateTime(2024, 12, 2, 6, 0, 0);
            TargetVisibility liveTv = new TargetVisibility("T1", 1, TestData.North_Mid_Lat, TestData.M42, date, sunset, sunrise, 0, 60);
            TargetVisibilityCache.Put(liveTv, "veil-night");

            PreviewContext.Enter();
            try {
                TargetVisibilityCache.Get("veil-night").Should().BeNull("a preview must start from an empty scratch visibility cache");

                TargetVisibility previewTv = new TargetVisibility("T1", 1, TestData.North_Mid_Lat, TestData.M42, date, sunset.AddHours(1), sunrise, 0, 60);
                TargetVisibilityCache.Put(previewTv, "veil-night");
                TargetVisibilityCache.Get("veil-night").Should().BeSameAs(previewTv);
                previewTv.Sunset.Should().Be(sunset.AddHours(1));
            } finally {
                PreviewContext.Exit();
            }

            TargetVisibilityCache.Get("veil-night").Should().BeSameAs(liveTv, "live visibility samples must survive the preview untouched");
            liveTv.Sunset.Should().Be(sunset);
        }

        /// <summary>Same isolation guarantee for the smart filter-rotation cache (the sibling bug).</summary>
        [Test]
        public void testRotateCacheIsolatesPreviewFromLive() {
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget("T1", TestData.M31);
            pt.SetupProperty(t => t.DatabaseId, 42);

            ExposureRotateStatus liveRs = new ExposureRotateStatus(pt.Object);
            liveRs.CreatedInPreview.Should().BeFalse();
            SmartExposureRotateCache.Put(pt.Object, liveRs);

            PreviewContext.Enter();
            try {
                SmartExposureRotateCache.Get(pt.Object).Should().BeNull("a preview must start from an empty scratch rotation cache");

                ExposureRotateStatus previewRs = new ExposureRotateStatus(pt.Object);
                previewRs.CreatedInPreview.Should().BeTrue();
                SmartExposureRotateCache.Put(pt.Object, previewRs);
                SmartExposureRotateCache.Get(pt.Object).Should().BeSameAs(previewRs);
            } finally {
                PreviewContext.Exit();
            }

            SmartExposureRotateCache.Get(pt.Object).Should().BeSameAs(liveRs, "live rotation state must survive the preview untouched");
        }

        /// <summary>
        /// End-to-end reproduction of the field bug. Live imaging takes an L (dither cadence = 1, so
        /// the NEXT L must dither). A plan preview then simulates a full night in which the target is
        /// pinned to a DIFFERENT filter (R) — exactly the divergence that made the bug bite: the
        /// preview's optimistic projection re-accepts filters the live run has rejected. Before the
        /// fix, the preview drove ExposureTaken straight into the live dither stack, leaving R on top,
        /// so the next live L saw "no prior L" and skipped its dither. After the fix the live stack is
        /// untouched and the L still dithers.
        ///
        /// This test FAILS against the pre-fix code (selectors captured the live DitherManager in their
        /// constructor, before any preview context existed) and PASSES with lazy resolution.
        /// </summary>
        [Test]
        public void testPreviewDoesNotCorruptLiveDither() {
            Mock<IProject> pp = PlanMocks.GetMockPlanProject("P1", ProjectState.Active);
            pp.SetupProperty(p => p.DitherEvery, 1);
            pp.SetupProperty(p => p.SmartExposureOrder, true);

            // Live target: L is the highest-scoring, non-rejected filter -> live images L.
            Mock<ITarget> live = ScoredTarget("Live", databaseId: 100, rejectL: false);
            live.SetupProperty(t => t.Project, pp.Object);

            SmartExposureSelector liveSel = new SmartExposureSelector(pp.Object, live.Object, new Target());
            IExposure le = liveSel.Select(DateTime.Now, pp.Object, live.Object);
            le.FilterName.Should().Be("L");
            le.PreDither.Should().BeFalse();
            liveSel.ExposureTaken(le);   // live dither stack is now [L]

            // Preview target: SAME db id (same dither cache key), but L rejected so the preview
            // diverges to R. Built OUTSIDE the preview context, exactly as LoadActiveProjects builds
            // the selectors before PreviewPlanner enters the context.
            Mock<ITarget> preview = ScoredTarget("Preview", databaseId: 100, rejectL: true);
            preview.SetupProperty(t => t.Project, pp.Object);
            preview.SetupProperty(t => t.IsPreview, true);
            SmartExposureSelector previewSel = new SmartExposureSelector(pp.Object, preview.Object, new Target());

            PreviewContext.Enter();
            try {
                for (int i = 0; i < 10; i++) {
                    IExposure pe = previewSel.Select(DateTime.Now, pp.Object, preview.Object);
                    pe.FilterName.Should().Be("R", "preview projection is pinned to R");
                    previewSel.ExposureTaken(pe);
                }
            } finally {
                PreviewContext.Exit();
            }

            // A fresh live selector (as built each live plan run) must still see the live [L] stack and
            // therefore dither before the next L. Pre-fix this returned false — the missed dither.
            SmartExposureSelector liveSel2 = new SmartExposureSelector(pp.Object, live.Object, new Target());
            IExposure le2 = liveSel2.Select(DateTime.Now, pp.Object, live.Object);
            le2.FilterName.Should().Be("L");
            le2.PreDither.Should().BeTrue("a plan preview must not corrupt live dither state");
        }

        /// <summary>
        /// L/R/G/B exposure plans with strictly-decreasing scores (no rotation ties). When rejectL is
        /// true the top filter becomes R, letting a preview target diverge from a live target that
        /// shares its database id.
        /// </summary>
        private Mock<ITarget> ScoredTarget(string name, int databaseId, bool rejectL) {
            Mock<ITarget> pt = PlanMocks.GetMockPlanTarget(name, TestData.M31);
            pt.SetupProperty(t => t.DatabaseId, databaseId);

            (string filter, int id, double score)[] plans = new[] {
                ("L", 1, 1.0), ("R", 2, 0.9), ("G", 3, 0.8), ("B", 4, 0.7),
            };
            foreach (var p in plans) {
                Mock<IExposure> ep = PlanMocks.GetMockPlanExposure(p.filter, 10, 0);
                ep.SetupProperty(e => e.DatabaseId, p.id);
                PlanMocks.AddMockPlanFilter(pt, ep);
                ep.Object.MoonAvoidanceScore = p.score;
            }

            if (rejectL) { pt.Object.ExposurePlans[0].Rejected = true; }
            return pt;
        }
    }
}
