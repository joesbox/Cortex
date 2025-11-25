namespace Cortex.Views
{
    using System.Threading;
    using System.Threading.Tasks;
    using Avalonia.Controls;
    using Avalonia.Input;
    using Avalonia.Threading;
    using Cortex.Models;
    using Cortex.Services;
    using Cortex.ViewModels;

    public partial class MainWindow : Window, IAppCloser
    {
        private CancellationTokenSource? _holdCts;

        public MainWindow()
        {
            InitializeComponent();

            var vm = new MainWindowViewModel(this);
            DataContext = vm;

            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(vm.IsConnected))
                {
                    UpdateConnectionState(vm.IsConnected);
                }

                if (args.PropertyName == nameof(vm.SdOK))
                {
                    UpdateSDStatus(vm.SdOK);
                }

                if (args.PropertyName == nameof(vm.OverCurrent))
                {
                    UpdateCurrentStatus(vm.OverCurrent);
                }

                if (args.PropertyName == nameof(vm.OverTemperature))
                {
                    UpdateTempStatus(vm.OverTemperature);
                }

                if (args.PropertyName == nameof(vm.UnderVoltage))
                {
                    UpdateVoltStatus(vm.UnderVoltage);
                }

                if (args.PropertyName == nameof(vm.GpsOK))
                {
                    UpdateGPSStatus(vm.GpsOK);
                }
            };

            // Subscribe to CollectionChanged event to auto-scroll when new log entries are added
            if (DataContext is MainWindowViewModel mainViewModel)
            {
                mainViewModel.LogEntries.CollectionChanged += LogEntries_CollectionChanged;
            }

            // Initial states
            UpdateConnectionState(vm.IsConnected);
            UpdateSDStatus(vm.SdOK);
            UpdateCurrentStatus(vm.OverCurrent);
            UpdateTempStatus(vm.OverTemperature);
            UpdateVoltStatus(vm.UnderVoltage);
            UpdateGPSStatus(vm.GpsOK);

            ChannelChart.SizeChanged += (s, e) =>
            {
                ChannelChart?.CoreChart?.Update();
            };
        }

        public void CloseApp()
        {
            Close();
        }

        private void UpdateConnectionState(bool isConnected)
        {
            StatusRect.Classes.Set("connected", isConnected);
            StatusRect.Classes.Set("disconnected", !isConnected);

            StatusIcon.Classes.Set("connected", isConnected);
            StatusIcon.Classes.Set("disconnected", !isConnected);
        }

        private void UpdateSDStatus(bool sdOK)
        {
            SDIcon.Classes.Set("sdOK", sdOK);
            SDIcon.Classes.Set("sdError", !sdOK);

            SDRect.Classes.Set("sdOK", sdOK);
            SDRect.Classes.Set("sdError", !sdOK);
        }

        private void UpdateCurrentStatus(bool currentOK)
        {
            currentIcon.Classes.Set("currentOK", currentOK);
            currentIcon.Classes.Set("overCurrrent", !currentOK);

            currentRect.Classes.Set("currentOK", currentOK);
            currentRect.Classes.Set("overCurrrent", !currentOK);
        }

        private void UpdateTempStatus(bool tempOK)
        {
            tempIcon.Classes.Set("tempOK", tempOK);
            tempIcon.Classes.Set("overTemp", !tempOK);

            tempRect.Classes.Set("tempOK", tempOK);
            tempRect.Classes.Set("overTemp", !tempOK);
        }

        private void UpdateVoltStatus(bool undervoltage)
        {
            voltIcon.Classes.Set("voltsOK", !undervoltage);
            voltIcon.Classes.Set("underVolts", undervoltage);

            voltRect.Classes.Set("voltsOK", !undervoltage);
            voltRect.Classes.Set("underVolts", undervoltage);
        }

        private void UpdateGPSStatus(bool gpsOk)
        {
            GPSRect.Classes.Set("gpsOK", gpsOk);
            GPSRect.Classes.Set("gpsError", !gpsOk);

            GPSIcon.Classes.Set("gpsOK", gpsOk);
            GPSIcon.Classes.Set("gpsError", !gpsOk);
        }

        private void LogEntries_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Ensure the scroll happens on the UI thread
            Dispatcher.UIThread.Post(() =>
            {
                LogScrollViewer?.ScrollToEnd();
            });
        }


        private bool _holdCompleted = false;

        private async void OnBorderPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is Border border && border.DataContext is OutputChannel item)
            {
                if (item.Override)
                {
                    // Already active → single click deactivates
                    item.Override = false;
                    item.HoldProgress = 0;

                    // Send command to device
                    if (DataContext is MainWindowViewModel vm)
                    {
                        vm.SendOverrideCommand(item);
                    }

                    return;
                }

                _holdCompleted = false;
                _holdCts?.Cancel();
                _holdCts = new CancellationTokenSource();
                var start = System.DateTime.Now;
                var holdTime = System.TimeSpan.FromSeconds(2);

                try
                {
                    while ((System.DateTime.Now - start) < holdTime)
                    {
                        await Task.Delay(50, _holdCts.Token);
                        var progress = (System.DateTime.Now - start).TotalMilliseconds / holdTime.TotalMilliseconds;
                        item.HoldProgress = System.Math.Clamp(progress, 0, 1);
                    }

                    item.Override = true;

                    // Send command to device
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
                return;
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