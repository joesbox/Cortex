using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using Cortex.Services;
using LiveChartsCore;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Extensions;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO.Ports;
using System.Linq;
using System.Timers;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Cortex.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        [ObservableProperty]
        private DataStructures dataStructuresView = new();

        [ObservableProperty]
        private bool isConnected;

        [ObservableProperty]
        private bool commsEstablished;

        [ObservableProperty]
        private List<Tuple<int, int>> testData;

        [ObservableProperty]
        private string systemDateTime;

        [ObservableProperty]
        private bool sdOK;

        [ObservableProperty]
        private bool overCurrent;

        [ObservableProperty]
        private bool overTemperature;

        [ObservableProperty]
        private bool underVoltage;

        [ObservableProperty]
        private bool crcFailed;

        [ObservableProperty]
        private ObservableCollection<string> serialPorts = new();

        [ObservableProperty]
        private string? selectedSerialPort;

        [ObservableProperty]
        private string? receivedData;

        [ObservableProperty]
        private bool isChannelGridEnabled = true;

        [ObservableProperty]
        private bool isRetryEnabled;

        [ObservableProperty]
        private bool isMultichannelEnabled;

        [ObservableProperty]
        private bool isRunOnEnabled;

        [ObservableProperty]
        private bool isOverrideToggled;

        [ObservableProperty]
        private bool isPWMChannel;

        [ObservableProperty]
        private bool isSoftStartEnabled;

        [ObservableProperty]
        private bool isAnalogue;

        [ObservableProperty]
        private bool pullUpEnabled;

        [ObservableProperty]
        private bool pullDownEnabled;

        [ObservableProperty]
        private bool activeLow;

        [ObservableProperty]
        private double lowerAnalogueTH;

        [ObservableProperty]
        private double upperAnalogueTH;

        [ObservableProperty]
        private int lowerPWMRange;

        [ObservableProperty]
        private int upperPWMRange;

        [ObservableProperty]
        private bool isThresholdMode = true;

        [ObservableProperty]
        private int selectedChannelIndex;

        [ObservableProperty]
        private ChannelLabel selectedChannelLabel;

        [ObservableProperty]
        private ObservableCollection<ChannelLabel> channelDisplayList;

        [ObservableProperty]
        private OutputChannel? selectedChannel;

        public bool IsScaledMode => !IsThresholdMode;

        private SerialPortService? _portService;

        private readonly Timer _pollTimer = new(3000); // Every 3 seconds

        private readonly Timer _commsTimer = new(1000); // Every 1000 millis

        private readonly IAppCloser _appCloser;

        public RelayCommand ExitCommand { get; }

        public ObservableCollection<string> LogEntries { get; } = new();

        public ObservableCollection<int> ChannelIndices { get; }        

        partial void OnSelectedChannelIndexChanged(int oldValue, int newValue)
        {
            OnPropertyChanged(nameof(SelectedChannel));
            SelectedChannelLabel = ChannelDisplayList.FirstOrDefault(c => c.Index == newValue);
            SelectedChannel = DataStructuresView.Channels.ElementAtOrDefault(SelectedChannelIndex);
        }

        partial void OnSelectedChannelLabelChanged(ChannelLabel value)
        {
            SelectedChannelIndex = value?.Index ?? 0;
            if (SelectedChannel != null)
            {
                SelectedChannel = DataStructuresView.Channels.ElementAtOrDefault(SelectedChannelIndex);
            }
        }

        public MainWindowViewModel(IAppCloser appCloser)
        {
            IsConnected = false;
            SdOK = false;
            OverCurrent = false;
            OverTemperature = false;
            UnderVoltage = true;
            CrcFailed = false;
            isPWMChannel = false;

            ChannelIndices = new ObservableCollection<int>(
            Enumerable.Range(0, DataStructuresView.Channels.Count));

            ChannelDisplayList = new ObservableCollection<ChannelLabel>(
    Enumerable.Range(0, DataStructuresView.Channels.Count)
    .Select(i => new ChannelLabel(i)));

            SelectedChannelLabel = ChannelDisplayList.FirstOrDefault();

            UpperAnalogueTH = 5.0;
            UpperPWMRange = 100;

            _appCloser = appCloser;
            ExitCommand = new RelayCommand(OnExit);

            SystemDateTime = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

            testData = new List<Tuple<int, int>>();
            for (int i = 0; i < 14; i++)
            {
                testData.Add(new Tuple<int, int>(i, i * i));
            }

            LoadSerialPorts();

            _pollTimer.Elapsed += (s, e) => LoadSerialPorts();
            _pollTimer.Start();

            _commsTimer.Elapsed += (s, e) => HandleComms();
            _commsTimer.Start();

        }

        public ISeries[] Series { get; set; }
            = new ISeries[]
            {
                    new LineSeries<double>
                    {
                        Values = new double[] { 2, 1, 3, 5, 3, 4, 6 },
                        Fill = null
                    },
                    new LineSeries<double>
                    {
                        Values = new double[] { 20, 10, 30, 50, 30, 40, 60 },
                        Fill = null
                    }
            };

        public IEnumerable<ISeries> SysTemp { get; set; } =
        GaugeGenerator.BuildSolidGauge(
            new GaugeItem(30, series =>
            {
                series.Fill = new SolidColorPaint(SKColor.Parse("#800071"));
                series.DataLabelsSize = 30;
                series.DataLabelsPaint = new SolidColorPaint(SKColors.White);
                series.DataLabelsPosition = PolarLabelsPosition.ChartCenter;
                series.InnerRadius = 50;
                series.CornerRadius = 0;
            }),
            new GaugeItem(GaugeItem.Background, series =>
            {
                series.InnerRadius = 50;
                series.Fill = new SolidColorPaint(SKColor.Parse("#30303b"));
            }));

        public IEnumerable<ISeries> SysCurrent { get; set; } =
        GaugeGenerator.BuildSolidGauge(
            new GaugeItem(125, series =>
            {
                series.Fill = new SolidColorPaint(SKColor.Parse("#800071"));
                series.DataLabelsSize = 30;
                series.DataLabelsPaint = new SolidColorPaint(SKColors.White);
                series.DataLabelsPosition = PolarLabelsPosition.ChartCenter;
                series.InnerRadius = 50;
                series.CornerRadius = 0;
            }),
            new GaugeItem(GaugeItem.Background, series =>
            {
                series.InnerRadius = 50;
                series.Fill = new SolidColorPaint(SKColor.Parse("#30303b"));
            }));

        public IEnumerable<ISeries> BattV { get; set; } =
        GaugeGenerator.BuildSolidGauge(
            new GaugeItem(14.2, series =>
            {
                series.Fill = new SolidColorPaint(SKColor.Parse("#800071"));
                series.DataLabelsSize = 30;
                series.DataLabelsPaint = new SolidColorPaint(SKColors.White);
                series.DataLabelsPosition = PolarLabelsPosition.ChartCenter;
                series.InnerRadius = 50;
                series.CornerRadius = 0;
            }),
            new GaugeItem(GaugeItem.Background, series =>
            {
                series.InnerRadius = 50;
                series.Fill = new SolidColorPaint(SKColor.Parse("#30303b"));
            }));

        public IEnumerable<ISeries> BattSoC { get; set; } =
        GaugeGenerator.BuildSolidGauge(
            new GaugeItem(95, series =>
            {
                series.Fill = new SolidColorPaint(SKColor.Parse("#800071"));
                series.DataLabelsSize = 30;
                series.DataLabelsPaint = new SolidColorPaint(SKColors.White);
                series.DataLabelsPosition = PolarLabelsPosition.ChartCenter;
                series.InnerRadius = 50;
                series.CornerRadius = 0;
            }),
            new GaugeItem(GaugeItem.Background, series =>
            {
                series.InnerRadius = 50;
                series.Fill = new SolidColorPaint(SKColor.Parse("#30303b"));
            }));
        public IEnumerable<ISeries> BattSoH { get; set; } =
        GaugeGenerator.BuildSolidGauge(
            new GaugeItem(95, series =>
            {
                series.Fill = new SolidColorPaint(SKColor.Parse("#800071"));
                series.DataLabelsSize = 30;
                series.DataLabelsPaint = new SolidColorPaint(SKColors.White);
                series.DataLabelsPosition = PolarLabelsPosition.ChartCenter;
                series.InnerRadius = 50;
                series.CornerRadius = 0;
            }),
            new GaugeItem(GaugeItem.Background, series =>
            {
                series.InnerRadius = 50;
                series.Fill = new SolidColorPaint(SKColor.Parse("#30303b"));
            }));
        public IEnumerable<ISeries> MobileSS { get; set; } =
        GaugeGenerator.BuildSolidGauge(
           new GaugeItem(75, series =>
           {
               series.Fill = new SolidColorPaint(SKColor.Parse("#800071"));
               series.DataLabelsSize = 30;
               series.DataLabelsPaint = new SolidColorPaint(SKColors.White);
               series.DataLabelsPosition = PolarLabelsPosition.ChartCenter;
               series.InnerRadius = 50;
               series.CornerRadius = 0;
           }),
           new GaugeItem(GaugeItem.Background, series =>
           {
               series.InnerRadius = 50;
               series.Fill = new SolidColorPaint(SKColor.Parse("#30303b"));
           }));

        private void LoadSerialPorts()
        {
            SerialPorts = new ObservableCollection<string>(SerialPort.GetPortNames());
        }

        private void OnExit() => _appCloser.CloseApp();

        [RelayCommand]
        private void RefreshSerialPorts()
        {
            LoadSerialPorts();
        }

        [RelayCommand]
        private void Connect(string selectedPort)
        {
            _portService = new SerialPortService(selectedPort);
            _portService.DataUpdated += _portService_DataUpdated;
            IsConnected = _portService.Open();

            if (IsConnected)
            {
                AddLog("Connecting to ECU on " + selectedPort + "...");
            }
            _portService.InitComms();
        }

        private void _portService_DataUpdated(DataStructures obj)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                DataStructuresView = obj;
            });
        }

        private void HandleComms()
        {
            if (IsConnected)
            {
                if (!CommsEstablished)
                {
                    if (_portService != null)
                    {
                        CommsEstablished = _portService.InitComms();
                        if (CommsEstablished)
                        {
                            AddLog("PDM Connected.");
                        }
                    }
                }
            }
        }


        [RelayCommand]
        private void Disconnect()
        {
            if (_portService != null)
            {
                _portService.DataUpdated -= _portService_DataUpdated;
                _portService.Close();
                _portService = null;
            }

            IsConnected = false;
            AddLog("Disconnected from ECU.");
        }

        public void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogEntries.Add($"[{timestamp}] {message}");
        }

        partial void OnPullUpEnabledChanged(bool value)
        {
            if (value)
                PullDownEnabled = false;
        }

        partial void OnPullDownEnabledChanged(bool value)
        {
            if (value)
                PullUpEnabled = false;
        }
    }
}

public partial class ChannelLabel : ObservableObject
{
    public int Index { get; }
    public string Label => $"Channel {Index + 1}";

    public ChannelLabel(int index)
    {
        Index = index;
    }
}
