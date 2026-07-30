# GET `/profiles/{id}/preview` mutates live scheduler state (rotation + dither caches)

> **Status:** draft bug report for upstream (tcpalmer/nina.plugin.targetscheduler). Not yet filed.
> All file:line references are against `upstream/main` and verified present in stock — this is not a fork artifact.

## Summary

The REST endpoint `GET /profiles/{id}/preview` is not side-effect-free. Generating a preview runs the
real planner forward through a simulated night, and that emulation mutates the **process-global, live**
planner state used by the running sequence:

- the smart-exposure **filter-rotation** cache (`SmartExposureRotateCache`), and
- the **dither** cache (`DitherManagerCache`).

Because it's a `GET` on an endpoint named *preview*, external clients reasonably treat it as a safe,
idempotent read and poll it freely. Each poll silently corrupts the live imaging session's rotation and
dither bookkeeping.

## Impact

For a target using **Smart Exposure Order** (and especially with a tight `FilterSwitchFrequency`):

- **Filter rotation jams / mis-sequences.** The live rotation bookmark is overwritten by the preview's
  emulated bookmark, so the running sequence stops cleanly alternating tied filters and repeats one filter.
- **Dithering becomes unreliable / effectively disabled.** `PreviewPlanner` calls
  `DitherManagerCache.Clear()` at the start of every run, which disposes the **entire** cache including the
  live target's dither stack. If a client polls more often than the imaging cadence, the live dither
  counter is wiped before it can reach `DitherEvery`, so dithers stop firing.

Severity scales with poll frequency and only manifests when a preview consumer runs *during* an active
session. The symptom is normally low-visibility (a cosmetic rotation/dither glitch that self-heals next
exposure), which is likely why it's gone unnoticed.

## Who triggers it

Any consumer of `GET /preview`, including TS's own Plan Preview UI. Observed in the wild from two
independent, well-behaved API clients during a live session:

- **Subframes plugin** — polls the TS API ~once/minute (its station heartbeat embeds a `tsPreview` block).
  Observed **64** `/preview` calls in ~40 min, each immediately followed by a `BEGIN PLAN PREVIEW` run.
- **Night Summary plugin** — calls `GET /profiles/{id}/preview` for its report / dashboard (cached ~5 min).
  Gentler, but trips the same mutation on each uncached call.

Neither client is doing anything wrong; both treat a GET preview as a safe read.

## Root cause (upstream file:line)

1. `API/APIController.cs:118` — `[Route(HttpVerbs.Get, "/profiles/{id}/preview")]` → `:135` `MarkForPreview(...)`
   → `:138` `PreviewPlanner.GetPlanPreview(...)`. A `GET` that drives the full planner.
2. `Planning/PreviewPlanner.cs:65` — the emulation loop calls
   `plan.PlanTarget.ExposureSelector.ExposureTaken(...)`, advancing rotation/dither state.
3. `Planning/PreviewPlanner.cs:36` — `DitherManagerCache.Clear()` disposes the whole (shared) dither cache.
4. `Planning/Exposures/SmartExposureRotate.cs:128-130` — `SmartExposureRotateCache.GetCacheKey(ITarget)`
   returns the bare `DatabaseId`. The cache is `static` and process-wide, so the preview emulation and the
   live sequence share the **same** entry for a given target. (`DitherManagerCache` is keyed the same way.)

There is no live-vs-preview isolation: the only axis on the cache key is target identity, and the preview
runs against the same target rows the live sequence uses. `Select()` also mutates via `ResetForSelect`, so
even reads advance shared state.

## Reproduce

1. Target with Smart Exposure Order, two tied filters (e.g. equal H/S), `FilterSwitchFrequency = 1`.
2. Start imaging; confirm it alternates H/S.
3. From another client, poll `GET /profiles/{id}/preview` every ~30–60 s (or open/refresh the Plan Preview).
4. The live sequence stops alternating and repeats one filter; dithers stop firing on the expected cadence.

## Suggested fix (approach, not a patch)

Isolate preview emulation from live state. `IsPreview` already exists and is set by `MarkForPreview`
(`APIController.cs:112`), and the selectors hold a reference to the target — so the signal is already
available at `Select`/`ExposureTaken` time. Minimal, merge-friendly:

- Namespace both static cache keys by `IsPreview` (e.g. prefix preview targets), so preview and live never
  share an entry.
- Replace `PreviewPlanner`'s global `DitherManagerCache.Clear()` with a preview-scoped clear (only the
  preview-namespaced keys), so previewing never disposes live dither state. (Leave the live-run
  `DitherManagerCache.Clear()` in `TargetSchedulerContainer` as-is.)

This keeps the static-cache-survives-between-exposures property the live path needs, preserves preview
fidelity (still the real planner), and makes `GET /preview` honor its contract: safe to call at any rate
from any consumer. ~15–20 lines across `SmartExposureRotate.cs`, `DitherManager.cs`, `PreviewPlanner.cs`.

Worth also auditing the rest of the read API for the same smell (any GET that drives planner globals).

## Notes

- The most *visible* symptom (broken maintain-exposure-ratio balance) comes from a downstream fork feature
  and is not part of stock; the underlying defect — a GET with live side effects — is entirely upstream.
- Evidence (TS logs, acquired-image DB, the two client call patterns) available on request.
