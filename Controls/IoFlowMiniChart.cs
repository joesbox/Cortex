using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using System;
using System.Globalization;
using System.Linq;

namespace Cortex.Controls;

public sealed class IoFlowMiniChart : Control
{
    private static readonly IBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#1B1E24"));
    private static readonly IBrush TextBrush = new SolidColorBrush(Color.Parse("#D0C3E8"));
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.FromRgb(120, 120, 120)), 1);
    private static readonly Pen MinorVerticalGridPen = new(new SolidColorBrush(Color.FromRgb(50, 50, 50)), 0.5);
    private static readonly Pen MinorHorizontalGridPen = new(new SolidColorBrush(Color.FromRgb(34, 34, 34)), 0.5);
    private static readonly Pen TracePen = new(new SolidColorBrush(Color.Parse("#52E08A")), 1.5)
    {
        LineCap = PenLineCap.Flat,
        LineJoin = PenLineJoin.Bevel,
    };
    private readonly DispatcherTimer _timer;

    public IoFlowMiniChart()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        Height = 140;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background,
            (_, _) => InvalidateVisual());
        _timer.Stop();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (IsVisible)
            _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty && _timer != null)
        {
            if (IsVisible && VisualRoot != null)
                _timer.Start();
            else
                _timer.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var bounds = new Rect(Bounds.Size);
        context.DrawRectangle(BackgroundBrush, null, bounds, 4, 4);
        if (DataContext is not IoFlowNodeViewModel node || bounds.Width < 20)
            return;

        TimeSpan now = IoFlowLiveHistory.Now;
        node.LiveHistory.Prune(now);
        var samples = node.LiveHistory.Samples;
        var values = samples.Where(sample => sample.Value.HasValue).Select(sample => sample.Value!.Value).ToArray();
        var plot = new Rect(5, 5, bounds.Width - 10, bounds.Height - 29);
        const int majorIntervals = 4;
        const int subdivisions = 10;
        for (int i = 0; i <= majorIntervals * subdivisions; i++)
        {
            double fraction = (double)i / (majorIntervals * subdivisions);
            double x = plot.X + plot.Width * fraction;
            double y = plot.Y + plot.Height * fraction;
            bool major = i % subdivisions == 0;
            context.DrawLine(major ? GridPen : MinorVerticalGridPen, new Point(x, plot.Y), new Point(x, plot.Bottom));
            context.DrawLine(major ? GridPen : MinorHorizontalGridPen, new Point(plot.X, y), new Point(plot.Right, y));
        }
        DrawText(context, "-10 s", new Point(5, bounds.Height - 22));
        DrawText(context, "now", new Point(bounds.Width - 35, bounds.Height - 22));
        if (values.Length == 0)
        {
            DrawText(context, "No live data", new Point(8, 24));
            return;
        }

        double minimum;
        double maximum;
        if (node is AnalogueInputNodeViewModel analogue && analogue.Input.ReportsConnectorVoltage)
        {
            minimum = AnalogueInput.ConnectorVoltageMinimum;
            maximum = AnalogueInput.ConnectorVoltageMaximum;
        }
        else
        {
            minimum = values.Min();
            maximum = values.Max();
            double padding = Math.Max((maximum - minimum) * 0.1, 0.5);
            minimum -= padding;
            maximum += padding;
        }
        Point? previous = null;
        TimeSpan? previousTime = null;
        var trace = new StreamGeometry();
        using (var path = trace.Open())
        {
            foreach (var sample in samples)
            {
                if (sample.Value is not double value)
                {
                    if (previous.HasValue)
                        path.EndFigure(false);
                    previous = null;
                    previousTime = null;
                    continue;
                }
                double x = plot.Right - (now - sample.Timestamp).TotalSeconds / IoFlowLiveHistory.Window.TotalSeconds * plot.Width;
                double y = plot.Bottom - (value - minimum) / (maximum - minimum) * plot.Height;
                var point = new Point(x, y);
                // A single unfilled path avoids per-segment caps resembling sample markers.
                if (previous is Point start && previousTime is TimeSpan time && sample.Timestamp - time <= TimeSpan.FromSeconds(1))
                {
                    if (node is DigitalInputNodeViewModel)
                        path.LineTo(new Point(point.X, start.Y));
                    path.LineTo(point);
                }
                else
                {
                    if (previous.HasValue)
                        path.EndFigure(false);
                    path.BeginFigure(point, false);
                }
                previous = point;
                previousTime = sample.Timestamp;
            }
            if (previous.HasValue)
                path.EndFigure(false);
        }
        using (context.PushClip(plot))
            context.DrawGeometry(null, TracePen, trace);
    }

    private static void DrawText(DrawingContext context, string text, Point origin)
    {
        context.DrawText(new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, 14, TextBrush), origin);
    }
}
