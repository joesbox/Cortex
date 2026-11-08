using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using System;

namespace Cortex.Controls;

public sealed class StationaryToolTipService : IDisposable
{
    private const int MinimumIdleDelayMilliseconds = 400;
    private readonly IDisposable[] _subscriptions;
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Background);
    private TopLevel? _root;
    private Point _position;
    private Control? _host;
    private Control? _openingControl;

    public StationaryToolTipService()
    {
        _timer.Tick += ShowToolTip;
        _subscriptions =
        [
            InputElement.PointerMovedEvent.AddClassHandler<TopLevel>(OnPointerMoved, handledEventsToo: true),
            InputElement.PointerEnteredEvent.AddClassHandler<TopLevel>(OnPointerMoved, handledEventsToo: true),
            InputElement.PointerExitedEvent.AddClassHandler<TopLevel>((_, _) => Reset(), handledEventsToo: true),
            InputElement.PointerPressedEvent.AddClassHandler<TopLevel>((_, _) => Reset(), RoutingStrategies.Tunnel, true),
            InputElement.PointerWheelChangedEvent.AddClassHandler<TopLevel>((_, _) => Reset(), RoutingStrategies.Tunnel, true),
            InputElement.KeyDownEvent.AddClassHandler<TopLevel>((_, _) => Reset(), RoutingStrategies.Tunnel, true),
            ToolTip.ToolTipOpeningEvent.AddClassHandler<Control>((control, args) =>
            {
                // Avalonia's normal hover timer does not restart on movement within a control.
                if (!ReferenceEquals(control, _openingControl))
                {
                    args.Cancel = true;
                }
            }, handledEventsToo: true),
        ];
    }

    public void Dispose()
    {
        Reset();
        _timer.Tick -= ShowToolTip;
        foreach (IDisposable subscription in _subscriptions)
        {
            subscription.Dispose();
        }
    }

    private void OnPointerMoved(TopLevel root, PointerEventArgs args)
    {
        if (args.Pointer.Type != PointerType.Mouse || args.Source is not Visual source
            || !ReferenceEquals(TopLevel.GetTopLevel(source), root))
        {
            return;
        }

        PointerPoint point = args.GetCurrentPoint(root);
        if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed
            || point.Properties.IsMiddleButtonPressed || point.Properties.IsXButton1Pressed
            || point.Properties.IsXButton2Pressed)
        {
            Reset();
            return;
        }

        Control? host = FindToolTipHost(root.InputHitTest(point.Position, enabledElementsOnly: false) as Visual);
        if (ReferenceEquals(root, _root) && point.Position == _position && ReferenceEquals(host, _host))
        {
            return;
        }

        Reset();
        _root = root;
        _position = point.Position;
        _host = host;
        root.Closed += OnRootClosed;
        if (root is Window window)
        {
            window.Deactivated += OnRootClosed;
        }

        if (host != null)
        {
            host.DetachedFromVisualTree += OnHostDetached;
            _timer.Interval = TimeSpan.FromMilliseconds(Math.Max(MinimumIdleDelayMilliseconds, ToolTip.GetShowDelay(host)));
            _timer.Start();
        }
    }

    private static Control? FindToolTipHost(Visual? visual)
    {
        for (; visual != null; visual = visual.GetVisualParent())
        {
            if (visual is ToolTip)
            {
                return null;
            }

            if (visual is Control control)
            {
                if (!ToolTip.GetServiceEnabled(control))
                {
                    return null;
                }

                if (ToolTip.GetTip(control) != null && (control.IsEffectivelyEnabled || ToolTip.GetShowOnDisabled(control)))
                {
                    return control;
                }
            }
        }

        return null;
    }

    private void ShowToolTip(object? sender, EventArgs args)
    {
        _timer.Stop();
        if (_host == null || _root == null || !_host.IsAttachedToVisualTree()
            || !ReferenceEquals(_host, FindToolTipHost(_root.InputHitTest(_position, enabledElementsOnly: false) as Visual)))
        {
            Reset();
            return;
        }

        _openingControl = _host;
        try
        {
            ToolTip.SetIsOpen(_host, true);
        }
        finally
        {
            _openingControl = null;
        }
    }

    private void OnRootClosed(object? sender, EventArgs args) => Reset();

    private void OnHostDetached(object? sender, VisualTreeAttachmentEventArgs args) => Reset();

    private void Reset()
    {
        _timer.Stop();
        Control? host = _host;
        TopLevel? root = _root;
        _host = null;
        _root = null;
        if (host != null)
        {
            host.DetachedFromVisualTree -= OnHostDetached;
            ToolTip.SetIsOpen(host, false);
        }

        if (root != null)
        {
            root.Closed -= OnRootClosed;
            if (root is Window window)
            {
                window.Deactivated -= OnRootClosed;
            }
        }
    }
}
