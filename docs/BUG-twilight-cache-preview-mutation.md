# `TwilightCircumstancesCache` is a live/preview-shared static cache — same class of bug as the dither/rotation caches

> **Status:** draft bug report for upstream (tcpalmer/nina.plugin.targetscheduler). Not yet filed.
> All file:line references are against `upstream/main` (verified present in `nina-ts-source`, unmodified from stock
> in this area prior to our fork's fix below) — this is not a fork-only defect.
> Fixed in our fork on `dither-diag` (commit `aacdf10`), extending the existing preview-isolation mechanism from
> [`BUG-preview-api-mutates-live-state.md`](BUG-preview-api-mutates-live-state.md) to a third static cache that
> was missed when that fix was written.

## Summary

`TwilightCircumstances.cs` caches the computed dusk/dawn boundaries for a given night (`TwilightCircumstancesCache`,
a static `MemoryCache` keyed by date + lat/long, 12-hour TTL). This cache has **no live/preview isolation**: a plan
preview (`GET /profiles/{id}/preview`, the Plan Preview UI, Plan Explainer) constructs `TwilightCircumstances`
objects for the same night the live sequencer is using, through the exact same static cache. `MemoryCache.Add()`
only writes when the key is absent, so whichever caller — live sequencer thread or a background preview thread —
happens to be first to (re)compute after a cache miss or TTL expiry **permanently seeds the entry the live
sequencer depends on for the rest of its 12-hour lifetime.**

This is the same underlying defect as `BUG-preview-api-mutates-live-state.md` (preview emulation mutating
process-global static state shared with the live engine), just against a cache that fix didn't cover.

## Impact

If a preview run wins the race to seed (or re-seed, after the 12h TTL expires mid-session) the cache entry for
the current night, the live sequencer's twilight-gated exposures (`TwilightLevel` + `MinutesOffset` on exposure
templates) can be held back well past the actual astronomical boundary — silently losing imaging time at the
start of the night, with no error, warning, or obviously-wrong log line. The delay is bounded only by whatever
the winning preview call happened to compute, which in the field was **~30 minutes** past the correct boundary.

## Who triggers it

Same population as the original bug: any consumer of `GET /preview`, including TS's own Plan Preview UI. A
preview run walks synthetic `atTime` values spanning hours (sometimes into the next calendar day) in a single
burst, constructing a fresh `TwilightCircumstances` for each — any one of those calls can be the one that races
the live sequencer's own first-of-the-session (or first-after-expiry) calculation.

## Root cause (upstream file:line, `NINA.Plugin.TargetScheduler/Astrometry/TwilightCircumstances.cs`)

1. `TwilightCircumstances.cs:195-209` — `TwilightCircumstancesCache` is a bare static `MemoryCache`, no
   preview-vs-live distinction on read or write.
2. `TwilightCircumstances.cs:50-71` (constructor) — every `new TwilightCircumstances(observerInfo, atTime)` call,
   live or preview, hits `TwilightCircumstancesCache.Get`/`.Put` unconditionally. `Planning/Planner.cs:207`
   (`FilterForTwilight`) and `Planning/TargetImagingExpert.cs:333` (`CheckFuture`) both construct these directly
   from whatever `atTime` they're given — for the live path that's real "now"; for `PreviewPlanner`'s forward
   simulation it's a synthetic future timestamp, but both resolve to the **same cache key** for a given calendar
   night (`GetCacheKey()` is keyed on `OnDate` — pinned to noon on the date — plus lat/long, not on wall-clock
   call time or `IsPreview`).
3. `TwilightCircumstances.cs:204` — `_cache.Add(cacheKey, this, DateTime.Now.Add(12h))`. `MemoryCache.Add` is a
   write-once-per-key operation (returns `false`, silently, if the key already exists) — so this isn't a
   last-write-wins overwrite race on every call, but whichever thread's `Add()` lands *first* after a miss wins
   for the full 12 hours.

There is no live-vs-preview isolation on this cache, unlike the fix already applied to
`DitherManagerCache`/`SmartExposureRotateCache` (`Planning/PreviewContext.cs`) — this cache was simply not in
scope when that mechanism was added.

## Reproduce

1. Run TS through a session long enough to cross a 12-hour boundary from its first plan evaluation (e.g. NINA
   started early morning, imaging that evening — very common for an all-day-armed rig).
2. Have something poll `GET /profiles/{id}/preview` periodically during the session (Night Summary, Subframes,
   or TS's own Plan Preview UI opened/refreshed near the 12h mark).
3. Watch the live "wait until" time for the night's first target: at the 12h mark the cache entry expires, and
   whichever caller (live or preview) wins the recompute race determines the boundary for the rest of the
   session — including whether it's correct.

Field repro on RBFocus, 2026-07-11: first plan evaluation of the session ran at 07:57:44 (NINA had been running
since morning). Live condition checks at 19:37:45 showed a wait-until of `22:19:19` (correct — matches
astronomical twilight boundary minus the exposure template's `-5` min offset). At 19:57:45 — one second past the
12h mark (07:57:44 + 12h = 19:57:44) — the same target/filter's wait-until jumped to `22:49:19` and stayed there
for the rest of the session, confirmed via `TargetSchedulerCondition` re-plans and a manual project toggle
(which forces a fresh, non-`CheckFuture` recompute and still returned `22:49:19`).

## Suggested fix (already applied in our fork, `dither-diag` branch, commit `aacdf10`)

Route `TwilightCircumstancesCache` reads/writes through the existing `PreviewContext` thread-local mechanism,
exactly like `DitherManagerCache`/`SmartExposureRotateCache`:

- Add a `[ThreadStatic] Dictionary<string, TwilightCircumstances> previewCache` to `TwilightCircumstancesCache`,
  with `EnterPreviewContext()`/`ExitPreviewContext()` mirroring the existing two caches.
- `Get`/`Put` check `previewCache != null` first and redirect there instead of the shared `MemoryCache`.
- Wire `TwilightCircumstancesCache.EnterPreviewContext()`/`ExitPreviewContext()` into `PreviewContext.Enter()`/
  `Exit()` alongside the other two.
- Log every fresh (non-cached) `Calculate()` result (`OnDate`, computed `NighttimeStart`/`NighttimeEnd`,
  `PreviewContext.IsActive`) so a repeat of this — or any future twilight-boundary discrepancy — is diagnosable
  directly from the log instead of requiring timestamp correlation across a multi-hour session.

~60 lines across `TwilightCircumstances.cs` and `PreviewContext.cs`. Preserves the cache's purpose (avoid
recomputing rise/set for every planner tick) while guaranteeing a preview run can never seed or clobber the
entry the live sequencer depends on.

## Notes

- Worth auditing for any other static, non-preview-aware caches in the planning engine beyond these three —
  the pattern ("static cache keyed by domain identity, no live/preview axis") is what keeps recurring.
- Evidence (TS log excerpts showing the `22:19:19` → `22:49:19` jump, `schedulerdb.sqlite` exposure template
  settings ruling out config/moon/altitude as the cause) available on request.
