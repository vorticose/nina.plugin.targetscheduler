# Planning Insight — scoring transparency + night timeline

**Branch:** `feature/planning-insight` (off `fork/main`)
**Status:** prototype, compiles + deploys locally, unit-tested for pure logic. Needs in-app human validation.

## What it adds

A new way to see *why* Target Scheduler intends to do what it does, inside the existing **Scheduler Preview**
panel (TS plugin Options → "Scheduler Preview" expander).

Two new buttons next to **Run** / **View Details**:

- **Insight** — builds and shows a per-target **timeline** for the night.
- **Export for LLM** — writes the explanation to disk as JSON + a condensed digest + a prompt template,
  so a small/local model (Gemma, Gemini Flash, Llama, etc.) can turn it into plain-English narration.

## The timeline panel

One horizontal lane per target across the night:

- **Altitude curve** (green) with dashed **min-altitude** (red) and **max-altitude** (orange) reference lines.
- **Eligibility band** under each curve, colored by state — green = eligible, other colors = rejected, with the
  planner's own reason (moon, twilight, altitude, meridian, visibility, lower score, complete). Hover for the
  exact reason + time range. See the legend at the top.
- **Scheduled spans** (blue) highlight where the planner actually chose that target, labeled with the filter(s).
- **Markers** (▼) at every target switch (amber) and wait (gray). Twilight is shaded in the background
  (darker toward astronomical night). Hour gridlines along the top.

Click a marker → the lower grids show the **full candidate slate at that decision**: each target's status
(selected / rejected-with-reason / considered) and total score, and — for the selected candidate — the
**per-rule scoring breakdown** (raw score, weight, weighted contribution). Disabled rules (project weight 0)
are listed as "(off)" so you can see they exist.

## Export files

Written to `%LOCALAPPDATA%\NINA\SchedulerPlugin\Explain\`:

- `plan-explanation-<stamp>.json` — full structured data (phases + per-target timelines).
- `plan-explanation-<stamp>-digest.txt` — condensed, narrative-ready facts.
- `llm-prompt-template.txt` — recommended prompt (written once).

**LLM workflow:** paste the digest (or JSON) where the prompt template says to. The prompt deliberately tells
the model to **narrate the given facts, not infer astronomy** — small models confabulate otherwise, and the
whole point of this feature is to *remove* opacity, not add a plausible-but-wrong layer. The generated prose is
a convenience; the timeline panel remains authoritative.

## How it's faithful (design)

Eligibility is **not** chain-dependent (visibility/moon/twilight/altitude don't care what was imaged before),
but scoring **is** (TargetSwitchPenalty, previous-target). So the data is split along that seam:

- **Decision snapshots** (`PlanExplainer`) — captured from the *real* planner chain (same `Planner.GetPlan`
  the live run uses) before the per-run state wipe. Source of the scoring breakdowns.
- **Continuous sweep** (`TimelineSweep`) — re-runs the planner's own visibility/twilight/moon filters at each
  time step. Source of eligibility bands + altitude. Faithful because it's chain-independent.

The chain-dependent *continuous score line* is intentionally **not** reconstructed — score detail comes only
from authoritative decision snapshots — so the panel can't teach a model the planner doesn't actually use.

Code: `NINA.Plugin.TargetScheduler/Planning/Explain/` (fork-only; upstream `PreviewPlanner` left untouched).
UI: `Controls/PlanPreview/TimelineView.xaml(.cs)` + additions to `PlanPreviewerViewVM` / `PlanPreviewerView.xaml`.

## Known limitations / things to validate in NINA

- Inherits all **perfect-plan preview** caveats (zero slew/focus/flip overhead, all images assumed accepted),
  so projects complete faster than reality.
- **Humidity** is skipped in the sweep (no weather forecast for a preview).
- Sweep astrometry uses the **active** profile's location (mirrors how the existing preview's planner works);
  the **selected** profile drives which projects load. Normally these are the same profile.
- **Custom horizon:** the dashed limit line shows the project's fixed minimum altitude, but the eligibility
  band honors the actual custom horizon (via the real Visibility check).
- **Performance:** the sweep steps every 10 min. With a very large target deck or long night it may take a few
  seconds; lower/raise `TimelineSweep` step if needed.
- The selected vs. rejected wording uses the planner's own reason strings verbatim.

### Quick validation checklist

1. Open TS Options → Scheduler Preview, pick a date/profile, click **Insight**.
2. Confirm lanes appear for your active targets, altitude curves look right, and twilight shading matches dusk/dawn.
3. Confirm scheduled (blue) spans line up with what **Run** / **View Details** show.
4. Click a switch marker; confirm the chosen target is `✓ selected`, rivals show plausible reasons/scores, and
   the rule breakdown sums to the total.
5. Click **Export for LLM**, open the digest, and (optionally) feed it to a small model with the prompt template.
