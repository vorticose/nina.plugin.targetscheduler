using NINA.Plugin.TargetScheduler.Planning.Explain;
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace NINA.Plugin.TargetScheduler.Controls.PlanPreview {

    /*
     * **CUSTOM FORK** Planning insight / scoring transparency feature.
     *
     * Dependency-light renderer for the night timeline. It draws onto a plain WPF Canvas and builds the
     * legend into a plain WPF Panel — no NINA resources, no plugin services, no DataContext. Brushes that
     * could be theme-driven (text) are injected by the caller. This separation lets:
     *   - TimelineView (the real control) draw with NINA's theme brush, and
     *   - a standalone preview harness render the exact same graphic to PNG from sample data.
     */

    public class TimelineRenderer {
        public const double TopMargin = 28;
        public const double BottomPad = 14;
        private const double LeftMargin = 150;
        private const double PxPerHour = 90;
        private const double LaneHeight = 72;
        private const double BandHeight = 12;
        private const double RightPad = 24;

        private readonly PlanExplanation ex;
        private readonly Brush textBrush;
        private Canvas canvas;

        public double CanvasWidth { get; private set; }
        public double CanvasHeight { get; private set; }
        public List<(double X, SchedulePhase Phase)> MarkerHits { get; } = new List<(double, SchedulePhase)>();

        public TimelineRenderer(PlanExplanation ex, Brush textBrush) {
            this.ex = ex;
            this.textBrush = textBrush ?? Frozen(Color.FromRgb(0xDD, 0xDD, 0xDD));
        }

        private double PlotRight => LeftMargin + (ex.NightEnd - ex.NightStart).TotalHours * PxPerHour;

        public double X(DateTime t) {
            DateTime clamped = t < ex.NightStart ? ex.NightStart : (t > ex.NightEnd ? ex.NightEnd : t);
            return LeftMargin + (clamped - ex.NightStart).TotalHours * PxPerHour;
        }

        public void DrawTimeline(Canvas target) {
            canvas = target;
            canvas.Children.Clear();
            MarkerHits.Clear();

            double plotHours = (ex.NightEnd - ex.NightStart).TotalHours;
            if (plotHours <= 0 || ex.Targets.Count == 0) { return; }

            double plotWidth = plotHours * PxPerHour;
            CanvasWidth = LeftMargin + plotWidth + RightPad;
            CanvasHeight = TopMargin + ex.Targets.Count * LaneHeight + BottomPad;
            canvas.Width = CanvasWidth;
            canvas.Height = CanvasHeight;

            double plotBottom = CanvasHeight - BottomPad;

            DrawTwilightShading(plotBottom);
            DrawHourGrid(plotBottom);
            for (int i = 0; i < ex.Targets.Count; i++) {
                DrawLane(ex.Targets[i], TopMargin + i * LaneHeight);
            }
            DrawMarkers(plotBottom);
        }

        public Line MakeHighlight(SchedulePhase phase) {
            double x = X(phase.Start);
            return new Line {
                X1 = x,
                Y1 = TopMargin,
                X2 = x,
                Y2 = CanvasHeight - BottomPad,
                Stroke = HighlightBrush,
                StrokeThickness = 5
            };
        }

        private void DrawTwilightShading(double plotBottom) {
            ShadeSpan(ex.Twilight.CivilStart, ex.Twilight.CivilEnd, ShadeCivil, plotBottom);
            ShadeSpan(ex.Twilight.NauticalStart, ex.Twilight.NauticalEnd, ShadeNautical, plotBottom);
            ShadeSpan(ex.Twilight.AstronomicalStart, ex.Twilight.AstronomicalEnd, ShadeAstro, plotBottom);
        }

        private void ShadeSpan(DateTime? start, DateTime? end, Brush brush, double plotBottom) {
            if (!start.HasValue || !end.HasValue) { return; }
            double x1 = X(start.Value);
            double x2 = X(end.Value);
            if (x2 <= x1) { return; }
            AddRect(x1, TopMargin, x2 - x1, plotBottom - TopMargin, brush, null);
        }

        private void DrawHourGrid(double plotBottom) {
            DateTime t = new DateTime(ex.NightStart.Year, ex.NightStart.Month, ex.NightStart.Day, ex.NightStart.Hour, 0, 0);
            if (t < ex.NightStart) { t = t.AddHours(1); }

            for (; t <= ex.NightEnd; t = t.AddHours(1)) {
                double x = X(t);
                AddLine(x, TopMargin, x, plotBottom, GridBrush, 1, false);
                AddText(x + 2, 8, t.ToString("HH:mm"), textBrush, 10, false);
            }
            AddLine(LeftMargin, TopMargin, LeftMargin, plotBottom, GridBrush, 1, false);
        }

        private void DrawLane(TargetTimeline tl, double laneTop) {
            double bandBottom = laneTop + LaneHeight - 2;
            double bandTop = bandBottom - BandHeight;
            double altTop = laneTop + 4;
            double altBottom = bandTop - 3;

            AddLine(0, laneTop, PlotRight, laneTop, LaneBrush, 1, false);

            AddText(4, laneTop + 4, Truncate(tl.TargetName, 22), textBrush, 11, true);
            AddText(4, laneTop + 20, Truncate(tl.ProjectName, 24), textBrush, 9, false);
            AddText(4, laneTop + 36, $"peak {tl.PeakAltitude:0}° / min {tl.MinimumAltitude:0}°", textBrush, 9, false);

            foreach (ScheduledBlock b in tl.ScheduledBlocks) {
                double bx1 = X(b.Start);
                double bx2 = X(b.End);
                double w = Math.Max(2, bx2 - bx1);
                Rectangle r = AddRect(bx1, laneTop + 2, w, LaneHeight - 4, ScheduledFill, ScheduledStroke);
                r.ToolTip = $"Scheduled {b.Start:HH:mm}–{b.End:HH:mm}" + (string.IsNullOrEmpty(b.Filter) ? "" : $"  [{b.Filter}]");
                if (!string.IsNullOrEmpty(b.Filter) && w > 24) {
                    AddText(bx1 + 3, laneTop + 2, b.Filter, ScheduledStroke, 9, true);
                }
            }

            DrawAltLine(tl.MinimumAltitude, altTop, altBottom, MinAltBrush);
            if (tl.MaximumAltitude > 0 && tl.MaximumAltitude < 90) {
                DrawAltLine(tl.MaximumAltitude, altTop, altBottom, MaxAltBrush);
            }

            if (tl.AltitudeSamples.Count > 1) {
                Polyline pl = new Polyline { Stroke = AltitudeBrush, StrokeThickness = 1.5 };
                PointCollection pts = new PointCollection();
                foreach (AltitudeSample s in tl.AltitudeSamples) {
                    pts.Add(new Point(X(s.Time), YForAltitude(s.Altitude, altTop, altBottom)));
                }
                pl.Points = pts;
                canvas.Children.Add(pl);
            }

            foreach (EligibilitySegment seg in tl.EligibilitySegments) {
                double sx1 = X(seg.Start);
                double sx2 = X(seg.End);
                double w = Math.Max(1, sx2 - sx1);
                Brush fill = seg.Eligible ? EligibleBrush : ReasonBrush(seg.Reason);
                Rectangle r = AddRect(sx1, bandTop, w, BandHeight, fill, null);
                r.ToolTip = $"{seg.Start:HH:mm}–{seg.End:HH:mm}: " + (seg.Eligible ? "eligible" : seg.Reason ?? "rejected");
            }
        }

        private void DrawAltLine(double altitude, double altTop, double altBottom, Brush brush) {
            double y = YForAltitude(altitude, altTop, altBottom);
            Line ln = new Line {
                X1 = LeftMargin,
                Y1 = y,
                X2 = PlotRight,
                Y2 = y,
                Stroke = brush,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 3 }
            };
            canvas.Children.Add(ln);
        }

        private double YForAltitude(double alt, double altTop, double altBottom) {
            double a = Math.Max(0, Math.Min(90, alt));
            return altBottom - (a / 90.0) * (altBottom - altTop);
        }

        private void DrawMarkers(double plotBottom) {
            foreach (SchedulePhase p in ex.Phases) {
                double x = X(p.Start);
                Brush brush = p.IsWait ? WaitMarker : SwitchMarker;
                AddLine(x, TopMargin, x, plotBottom, brush, p.IsWait ? 1 : 1.5, p.IsWait);

                Polygon tri = new Polygon {
                    Fill = brush,
                    Points = new PointCollection { new Point(x - 5, TopMargin - 8), new Point(x + 5, TopMargin - 8), new Point(x, TopMargin) }
                };
                tri.ToolTip = p.IsWait ? $"Wait at {p.Start:HH:mm}" : $"Switch to {p.TargetName} at {p.Start:HH:mm}";
                canvas.Children.Add(tri);

                MarkerHits.Add((x, p));
            }
        }

        public void DrawLegend(Panel panel) {
            panel.Children.Clear();
            AddLegendItem(panel, EligibleBrush, "eligible");
            AddLegendItem(panel, ReasonBrush("lower score"), "lower score");
            AddLegendItem(panel, ReasonBrush("max altitude"), "altitude");
            AddLegendItem(panel, ReasonBrush("moon avoidance"), "moon");
            AddLegendItem(panel, ReasonBrush("twilight"), "twilight");
            AddLegendItem(panel, ReasonBrush("not visible"), "visibility");
            AddLegendItem(panel, ReasonBrush("before meridian window"), "meridian");
            AddLegendItem(panel, ScheduledStroke, "scheduled");
            AddLegendItem(panel, SwitchMarker, "▼ switch / wait");

            if (ex.CompletedTargets != null && ex.CompletedTargets.Count > 0) {
                panel.Children.Add(new TextBlock {
                    Text = $"({ex.CompletedTargets.Count} complete target(s) hidden)",
                    Foreground = textBrush,
                    FontSize = 10,
                    FontStyle = FontStyles.Italic,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0, 0, 0)
                });
            }
        }

        private void AddLegendItem(Panel panel, Brush brush, string label) {
            StackPanel sp = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 12, 2) };
            sp.Children.Add(new Rectangle { Width = 12, Height = 12, Fill = brush, Margin = new Thickness(0, 0, 4, 0) });
            sp.Children.Add(new TextBlock { Text = label, Foreground = textBrush, FontSize = 10, VerticalAlignment = VerticalAlignment.Center });
            panel.Children.Add(sp);
        }

        // -- Reason -> color (semi-transparent), matched on the planner's reason vocabulary --
        public static Brush ReasonBrush(string reason) {
            string r = (reason ?? "").ToLowerInvariant();
            if (r.Contains("lower score")) { return Frozen(Color.FromArgb(0xA0, 0xFF, 0x98, 0x00)); }
            if (r.Contains("max altitude")) { return Frozen(Color.FromArgb(0xA0, 0x9C, 0x27, 0xB0)); }
            if (r.Contains("moon")) { return Frozen(Color.FromArgb(0xA0, 0x00, 0xBC, 0xD4)); }
            if (r.Contains("twilight")) { return Frozen(Color.FromArgb(0xA0, 0x3F, 0x51, 0xB5)); }
            if (r.Contains("meridian")) { return Frozen(Color.FromArgb(0xA0, 0x79, 0x55, 0x48)); }
            if (r.Contains("humidity")) { return Frozen(Color.FromArgb(0xA0, 0x00, 0x96, 0x88)); }
            if (r.Contains("complete")) { return Frozen(Color.FromArgb(0xA0, 0x55, 0x55, 0x55)); }
            if (r.Contains("visible") || r.Contains("rises")) { return Frozen(Color.FromArgb(0xA0, 0x60, 0x7D, 0x8B)); }
            return Frozen(Color.FromArgb(0xA0, 0x88, 0x88, 0x88));
        }

        // -- palette --
        private static readonly Brush GridBrush = Frozen(Color.FromArgb(0x40, 0x88, 0x88, 0x88));
        private static readonly Brush LaneBrush = Frozen(Color.FromArgb(0x30, 0x88, 0x88, 0x88));
        private static readonly Brush EligibleBrush = Frozen(Color.FromArgb(0x88, 0x4C, 0xAF, 0x50));
        private static readonly Brush AltitudeBrush = Frozen(Color.FromArgb(0xFF, 0x8B, 0xC3, 0x4A));
        private static readonly Brush MinAltBrush = Frozen(Color.FromArgb(0xFF, 0xE5, 0x39, 0x35));
        private static readonly Brush MaxAltBrush = Frozen(Color.FromArgb(0xFF, 0xFF, 0x70, 0x43));
        private static readonly Brush ScheduledFill = Frozen(Color.FromArgb(0x55, 0x32, 0xA0, 0xFF));
        private static readonly Brush ScheduledStroke = Frozen(Color.FromArgb(0xFF, 0x32, 0xA0, 0xFF));
        private static readonly Brush SwitchMarker = Frozen(Color.FromArgb(0xFF, 0xFF, 0xC1, 0x07));
        private static readonly Brush WaitMarker = Frozen(Color.FromArgb(0xFF, 0x9E, 0x9E, 0x9E));
        private static readonly Brush HighlightBrush = Frozen(Color.FromArgb(0x90, 0xFF, 0xEB, 0x3B));
        private static readonly Brush ShadeCivil = Frozen(Color.FromArgb(0x22, 0x00, 0x00, 0x00));
        private static readonly Brush ShadeNautical = Frozen(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
        private static readonly Brush ShadeAstro = Frozen(Color.FromArgb(0x44, 0x00, 0x00, 0x00));

        // -- low-level helpers --
        private Rectangle AddRect(double x, double y, double w, double h, Brush fill, Brush stroke) {
            Rectangle r = new Rectangle { Width = Math.Max(0, w), Height = Math.Max(0, h), Fill = fill };
            if (stroke != null) { r.Stroke = stroke; r.StrokeThickness = 1; }
            Canvas.SetLeft(r, x);
            Canvas.SetTop(r, y);
            canvas.Children.Add(r);
            return r;
        }

        private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness, bool dashed) {
            Line ln = new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness };
            if (dashed) { ln.StrokeDashArray = new DoubleCollection { 3, 3 }; }
            canvas.Children.Add(ln);
        }

        private void AddText(double x, double y, string text, Brush brush, double size, bool bold) {
            TextBlock tb = new TextBlock {
                Text = text,
                Foreground = brush,
                FontSize = size,
                FontWeight = bold ? FontWeights.Bold : FontWeights.Normal
            };
            Canvas.SetLeft(tb, x);
            Canvas.SetTop(tb, y);
            canvas.Children.Add(tb);
        }

        private static string Truncate(string s, int max) {
            if (string.IsNullOrEmpty(s)) { return ""; }
            return s.Length <= max ? s : s.Substring(0, max - 1) + "…";
        }

        private static Brush Frozen(Color c) {
            SolidColorBrush b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
