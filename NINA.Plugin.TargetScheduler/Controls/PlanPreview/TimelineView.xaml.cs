using NINA.Plugin.TargetScheduler.Planning.Explain;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NINA.Plugin.TargetScheduler.Controls.PlanPreview {

    /*
     * **CUSTOM FORK** Planning insight / scoring transparency feature.
     *
     * The interactive timeline control. Drawing is delegated to TimelineRenderer (dependency-light, also
     * driven by the preview harness); this class owns the WPF wiring: the Explanation dependency property,
     * marker click hit-testing, and the decision-detail grids (candidate slate + per-rule breakdown).
     */

    public partial class TimelineView : UserControl {
        private TimelineRenderer renderer;
        private Line highlightLine;

        public TimelineView() {
            InitializeComponent();
            TimelineCanvas.MouseLeftButtonDown += Canvas_Click;
            CandidatesGrid.SelectionChanged += CandidatesGrid_SelectionChanged;
            Loaded += (s, e) => Render();
        }

        public static readonly DependencyProperty ExplanationProperty =
            DependencyProperty.Register(nameof(Explanation), typeof(PlanExplanation), typeof(TimelineView),
                new PropertyMetadata(null, OnExplanationChanged));

        public PlanExplanation Explanation {
            get => (PlanExplanation)GetValue(ExplanationProperty);
            set => SetValue(ExplanationProperty, value);
        }

        private static void OnExplanationChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
            ((TimelineView)d).Render();
        }

        private void Render() {
            if (TimelineCanvas == null) { return; }
            TimelineCanvas.Children.Clear();
            LegendPanel.Children.Clear();
            highlightLine = null;
            DecisionLabel.Text = "Click a target-switch marker (▼) on the timeline to see why that target was chosen over the others.";
            CandidatesGrid.ItemsSource = null;
            RulesGrid.ItemsSource = null;

            PlanExplanation ex = Explanation;
            if (ex == null || ex.Targets.Count == 0) { renderer = null; return; }

            renderer = new TimelineRenderer(ex, TextBrush);
            renderer.DrawLegend(LegendPanel);
            renderer.DrawTimeline(TimelineCanvas);
        }

        private void Canvas_Click(object sender, System.Windows.Input.MouseButtonEventArgs e) {
            if (renderer == null || renderer.MarkerHits.Count == 0) { return; }
            double clickX = e.GetPosition(TimelineCanvas).X;
            SchedulePhase nearest = null;
            double best = double.MaxValue;
            foreach ((double x, SchedulePhase phase) in renderer.MarkerHits) {
                double dist = Math.Abs(x - clickX);
                if (dist < best) { best = dist; nearest = phase; }
            }
            if (nearest != null) { SelectPhase(nearest); }
        }

        private void SelectPhase(SchedulePhase phase) {
            if (highlightLine != null) { TimelineCanvas.Children.Remove(highlightLine); }
            highlightLine = renderer.MakeHighlight(phase);
            TimelineCanvas.Children.Add(highlightLine);

            DecisionSnapshot d = phase.InitiatingDecision;
            if (phase.IsWait) {
                DecisionLabel.Text = $"Wait at {phase.Start:HH:mm} until {Fmt(phase.WaitUntil)} — nothing eligible. Candidates and why each was rejected:";
            } else {
                string filters = phase.Filters.Count > 0 ? $" [{string.Join(", ", phase.Filters)}]" : "";
                int n = d?.Candidates?.Count ?? 0;
                DecisionLabel.Text = $"Switch at {phase.Start:HH:mm} → {phase.ProjectName} / {phase.TargetName}{filters} — chosen from {n} candidate(s). Select a candidate to see its rule breakdown:";
            }

            List<TimelineCandidateRow> rows = new List<TimelineCandidateRow>();
            if (d != null) {
                foreach (TargetEvaluation c in d.Candidates
                        .OrderByDescending(c => c.Selected)
                        .ThenByDescending(c => c.Scored ? c.TotalScore.GetValueOrDefault() : double.MinValue)
                        .ThenBy(c => c.TargetName)) {
                    rows.Add(new TimelineCandidateRow {
                        Target = $"{c.ProjectName} / {c.TargetName}",
                        Status = c.Selected ? "✓ selected" : (c.Rejected ? c.RejectedReason : (c.Scored ? "considered" : "—")),
                        Score = c.Scored ? (c.TotalScore.GetValueOrDefault() * 100).ToString("0.00") : "—",
                        Eval = c
                    });
                }
            }

            CandidatesGrid.ItemsSource = rows;
            if (rows.Count > 0) { CandidatesGrid.SelectedIndex = 0; }
        }

        private void CandidatesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) {
            TimelineCandidateRow row = CandidatesGrid.SelectedItem as TimelineCandidateRow;
            if (row == null || row.Eval == null || !row.Eval.Scored) {
                RulesGrid.ItemsSource = null;
                return;
            }

            List<TimelineRuleRow> rules = new List<TimelineRuleRow>();
            foreach (RuleContribution c in row.Eval.RuleContributions) {
                rules.Add(new TimelineRuleRow {
                    Rule = c.Enabled ? c.RuleName : c.RuleName + " (off)",
                    Raw = c.Enabled ? (c.RawScore * 100).ToString("0.0") : "—",
                    Weight = c.WeightPercent.ToString("0") + "%",
                    Contribution = (c.Contribution * 100).ToString("0.00")
                });
            }
            RulesGrid.ItemsSource = rules;
        }

        private Brush TextBrush => ResourceBrush("ButtonForegroundBrush", Color.FromRgb(0xDD, 0xDD, 0xDD));

        private Brush ResourceBrush(string key, Color fallback) {
            object res = TryFindResource(key);
            return res is Brush b ? b : new SolidColorBrush(fallback);
        }

        private static string Fmt(DateTime? dt) => dt.HasValue ? dt.Value.ToString("HH:mm") : "—";
    }

    public class TimelineCandidateRow {
        public string Target { get; set; }
        public string Status { get; set; }
        public string Score { get; set; }
        public TargetEvaluation Eval { get; set; }
    }

    public class TimelineRuleRow {
        public string Rule { get; set; }
        public string Raw { get; set; }
        public string Weight { get; set; }
        public string Contribution { get; set; }
    }
}
