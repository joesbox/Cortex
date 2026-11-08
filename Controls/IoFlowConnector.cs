using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Interactivity;
using Nodify.Avalonia;
using Nodify.Avalonia.Connections;
using Nodify.Avalonia.Extensions;
using System;

namespace Cortex.Controls;

public sealed class IoFlowConnector : Connector
{
    protected override Type StyleKeyOverride => typeof(Connector);

    public static readonly DirectProperty<IoFlowConnector, Point> WireAnchorProperty =
        AvaloniaProperty.RegisterDirect<IoFlowConnector, Point>(
            nameof(WireAnchor), connector => connector.WireAnchor,
            defaultBindingMode: BindingMode.OneWayToSource);

    private Point _wireAnchor;
    private Point? _previewLocation;
    private ItemContainer? _subscribedContainer;

    public IoFlowConnector()
    {
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += OnSizeChanged;
    }

    public Point WireAnchor
    {
        get => _wireAnchor;
        private set => SetAndRaise(WireAnchorProperty, ref _wireAnchor, value);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (Container == null || _subscribedContainer != null)
            return;

        _subscribedContainer = Container;
        _subscribedContainer.PreviewLocationChanged += OnPreviewLocationChanged;
        _subscribedContainer.LocationChanged += OnLocationChanged;
        _subscribedContainer.SizeChanged += OnSizeChanged;
        UpdateWireAnchor();
    }

    private void OnUnloaded(object? sender, RoutedEventArgs e)
    {
        if (_subscribedContainer == null)
            return;

        _subscribedContainer.PreviewLocationChanged -= OnPreviewLocationChanged;
        _subscribedContainer.LocationChanged -= OnLocationChanged;
        _subscribedContainer.SizeChanged -= OnSizeChanged;
        _subscribedContainer = null;
        _previewLocation = null;
    }

    private void OnPreviewLocationChanged(Point location)
    {
        // Nodify's size-change handler otherwise recalculates from the old committed location.
        _previewLocation = location;
        UpdateWireAnchor();
    }

    private void OnLocationChanged(object? sender, RoutedEventArgs e)
    {
        _previewLocation = null;
        UpdateWireAnchor();
    }

    private void OnSizeChanged(object? sender, SizeChangedEventArgs e) => UpdateWireAnchor();

    private void UpdateWireAnchor()
    {
        if (Thumb == null || Container == null)
            return;

        Vector margin = Container.Bounds.Size.ToVector() - Container.DesiredSize.ToVector();
        Point? relative = Thumb.TranslatePoint((Point)(Thumb.Bounds.Size.ToVector() / 2 - margin / 2), Container);
        if (relative is not Point position)
            return;

        Point location = Container.IsPreviewingLocation && _previewLocation is Point preview
            ? preview
            : Container.Location;
        WireAnchor = new Point(location.X + position.X, location.Y + position.Y);
    }
}
