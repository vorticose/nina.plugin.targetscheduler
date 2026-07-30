using NINA.Plugin.TargetScheduler.Shared.Utility;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace NINA.Plugin.TargetScheduler.Planning.Explain {

    /*
     * **CUSTOM FORK** Planning insight / scoring transparency feature.
     *
     * Top-level container assembled by PlanExplanationBuilder. Holds everything needed to render the
     * timeline panel and to emit an LLM-ingestible sidecar (full JSON + condensed digest).
     */

    public class PlanExplanation {
        public DateTime GeneratedAt { get; set; }
        public string ProfileName { get; set; }

        /// <summary>The preview start instant the user requested.</summary>
        public DateTime PreviewStart { get; set; }

        /// <summary>Timeline axis bounds (typically civil dusk -> civil dawn, widened to cover all decisions).</summary>
        public DateTime NightStart { get; set; }

        public DateTime NightEnd { get; set; }

        public TwilightWindows Twilight { get; set; } = new TwilightWindows();
        public List<DecisionSnapshot> Decisions { get; set; } = new List<DecisionSnapshot>();
        public List<SchedulePhase> Phases { get; set; } = new List<SchedulePhase>();
        public List<TargetTimeline> Targets { get; set; } = new List<TargetTimeline>();

        /// <summary>"Project / Target" names of enabled targets excluded from the timeline because they are
        /// already complete (no remaining exposures) — reported so they aren't silently dropped.</summary>
        public List<string> CompletedTargets { get; set; } = new List<string>();

        public const string Disclaimer =
            "Generated from the Target Scheduler 'perfect plan' preview (zero overhead for slews/centering/" +
            "focus/flips; all images assumed acceptable). It approximates what the planner intends, not exact " +
            "runtime behavior. The timeline panel in Target Scheduler is the authoritative view; any natural-" +
            "language summary derived from this file is a convenience and should not be treated as ground truth.";

        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings {
            Formatting = Formatting.Indented,
            NullValueHandling = NullValueHandling.Ignore,
            DateFormatString = "yyyy-MM-ddTHH:mm:ss",
            Converters = new List<JsonConverter> { new StringEnumConverter() }
        };

        /// <summary>Full structured export — for power users, larger models, and debugging.</summary>
        public string ToJson() {
            var doc = new {
                disclaimer = Disclaimer,
                generatedAt = GeneratedAt.ToString("yyyy-MM-ddTHH:mm:ss"),
                profile = ProfileName,
                previewStart = PreviewStart.ToString("yyyy-MM-ddTHH:mm:ss"),
                nightStart = NightStart.ToString("yyyy-MM-ddTHH:mm:ss"),
                nightEnd = NightEnd.ToString("yyyy-MM-ddTHH:mm:ss"),
                twilight = Twilight,
                phases = Phases,
                targets = Targets,
                completedTargets = CompletedTargets
            };
            return JsonConvert.SerializeObject(doc, JsonSettings);
        }

        /// <summary>
        /// Condensed, narrative-ready digest. Carries the causal facts explicitly (chosen-over / rejected-for /
        /// score drivers) so a small model only has to render prose, not infer astronomy.
        /// </summary>
        public string ToDigest() {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"NIGHT PLAN EXPLANATION — profile '{ProfileName}'");
            sb.AppendLine($"Preview start: {Fmt(PreviewStart)}");
            sb.AppendLine($"Timeline window: {Fmt(NightStart)} → {Fmt(NightEnd)}");
            sb.AppendLine(TwilightLine());
            sb.AppendLine();

            sb.AppendLine("SCHEDULE (what Target Scheduler intends to do):");
            int step = 0;
            foreach (SchedulePhase p in Phases) {
                step++;
                if (p.IsWait) {
                    sb.AppendLine($"{step}. {Fmt(p.Start)} → WAIT until {Fmt(p.WaitUntil)} — nothing eligible now.");
                    AppendWaitReasons(sb, p.InitiatingDecision);
                    continue;
                }

                string filter = p.Filters.Count > 0 ? $" ({string.Join(", ", p.Filters)})" : "";
                sb.AppendLine($"{step}. {Fmt(p.Start)} → {Fmt(p.End)}  image {p.ProjectName} / {p.TargetName}{filter}");

                if (p.InitiatingDecision != null && p.InitiatingDecision.Type == DecisionType.Selection) {
                    AppendSelectionReasons(sb, p.InitiatingDecision);
                }
            }

            sb.AppendLine();
            sb.AppendLine("TARGETS NOT SCHEDULED TONIGHT:");
            HashSet<int> scheduled = new HashSet<int>(
                Phases.Where(x => !x.IsWait).Select(x => x.TargetDatabaseId));
            bool anyUnscheduled = false;
            foreach (TargetTimeline t in Targets) {
                if (scheduled.Contains(t.TargetDatabaseId)) { continue; }
                anyUnscheduled = true;
                sb.AppendLine($"- {t.ProjectName} / {t.TargetName}: {UnscheduledReason(t)}");
            }
            if (!anyUnscheduled) { sb.AppendLine("- (none — every loaded target was scheduled at least once)"); }

            if (CompletedTargets != null && CompletedTargets.Count > 0) {
                sb.AppendLine();
                sb.AppendLine("COMPLETE (already finished — excluded from the timeline):");
                foreach (string name in CompletedTargets) {
                    sb.AppendLine($"- {name}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("NOTE: " + Disclaimer);
            return sb.ToString();
        }

        private void AppendSelectionReasons(StringBuilder sb, DecisionSnapshot d) {
            TargetEvaluation winner = d.Candidates.FirstOrDefault(c => c.Selected);

            // Top score drivers for the winner.
            if (winner != null && winner.Scored && winner.RuleContributions.Any(c => c.Enabled)) {
                var drivers = winner.RuleContributions
                    .Where(c => c.Enabled)
                    .OrderByDescending(c => c.Contribution)
                    .Take(3)
                    .Select(c => $"{c.RuleName} (+{c.Contribution:0.00})");
                sb.AppendLine($"     score {winner.TotalScore:0.00}; key drivers: {string.Join(", ", drivers)}");
            }

            // What it was chosen over.
            var others = d.Candidates.Where(c => !c.Selected).ToList();
            foreach (TargetEvaluation o in others) {
                if (o.Rejected) {
                    sb.AppendLine($"     chosen over {o.ProjectName}/{o.TargetName} — rejected: {o.RejectedReason}");
                } else if (o.Scored) {
                    sb.AppendLine($"     chosen over {o.ProjectName}/{o.TargetName} — lower score ({o.TotalScore:0.00})");
                }
            }
        }

        private void AppendWaitReasons(StringBuilder sb, DecisionSnapshot d) {
            // Group rejected candidates by reason for a compact explanation of why nothing is available.
            var byReason = d.Candidates
                .Where(c => c.Rejected && !string.IsNullOrEmpty(c.RejectedReason))
                .GroupBy(c => c.RejectedReason)
                .OrderByDescending(g => g.Count());
            foreach (var g in byReason) {
                string names = string.Join(", ", g.Select(c => $"{c.ProjectName}/{c.TargetName}").Take(6));
                sb.AppendLine($"     {g.Key}: {names}{(g.Count() > 6 ? ", …" : "")}");
            }
        }

        private string UnscheduledReason(TargetTimeline t) {
            if (!t.ImagingPossible) { return "never imageable tonight (never rises high enough / not visible)."; }

            var eligible = t.EligibilitySegments.Where(s => s.Eligible).ToList();
            if (eligible.Count == 0) {
                // Report the dominant rejection reason across the night.
                var dom = t.EligibilitySegments
                    .Where(s => !s.Eligible && !string.IsNullOrEmpty(s.Reason))
                    .GroupBy(s => s.Reason)
                    .OrderByDescending(g => g.Sum(s => (s.End - s.Start).TotalMinutes))
                    .FirstOrDefault();
                return dom != null
                    ? $"never eligible — {dom.Key} (peak altitude {t.PeakAltitude:0}°, limit {t.MinimumAltitude:0}°)."
                    : $"never eligible (peak altitude {t.PeakAltitude:0}°, limit {t.MinimumAltitude:0}°).";
            }

            DateTime from = eligible.Min(s => s.Start);
            DateTime to = eligible.Max(s => s.End);
            return $"eligible {Fmt(from)}–{Fmt(to)} but always outscored by other targets.";
        }

        private string TwilightLine() {
            List<string> parts = new List<string>();
            if (Twilight.CivilStart.HasValue) { parts.Add($"civil dusk {Fmt(Twilight.CivilStart)}"); }
            if (Twilight.AstronomicalStart.HasValue) { parts.Add($"astro dusk {Fmt(Twilight.AstronomicalStart)}"); }
            if (Twilight.AstronomicalEnd.HasValue) { parts.Add($"astro dawn {Fmt(Twilight.AstronomicalEnd)}"); }
            if (Twilight.CivilEnd.HasValue) { parts.Add($"civil dawn {Fmt(Twilight.CivilEnd)}"); }
            return parts.Count > 0 ? "Twilight: " + string.Join(", ", parts) + "." : "Twilight: (unavailable).";
        }

        private static string Fmt(DateTime dt) => dt.ToString("HH:mm");

        private static string Fmt(DateTime? dt) => dt.HasValue ? dt.Value.ToString("HH:mm") : "—";

        /// <summary>
        /// Writes the full JSON, the condensed digest, and a reusable LLM prompt template to the TS plugin
        /// data folder. Returns the directory the files were written to.
        /// </summary>
        public string Export() {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA", "SchedulerPlugin", "Explain");
            Directory.CreateDirectory(dir);

            string stamp = GeneratedAt.ToString("yyyyMMdd-HHmmss");
            string jsonPath = Path.Combine(dir, $"plan-explanation-{stamp}.json");
            string digestPath = Path.Combine(dir, $"plan-explanation-{stamp}-digest.txt");
            string promptPath = Path.Combine(dir, "llm-prompt-template.txt");

            File.WriteAllText(jsonPath, ToJson());
            File.WriteAllText(digestPath, ToDigest());
            if (!File.Exists(promptPath)) {
                File.WriteAllText(promptPath, PromptTemplate);
            }

            TSLogger.Info($"plan explanation exported to {dir}");
            return dir;
        }

        /// <summary>
        /// Recommended prompt for a small/local model. The framing is deliberate: the model NARRATES the facts
        /// it is given and must NOT infer astronomy on its own (small models will confabulate otherwise).
        /// </summary>
        public const string PromptTemplate =
@"SYSTEM:
You are an assistant that explains an astrophotography night plan in plain, friendly English for the
telescope operator. You will be given a JSON (or digest) describing what the 'Target Scheduler' plugin
intends to image tonight and exactly why.

Rules:
- NARRATE the facts you are given. Do NOT infer or invent astronomy, reasons, scores, or times that are
  not present in the input. If a reason is given as 'moon avoidance', say moon avoidance — do not guess.
- Be concise: a short paragraph for the night overview, then a brief bullet per scheduled block.
- Use the explicit reasons (chosen-over / rejected-for / score drivers) to explain transitions.
- For targets that were not scheduled, state the given reason plainly.
- End with one sentence reminding the user this is a preview approximation, not exact runtime behavior.

USER:
Here is tonight's plan. Write the explanation.

<PASTE plan-explanation-*.json OR plan-explanation-*-digest.txt HERE>
";
    }
}
