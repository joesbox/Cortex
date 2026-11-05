using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace Cortex.Controls
{
    public class ConnectorDiagramControl : Control
    {
        public static readonly StyledProperty<IEnumerable<ConnectorPinDisplayItem>?> PinsProperty =
            AvaloniaProperty.Register<ConnectorDiagramControl, IEnumerable<ConnectorPinDisplayItem>?>(nameof(Pins));

        private static readonly Rect DesignViewport = new(0, 150, ConnectorPinCatalog.ViewWidth, 560);
        private static readonly Rect ShellBounds = new(26.3335, 217.763, 971.3325, 366.276);
        private readonly Dictionary<ConnectorPinDisplayItem, PropertyChangedEventHandler> _itemHandlers = new();
        private IEnumerable<ConnectorPinDisplayItem>? _attachedPins;

        static ConnectorDiagramControl()
        {
            AffectsRender<ConnectorDiagramControl>(PinsProperty);
        }

        public ConnectorDiagramControl()
        {
            ClipToBounds = true;
        }

        public IEnumerable<ConnectorPinDisplayItem>? Pins
        {
            get => GetValue(PinsProperty);
            set => SetValue(PinsProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == PinsProperty)
            {
                DetachPins();
                AttachPins(change.NewValue as IEnumerable<ConnectorPinDisplayItem>);
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            Rect bounds = Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            Rect contentBounds = bounds.Deflate(Math.Max(10, Math.Min(bounds.Width, bounds.Height) * 0.035));
            Rect diagramBounds = FitRect(contentBounds, DesignViewport.Width, DesignViewport.Height);
            double scale = diagramBounds.Width / DesignViewport.Width;

            DrawConnectorBody(context, diagramBounds, scale);

            if (Pins == null)
            {
                return;
            }

            Typeface typeface = new("Inter, Segoe UI, Arial", FontStyle.Normal, FontWeight.Bold);
            Pen pinOutlinePen = new(new SolidColorBrush(Color.Parse(ConnectorPinPalette.Outline)), Math.Max(1.6, 2.1 * scale));
            Pen socketRingPen = new(new SolidColorBrush(Color.Parse("#AA0C111B")), Math.Max(2.4, 3.2 * scale));
            Pen highlightRingPen = new(new SolidColorBrush(Color.Parse("#CBA5FF")), Math.Max(2.0, 2.8 * scale));
            IBrush socketBrush = new SolidColorBrush(Color.Parse("#FF121923"));
            IBrush pinShadowBrush = new SolidColorBrush(Color.Parse("#55141722"));
            IBrush highlightOuterGlowBrush = new SolidColorBrush(Color.Parse("#339C4DFF"));
            IBrush highlightInnerGlowBrush = new SolidColorBrush(Color.Parse("#66B77BFF"));

            foreach (ConnectorPinDisplayItem pin in Pins)
            {
                double radius = ConnectorPinCatalog.PinRadius * scale;
                Point center = TransformPoint(diagramBounds, pin.X, pin.Y);

                if (pin.IsHighlighted)
                {
                    context.DrawEllipse(highlightOuterGlowBrush, null, center, radius + (14.5 * scale), radius + (14.5 * scale));
                    context.DrawEllipse(highlightInnerGlowBrush, null, center, radius + (9.5 * scale), radius + (9.5 * scale));
                    context.DrawEllipse(null, highlightRingPen, center, radius + (6.2 * scale), radius + (6.2 * scale));
                }

                context.DrawEllipse(pinShadowBrush, null, new Point(center.X, center.Y + (1.6 * scale)), radius + (1.5 * scale), radius + (1.5 * scale));
                context.DrawEllipse(socketBrush, socketRingPen, center, radius + (4.2 * scale), radius + (4.2 * scale));
                context.DrawEllipse(pin.PinBrush, pinOutlinePen, center, radius, radius);

                double pinFontSize = Math.Max(10, radius * (pin.PinNumber >= 10 ? 0.9 : 1.12));
                FormattedText pinText = new(
                    pin.PinNumberText,
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    typeface,
                    pinFontSize,
                    pin.PinTextBrush);
                Point pinTextOrigin = new(
                    center.X - (pinText.Width / 2),
                    center.Y - (pinText.Height / 2));
                context.DrawText(pinText, pinTextOrigin);
            }
        }

        private static void DrawConnectorBody(DrawingContext context, Rect diagramBounds, double scale)
        {
            Rect shell = TransformRect(diagramBounds, ShellBounds);
            Rect outerShell = shell;
            Rect innerShell = shell.Deflate(9 * scale);
            Rect cavity = new(
                innerShell.X + (46 * scale),
                innerShell.Y + (48 * scale),
                innerShell.Width - (92 * scale),
                innerShell.Height - (96 * scale));
            Rect latchSlot = new(
                shell.Center.X - (48 * scale),
                innerShell.Y + (12 * scale),
                96 * scale,
                18 * scale);

            IBrush shadowBrush = new SolidColorBrush(Color.Parse("#55141722"));
            IBrush outerShellBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new() { Color = Color.Parse("#FF505A66"), Offset = 0 },
                    new() { Color = Color.Parse("#FF343D48"), Offset = 0.45 },
                    new() { Color = Color.Parse("#FF262E37"), Offset = 1 }
                }
            };
            IBrush innerShellBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new() { Color = Color.Parse("#FF6A7380"), Offset = 0 },
                    new() { Color = Color.Parse("#FF444C57"), Offset = 0.55 },
                    new() { Color = Color.Parse("#FF323944"), Offset = 1 }
                }
            };
            IBrush cavityBrush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops = new GradientStops
                {
                    new() { Color = Color.Parse("#FF0E141C"), Offset = 0 },
                    new() { Color = Color.Parse("#FF141C26"), Offset = 0.5 },
                    new() { Color = Color.Parse("#FF0B1017"), Offset = 1 }
                }
            };
            IBrush slotBrush = new SolidColorBrush(Color.Parse("#FF111821"));
            Pen outerPen = new(new SolidColorBrush(Color.Parse("#FF9AA6B2")), Math.Max(1.5, 2.2 * scale));
            Pen innerPen = new(new SolidColorBrush(Color.Parse("#FFCAD1DA")), Math.Max(1.2, 1.8 * scale));
            Pen cavityPen = new(new SolidColorBrush(Color.Parse("#FF728091")), Math.Max(1.0, 1.5 * scale));
            Pen detailPen = new(new SolidColorBrush(Color.Parse("#55FFFFFF")), Math.Max(0.8, 1.0 * scale));

            Rect shadowRect = new(outerShell.X, outerShell.Y + (6 * scale), outerShell.Width, outerShell.Height);
            context.DrawRectangle(shadowBrush, null, shadowRect, 92 * scale, 92 * scale);
            context.DrawRectangle(outerShellBrush, outerPen, outerShell, 92 * scale, 92 * scale);
            context.DrawRectangle(innerShellBrush, innerPen, innerShell, 82 * scale, 82 * scale);
            context.DrawRectangle(cavityBrush, cavityPen, cavity, 58 * scale, 58 * scale);
            context.DrawRectangle(slotBrush, detailPen, latchSlot, 9 * scale, 9 * scale);

            double boltRadius = 9 * scale;
            Point leftBolt = new(cavity.Left + (20 * scale), shell.Center.Y);
            Point rightBolt = new(cavity.Right - (20 * scale), shell.Center.Y);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#FF1A212B")), detailPen, leftBolt, boltRadius, boltRadius);
            context.DrawEllipse(new SolidColorBrush(Color.Parse("#FF1A212B")), detailPen, rightBolt, boltRadius, boltRadius);

            }

        private void AttachPins(IEnumerable<ConnectorPinDisplayItem>? pins)
        {
            _attachedPins = pins;
            if (_attachedPins == null)
            {
                return;
            }

            foreach (ConnectorPinDisplayItem pin in _attachedPins)
            {
                PropertyChangedEventHandler handler = (_, _) => InvalidateVisual();
                _itemHandlers[pin] = handler;
                pin.PropertyChanged += handler;
            }

            if (_attachedPins is INotifyCollectionChanged collection)
            {
                collection.CollectionChanged += Pins_CollectionChanged;
            }
        }

        private void DetachPins()
        {
            if (_attachedPins is INotifyCollectionChanged collection)
            {
                collection.CollectionChanged -= Pins_CollectionChanged;
            }

            foreach ((ConnectorPinDisplayItem pin, PropertyChangedEventHandler handler) in _itemHandlers.ToList())
            {
                pin.PropertyChanged -= handler;
            }

            _itemHandlers.Clear();
            _attachedPins = null;
        }

        private void Pins_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            DetachPins();
            AttachPins(Pins);
            InvalidateVisual();
        }

        private static Rect FitRect(Rect bounds, double contentWidth, double contentHeight)
        {
            double scale = Math.Min(bounds.Width / contentWidth, bounds.Height / contentHeight);
            double width = contentWidth * scale;
            double height = contentHeight * scale;
            double x = bounds.X + ((bounds.Width - width) / 2);
            double y = bounds.Y + ((bounds.Height - height) / 2);
            return new Rect(x, y, width, height);
        }

        private static Rect TransformRect(Rect diagramBounds, Rect sourceRect)
        {
            Point topLeft = TransformPoint(diagramBounds, sourceRect.TopLeft.X, sourceRect.TopLeft.Y);
            Point bottomRight = TransformPoint(diagramBounds, sourceRect.BottomRight.X, sourceRect.BottomRight.Y);
            return new Rect(topLeft, bottomRight);
        }

        private static Point TransformPoint(Rect diagramBounds, double x, double y)
        {
            double scale = diagramBounds.Width / DesignViewport.Width;
            return new Point(
                diagramBounds.X + ((x - DesignViewport.X) * scale),
                diagramBounds.Y + ((y - DesignViewport.Y) * scale));
        }
    }
}
