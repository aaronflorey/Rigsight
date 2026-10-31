using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Rigsight.Core.Boot;

namespace Rigsight.Controls;

public sealed class BootActivityChart : FrameworkElement
{
    public bool Disk { get; set; }
    private BootReport? report;
    public void SetReport(BootReport value) { report = value; InvalidateVisual(); }
    protected override void OnRender(DrawingContext dc)
    {
        if (report is null || ActualWidth < 100 || ActualHeight < 60) return;
        var brush = TryFindResource("MutedBrush") as Brush ?? Brushes.Gray;
        var line = TryFindResource(Disk ? "RamBrush" : "TextBrush") as Brush ?? Brushes.SteelBlue;
        double Value(BootActivity point) => Disk ? point.DiskBytes / 1048576.0 : point.CpuSeconds;
        double max = Math.Max(1, report.Activity.Select(Value).DefaultIfEmpty(0).Max());
        double width = ActualWidth - 66, height = ActualHeight - 35, duration = Math.Max(1, report.DurationSeconds);
        void Label(string text, double x, double y) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip), new Point(x, y));
        dc.DrawLine(new Pen(brush, 1), new Point(50, 10), new Point(50, height + 10));
        dc.DrawLine(new Pen(brush, 1), new Point(50, height + 10), new Point(50 + width, height + 10));
        Label(max.ToString("F1"), 4, 5); Label("0", 30, height); Label("0 s", 50, height + 17); Label($"{duration:F0} s", 22 + width, height + 17);
        var lookup = report.Activity.ToDictionary(p => p.Second);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(50, height + 10), false, false);
            for (int second = 0; second <= Math.Ceiling(duration); second++)
            {
                double value = lookup.TryGetValue(second, out var point) ? Value(point) : 0;
                context.LineTo(new Point(50 + Math.Min(second / duration, 1) * width, 10 + height * (1 - value / max)), true, false);
            }
        }
        dc.DrawGeometry(null, new Pen(line, 1.8), geometry);
    }
}
