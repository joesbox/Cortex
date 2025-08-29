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
using System.Text.Json;
using System.Timers;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace Cortex.ViewModels
{
    public partial class MainWindowViewModel : ObservableObject
    {
        [ObservableProperty]
        private DataStructures liveDataView = new(); // For live/status data

        [ObservableProperty]
        private DataStructures settingsDataView = new(); // For settings (user editable)        

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

        [ObservableProperty]
        private ObservableCollection<InputLabel> inputDisplayList;

        [ObservableProperty]
        private InputLabel? selectedInputLabel;

        [ObservableProperty]
        private byte selectedPinNumber;

        [ObservableProperty]
        private string? selectedChannelName;

        [ObservableProperty]
        private ChannelTypeDisplay? selectedChannelTypeDisplay;

        [ObservableProperty]
        private DigitalInput? selectedDigitalInput;

        [ObservableProperty]
        private AnalogueInput? _selectedAnalogueInput;

        private InputDisplayItem? _selectedInputItem;

        public bool IsScaledMode => !IsThresholdMode;

        private SerialPortService? _portService;

        private readonly Timer _pollTimer = new(3000); // Every 3 seconds

        private readonly Timer _commsTimer = new(1000); // Every 1000 millis

        private readonly IAppCloser _appCloser;

        // STM32 pin definitions
        private static readonly byte[] DIChannelInputPins = { 79, 78, 77, 76, 75, 74, 73, 72 };

        private static readonly byte[] ANAChannelInputPins = { 208, 209, 210, 211, 212, 213, 214, 215 };

        private static readonly byte[] AllInputPins = DIChannelInputPins.Concat(ANAChannelInputPins).ToArray();

        public RelayCommand ExitCommand { get; }

        public ObservableCollection<string> LogEntries { get; } = new();

        public ObservableCollection<int> ChannelIndices { get; }

        public ObservableCollection<ChannelTypeDisplay> ChannelTypes { get; }

        private bool refreshStaticData = true;

        public InputDisplayItem? SelectedInputItem
        {
            get => _selectedInputItem;
            set
            {
                if (SetProperty(ref _selectedInputItem, value) && value != null)
                {
                    // Keep SelectedChannel.ControlPin in sync
                    SelectedChannel.InputControlPin = value.Pin;
                }
            }
        }

        partial void OnSelectedPinNumberChanged(byte value)
        {
            var index = Array.IndexOf(AllInputPins, value);
            SelectedInputLabel = index >= 0 ? InputDisplayList.ElementAtOrDefault(index) : null;
        }

        partial void OnSelectedChannelIndexChanged(int oldValue, int newValue)
        {
            OnPropertyChanged(nameof(SelectedChannel));
            SelectedChannelLabel = ChannelDisplayList.FirstOrDefault(c => c.Index == newValue);
            SelectedChannel = SettingsDataView.ChannelsStaticData.ElementAtOrDefault(SelectedChannelIndex);

            if (SelectedChannel != null)
            {
                SelectedPinNumber = SelectedChannel.InputControlPin;
                SelectedChannelTypeDisplay = ChannelTypes.FirstOrDefault(ctd => ctd.ChannelType == SelectedChannel.ChanType);
                SelectedChannelName = new string(SelectedChannel.Name).TrimEnd('\0');
            }
        }

        partial void OnSelectedChannelLabelChanged(ChannelLabel value)
        {
            SelectedChannelIndex = value?.Index ?? 0;
            if (SelectedChannel != null)
            {
                SelectedChannel = SettingsDataView.ChannelsStaticData.ElementAtOrDefault(SelectedChannelIndex);
            }
        }

        partial void OnSelectedChannelTypeDisplayChanged(ChannelTypeDisplay? value)
        {
            // Check that both the selected channel and the new value are not null
            if (SelectedChannel != null && value != null)
            {
                // Update the ChanType property of the SelectedChannel with the new enum value
                SelectedChannel.ChanType = value.ChannelType;
            }
        }

        partial void OnSelectedChannelNameChanged(string? value)
        {
            if (SelectedChannel != null && value != null)
            {
                // Update the channel name in the data model
                var charArray = new char[Constants.CHANNEL_NAME_LENGTH];
                value.CopyTo(0, charArray, 0, Math.Min(value.Length, charArray.Length));
                SelectedChannel.Name = charArray;
            }
        }

        partial void OnSelectedInputLabelChanged(InputLabel? value)
        {
            if (SelectedChannel != null && value != null)
            {
                SelectedChannel.InputControlPin = AllInputPins[value.Index];
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
            Enumerable.Range(0, SettingsDataView.ChannelsStaticData.Count));

            ChannelDisplayList = new ObservableCollection<ChannelLabel>(
    Enumerable.Range(0, SettingsDataView.ChannelsStaticData.Count)
    .Select(i => new ChannelLabel(i)));

            InputDisplayList = new ObservableCollection<InputLabel>(
    Enumerable.Range(0, Constants.NUM_DIGITAL_INPUTS + Constants.NUM_ANALOGUE_INPUTS)
    .Select(i => new InputLabel(i)));

            ChannelTypes = new ObservableCollection<ChannelTypeDisplay>
    {
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.DIG, Label = "Digital Input" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.DIG_PWM, Label = "Digital PWM" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.ANA, Label = "Analogue threshold" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.ANA_PWM, Label = "Analogue scaled PWM" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.CAN_DIGITAL, Label = "CAN Digital" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.CAN_PWM, Label = "CAN PWM" }
    };

            SelectedChannelLabel = ChannelDisplayList.FirstOrDefault();

            SelectedDigitalInput = SettingsDataView.DigitalInputs.FirstOrDefault();

            SelectedAnalogueInput = SettingsDataView.AnalogueInputsStaticData.FirstOrDefault();

            SelectedChannel = new OutputChannel();

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
            SettingsDataView.PropertyChanged += SettingsDataView_PropertyChanged;
        }

        

        private void SettingsDataView_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (SelectedChannel == null)
            {
                SelectedChannel = SettingsDataView.ChannelsStaticData.ElementAtOrDefault(SelectedChannelIndex);
            }

            if (SelectedAnalogueInput == null)
            {
                SelectedAnalogueInput = SettingsDataView.AnalogueInputsStaticData.FirstOrDefault();
            }

            if (SelectedDigitalInput == null)
            {
                SelectedDigitalInput = SettingsDataView.DigitalInputs.FirstOrDefault();
            }
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
                AddLog("Connecting to PDM on " + selectedPort + "...");
            }
            _portService.InitComms();
        }

        private void _portService_DataUpdated(DataStructures obj)
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                LiveDataView = obj;
                // Only update settings data on initial load or when user requests refresh
                if (refreshStaticData)
                {
                    // Initial load - copy data to settings
                    refreshStaticData = false;
                    SettingsDataView = DeepCopyDataStructures(obj);
                    OnSelectedChannelIndexChanged(SelectedChannelIndex, SelectedChannelIndex);
                    SelectedAnalogueInput = SettingsDataView.AnalogueInputsStaticData.FirstOrDefault();
                    SelectedDigitalInput = SettingsDataView.DigitalInputs.FirstOrDefault();
                }
            });
        }

        private DataStructures DeepCopyDataStructures(DataStructures source)
        {
            var json = JsonSerializer.Serialize(source);
            return JsonSerializer.Deserialize<DataStructures>(json) ?? new DataStructures();
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
            AddLog("Disconnected from PDM.");
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

public partial class InputLabel : ObservableObject
{
    public int Index { get; }
    public string Label => Index < 8 ? $"Digital {Index + 1}" : $"Ana/Dig {(Index - 8) + 1}";

    public InputLabel(int index)
    {
        Index = index;
    }
}

public class InputDisplayItem
{
    public byte Pin { get; set; }
    public string Label { get; set; } = "";
}

public class ChannelTypeDisplay
{
    public OutputChannel.ChannelType ChannelType { get; set; }
    public string Label { get; set; }
}

