## Bug: Target Twilight Eligibility Uses Wrong Filter

### Message for Developer

Hey, I found a bug where a target can grab the scope during twilight based on one filter's twilight setting but then actually shoot a different filter that shouldn't be eligible yet. In my case, M101 has H at Astronomical -5 and LRGB at Nighttime -5. At dusk, GetTwilightSpan uses H's more permissive Astronomical twilight to make M101 "visible" about 5 minutes before my Spaghetti Nebula target (which only needs O at Nighttime -5). M101 grabs the scope, but SmartExposureSelector picks L instead of H because LRGB has a better moon avoidance score. So it's shooting broadband during a twilight window that only narrowband should qualify for. Then the Target Switch Penalty keeps M101 locked in and Spaghetti never gets imaged despite having a higher priority, higher percent complete, and a closing visibility window. I traced it to GetTwilightSpan in TargetImagingExpert.cs which takes the most permissive twilight across all incomplete exposure plans for visibility, but there's no coordination with the exposure selector to ensure the filter that actually gets shot is valid for the current twilight level. Full write-up with scoring breakdowns and log evidence below.

---

### Summary

TS determines target visibility using the **most permissive twilight** across all
incomplete exposure plans. But the exposure selector independently picks which filter
to shoot based on moon avoidance scores and rotation. This means a target can qualify
as "visible" based on a narrowband filter's twilight setting, then shoot a broadband
filter that shouldn't be eligible yet.

### Root Cause

`TargetImagingExpert.cs` line ~165: `GetTwilightSpan()` returns the span for the
exposure with the "most permissive (brightest) twilight." This makes the entire target
eligible. Then `SmartExposureSelector.Select()` picks the filter independently based
on moon avoidance scores, with no twilight re-check.

### How It Manifests

1. Target has H (Ha) with permissive twilight AND LRGB with strict twilight (nighttime-5)
2. At nautical twilight, H makes the target "visible" 5 minutes before LRGB targets
3. SmartExposureSelector picks L (not H) because LRGB has better moon avoidance score
4. Target is now shooting broadband during twilight that only narrowband qualifies for
5. Target Switch Penalty locks the target in, preventing other targets from getting time

### Real-World Impact (Observed 2026-04-06)

- M101 (H=permissive twilight, LRGB=nighttime-5) became eligible at 21:13:53
- Spaghetti Nebula (only O remaining, nighttime-5) became eligible at 21:18:23
- M101 grabbed the scope 5 minutes early, shot LRGB (not H)
- Target Switch Penalty (+67 points) kept M101 on scope ALL NIGHT
- Spaghetti Panel 3 (7 O frames remaining, limited visibility window) never got imaged
- M101 scored 104.71 vs Spaghetti's 100.21 — the 67-point switch penalty was the difference

### Scoring Breakdown (from TRACE log)

```
Spaghetti Nebula/Sh2 240 Panel 3:
  Meridian Window Priority     0.00    75.00%     0.00
  Percent Complete            96.00    50.00%    48.00
  Project Priority            50.00    50.00%    25.00
  Setting Soonest             54.42    50.00%    27.21
  Target Switch Penalty        0.00    67.00%     0.00
  TOTAL SCORE: 100.21

M101/M 101:
  Meridian Window Priority     0.00    75.00%     0.00
  Percent Complete            52.13    50.00%    26.07
  Project Priority             0.00   100.00%     0.00
  Setting Soonest             23.28    50.00%    11.64
  Target Switch Penalty      100.00    67.00%    67.00
  TOTAL SCORE: 104.71
```

Spaghetti wins on EVERY rule except Target Switch Penalty. The penalty alone (67 points)
overcomes Spaghetti's combined 52-point advantage.

### Exact Filter Settings (Observed Case)

- M101 LRGB: Nighttime, offset -5
- M101 H: **Astronomical, offset -5** ← this is the culprit
- Spaghetti O: Nighttime, offset -5

Astronomical twilight starts ~5 minutes before nighttime. H's Astronomical -5 opens
M101's visibility window at 21:13. Spaghetti's O at Nighttime -5 opens at 21:18.

### How to Reproduce

1. Create two targets:
   - Target A: has H at Astronomical -5 + LRGB at Nighttime -5
   - Target B: has only O at Nighttime -5, limited visibility window
2. Set Target A to Low priority, Target B to Normal
3. Set Target Switch Penalty weight to default (67%)
4. Run plan preview — Target A grabs scope during astronomical twilight, shoots LRGB
   (which requires nighttime), holds all night via switch penalty

**Quick verification**: disable the H exposure plan on Target A → Target B correctly
gets scheduled first.

### Confirmed Stock TS Bug (Not Our Code)

Verified 2026-04-06: `TargetImagingExpert.GetTwilightSpan()` at line 172-189 in the
stock TS source (`C:\Users\Evan\Documents\nina-ts-source`) is character-for-character
identical to our fork. Our ratio selector code only runs inside `SmartExposureSelector.Select()`
AFTER target selection — it never touches the visibility/twilight pipeline.

The pipeline order in `Planner.GetPlan()`:
1. `FilterForVisibility` → uses `GetTwilightSpan` (most permissive filter) — **bug is here**
2. `FilterForMoonAvoidance` → rejects individual filters
3. `FilterForTwilight` → rejects individual filters by current twilight level
4. `FilterForHumidity` → rejects individual filters
5. `SelectTargetExposures` → SmartExposureSelector picks filter (our code runs here)
6. `SelectTargetByScore` → scoring engine picks target

Note: `FilterForTwilight` (step 3) does NOT reject filters with negative offset because
`ExposureTwilightFilter` only checks `offset == 0` and `offset > 0`. Negative offsets
(like -5, meaning "start 5 min before twilight boundary") bypass both conditions entirely.
This is by design (negative offset = more permissive) but contributes to the issue.

### Log Evidence

Log file: `K:\Remote Astro\Logs\TS-20260406-200303-3.2.0.9001.8532.log`
- Line 97989-98008: TARGETS CONSIDERED at 21:13 showing Spaghetti as "not yet visible"
- Line 98309-98380: SCORING RUNS showing M101 winning via Target Switch Penalty
- Set log level to TRACE to see scoring breakdowns

### Potential Fixes (Stock TS)

1. **Filter-aware visibility**: only consider twilight of filters that would actually be
   selected by the exposure selector
2. **Twilight re-check after filter selection**: after SmartExposureSelector picks a filter,
   verify it's valid for the current twilight level; if not, re-select or skip the target
3. **Document the interaction**: at minimum, document that the most-permissive-twilight
   behavior can cause targets to shoot wrong filters during twilight transitions

### Workarounds

- Set all filters on a target to the same twilight level
- Or ensure narrowband twilight is not more permissive than broadband on the same target
- Lower Target Switch Penalty weight to allow other targets to reclaim the scope
