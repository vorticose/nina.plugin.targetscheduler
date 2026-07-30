# Design notes — Condition-aware target prioritization (moon)

**Status:** design discussion captured 2026-06-19. Expand into a full planning doc next session.
**Relationship:** separate feature from Planning Insight, but designed to surface *through* it (score breakdown).

> These are seed notes from the initial discussion, to be worked up into a detailed planning doc
> (data model, scoring math, config UX, switch-penalty handling, test plan).

## Problem / goal

Today TS prioritizes *filters within a target* by moon conditions (smart exposure selection), but it does
**not** prioritize *targets* by real-time conditions. Moon/twilight are per-exposure **eligibility filters**,
not target score boosts. So when the moon sets mid-session, the scheduler won't reliably swap from a
narrowband target to a broadband one and prioritize it.

Goal: give the user **maximum control** to build an **automated target deck that behaves logically across
moon phases** with no nightly babysitting.

User's stated strategy (canonical example):
- Treat dark/moonless time as the scarce resource → spend it on the most moon-sensitive data.
- Moon **down**: shoot **LRGB first, then O(III), then H/S only if nothing else is available — or if H/S is
  intentionally promoted** (e.g. wanting very clean H on a faint background).
- Moon **up**: the sensitive data is off the table anyway → do the moon-resilient narrowband.

Framing: **opportunity-cost scheduling** — a filter/target's "moon sensitivity" is really its *claim on dark time*.

## Current-state findings (what already exists)

- `SmartExposureSelector.Select` picks, among a target's **non-rejected** exposures, the one with the highest
  `MoonAvoidanceScore` — `NINA.Plugin.TargetScheduler/Planning/Exposures/SmartExposureSelector.cs:37`.
- `MoonAvoidanceScore` is **static** (independent of current moon alt/sep): `(separation × width)/(180×14)`,
  or 1 if "moon must be down" — `NINA.Plugin.TargetScheduler/Planning/MoonAvoidanceExpert.cs:93`.
- `SmartExposureOrderRule` propagates the selected exposure's score into **target** scoring, but is **OFF by
  default** (weight 0) — `NINA.Plugin.TargetScheduler/Planning/Scoring/Rules/SmartExposureOrderRule.cs`.
- Smart order vs Override order are **mutually exclusive** — `Planning/Exposures/ExposureSelectionExpert.cs:16`.
- `TargetSwitchPenaltyRule` (default weight 0.67) biases toward staying on the current target — works against
  condition-driven swaps — `Planning/Scoring/Rules/TargetSwitchPenaltyRule.cs:20`.

**So you can approximate the strategy today:** enable Smart Exposure Order, weight the Smart Exposure Order
rule, and encode priority via avoidance separations (LRGB high, O medium, H/S low). Worth trying on-sky to
validate the concept before building the real thing.

### Why "approximate today" isn't enough (the gaps to fix)
1. **Overloaded knob** — avoidance separation controls *both* rejection *and* dark-time priority; can't set
   priority independently of rejection thresholds.
2. **Static signal** — the target boost doesn't scale with how dark it actually is right now; only rejection
   changes with the moon.
3. **No decoupled promote/demote** — "force clean H here" needs a per-exposure priority separate from
   avoidance; Override order can't combine with Smart order.
4. **Switch penalty** fights the swap unless the condition signal clearly wins.

## Proposed shape (first-class, decoupled)

- **Explicit per-exposure "dark-time priority"**, decoupled from avoidance. On the **exposure template**
  (reusable defaults: LRGB high, O med, H/S low) + optional **per-plan override** (promotion).
- **Within-target selection**: pick highest-priority *acceptable* exposure (smart selector, keyed on explicit
  priority rather than the avoidance proxy).
- **New 9th scoring rule** (condition-aware target boost): score a target by its currently-selectable
  exposure's priority **scaled by current conditions** (moonlight level), so dark time flows to broadband-
  capable targets and the boost fades as the moon rises. "H/S only if no other option" falls out; "promote H"
  is an override that wins even when dark.
- **Surfaces in Insight** score breakdown + a current-condition indicator on the timeline.

## Open decisions (resolve in the planning doc)

1. **Priority representation** — numeric rank / named class (Broadband/Medium/Narrowband) w/ defaults /
   hybrid. (Lean: hybrid — class default + numeric override.)
2. **Condition model** — discrete tiers (Dark / Moon-up) vs continuous moonlight level (illumination ×
   altitude). (OIII-vs-Ha nuance argues continuous; tiers easier to configure.)
3. **Greedy vs look-ahead** — "best-fit acceptable now" vs reason about remaining dark time vs remaining
   broadband work. (Lean: greedy first.)
4. **Switch-penalty interaction** — condition rule dominates / relax penalty on condition-tier change /
   hysteresis at the moon-set boundary.
5. **Scope** — moon-only first (user focus); build the abstraction to extend to twilight later.

## To expand tomorrow
- [ ] Data model (template field + per-plan override; DB migration considerations).
- [ ] Scoring rule math (priority × condition scaling), default weight, interaction with PercentComplete/Priority/Switch.
- [ ] Condition model definition (moonlight metric or tiers; thresholds).
- [ ] Config UX (where the knobs live in the TS UI).
- [ ] Hysteresis / anti-thrash strategy.
- [ ] Test plan; Insight integration.
- [ ] "Approximate with today's knobs" recipe (for early on-sky validation).
