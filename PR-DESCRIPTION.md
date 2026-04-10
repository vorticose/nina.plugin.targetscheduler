# Feature: Maintain Exposure Profile Ratio

## Summary

Adds a per-project "Maintain Exposure Ratio" toggle that keeps all filters balanced
throughout an imaging session by maintaining the proportions implied by the user's
desired counts. Uses a two-phase algorithm: greedy catch-up to close large deficits,
then a stable GCD-normalized weighted rotation that matches the target ratio exactly.

## Motivation

Many astrophotographers want specific ratios between filters in their final stack.
For example, a typical LRGB project might allocate 50% of total exposure time to
Luminance and split the remaining 50% equally across R, G, and B -- a 3:1:1:1 ratio
if desired counts are L=300, R=100, G=100, B=100.

The existing Smart Exposure Selection feature does an excellent job prioritizing
filters based on moon avoidance conditions (broadband when the moon is down,
narrowband when it's up). However, when multiple filters share the same moon
avoidance score -- which is common for all broadband filters or all narrowband
filters -- it rotates through them equally (L, R, G, B, L, R, G, B...) regardless
of the user's desired ratio.

This creates a problem: if a user wants 3x more Luminance than each RGB channel
but uses Smart Exposure, they get equal amounts of each. The only workaround is to
use Override Exposure Order, but that sacrifices the automatic moon-aware filter
switching that makes Smart Exposure valuable.

The deeper motivation is ensuring the project is "imageable at any stopping point."
If a user maintains the correct ratio throughout acquisition, they can stop at any
time and have a well-balanced dataset for processing. If the ratio is only corrected
at the end, and they need to stop early, they're left with an imbalanced dataset.

## How It Works

### The Setting

"Maintain Exposure Ratio" is a per-project boolean toggle (default: off) in the
project settings panel, positioned after "Smart Exposure Selection." It is
independent of Smart Exposure -- it works with both the Smart Exposure selector and
the Basic (filter cadence) selector.

### Core Algorithm: Two-Phase Deficit Correction

The ratio is derived from the Accepted and Desired counts on each exposure plan --
no new configuration is needed. If a user sets L=300, R=100, G=100, B=100 as their
desired counts, the target ratio is implicitly 3:1:1:1.

At each selection point, the `ExposureRatioSelector` computes the frame deficit for
each eligible (non-rejected, desired > 0) filter:

```
idealCount = totalFrames * (desired / totalDesired)
deficit    = idealCount - actualCount
```

A positive deficit means the filter is behind its ideal allocation; negative means
ahead. The selector then operates in one of two phases:

#### Phase 1: Greedy Catch-Up

**Condition:** any filter has a deficit >= 1 frame.

**Action:** select the filter with the largest deficit.

This closes large imbalances quickly and minimizes filter switches during catch-up
(a filter 28 frames behind gets 28+ consecutive frames rather than interleaving).
The greedy approach is near-optimal: convergence time is bounded by the starting
state geometry (how far ahead/behind each filter is), not the selection strategy.

#### Phase 2: Weighted Rotation

**Condition:** all filters are within 1 frame of their ideal allocation.

**Action:** rotate proportional to desired counts using GCD-normalized base weights.

For L=300, R=100, G=100, B=100: GCD=100, weights=[3,1,1,1], producing a cycle of
L,L,L,R,G,B that repeats indefinitely. Each filter gets exactly its ideal share
per cycle (L=3/6=50%, R=G=B=1/6=16.7%), so no deficit accumulates and the system
never re-enters Phase 1.

When `FilterSwitchFrequency > 1`, each weight slot is scaled by FSF for block
shooting. For FSF=10 with weights [3,1,1,1]: each position becomes a block of 10,
producing L x30, R x10, G x10, B x10 per cycle.

#### Why It Doesn't Oscillate

Previous approaches using percentage-based thresholds (e.g., 5% spread) suffered
from oscillation: catch-up would reduce the spread below the threshold, rotation
would let it drift back above, and the system would flip between modes indefinitely.

The 1-frame absolute threshold avoids this because the weighted rotation gives each
filter exactly its ideal share per cycle. Once all deficits are below 1.0, no filter
can accumulate additional deficit through normal rotation. The transition from Phase 1
to Phase 2 is a one-way gate.

#### Equal Desired Counts (Special Case)

When all eligible filters have the same desired count (e.g., L=100, R=100, G=100,
B=100), the ratio is implicitly 1:1:1:1. In this case the selector uses a simpler
5% spread dead band: if any filter's completion ratio is > 5% behind the others, it
is force-selected; otherwise the selector returns null, deferring to the stock
rotation behavior (SmartExposureRotateManager or filter cadence).

### Delayed Grading Threshold

When image grading is enabled with a delay threshold (delaying grading until a
certain percentage of desired exposures are acquired), the Accepted count stays at 0
until the threshold is reached. During this provisional period, the ratio calculation
falls back to using `Acquired / Desired` instead, matching the same logic used by
`ExposureCompletionHelper.PercentComplete()`.

### Integration with Smart Exposure

When both Smart Exposure and Maintain Exposure Ratio are enabled:

1. Moon avoidance score determines which filters are available (the "group")
2. Among filters tied on moon score, the ratio selector picks the one to image
3. If the ratio selector returns null (balanced, or equal desired counts within
   dead band), the existing `SmartExposureRotateManager` handles rotation as before

Moon avoidance always remains the gatekeeper -- ratio catch-up never overrides a
filter that was rejected by moon avoidance.

**Example scenario:**
- Moon is down: all broadband filters available, tied moon scores
- L=300 desired, R=100, G=100, B=100 desired
- L has 90/300 accepted (30%), R has 40/100 (40%), G/B have 40/100 (40%)
- L deficit = 28 frames -> Phase 1 catch-up -> L is selected repeatedly
- After L catches up, all deficits < 1 -> Phase 2 rotation: L,L,L,R,G,B

### Integration with Basic Selector

When Maintain Exposure Ratio is enabled without Smart Exposure:

1. Before consulting the filter cadence, the ratio selector checks all non-rejected
   filters
2. In Phase 1 (any deficit >= 1), the most-behind filter is selected directly
3. In Phase 2 (all within 1 frame), the weighted rotation selects proportionally
4. When the ratio selector returns null (equal desired counts within dead band),
   normal cadence-based rotation continues
5. The filter cadence position is only advanced when the cadence was actually used,
   preventing cadence state drift

### What Is NOT Affected

- **Override Exposure Order**: Completely unaffected. Manual ordering always takes
  precedence. If a user has set a specific override order, they've already made a
  deliberate choice about filter sequencing.
- **RepeatUntilDone selector**: Not applicable (only one filter active at a time).
- **ExposureSelectionExpert dispatch logic**: No changes. The selector hierarchy
  remains the same.
- **Moon avoidance, twilight, humidity filters**: These still gate which filters are
  available before the ratio selector sees them.
- **Dithering**: Dither logic is independent and continues to work normally.
- **Single-filter targets**: The selector requires > 1 eligible candidate. Targets
  with only one incomplete filter are unaffected.

## Design Decisions

### Why 1-frame threshold (not percentage-based)

A percentage-based threshold (e.g., 5% spread) oscillates because the catch-up
phase can reduce the spread to 4.9%, causing a switch to rotation, which lets it
drift back to 5.1%, re-entering catch-up. This creates a sawtooth pattern.

The 1-frame absolute threshold is immune: rotation gives exact ideal shares, so
once all deficits are < 1.0, they stay < 1.0. The transition is permanent.

### Why greedy catch-up (not proportional)

During catch-up, greedy selection (always pick the most-behind filter) minimizes
filter switches. A filter 28 frames behind gets ~28 consecutive frames, saving 27
filter wheel movements compared to interleaving. The total convergence time is the
same either way -- it's bounded by the most-ahead filter's deficit unwinding as the
total frame count rises.

### Why GCD normalization for weights

GCD normalization produces the shortest possible cycle. L=300, R=100, G=100, B=100
becomes [3,1,1,1] (cycle=6) rather than [300,100,100,100] (cycle=600). A cap at
MAX_CYCLE_LENGTH=50 handles extreme ratios gracefully.

### FilterSwitchFrequency (block shooting)

Each weight position in the rotation is multiplied by FSF. With FSF=10 and weights
[3,1,1,1], the cycle becomes L x30, R x10, G x10, B x10 (length=60). This
integrates cleanly with users who prefer to shoot multiple frames of each filter
before switching.

## Edge Cases Considered

**Moon transitions**: When the moon rises, broadband filters get rejected. Narrowband
filters continue and their ratios diverge from broadband. When the moon sets,
broadband filters become available again, and the ratio selector naturally prioritizes
whichever is most behind -- this is the primary use case and works correctly.

**Disabled exposure profiles**: Handled naturally. Disabled profiles are not included
in `ExposurePlans`, so they're never candidates for the ratio selector.

**Desired count of 0**: Treated as ratio 1.0 (fully complete) and filtered out of
eligible candidates.

**All filters complete**: When all `Accepted >= Desired`, the ratios are all >= 1.0.
The dead band applies and default behavior takes over. In practice, completed
exposures move to `CompletedExposurePlans` before this point.

**Image grading rejects**: If grading rejects many frames of one filter, its Accepted
count drops relative to others, causing the ratio selector to prioritize it for
catch-up. This is the desired behavior -- the user wants accepted frames in the
correct ratio.

**Multi-night sessions**: Acquired/accepted counts persist in the database across
nights. The ratio selector picks up exactly where it left off. If a prior session
left filters unbalanced, Phase 1 catch-up corrects this before entering rotation.

**Candidate set changes**: If a filter is rejected mid-session (moon avoidance,
humidity) or completes its desired count, the rotation cycle resets and
`FindStartIndex` positions the cycle at the most-behind remaining filter.

**Very large ratio disparity**: Weights are capped so cycle length stays <= 50.
For L=10000, R=1, the raw weights [10000,1] are reduced to [50,1] to keep the
cycle manageable.

## Files Changed

| File | Change |
|------|--------|
| `Database/Migrate/24.sql` | **New.** Migration adding `maintainexposureratio` column to project table |
| `Database/Migrate/SQL.resx` | Added entry for migration 24 |
| `Database/Migrate/SQL.Designer.cs` | Added accessor for migration 24 |
| `Database/Schema/Project.cs` | Added `maintainexposureratio` backing field, `MaintainExposureRatio` bool property, constructor init, copy, toString |
| `Planning/Interfaces/IProject.cs` | Added `MaintainExposureRatio` to interface |
| `Planning/Entities/PlanningProject.cs` | Added property, constructor mapping, toString |
| `Planning/PlannerEmulator.cs` | Added `MaintainExposureRatio` to `EmulatedProject` |
| `Planning/Exposures/ExposureRatioSelector.cs` | **New.** Two-phase selector: greedy catch-up + GCD-normalized weighted rotation |
| `Planning/Exposures/ExposureCompletionHelper.cs` | Added `ImageGradingEnabled` public property for ratio selector to distinguish grading modes |
| `Planning/Exposures/SmartExposureSelector.cs` | Instantiates `ExposureRatioSelector` when enabled; ratio overrides rotate manager among tied candidates; passes `FilterSwitchFrequency` |
| `Planning/Exposures/BasicExposureSelector.cs` | Instantiates `ExposureRatioSelector` when enabled; ratio overrides cadence; cadence advances only when used |
| `Controls/DatabaseManager/ProjectView.xaml` | Added "Maintain Exposure Ratio" checkbox row in project settings grid |
| `Test/.../ExposureRatioSelectorTest.cs` | **New.** 31 tests: catch-up, weighted rotation, phase transitions, block shooting, GCD normalization, edge cases |
| `Test/.../SmartExposureSelectorTest.cs` | 4 new tests: ratio selection, dead band fallback, moon priority, disabled behavior |
| `Test/.../BasicExposureSelectorTest.cs` | 3 new tests: ratio selection, dead band fallback, disabled behavior |
| `Test/.../PlanMocks.cs` | Added `MaintainExposureRatio` mock property (defaults to false) |

## Backward Compatibility

- The setting defaults to **off** for all existing projects
- The database migration adds the column with `DEFAULT 0`
- When disabled, all selector behavior is identical to before -- no code paths change
- No existing tests were modified (only new tests added)
- The `ExposureSelectionExpert` dispatch logic is unchanged
- All 470 tests pass

## Testing Notes

- The .NET 10 SDK has a test adapter discovery bug with NUnit3TestAdapter on net8.0
  projects. If tests fail to discover, pin to the .NET 8 SDK using a `global.json`
  with `{"sdk":{"version":"8.0.418"}}`.
- The TS preview API (`/ts/v0/profiles/{id}/preview`) is useful for verifying the
  rotation pattern end-to-end, since `PrepForNextRun` increments counts each step
  and shows the full convergence from any starting state.
