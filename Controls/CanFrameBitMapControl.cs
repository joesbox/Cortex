using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
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
    /// <summary>
    /// Byte x bit grid of one CAN frame showing which bits each signal occupies.
    /// </summary>
    public class CanFrameBitMapControl : Control
    {
        public static readonly StyledProperty<IEnumerable<CustomCanSignal>?> SignalsProperty =
            AvaloniaProperty.Register<CanFrameBitMapControl, IEnumerable<CustomCanSignal>?>(nameof(Signals));

        public static readonly StyledProperty<CustomCanSignal?> SelectedSignalProperty =
            AvaloniaProperty.Register<CanFrameBitMapControl, CustomCanSignal?>(nameof(SelectedSignal), defaultBindingMode: BindingMode.TwoWay);

        public static readonly StyledProperty<int> DlcProperty =
            AvaloniaProperty.Register<CanFrameBitMapControl, int>(nameof(Dlc), 8);

        private const int Bytes = 8;
        private const int BitsPerByte = 8;

        private static readonly Color[] Palette =
        {
            Color.Parse("#9C6BFF"), Color.Parse("#3FC1C9"), Color.Parse("#F5A65B"), Color.Parse("#5BD18B"),
            Color.Parse("#E86A92"), Color.Parse("#6BA8FF"), Color.Parse("#D7B66B"), Color.Parse("#C77DFF"),
            Color.Parse("#4FD1A5"), Color.Parse("#FF8A65"), Color.Parse("#8FB3FF"), Color.Parse("#E0D46B"),
        };

        private static readonly IBrush EmptyCellBrush = new SolidColorBrush(Color.Parse("#FF1B1A2A"));
        private static readonly IBrush UnusedCellBrush = new SolidColorBrush(Color.Parse("#FF121119"));
        private static readonly IBrush HeaderBrush = new SolidColorBrush(Color.Parse("#FFD0C3E8"));
        private static readonly IBrush DimTextBrush = new SolidColorBrush(Color.Parse("#66D0C3E8"));
        private static readonly IBrush CellTextBrush = new SolidColorBrush(Color.Parse("#E6FFFFFF"));
        private static readonly IBrush SelectedTextBrush = new SolidColorBrush(Color.Parse("#FF141722"));
        private static readonly Pen GridPen = new(new SolidColorBrush(Color.Parse("#FF443553")), 1);
        private static readonly Pen UnusedHatchPen = new(new SolidColorBrush(Color.Parse("#33443553")), 1);
        private static readonly Pen SelectedPen = new(Brushes.White, 2);
        private static readonly Pen OverlapPen = new(new SolidColorBrush(Color.Parse("#FFFF6B6B")), 2);
        private static readonly Typeface LabelTypeface = new("Inter, Segoe UI, Arial", FontStyle.Normal, FontWeight.SemiBold);

        private readonly List<CustomCanSignal>?[] _cellOwners = new List<CustomCanSignal>?[Bytes * BitsPerByte];
        private readonly Dictionary<CustomCanSignal, Color> _colors = new();
        private readonly Dictionary<CustomCanSignal, (int Msb, int Lsb)> _ends = new();
        private readonly HashSet<CustomCanSignal> _attachedItems = new();
        private INotifyCollectionChanged? _attachedCollection;
        private Rect _gridRect;
        private double _cellSize;
        private int _hoverBit = -1;

        static CanFrameBitMapControl()
        {
            AffectsRender<CanFrameBitMapControl>(SignalsProperty, SelectedSignalProperty, DlcProperty);
        }

        public CanFrameBitMapControl()
        {
            ClipToBounds = true;
            Cursor = new Cursor(StandardCursorType.Hand);
        }

        public IEnumerable<CustomCanSignal>? Signals
        {
            get => GetValue(SignalsProperty);
            set => SetValue(SignalsProperty, value);
        }

        public CustomCanSignal? SelectedSignal
        {
            get => GetValue(SelectedSignalProperty);
            set => SetValue(SelectedSignalProperty, value);
        }

        public int Dlc
        {
            get => GetValue(DlcProperty);
            set => SetValue(DlcProperty, value);
        }

        /// <summary>
        /// Absolute bit indexes (byte * 8 + bit) the signal occupies, in MSB-to-LSB order for
        /// Motorola and LSB-to-MSB order for Intel. Mirrors the firmware's ValidSignal walk;
        /// bits outside the 64-bit frame are dropped.
        /// </summary>
        public static IReadOnlyList<int> GetOccupiedBits(int startBit, int bitLength, bool isMotorola)
        {
            List<int> bits = new();
            int bit = startBit;
            for (int i = 0; i < bitLength; i++)
            {
                if (bit < 0 || bit >= Bytes * BitsPerByte)
                {
                    break;
                }

                bits.Add(bit);
                bit = isMotorola ? ((bit % 8 == 0) ? bit + 15 : bit - 1) : bit + 1;
            }

            return bits;
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);

            if (change.Property == SignalsProperty)
            {
                DetachSignals();
                AttachSignals(change.NewValue as IEnumerable<CustomCanSignal>);
                InvalidateVisual();
            }
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            DetachSignals();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            DetachSignals();
            AttachSignals(Signals);
        }

        public override void Render(DrawingContext context)
        {
            base.Render(context);

            Rect bounds = Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                return;
            }

            BuildOccupancy();

            const double headerFontSize = 11;
            double labelWidth = 44;
            double headerHeight = 20;
            double footerHeight = 22;
            _cellSize = Math.Floor(Math.Min(
                (bounds.Width - labelWidth - 4) / BitsPerByte,
                (bounds.Height - headerHeight - footerHeight) / Bytes));
            if (_cellSize < 8)
            {
                return;
            }

            double gridWidth = _cellSize * BitsPerByte;
            double gridHeight = _cellSize * Bytes;
            double left = Math.Floor(labelWidth + Math.Max(0, (bounds.Width - labelWidth - gridWidth) / 2));
            _gridRect = new Rect(left, headerHeight, gridWidth, gridHeight);

            for (int col = 0; col < BitsPerByte; col++)
            {
                DrawCentered(context, (7 - col).ToString(CultureInfo.InvariantCulture), headerFontSize, HeaderBrush,
                    new Rect(left + (col * _cellSize), 0, _cellSize, headerHeight));
            }

            int dlc = Math.Clamp(Dlc, 0, Bytes);
            for (int row = 0; row < Bytes; row++)
            {
                DrawCentered(context, $"Byte {row}", headerFontSize, row < dlc ? HeaderBrush : DimTextBrush,
                    new Rect(0, headerHeight + (row * _cellSize), left - 6, _cellSize), alignRight: true);
            }

            CustomCanSignal? selected = SelectedSignal;
            bool hasSelection = selected != null && _colors.ContainsKey(selected);
            double cellFont = Math.Max(8, Math.Min(12, _cellSize * 0.34));

            for (int row = 0; row < Bytes; row++)
            {
                for (int col = 0; col < BitsPerByte; col++)
                {
                    int bit = (row * BitsPerByte) + (7 - col);
                    Rect cell = new(left + (col * _cellSize), headerHeight + (row * _cellSize), _cellSize, _cellSize);
                    List<CustomCanSignal>? owners = _cellOwners[bit];

                    if (row >= dlc)
                    {
                        context.FillRectangle(UnusedCellBrush, cell);
                        using (context.PushClip(cell))
                        {
                            for (double d = -_cellSize; d < _cellSize; d += 6)
                            {
                                context.DrawLine(UnusedHatchPen, new Point(cell.X + d, cell.Bottom), new Point(cell.X + d + _cellSize, cell.Y));
                            }
                        }
                    }
                    else if (owners == null || owners.Count == 0)
                    {
                        context.FillRectangle(EmptyCellBrush, cell);
                    }

                    if (owners != null && owners.Count > 0)
                    {
                        CustomCanSignal owner = selected != null && owners.Contains(selected) ? selected : owners[0];
                        Color color = _colors[owner];
                        bool isSelectedOwner = owner == selected;
                        byte alpha = !hasSelection || isSelectedOwner ? (byte)0xFF : (byte)0x55;
                        if (bit == _hoverBit && !isSelectedOwner)
                        {
                            alpha = 0xC0;
                        }

                        context.FillRectangle(new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B)), cell.Deflate(1));
                    }

                    context.DrawRectangle(null, GridPen, cell);

                    if (owners != null && owners.Count > 1)
                    {
                        context.DrawRectangle(null, OverlapPen, cell.Deflate(2));
                    }

                    bool inSelected = owners != null && selected != null && owners.Contains(selected);
                    IBrush textBrush = inSelected ? SelectedTextBrush : (owners != null && owners.Count > 0 ? CellTextBrush : DimTextBrush);
                    string text = bit.ToString(CultureInfo.InvariantCulture);
                    if (inSelected && _ends.TryGetValue(selected!, out var ends) && ends.Msb != ends.Lsb)
                    {
                        text = bit == ends.Msb ? "MSB" : bit == ends.Lsb ? "LSB" : text;
                    }

                    DrawCentered(context, text, text.Length > 2 ? cellFont * 0.8 : cellFont, textBrush, cell);
                }
            }

            if (hasSelection)
            {
                DrawSelectionOutline(context, selected!);
            }

            DrawFooter(context, bounds, hasSelection ? selected : null, headerFontSize);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            int bit = HitTestBit(e.GetPosition(this));
            if (bit == _hoverBit)
            {
                return;
            }

            _hoverBit = bit;
            List<CustomCanSignal>? owners = bit >= 0 ? _cellOwners[bit] : null;
            string? tip = bit < 0
                ? null
                : owners == null || owners.Count == 0
                    ? $"Bit {bit} (byte {bit / 8}, bit {bit % 8}): unused"
                    : $"Bit {bit} (byte {bit / 8}, bit {bit % 8}): {string.Join(", ", owners.Select(s => s.Name))}" +
                      (owners.Count > 1 ? " - overlapping signals" : string.Empty);
            ToolTip.SetTip(this, tip);
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _hoverBit = -1;
            ToolTip.SetTip(this, null);
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            int bit = HitTestBit(e.GetPosition(this));
            List<CustomCanSignal>? owners = bit >= 0 ? _cellOwners[bit] : null;
            if (owners == null || owners.Count == 0)
            {
                return;
            }

            // Repeated clicks on an overlapping bit cycle through its signals.
            int current = SelectedSignal == null ? -1 : owners.IndexOf(SelectedSignal);
            SetCurrentValue(SelectedSignalProperty, owners[(current + 1) % owners.Count]);
            e.Handled = true;
        }

        private int HitTestBit(Point point)
        {
            if (_cellSize <= 0 || !_gridRect.Contains(point))
            {
                return -1;
            }

            int col = Math.Clamp((int)((point.X - _gridRect.X) / _cellSize), 0, BitsPerByte - 1);
            int row = Math.Clamp((int)((point.Y - _gridRect.Y) / _cellSize), 0, Bytes - 1);
            return (row * BitsPerByte) + (7 - col);
        }

        private void BuildOccupancy()
        {
            Array.Clear(_cellOwners);
            _colors.Clear();
            _ends.Clear();
            if (Signals == null)
            {
                return;
            }

            int index = 0;
            foreach (CustomCanSignal signal in Signals)
            {
                _colors[signal] = Palette[index++ % Palette.Length];
                IReadOnlyList<int> bits = GetOccupiedBits(signal.StartBit, signal.BitLength, signal.IsMotorola);
                if (bits.Count > 0)
                {
                    _ends[signal] = signal.IsMotorola ? (bits[0], bits[^1]) : (bits[^1], bits[0]);
                }

                foreach (int bit in bits)
                {
                    (_cellOwners[bit] ??= new List<CustomCanSignal>()).Add(signal);
                }
            }
        }

        private void DrawSelectionOutline(DrawingContext context, CustomCanSignal selected)
        {
            for (int bit = 0; bit < _cellOwners.Length; bit++)
            {
                if (_cellOwners[bit]?.Contains(selected) != true)
                {
                    continue;
                }

                int row = bit / BitsPerByte;
                int col = 7 - (bit % BitsPerByte);
                Rect cell = new(_gridRect.X + (col * _cellSize), _gridRect.Y + (row * _cellSize), _cellSize, _cellSize);

                // Outline only the outer edges so the signal reads as one block.
                if (!Owns(selected, row - 1, col)) context.DrawLine(SelectedPen, cell.TopLeft, cell.TopRight);
                if (!Owns(selected, row + 1, col)) context.DrawLine(SelectedPen, cell.BottomLeft, cell.BottomRight);
                if (!Owns(selected, row, col - 1)) context.DrawLine(SelectedPen, cell.TopLeft, cell.BottomLeft);
                if (!Owns(selected, row, col + 1)) context.DrawLine(SelectedPen, cell.TopRight, cell.BottomRight);
            }
        }

        private bool Owns(CustomCanSignal signal, int row, int col)
        {
            if (row < 0 || row >= Bytes || col < 0 || col >= BitsPerByte)
            {
                return false;
            }

            return _cellOwners[(row * BitsPerByte) + (7 - col)]?.Contains(signal) == true;
        }

        private void DrawFooter(DrawingContext context, Rect bounds, CustomCanSignal? selected, double fontSize)
        {
            Rect footer = new(_gridRect.X, _gridRect.Bottom + 4, _gridRect.Width, 18);
            if (Signals == null || !Signals.Any())
            {
                DrawCentered(context, "No message selected", fontSize, DimTextBrush, footer);
                return;
            }

            if (selected == null)
            {
                DrawCentered(context, "Click a bit or a signal row", fontSize, DimTextBrush, footer);
                return;
            }

            Color color = _colors[selected];
            context.FillRectangle(new SolidColorBrush(color), new Rect(footer.X, footer.Y + 4, 10, 10));
            string text = $"{selected.Name}  ·  {selected.BitLength} bit{(selected.BitLength == 1 ? string.Empty : "s")}, {(selected.IsMotorola ? "Motorola" : "Intel")}";
            FormattedText formatted = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, fontSize, HeaderBrush)
            {
                MaxTextWidth = Math.Max(1, footer.Width - 16),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
            context.DrawText(formatted, new Point(footer.X + 16, footer.Y + ((footer.Height - formatted.Height) / 2)));
        }

        private static void DrawCentered(DrawingContext context, string text, double fontSize, IBrush brush, Rect area, bool alignRight = false)
        {
            FormattedText formatted = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, fontSize, brush);
            double x = alignRight ? area.Right - formatted.Width : area.X + ((area.Width - formatted.Width) / 2);
            context.DrawText(formatted, new Point(x, area.Y + ((area.Height - formatted.Height) / 2)));
        }

        private void AttachSignals(IEnumerable<CustomCanSignal>? signals)
        {
            if (signals == null)
            {
                return;
            }

            if (signals is INotifyCollectionChanged collection)
            {
                _attachedCollection = collection;
                collection.CollectionChanged += Signals_CollectionChanged;
            }

            SyncItemSubscriptions(signals);
        }

        private void DetachSignals()
        {
            if (_attachedCollection != null)
            {
                _attachedCollection.CollectionChanged -= Signals_CollectionChanged;
                _attachedCollection = null;
            }

            foreach (CustomCanSignal item in _attachedItems)
            {
                item.PropertyChanged -= Signal_PropertyChanged;
            }

            _attachedItems.Clear();
        }

        private void SyncItemSubscriptions(IEnumerable<CustomCanSignal> signals)
        {
            HashSet<CustomCanSignal> current = new(signals);
            foreach (CustomCanSignal stale in _attachedItems.Where(item => !current.Contains(item)).ToList())
            {
                stale.PropertyChanged -= Signal_PropertyChanged;
                _attachedItems.Remove(stale);
            }

            foreach (CustomCanSignal item in current)
            {
                if (_attachedItems.Add(item))
                {
                    item.PropertyChanged += Signal_PropertyChanged;
                }
            }
        }

        private void Signals_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (Signals != null)
            {
                SyncItemSubscriptions(Signals);
            }

            InvalidateVisual();
        }

        private void Signal_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(CustomCanSignal.StartBit) or nameof(CustomCanSignal.BitLength)
                or nameof(CustomCanSignal.Flags) or nameof(CustomCanSignal.IsMotorola) or nameof(CustomCanSignal.Name))
            {
                InvalidateVisual();
            }
        }
    }
}
