using Avalonia.Controls;
using Avalonia.Threading;
using Cortex.Services;
using Cortex.ViewModels;

namespace Cortex.Views
{
    public partial class MainWindow : Window, IAppCloser
    {
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

            ChannelChart.SizeChanged += (s, e) =>
            {
                ChannelChart?.CoreChart?.Update();
            };
        }

        public void CloseApp()
        {            
            this.Close();
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

        private void LogEntries_CollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
            // Ensure the scroll happens on the UI thread
            Dispatcher.UIThread.Post(() =>
            {
                LogScrollViewer?.ScrollToEnd();
            });
        }
    }
}