using System;
using System.Collections.Generic;

namespace NINA.Plugin.TargetScheduler.Planning.Explain {

    /*
     * **CUSTOM FORK** Planning insight / scoring transparency feature.
     *
     * These are plain DTOs (no back-references) describing what the planner decided on a given
     * night and why.  They are designed to be:
     *   1. Rendered in the WPF timeline panel (TimelineView).
     *   2. Serialized to JSON / a condensed text digest that a small LLM can turn into prose.
     *
     * Two faithful data sources feed these:
     *   - Decision snapshots (DecisionSnapshot): captured from the real planner chain (PlanExplainer)
     *     before the per-run state wipe.  These carry the authoritative per-rule scoring breakdown.
     *   - Continuous sweep (TargetTimeline): chain-independent per-target eligibility + altitude over
     *     the night (TimelineSweep).  Eligibility does not depend on what was imaged before, so this
     *     stays faithful without needing the chain.
     */

    public enum DecisionType {
        Selection,    // multiple/one targets evaluated, best selected by score (or sole candidate)
        Continuation, // previous target continued within its minimum time span (no re-evaluation)
        Wait          // nothing eligible now, waiting for the next possible target
    }

    /// <summary>
    /// One scoring rule's contribution to a target's total score at a decision point.
    /// Mirrors the values shown in the existing "View Details" scoring table so users can cross-reference.
    /// Disabled rules (project weight == 0) are included with Enabled=false so the user can see they exist.
    /// </summary>
    public class RuleContribution {
        public string RuleName { get; set; }
        public bool Enabled { get; set; }

        /// <summary>Raw rule output, 0..1.</summary>
        public double RawScore { get; set; }

        /// <summary>Normalized weight, 0..1 (project weight / 100).</summary>
        public double Weight { get; set; }

        /// <summary>Project weight as configured, 0..100.</summary>
        public double WeightPercent { get; set; }

        /// <summary>Weighted contribution to the total (RawScore * Weight), 0..1.</summary>
        public double Contribution { get; set; }
    }

    /// <summary>
    /// How a single target fared at one decision point: rejected (with reason) or scored (with breakdown),
    /// and whether it was the selected winner.
    /// </summary>
    public class TargetEvaluation {
        public string ProjectName { get; set; }
        public string TargetName { get; set; }
        public int TargetDatabaseId { get; set; }

        public bool Selected { get; set; }
        public bool Rejected { get; set; }
        public string RejectedReason { get; set; }

        public bool Scored { get; set; }
        public double? TotalScore { get; set; }
        public string SelectedExposureFilter { get; set; }

        public List<RuleContribution> RuleContributions { get; set; } = new List<RuleContribution>();
    }

    /// <summary>
    /// A snapshot of one planner decision: what was chosen, when, and the full slate of candidates
    /// considered (each rejected-with-reason or scored). This is the "why this, not that" record.
    /// </summary>
    public class DecisionSnapshot {
        public int Index { get; set; }
        public DateTime AtTime { get; set; }
        public DecisionType Type { get; set; }

        public DateTime StartTime { get; set; }
        public DateTime? EndTime { get; set; }
        public DateTime? WaitUntil { get; set; }

        public string SelectedProjectName { get; set; }
        public string SelectedTargetName { get; set; }
        public int SelectedTargetDatabaseId { get; set; }
        public string SelectedExposureFilter { get; set; }

        public List<TargetEvaluation> Candidates { get; set; } = new List<TargetEvaluation>();
    }

    /// <summary>A single eligibility band on a target's timeline lane.</summary>
    public class EligibilitySegment {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public bool Eligible { get; set; }

        /// <summary>Rejection reason when not eligible; null when eligible.</summary>
        public string Reason { get; set; }
    }

    /// <summary>A sampled altitude point for a target's altitude curve.</summary>
    public class AltitudeSample {
        public DateTime Time { get; set; }
        public double Altitude { get; set; }
    }

    /// <summary>A span where the planner actually scheduled this target (derived from decision snapshots).</summary>
    public class ScheduledBlock {
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public string Filter { get; set; }
        public int DecisionIndex { get; set; }
    }

    /// <summary>
    /// Per-target lane data for the timeline: eligibility over the night, altitude curve, the configured
    /// altitude limits, and the spans actually scheduled.
    /// </summary>
    public class TargetTimeline {
        public string ProjectName { get; set; }
        public string TargetName { get; set; }
        public int TargetDatabaseId { get; set; }

        public double MinimumAltitude { get; set; }
        public double MaximumAltitude { get; set; }
        public bool UsesCustomHorizon { get; set; }
        public bool ImagingPossible { get; set; }

        /// <summary>Peak altitude reached during the night (for axis scaling / quick reference).</summary>
        public double PeakAltitude { get; set; }

        public List<EligibilitySegment> EligibilitySegments { get; set; } = new List<EligibilitySegment>();
        public List<AltitudeSample> AltitudeSamples { get; set; } = new List<AltitudeSample>();
        public List<ScheduledBlock> ScheduledBlocks { get; set; } = new List<ScheduledBlock>();
    }

    /// <summary>Twilight transition times for the night, used to shade the timeline background.</summary>
    public class TwilightWindows {
        public DateTime? CivilStart { get; set; }
        public DateTime? CivilEnd { get; set; }
        public DateTime? NauticalStart { get; set; }
        public DateTime? NauticalEnd { get; set; }
        public DateTime? AstronomicalStart { get; set; }
        public DateTime? AstronomicalEnd { get; set; }
        public DateTime? NighttimeStart { get; set; }
        public DateTime? NighttimeEnd { get; set; }
    }

    /// <summary>
    /// A coalesced run of consecutive decisions for the same target (or a single wait). The perfect-plan
    /// preview produces one decision per exposure; phases collapse those into the meaningful units a user
    /// reasons about — "image M31 from 21:35 to 02:40" — while keeping the initiating decision (the scoring
    /// contest that chose the target over its rivals) for drill-down.
    /// </summary>
    public class SchedulePhase {
        public int Index { get; set; }
        public DateTime Start { get; set; }
        public DateTime End { get; set; }
        public bool IsWait { get; set; }
        public DateTime? WaitUntil { get; set; }

        public string ProjectName { get; set; }
        public string TargetName { get; set; }
        public int TargetDatabaseId { get; set; }

        /// <summary>Distinct filters used during the phase, in first-use order.</summary>
        public List<string> Filters { get; set; } = new List<string>();

        /// <summary>The decision that started this phase: for a target switch, the candidate scoring slate.</summary>
        public DecisionSnapshot InitiatingDecision { get; set; }
    }
}
