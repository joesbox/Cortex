using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cortex.ViewModels.IoFlow;
using Nodify.Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.Controls;

public sealed class IoFlowMinimap : Control
{
    private const double PreviewPadding = 8;
    private static readonly IBrush CanvasBackground = new SolidColorBrush(Color.Parse("#1B1E24"));
    private static readonly Pen CanvasBorder = new(new SolidColorBrush(Color.Parse("#443553")), 1);
    private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#557A889A")), 0.6);
    private static readonly Pen ViewportPen = new(new SolidColorBrush(Color.Parse("#800071")), 2);

    public static readonly StyledProperty<NodifyEditor?> EditorProperty =
        AvaloniaProperty.Register<IoFlowMinimap, NodifyEditor?>(nameof(Editor));

    private NodifyEditor? _subscribedEditor;
    private readonly HashSet<ItemContainer> _containers = [];
    private Rect? _dragExtent;

    public NodifyEditor? Editor
    {
        get => GetValue(EditorProperty);
        set => SetValue(EditorProperty, value);
    }

    public IoFlowMinimap()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        ContextRequested += (_, e) => e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeEditor();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        UnsubscribeEditor();
        _dragExtent = null;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty && this.GetVisualRoot() != null)
        {
            UnsubscribeEditor();
            SubscribeEditor();
            InvalidateVisual();
        }
    }

    private void SubscribeEditor()
    {
        if (Editor is not { } editor || _subscribedEditor != null)
            return;
        _subscribedEditor = editor;
        editor.PropertyChanged += OnEditorPropertyChanged;
        editor.LayoutUpdated += OnEditorLayoutUpdated;
        editor.ContainerPrepared += OnContainerPrepared;
        editor.ContainerClearing += OnContainerClearing;
        UpdateContainers();
    }

    private void UnsubscribeEditor()
    {
        if (_subscribedEditor is not { } editor)
            return;
        editor.LayoutUpdated -= OnEditorLayoutUpdated;
        editor.PropertyChanged -= OnEditorPropertyChanged;
        editor.ContainerPrepared -= OnContainerPrepared;
        editor.ContainerClearing -= OnContainerClearing;
        foreach (var container in _containers.ToList())
            RemoveContainer(container);
        _subscribedEditor = null;
    }

    private void OnEditorLayoutUpdated(object? sender, EventArgs e)
    {
        InvalidateVisual();
    }

    private void OnEditorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == NodifyEditor.ViewportLocationProperty || e.Property == NodifyEditor.ViewportSizeProperty
            || e.Property == NodifyEditor.ViewportZoomProperty)
            InvalidateVisual();
    }

    private void UpdateContainers()
    {
        if (_subscribedEditor == null)
            return;
        foreach (var container in _subscribedEditor.GetVisualDescendants().OfType<ItemContainer>())
            AddContainer(container);
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (e.Container is ItemContainer container)
            AddContainer(container);
        InvalidateVisual();
    }

    private void OnContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (e.Container is ItemContainer container)
            RemoveContainer(container);
        InvalidateVisual();
    }

    private void AddContainer(ItemContainer container)
    {
        if (_containers.Add(container))
            container.PreviewLocationChanged += OnPreviewLocationChanged;
    }

    private void RemoveContainer(ItemContainer container)
    {
        if (_containers.Remove(container))
            container.PreviewLocationChanged -= OnPreviewLocationChanged;
    }

    private void OnPreviewLocationChanged(Point location) => InvalidateVisual();

    private IEnumerable<(Rect Bounds, IoFlowNodeViewModel Node)> GetNodeBounds()
    {
        if (Editor is not { } editor)
            yield break;
        foreach (var container in _containers)
        {
            if (container.DataContext is not IoFlowNodeViewModel node || !container.IsVisible
                || container.TranslatePoint(default, editor) is not Point origin)
                continue;
            yield return (new Rect(
                new Point(origin.X / editor.ViewportZoom + editor.ViewportLocation.X,
                    origin.Y / editor.ViewportZoom + editor.ViewportLocation.Y),
                container.Bounds.Size), node);
        }
    }

    private Rect GetExtent()
    {
        var extent = new Rect(Editor!.ViewportLocation, Editor.ViewportSize);
        foreach (var node in GetNodeBounds())
            extent = extent.Union(node.Bounds);
        extent = extent.Inflate(60);
        double width = Math.Max(1, Bounds.Width - PreviewPadding * 2);
        double height = Math.Max(1, Bounds.Height - PreviewPadding * 2);
        double scale = Math.Min(width / extent.Width, height / extent.Height);
        // Extend the world bounds instead of letterboxing the preview background.
        var size = new Size(width / scale, height / scale);
        return new Rect(new Point(extent.Center.X - size.Width / 2, extent.Center.Y - size.Height / 2), size);
    }

    private (double Scale, Point Offset) GetTransform(Rect extent)
    {
        double scale = Math.Min(Math.Max(1, Bounds.Width - PreviewPadding * 2) / extent.Width,
            Math.Max(1, Bounds.Height - PreviewPadding * 2) / extent.Height);
        return (scale, new Point((Bounds.Width - extent.Width * scale) / 2,
            (Bounds.Height - extent.Height * scale) / 2));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.DrawRectangle(Brushes.Transparent, null, new Rect(Bounds.Size));
        if (Editor is not { } editor || editor.ViewportSize.Width <= 0 || editor.ViewportSize.Height <= 0)
            return;
        Rect extent = _dragExtent ?? GetExtent();
        var (scale, offset) = GetTransform(extent);
        Rect Map(Rect rect) => new(
            new Point(offset.X + (rect.X - extent.X) * scale, offset.Y + (rect.Y - extent.Y) * scale),
            new Size(rect.Width * scale, rect.Height * scale));

        Rect canvas = Map(extent);
        context.DrawRectangle(CanvasBackground, CanvasBorder, canvas);
        // Keep the grid aligned to world coordinates, thinning it at small overview scales.
        double spacing = 50;
        while (spacing * scale < 6)
            spacing *= 2;
        var grid = new StreamGeometry();
        using (var geometry = grid.Open())
        {
            for (double x = Math.Ceiling(extent.X / spacing) * spacing; x < extent.Right; x += spacing)
            {
                double mappedX = offset.X + (x - extent.X) * scale;
                geometry.BeginFigure(new Point(mappedX, canvas.Top), false);
                geometry.LineTo(new Point(mappedX, canvas.Bottom));
                geometry.EndFigure(false);
            }
            for (double y = Math.Ceiling(extent.Y / spacing) * spacing; y < extent.Bottom; y += spacing)
            {
                double mappedY = offset.Y + (y - extent.Y) * scale;
                geometry.BeginFigure(new Point(canvas.Left, mappedY), false);
                geometry.LineTo(new Point(canvas.Right, mappedY));
                geometry.EndFigure(false);
            }
        }
        using (context.PushClip(canvas))
            context.DrawGeometry(null, GridPen, grid);
        foreach (var (bounds, node) in GetNodeBounds())
        {
            var brush = new SolidColorBrush(Color.Parse(node.BorderHex));
            context.DrawRectangle(brush, null, Map(bounds), 1, 1);
        }
        context.DrawRectangle(null, ViewportPen,
            Map(new Rect(editor.ViewportLocation, editor.ViewportSize)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Editor is { } editor && editor.ViewportSize.Width > 0 && editor.ViewportSize.Height > 0
            && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _dragExtent = GetExtent();
            e.Pointer.Capture(this);
            Navigate(e.GetPosition(this));
        }
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragExtent != null && e.Pointer.Captured == this)
        {
            Navigate(e.GetPosition(this));
            e.Handled = true;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left && _dragExtent != null)
        {
            _dragExtent = null;
            e.Pointer.Capture(null);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragExtent = null;
        InvalidateVisual();
    }

    private void Navigate(Point point)
    {
        if (Editor is not { } editor || _dragExtent is not Rect extent)
            return;
        var (scale, offset) = GetTransform(extent);
        var center = new Point(
            extent.X + (Math.Clamp(point.X, 0, Bounds.Width) - offset.X) / scale,
            extent.Y + (Math.Clamp(point.Y, 0, Bounds.Height) - offset.Y) / scale);
        editor.SetCurrentValue(NodifyEditor.ViewportLocationProperty,
            new Point(center.X - editor.ViewportSize.Width / 2, center.Y - editor.ViewportSize.Height / 2));
        InvalidateVisual();
    }
}
