using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cortex.Models;
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

        public StatusTabView()
        {
            AvaloniaXamlLoader.Load(this);
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
    }
}
