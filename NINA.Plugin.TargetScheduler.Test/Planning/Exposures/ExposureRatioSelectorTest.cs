using FluentAssertions;
using Moq;
using NINA.Plugin.TargetScheduler.Database.Schema;
using NINA.Plugin.TargetScheduler.Planning.Exposures;
using NINA.Plugin.TargetScheduler.Planning.Interfaces;
using NUnit.Framework;
using System.Collections.Generic;

namespace NINA.Plugin.TargetScheduler.Test.Planning.Exposures {

    [TestFixture]
    public class ExposureRatioSelectorTest {

        [Test]
        public void testSelectMostBehind() {
            // L has 5/20 = 25%, R has 3/10 = 30%, G has 4/10 = 40%, B has 5/10 = 50%
            // L is most behind, should be selected
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 20, 5));
            candidates.Add(MakeExposure("R", 10, 3));
            candidates.Add(MakeExposure("G", 10, 4));
            candidates.Add(MakeExposure("B", 10, 5));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("L");
        }

        [Test]
        public void testSelectCatchesUpNarrowband() {
            // Simulates moon-up scenario where Ha/SII got ahead, OIII fell behind
            // Ha has 15/20 = 75%, SII has 14/20 = 70%, OIII has 5/20 = 25%
            // OIII is most behind, should be selected
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
        public void testDeadBandReturnsNull() {
            // All within 5%: L = 50%, R = 50%, G = 52%, B = 48%
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 100, 50));
            candidates.Add(MakeExposure("R", 100, 50));
            candidates.Add(MakeExposure("G", 100, 52));
            candidates.Add(MakeExposure("B", 100, 48));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            sut.Select(candidates).Should().BeNull();
        }

        [Test]
        public void testDeadBandBoundaryJustOutside() {
            // Spread is exactly 5.1%: L = 50%, B = 55.1% -> should select
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 1000, 500));
            candidates.Add(MakeExposure("B", 1000, 551));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("L");
        }

        [Test]
        public void testDeadBandBoundaryJustInside() {
            // Spread is exactly 4.9%: L = 50%, B = 54.9% -> dead band, null
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 1000, 500));
            candidates.Add(MakeExposure("B", 1000, 549));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            sut.Select(candidates).Should().BeNull();
        }

        [Test]
        public void testSingleCandidateReturnsNull() {
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 20, 5));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            sut.Select(candidates).Should().BeNull();
        }

        [Test]
        public void testDesiredZeroTreatedAsComplete() {
            // L has Desired=0 (ratio=1.0), R has 0/10 = 0%
            // Only one eligible candidate (R, since L has Desired=0), returns null (single candidate)
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 0, 0));
            candidates.Add(MakeExposure("R", 10, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            sut.Select(candidates).Should().BeNull();
        }

        [Test]
        public void testTieBreakingFirstInListWins() {
            // L and R both at 0/10 = 0%, same ratio
            // G and B both at 5/10 = 50%
            // L should win because it's first in the list
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
            // L: 2 acquired out of 10 desired (20%), 0 accepted (below 80% threshold)
            Mock<IExposure> lMock = PlanMocks.GetMockPlanExposure("L", 10, 0);
            lMock.SetupProperty(m => m.Acquired, 2);
            candidates.Add(lMock.Object);

            // R: 5 acquired out of 10 desired (50%), 0 accepted (below 80% threshold)
            Mock<IExposure> rMock = PlanMocks.GetMockPlanExposure("R", 10, 0);
            rMock.SetupProperty(m => m.Acquired, 5);
            candidates.Add(rMock.Object);

            // Both have 0 accepted, but L has lower acquired ratio
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("L");
        }

        [Test]
        public void testGradingDisabledUsesAcquired() {
            // With grading disabled, CompletionRatio should use Acquired/Desired, not Accepted/Desired
            // L: Acquired=2, Accepted=10, Desired=20 -> ratio should be 2/20=0.10 (not 10/20=0.50)
            // R: Acquired=8, Accepted=1, Desired=20 -> ratio should be 8/20=0.40 (not 1/20=0.05)
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            IExposure l = MakeExposure("L", 20, 10, acquired: 2);
            sut.CompletionRatio(l).Should().BeApproximately(0.10, 0.0001);

            IExposure r = MakeExposure("R", 20, 1, acquired: 8);
            sut.CompletionRatio(r).Should().BeApproximately(0.40, 0.0001);

            // In a selection, L (0.10) should be selected over R (0.40)
            List<IExposure> candidates = new List<IExposure> { l, r };
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("L");
        }

        [Test]
        public void testGradingEnabledUsesAccepted() {
            // With grading enabled (no delay), CompletionRatio should use Accepted/Desired
            // L: Acquired=10, Accepted=2, Desired=20 -> ratio should be 2/20=0.10
            // R: Acquired=1, Accepted=8, Desired=20 -> ratio should be 8/20=0.40
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(true, 0, 100));

            IExposure l = MakeExposure("L", 20, 2, acquired: 10);
            sut.CompletionRatio(l).Should().BeApproximately(0.10, 0.0001);

            IExposure r = MakeExposure("R", 20, 8, acquired: 1);
            sut.CompletionRatio(r).Should().BeApproximately(0.40, 0.0001);

            // In a selection, L (0.10) should be selected over R (0.40)
            List<IExposure> candidates = new List<IExposure> { l, r };
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("L");
        }

        [Test]
        public void testUnequalDesiredCountsRatio() {
            // L=300 desired, 150 accepted (50%), R=100 desired, 50 accepted (50%),
            // G=100 desired, 50 accepted (50%), B=100 desired, 20 accepted (20%)
            // B is most behind at 20%, should be selected
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 150));
            candidates.Add(MakeExposure("R", 100, 50));
            candidates.Add(MakeExposure("G", 100, 50));
            candidates.Add(MakeExposure("B", 100, 20));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("B");
        }

        // =====================================================================
        // Weighted Rotation Tests
        // =====================================================================

        [Test]
        public void testWeightedRotationSequence() {
            // L:300, R:100, G:100, B:100 all at 0% -> weighted rotation
            // GCD=100, weights [3,1,1,1], cycle = L,L,L,R,G,B
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 0));
            candidates.Add(MakeExposure("R", 100, 0));
            candidates.Add(MakeExposure("G", 100, 0));
            candidates.Add(MakeExposure("B", 100, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            string[] expected = { "L", "L", "L", "R", "G", "B" };
            for (int i = 0; i < expected.Length; i++) {
                IExposure result = sut.Select(candidates);
                result.Should().NotBeNull($"iteration {i}");
                result.FilterName.Should().Be(expected[i], $"iteration {i}");
            }
        }

        [Test]
        public void testWeightedRotationCycleWraps() {
            // Verify the cycle repeats: L,L,L,R,G,B,L,L,L,R,G,B
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 0));
            candidates.Add(MakeExposure("R", 100, 0));
            candidates.Add(MakeExposure("G", 100, 0));
            candidates.Add(MakeExposure("B", 100, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            string[] expected = { "L", "L", "L", "R", "G", "B", "L", "L", "L", "R", "G", "B" };
            for (int i = 0; i < expected.Length; i++) {
                IExposure result = sut.Select(candidates);
                result.Should().NotBeNull($"iteration {i}");
                result.FilterName.Should().Be(expected[i], $"iteration {i}");
            }
        }

        [Test]
        public void testWeightedRotationTwoToOne() {
            // S:20, H:10 -> GCD=10, weights [2,1], cycle = S,S,H
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("S", 20, 0));
            candidates.Add(MakeExposure("H", 10, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            string[] expected = { "S", "S", "H", "S", "S", "H" };
            for (int i = 0; i < expected.Length; i++) {
                IExposure result = sut.Select(candidates);
                result.Should().NotBeNull($"iteration {i}");
                result.FilterName.Should().Be(expected[i], $"iteration {i}");
            }
        }

        [Test]
        public void testEqualDesiredDefersToDefault() {
            // All same desired, balanced -> should return null (defer to stock rotation)
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 100, 50));
            candidates.Add(MakeExposure("R", 100, 50));
            candidates.Add(MakeExposure("G", 100, 50));
            candidates.Add(MakeExposure("B", 100, 50));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            sut.Select(candidates).Should().BeNull();
        }

        [Test]
        public void testCandidateSetChangeResetsRotation() {
            // Start with 3 filters, weighted rotation in progress
            List<IExposure> candidates3 = new List<IExposure>();
            candidates3.Add(MakeExposure("L", 300, 0));
            candidates3.Add(MakeExposure("R", 100, 0));
            candidates3.Add(MakeExposure("G", 100, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            // Take 2 from the 3-filter cycle
            sut.Select(candidates3).FilterName.Should().Be("L");
            sut.Select(candidates3).FilterName.Should().Be("L");

            // Now add B -> candidate set changes, cycle should reset
            List<IExposure> candidates4 = new List<IExposure>();
            candidates4.Add(MakeExposure("L", 300, 0));
            candidates4.Add(MakeExposure("R", 100, 0));
            candidates4.Add(MakeExposure("G", 100, 0));
            candidates4.Add(MakeExposure("B", 100, 0));

            // Should restart from beginning: L,L,L,R,G,B
            sut.Select(candidates4).FilterName.Should().Be("L");
        }

        // =====================================================================
        // Deficit-Adjusted Rotation Tests
        // =====================================================================

        [Test]
        public void testDeficitAdjustsWeights() {
            // L=130/300=0.433, R=50/100=0.500 -> L is behind
            // Total=180, L ideal=180*300/400=135, L actual=130, deficit=5
            // Base weights [3,1], adjusted [3+5, 1] = [8, 1], cycle length 9
            // Should start at L (most behind) and give L extra frames
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 130));
            candidates.Add(MakeExposure("R", 100, 50));

            // First 8 should be L (adjusted weight 8), then R
            for (int i = 0; i < 8; i++) {
                IExposure result = sut.Select(candidates);
                result.FilterName.Should().Be("L", $"iteration {i}: L should get extra frames for deficit");
            }
            sut.Select(candidates).FilterName.Should().Be("R", "R gets its turn after L's adjusted block");
        }

        [Test]
        public void testBalancedUsesBaseWeights() {
            // L=150/300=0.500, R=50/100=0.500, spread=0, no deficit -> base weights only
            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 150));
            candidates.Add(MakeExposure("R", 100, 50));

            // Should produce base weighted rotation: L,L,L,R
            string[] expected = { "L", "L", "L", "R" };
            for (int i = 0; i < expected.Length; i++) {
                IExposure result = sut.Select(candidates);
                result.Should().NotBeNull($"iteration {i}");
                result.FilterName.Should().Be(expected[i], $"iteration {i}");
            }
        }

        [Test]
        public void testMultipleFiltersBehind() {
            // L=300, R=100, G=100, B=100. R and B are behind.
            // Total=240, R ideal=240*100/600=40, R actual=30, deficit=10
            // B ideal=40, B actual=25, deficit=15
            // L ideal=240*300/600=120, L actual=120, deficit=0
            // G ideal=40, G actual=65 -> ahead (deficit negative, no adjustment)
            // Base weights [3,1,1,1], adjusted [3, 1+10, 1, 1+15] = [3, 11, 1, 16]
            // B is most behind -> starts at B's position
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 120));
            candidates.Add(MakeExposure("R", 100, 30));
            candidates.Add(MakeExposure("G", 100, 65));
            candidates.Add(MakeExposure("B", 100, 25));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            // B is most behind, should start with B and get lots of frames
            IExposure first = sut.Select(candidates);
            first.FilterName.Should().Be("B", "B has the largest deficit, should start there");
        }

        [Test]
        public void testSmallDeficitGetsOneExtraFrame() {
            // R is 0.8 frames behind -> ceil(0.8) = 1 extra frame
            // Total=200, R ideal=200*100/400=50, R actual=49, deficit=1.0 -> ceil=1
            // Base [3,1,1,1], adjusted [3,2,1,1]
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 150));
            candidates.Add(MakeExposure("R", 100, 49));
            candidates.Add(MakeExposure("G", 100, 50));
            candidates.Add(MakeExposure("B", 100, 50));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            // R is behind, starts at R position. Adjusted weights [3,2,1,1] -> R gets 2 frames
            IExposure first = sut.Select(candidates);
            first.FilterName.Should().Be("R", "R has deficit, rotation should start there");
        }

        // =====================================================================
        // Block Shooting (FilterSwitchFrequency > 1) Tests
        // =====================================================================

        [Test]
        public void testBlockShootingWeightedRotation() {
            // FSF=3, L:200, R:100 -> GCD=100, weights [2,1], base cycle=3
            // Block cycle: L×3, L×3, R×3 (length 9)
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 200, 0));
            candidates.Add(MakeExposure("R", 100, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100), filterSwitchFrequency: 3);

            string[] expected = { "L", "L", "L", "L", "L", "L", "R", "R", "R" };
            for (int i = 0; i < expected.Length; i++) {
                IExposure result = sut.Select(candidates);
                result.Should().NotBeNull($"iteration {i}");
                result.FilterName.Should().Be(expected[i], $"iteration {i}");
            }
        }

        [Test]
        public void testBlockShootingCycleWraps() {
            // FSF=2, L:300, R:100, G:100, B:100 -> weights [3,1,1,1], base cycle=6
            // Block cycle: L×2, L×2, L×2, R×2, G×2, B×2 (length 12)
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 0));
            candidates.Add(MakeExposure("R", 100, 0));
            candidates.Add(MakeExposure("G", 100, 0));
            candidates.Add(MakeExposure("B", 100, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100), filterSwitchFrequency: 2);

            string[] expected = { "L", "L", "L", "L", "L", "L", "R", "R", "G", "G", "B", "B",
                                  "L", "L", "L", "L", "L", "L", "R", "R", "G", "G", "B", "B" };
            for (int i = 0; i < expected.Length; i++) {
                IExposure result = sut.Select(candidates);
                result.Should().NotBeNull($"iteration {i}");
                result.FilterName.Should().Be(expected[i], $"iteration {i}");
            }
        }

        [Test]
        public void testStartsWithMostBehindFilter() {
            // R is 2 frames behind, rotation should start at R's adjusted block
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 147));
            candidates.Add(MakeExposure("R", 100, 47));
            candidates.Add(MakeExposure("G", 100, 50));
            candidates.Add(MakeExposure("B", 100, 50));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("R", "should start rotation at the most-behind filter");
        }

        [Test]
        public void testBlockShootingStartsWithMostBehindFilter() {
            // Same concept but with FSF=5: should start at R's block
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 147));
            candidates.Add(MakeExposure("R", 100, 47));
            candidates.Add(MakeExposure("G", 100, 50));
            candidates.Add(MakeExposure("B", 100, 50));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100), filterSwitchFrequency: 5);
            IExposure result = sut.Select(candidates);
            result.Should().NotBeNull();
            result.FilterName.Should().Be("R", "block rotation should start at the most-behind filter's block");

            // Should stay on R for at least FSF calls (R has extra deficit weight)
            for (int i = 1; i < 5; i++) {
                sut.Select(candidates).FilterName.Should().Be("R", $"should stay in R block, call {i+1}");
            }
        }

        [Test]
        public void testBlockShootingFSF1SameAsDefault() {
            // FSF=1 should behave exactly like no FSF parameter
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("L", 300, 0));
            candidates.Add(MakeExposure("R", 100, 0));
            candidates.Add(MakeExposure("G", 100, 0));
            candidates.Add(MakeExposure("B", 100, 0));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100), filterSwitchFrequency: 1);

            string[] expected = { "L", "L", "L", "R", "G", "B" };
            for (int i = 0; i < expected.Length; i++) {
                IExposure result = sut.Select(candidates);
                result.Should().NotBeNull($"iteration {i}");
                result.FilterName.Should().Be(expected[i], $"iteration {i}");
            }
        }

        // =====================================================================
        // Catch-Up Tests
        // =====================================================================

        [Test]
        public void testCatchUpMoonAvoidanceScenario() {
            // Simulates O being blocked by moon while H and S accumulate
            // H=50/100=0.50, S=50/100=0.50, O=5/100=0.05
            // O is way behind, should be forced in catch-up
            List<IExposure> candidates = new List<IExposure>();
            candidates.Add(MakeExposure("H", 100, 50));
            candidates.Add(MakeExposure("S", 100, 50));
            candidates.Add(MakeExposure("O", 100, 5));

            ExposureRatioSelector sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));

            // O is massively behind, should keep selecting O
            for (int i = 0; i < 5; i++) {
                IExposure result = sut.Select(candidates);
                result.Should().NotBeNull($"iteration {i}");
                result.FilterName.Should().Be("O", $"iteration {i}: O should stay in catch-up");
            }
        }

        [Test]
        public void testWeightedRotationVariousRatios() {
            // Verify GCD normalization works for different ratios by checking output patterns
            ExposureRatioSelector sut;

            // 150:100 -> GCD=50, weights [3,2], cycle = H,H,H,S,S
            sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            List<IExposure> candidates1 = new List<IExposure>();
            candidates1.Add(MakeExposure("H", 150, 0));
            candidates1.Add(MakeExposure("S", 100, 0));

            string[] expected1 = { "H", "H", "H", "S", "S" };
            for (int i = 0; i < expected1.Length; i++) {
                sut.Select(candidates1).FilterName.Should().Be(expected1[i], $"150:100 iteration {i}");
            }

            // 7:3 -> GCD=1, weights [7,3], cycle length 10
            sut = new ExposureRatioSelector(new ExposureCompletionHelper(false, 0, 100));
            List<IExposure> candidates2 = new List<IExposure>();
            candidates2.Add(MakeExposure("L", 7, 0));
            candidates2.Add(MakeExposure("R", 3, 0));

            int lCount = 0, rCount = 0;
            for (int i = 0; i < 10; i++) {
                IExposure r = sut.Select(candidates2);
                if (r.FilterName == "L") lCount++;
                else rCount++;
            }
            lCount.Should().Be(7);
            rCount.Should().Be(3);
        }

        /// <summary>
        /// Creates a mock exposure for ratio testing.
        /// Sets both Acquired and Accepted so the ratio works regardless of grading mode.
        /// </summary>
        private IExposure MakeExposure(string filterName, int desired, int accepted, int acquired = -1) {
            Mock<IExposure> pe = PlanMocks.GetMockPlanExposure(filterName, desired, accepted);
            pe.SetupProperty(m => m.Acquired, acquired >= 0 ? acquired : accepted);
            return pe.Object;
        }
    }
}
