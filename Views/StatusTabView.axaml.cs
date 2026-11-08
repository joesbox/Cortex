using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cortex.Controls;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using LiveChartsCore.SkiaSharpView.Avalonia;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cortex.Views
{
    public partial class StatusTabView : UserControl
    {
        private readonly CartesianChart? _channelChart;
        private CancellationTokenSource? _holdCts;
        private bool _holdCompleted;

        public StatusTabView() : this(new PanelLayoutStore("live-data-layout.json", 1.0 / 2.4))
        {
        }

        public StatusTabView(PanelLayoutStore layoutStore)
        {
            AvaloniaXamlLoader.Load(this);
            Grid layoutGrid = this.FindControl<Grid>("LiveDataLayoutGrid")!;
            GridSplitter splitter = this.FindControl<GridSplitter>("LiveDataSplitter")!;
            VerticalPanelLayout.RestoreAndTrack(layoutGrid, splitter, layoutStore);
            _channelChart = this.FindControl<CartesianChart>("ChannelChart");
            if (_channelChart == null)
            {
                return;
            }

            _channelChart.SizeChanged += (_, _) =>
            {
                _channelChart.CoreChart?.Update();
            };
        }

        private async void OnBorderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && border.DataContext is OutputChannel item)
            {
                if (item.Override)
                {
                    item.Override = false;
                    item.HoldProgress = 0;

                    if (DataContext is MainWindowViewModel vm)
                    {
                        vm.SendOverrideCommand(item);
                    }

                    return;
                }

                _holdCompleted = false;
                _holdCts?.Cancel();
                _holdCts = new CancellationTokenSource();
                var start = DateTime.Now;
                var holdTime = TimeSpan.FromSeconds(2);

                try
                {
                    while ((DateTime.Now - start) < holdTime)
                    {
                        await Task.Delay(50, _holdCts.Token);
                        var progress = (DateTime.Now - start).TotalMilliseconds / holdTime.TotalMilliseconds;
                        item.HoldProgress = Math.Clamp(progress, 0, 1);
                    }

                    item.Override = true;

                    if (DataContext is MainWindowViewModel vm)
                    {
                        vm.SendOverrideCommand(item);
                    }

                    item.HoldProgress = 0;
                    _holdCompleted = true;
                }
                catch (TaskCanceledException)
                {
                    item.HoldProgress = 0;
                    _holdCompleted = false;
                }
            }
        }

        private void OnBorderPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            _holdCts?.Cancel();

            if (_holdCompleted)
            {
                _holdCompleted = false;
            }
        }

        private void OnBorderPointerExited(object? sender, PointerEventArgs e)
        {
            _holdCts?.Cancel();
            _holdCompleted = false;

            if (sender is Border border && border.DataContext is OutputChannel item)
            {
                item.HoldProgress = 0;
            }
        }

        private void OnChannelChartPointerEntered(object? sender, PointerEventArgs e)
        {
            UpdateLiveHoverAnchorFromPointer(e);
        }

        private void OnChannelChartPointerMoved(object? sender, PointerEventArgs e)
        {
            UpdateLiveHoverAnchorFromPointer(e);
        }

        private void OnChannelChartPointerExited(object? sender, PointerEventArgs e)
        {
            if (DataContext is MainWindowViewModel vm)
            {
                vm.ClearLiveChartSelectionCommand.Execute(null);
            }
        }

        private void UpdateLiveHoverAnchorFromPointer(PointerEventArgs e)
        {
            if (_channelChart == null || DataContext is not MainWindowViewModel vm)
            {
                return;
            }

            Point pointerPosition = e.GetPosition(_channelChart);

            Rect drawArea = _channelChart.Bounds;
            if (TryGetDrawAreaBounds(out Rect coreDrawArea))
            {
                drawArea = coreDrawArea;
            }

            if (drawArea.Width <= 0)
            {
                return;
            }

            double relativeX = Math.Clamp((pointerPosition.X - drawArea.X) / drawArea.Width, 0d, 1d);
            vm.UpdateLiveHoverAnchorFromRelativeX(relativeX);
        }

        private bool TryGetDrawAreaBounds(out Rect bounds)
        {
            bounds = default;

            if (_channelChart?.CoreChart == null)
            {
                return false;
            }

            object coreChart = _channelChart.CoreChart;
            Type coreChartType = coreChart.GetType();

            object? drawMarginLocation = coreChartType.GetProperty("DrawMarginLocation")?.GetValue(coreChart);
            object? drawMarginSize = coreChartType.GetProperty("DrawMarginSize")?.GetValue(coreChart);
            if (drawMarginLocation == null || drawMarginSize == null)
            {
                return false;
            }

            if (!TryGetNumericProperty(drawMarginLocation, "X", out double x) ||
                !TryGetNumericProperty(drawMarginLocation, "Y", out double y) ||
                !TryGetNumericProperty(drawMarginSize, "Width", out double width) ||
                !TryGetNumericProperty(drawMarginSize, "Height", out double height) ||
                width <= 0 ||
                height <= 0)
            {
                return false;
            }

            bounds = new Rect(x, y, width, height);
            return true;
        }

        private static bool TryGetNumericProperty(object instance, string propertyName, out double value)
        {
            value = 0;

            object? raw = instance.GetType().GetProperty(propertyName)?.GetValue(instance);
            if (raw == null)
            {
                return false;
            }

            try
            {
                value = Convert.ToDouble(raw);
                return true;
            }
            catch
            {
                return false;
            }
        }

    }
}
