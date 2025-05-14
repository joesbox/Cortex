using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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
using System.Timers;

namespace Cortex.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        [ObservableProperty]
        private bool isConnected;

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

        private SerialPortService? _portService;

        private readonly Timer _pollTimer = new(3000); // Every 3 seconds

        private readonly IAppCloser _appCloser;

        public RelayCommand ExitCommand { get; }

        public ObservableCollection<string> LogEntries { get; } = new();

        public MainWindowViewModel(IAppCloser appCloser)
        {
            IsConnected = false;
            SdOK = false;
            OverCurrent = false;
            OverTemperature = false;
            UnderVoltage = true;
            CrcFailed = false;

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
            _portService.DataReceived += OnDataReceived;
            _portService.Open();
        }

        private void OnDataReceived(object? sender, string data)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                ReceivedData = data;
            });
        }

        [RelayCommand]
        private void Disconnect()
        {
            if (_portService != null)
            {
                _portService.DataReceived -= OnDataReceived;
                _portService.Close();
                _portService = null;
            }
        }

        public void AddLog(string message)
        {
            var timestamp = DateTime.Now.ToString("HH:mm:ss");
            LogEntries.Add($"[{timestamp}] {message}");
        }
    }
}
