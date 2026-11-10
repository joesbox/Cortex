using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels.IoFlow;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using static Cortex.ViewModels.MainWindowViewModel;

/*

 * Version history:
    ----              -------       ----------------------------------------------------------------------------------------------------------------------------------------------------
    2026-10-15        v1.0.0        Added support for new math channel type and functions.
                                    New CAN message import support
                                    PIN locking feature
                                    I/O Flow feature
    2026-08-19        v0.1.7        Firmware update check fix for version parsing (detects v0.10 correctly).
    2026-06-20        v0.1.6        Comms protocol fix - much faster saves
                                    Added delayed ON and OFF functions
                                    CAN bus sbaud rate selection added to config screen.
                                    Mouse over tooltips added in settings.
                                    Added analogue channels to live view with selectable series persisted between sessions.
                                    Added pause/resume in live view.
                                    Cortex application update check and download from GitHub releases.
                                    Added telemetry upload and GSM data settings to config screen.
                                    Debug log added to help menu.
    2026-04-29        v0.1.5        Channel type change bug fix.
                                    Minor UI bug fixes
                                    Added digital and analogue status CAN IDs to config screen.
    2026-02-25        v0.1.4        Soft start/stop and inrush current parameters added to OutputChannel and config sending updated to include them.
                                    Save and load config file functionality added to UI, using JSON format for readability and ease of debugging. Config file operations are asynchronous to prevent UI blocking.
                                    Working analogue threshold and scaled parameters. Moved to channel configuration.
                                    Log download and visualisation.
                                    Splash screen and about window.
                                    New intermittent channel type.
                                    Firmware update functionality added, including checking for updates from GitHub and updating via local file selection.
    2026-02-16        v0.1.3        Added new CAN bus system parameters and updated config sending to include them.
    2026-01-20        v0.1.2        Added motion detect system parameters and corrected byte padding for system parameters.
    2025-12-12        v0.1.1        Removed LiPo backup battery gauges and serial comms data.
    2025-12-12        v0.1.0        Fixes:
                                    - Send analogue input parameters when sending config.
                                    - Some UI optimisations
 */

namespace Cortex.ViewModels
{
    public sealed class CellularTestStatusItem
    {
        private const string GreenTickIcon = "avares://Cortex/Assets/green_tick.png";
        private const string RedCrossIcon = "avares://Cortex/Assets/red_cross.png";

        public CellularTestStatusItem(string stage, string status, string message)
        {
            Stage = stage;
            Status = status;
            Message = message;
        }

        public string Stage { get; }

        public string Status { get; }

        public string Message { get; }

        public bool HasMessage => !string.IsNullOrWhiteSpace(Message);

        public string? IconPath => IsSuccessStatus(Status) ? GreenTickIcon : IsFailureStatus(Status) ? RedCrossIcon : null;

        private static bool IsSuccessStatus(string status)
        {
            return status.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Connected", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Ready", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Fix", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsFailureStatus(string status)
        {
            return status.Equals("Blocked", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Skipped", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Timeout", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Timed out", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("No response", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Needs setup", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Problem", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Update needed", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Legacy", StringComparison.OrdinalIgnoreCase) ||
                   status.Equals("Warning", StringComparison.OrdinalIgnoreCase);
        }
    }

    public partial class MainWindowViewModel : ObservableObject, IDisposable
    {
        private const byte TimeZoneFixedDateFlag = 0x80;
        private const byte TimeZoneDayMask = 0x1F;
        private const string DefaultCellularTestStatusMessage = "Run a connection test after saving GSM/data settings to the PDM.";
        private const string LegacyCellularEnableDataMessage = "Cellular connection is disabled in GSM/data settings.";
        private const int AutomaticCellularTestMaxAttempts = 6;
        private const int ManualCellularRetryDelayMs = 5000;
        private const int CellularHealthPollIntervalMs = 5000;
        private const int CellularHealthRegistrationWarmupMs = 45000;

        // Shown in the UI instead of naming OpenRemote, so the wording stays valid for any telemetry backend.
        private const string TelemetryServiceStage = "Telemetry service";

        private static readonly string[] CellularTestStageOrder =
        [
            "Settings",
            "Mobile data",
            "Internet",
            TelemetryServiceStage,
            "Health"
        ];

        [ObservableProperty]
        private DataStructures liveDataView = new(); // For live/status data

        [ObservableProperty]
        private DataStructures settingsDataView = new(); // For settings (user editable)

        [ObservableProperty]
        private bool isConnected;

        [ObservableProperty]
        private bool commsEstablished;

        [ObservableProperty]
        private bool isInitialSnapshotLoaded;

        public bool IsControllerUiReady => IsConnected && CommsEstablished && IsInitialSnapshotLoaded;

        [ObservableProperty]
        private string firmwareUpdateButtonText = "No firmware available";

        [ObservableProperty]
        private IBrush firmwareUpdateButtonBackground = FirmwareIdleBrush;

        [ObservableProperty]
        private bool isFirmwareUpdateAvailable;

        [ObservableProperty]
        private bool isCheckingFirmwareUpdate;

        [ObservableProperty]
        private string applicationUpdateButtonText = "Check for updates";

        [ObservableProperty]
        private IBrush applicationUpdateButtonBackground = FirmwareIdleBrush;

        [ObservableProperty]
        private bool isApplicationUpdateAvailable;

        [ObservableProperty]
        private bool isCheckingApplicationUpdate;

        [ObservableProperty]
        private string currentApplicationVersion = AppUpdateService.GetCurrentVersion();

        [ObservableProperty]
        private string applicationUpdateStatusMessage = "Checking GitHub releases...";

        [ObservableProperty]
        private string controllerFirmwareVersion = string.Empty;

        [ObservableProperty]
        private string systemDateTime;

        [ObservableProperty]
        private DateTimeOffset? controllerRtcDate;

        [ObservableProperty]
        private TimeSpan? controllerRtcTime;

        [ObservableProperty]
        private TimeZoneDisplay? selectedTimeZoneDisplay;

        [ObservableProperty]
        private bool isSettingControllerRtc;

        [ObservableProperty]
        private bool isFactoryResetInProgress;

        [ObservableProperty]
        private string factoryResetStatusMessage = string.Empty;

        private bool _isPinLockBusy;

        public bool IsPinLockBusy
        {
            get => _isPinLockBusy;
            set
            {
                if (!SetProperty(ref _isPinLockBusy, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CanManagePinLock));
                OnPropertyChanged(nameof(CanConfigurePinLock));
                OnPropertyChanged(nameof(CanDisablePinLock));
                OnPropertyChanged(nameof(DisablePinLockButtonText));
            }
        }

        private bool _isPinLockSupported;

        public bool IsPinLockSupported
        {
            get => _isPinLockSupported;
            set
            {
                if (!SetProperty(ref _isPinLockSupported, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CanConfigurePinLock));
                OnPropertyChanged(nameof(CanDisablePinLock));
                OnPropertyChanged(nameof(ShowPinLockStatus));
            }
        }

        private bool _isPinLockEnabled;

        public bool IsPinLockEnabled
        {
            get => _isPinLockEnabled;
            set
            {
                if (!SetProperty(ref _isPinLockEnabled, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CanDisablePinLock));
                OnPropertyChanged(nameof(ConfigurePinLockButtonText));
            }
        }

        private string _pinLockStatusMessage = "PIN lock unavailable.";

        public string PinLockStatusMessage
        {
            get => _pinLockStatusMessage;
            set
            {
                if (!SetProperty(ref _pinLockStatusMessage, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(ShowPinLockStatus));
            }
        }

        private uint _pinLockRetryAfterMs;

        public uint PinLockRetryAfterMs
        {
            get => _pinLockRetryAfterMs;
            set
            {
                if (!SetProperty(ref _pinLockRetryAfterMs, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(HasPinLockRetryDelay));
                OnPropertyChanged(nameof(PinLockRetryMessage));
            }
        }

        [ObservableProperty]
        private bool isTestingCellularConnection;

        [ObservableProperty]
        private bool isAutomaticCellularTestInProgress;

        [ObservableProperty]
        private double cellularTestProgressValue;

        [ObservableProperty]
        private string cellularTestStatusMessage = DefaultCellularTestStatusMessage;

        [ObservableProperty]
        private string cellularConnectionHealthStatus = "Offline";

        [ObservableProperty]
        private bool isOpenRemoteProvisioningInProgress;

        [ObservableProperty]
        private string openRemoteProvisioningStatusMessage = "Sign in to the telemetry service to register this PDM.";

        public ObservableCollection<CellularTestStatusItem> CellularTestStatusItems { get; } = [];

        public bool HasControllerSaveTimestamp => !string.IsNullOrWhiteSpace(SystemDateTime);

        public bool HasControllerSaveStatus => IsSendingConfig || HasControllerSaveTimestamp;

        public string ControllerSaveStatusText => IsSendingConfig ? "Updating PDM" : SystemDateTime;

        public bool CanSetControllerRtc => IsConnected && CommsEstablished && !IsLogBusy && !IsSettingControllerRtc && _portService != null;

        public bool CanFactoryReset => IsConnected && CommsEstablished && !IsLogBusy && !IsFactoryResetInProgress && _portService != null;

        public bool CanManagePinLock => IsConnected && CommsEstablished && !IsLogBusy && !IsPinLockBusy && _portService != null;

        public bool CanConfigurePinLock => CanManagePinLock && IsPinLockSupported;

        public bool CanDisablePinLock => CanManagePinLock && IsPinLockSupported && IsPinLockEnabled;

        public string ConfigurePinLockButtonText => IsPinLockEnabled ? "Change PIN" : "Set PIN Lock";

        public string DisablePinLockButtonText => IsPinLockBusy ? "Disabling..." : "Disable PIN Lock";

        public bool ShowPinLockStatus => IsPinLockSupported || !string.IsNullOrWhiteSpace(PinLockStatusMessage);

        public string PinLockRetryMessage => PinLockRetryAfterMs > 0
            ? $"Retry after {Math.Ceiling(PinLockRetryAfterMs / 1000d)} seconds."
            : string.Empty;

        public bool HasPinLockRetryDelay => PinLockRetryAfterMs > 0;

        public bool CanTestCellularConnection => IsConnected && CommsEstablished && !IsLogBusy && !IsSendingConfig && !IsTestingCellularConnection && !IsAutomaticCellularTestInProgress && _portService != null;

        public bool CanUseOpenRemoteSettings => SettingsDataView.SystemParamsStaticData.AllowData && IsInternetAvailable;

        public string OpenRemoteAvailabilityMessage => SettingsDataView.SystemParamsStaticData.AllowData
            ? IsInternetAvailable
                ? string.Empty
                : "Internet access is required for telemetry service setup and name updates."
            : "Enable Mobile data before using the telemetry service.";

        public bool CanProvisionOpenRemote => IsOpenRemoteSignedIn && HasValidPdmName && CanUseOpenRemoteSettings && IsConnected && CommsEstablished && !IsLogBusy && !IsSendingConfig && !IsOpenRemoteProvisioningInProgress && _portService != null;

        public string SetControllerRtcButtonText => IsSettingControllerRtc ? "Setting..." : "Set controller clock";

        public bool IsManualPwmDutyChannel => SelectedChannel?.ChanType == OutputChannel.ChannelType.PWM;

        public string FactoryResetButtonText => IsFactoryResetInProgress ? "Resetting..." : "Factory Reset PDM";

        public string CellularTestButtonText => (IsTestingCellularConnection || IsAutomaticCellularTestInProgress) ? "Testing..." : "Test data connection";

        public bool IsCellularTestInProgress => IsTestingCellularConnection || IsAutomaticCellularTestInProgress;

        public string OpenRemoteProvisioningButtonText => IsOpenRemoteProvisioningInProgress ? "Registering..." : "Register PDM";

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
        private bool gpsOK;

        [ObservableProperty]
        private ObservableCollection<string> serialPorts = [];

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
        private int selectedMathRuleSlotIndex;

        [ObservableProperty]
        private string mathValidationErrorMessage = string.Empty;

        [ObservableProperty]
        private ChannelCategoryDisplay? selectedChannelCategoryDisplay;

        [ObservableProperty]
        private DigitalInput? selectedDigitalInput;

        [ObservableProperty]
        private AnalogueInput? selectedAnalogueInput = new(1, false, false, null);

        [ObservableProperty]
        private ObservableCollection<ISeries> seriesCollection = [];

        [ObservableProperty]
        private ObservableCollection<ChartSeriesToggleItem> liveSeriesToggles = [];

        [ObservableProperty]
        private ObservableCollection<ChartSeriesToggleItem> channelLiveSeriesToggles = [];

        [ObservableProperty]
        private ObservableCollection<ChartSeriesToggleItem> analogueLiveSeriesToggles = [];

        [ObservableProperty]
        private string liveChartHoverSummary = string.Empty;

        [ObservableProperty]
        private bool isLiveChartSelectionPinned;

        [ObservableProperty]
        private bool isLiveCrosshairEnabled = true;

        [ObservableProperty]
        private bool isLiveChartPaused;

        [ObservableProperty]
        private int selectedTimeWindowSeconds = 60;

        [ObservableProperty]
        private bool hasPendingConfigChanges;

        [ObservableProperty]
        private bool isSendingConfig;

        [ObservableProperty]
        private bool isInternetAvailable = NetworkInterface.GetIsNetworkAvailable();

        [ObservableProperty]
        private int selectedLogDetailTabIndex;

        [ObservableProperty]
        private ObservableCollection<LogMapGridRow> logMapGridRows = [];

        [ObservableProperty]
        private ObservableCollection<LogMapInspectionRow> logMapInspectionRows = [];

        private ObservableCollection<ConnectorPinDisplayItem> connectorPins = [];

        public ObservableCollection<ConnectorPinDisplayItem> ConnectorPins
        {
            get => connectorPins;
            private set => SetProperty(ref connectorPins, value);
        }

        private ObservableCollection<ConnectorLegendItem> connectorLegendItems = [];

        public ObservableCollection<ConnectorLegendItem> ConnectorLegendItems
        {
            get => connectorLegendItems;
            private set => SetProperty(ref connectorLegendItems, value);
        }

        [ObservableProperty]
        private int logMapDataVersion;

        [ObservableProperty]
        private int logMapRouteVersion;

        private readonly DateTime startTime = DateTime.UtcNow;

        private readonly System.Timers.Timer _uiUpdateTimer;
        private DataStructures? _pendingLiveData;
        private readonly object _pendingDataLock = new();
        private bool _hasPendingData = false;
        private bool _hasReceivedLiveData;
        private bool[] _liveSeriesHasSyntheticTail = Array.Empty<bool>();
        private List<ObservablePoint>[] _liveSeriesHistory = Array.Empty<List<ObservablePoint>>();
        private bool _pauseLiveUiUpdates;
        private double _lastUpdatedHighlightOpacity;
        private readonly DispatcherTimer _logViewportMonitorTimer;
        private readonly DispatcherTimer _liveHoverClearTimer;
        private bool _suppressLiveChartAxisRefresh;
        private string _lastLiveChartAxisSignature = string.Empty;
        private List<ChartPoint> _lastHoveredLiveChartPoints = [];
        private bool _hasLiveHoverAnchor;
        private double _liveHoverAnchorTimestampMs;

        private InputDisplayItem? _selectedInputItem;

        public bool IsScaledMode => !IsThresholdMode;

        private SerialPortService? _portService;

        private readonly System.Timers.Timer _pollTimer = new(3000); // Every 3 seconds

        private readonly System.Timers.Timer _commsTimer = new(200); // Every 200 millis

        private readonly IAppCloser _appCloser;
        private bool _isDisposed;
        private bool _isSecureUnlockWorkflowActive;
        private bool _isSecureKeepaliveInProgress;

        private readonly Dictionary<string, SKColor> _logSeriesColorRegistry = new(StringComparer.OrdinalIgnoreCase);

        private static readonly SKColor[] LiveSeriesPalette =
        [
            new SKColor(0x4D, 0xC3, 0xFF),
            new SKColor(0xFF, 0x9F, 0x43),
            new SKColor(0x4C, 0xD1, 0x7A),
            new SKColor(0xFF, 0x6B, 0x81),
            new SKColor(0xC5, 0x86, 0xFF),
            new SKColor(0xFF, 0xD1, 0x66),
            new SKColor(0x5A, 0xE0, 0xD8),
            new SKColor(0xFF, 0x7A, 0x59),
            new SKColor(0x8A, 0xF0, 0x5A),
            new SKColor(0x7E, 0xA8, 0xFF),
            new SKColor(0xFF, 0x86, 0xC8),
            new SKColor(0xA7, 0xB5, 0xC5),
        ];

        private static readonly byte[] DIChannelInputPins = InputPinCatalog.DIChannelInputPins;
        private static readonly byte[] ANAChannelInputPins = InputPinCatalog.ANAChannelInputPins;

        private static readonly byte[] AllInputPins = InputPinCatalog.AllInputPins;

        public RelayCommand ExitCommand { get; }

        public bool HasLiveChartSelection => _lastHoveredLiveChartPoints.Count > 0 || _hasLiveHoverAnchor;

        public bool CanSendConfig => HasPendingConfigChanges && !IsSendingConfig;

        public string LiveChartPauseButtonText => IsLiveChartPaused ? "RESUME" : "PAUSE";

        public string LiveChartPinButtonText => IsLiveChartSelectionPinned ? "Unpin" : "Pin";

        public ObservableCollection<string> LogEntries => LoggingService.LogEntries;

        public ObservableCollection<int> ChannelIndices { get; }

        public ObservableCollection<ChannelTypeDisplay> ChannelTypes { get; }

        public ObservableCollection<ChannelCategoryDisplay> ChannelCategories { get; }

        public ObservableCollection<string> MathOutputModes { get; }

        public ObservableCollection<string> MathOperations { get; }

        public ObservableCollection<string> MathOperandTypes { get; }

        public ObservableCollection<string> MathSystemValues { get; }

        public ObservableCollection<AnalogueTypeDisplay> AnalogueChannelTypes { get; }

        public ObservableCollection<AnalogueUnitDisplay> AnalogueUnits { get; }

        public ObservableCollection<byte> AnalogueCalibrationPointOptions { get; }

        public ObservableCollection<CanIdOption> AvailableCanIds { get; }

        public ObservableCollection<CanBitrateOption> AvailableCanBitrates { get; }

        public ObservableCollection<TimeZoneDisplay> TimeZones { get; }

        public string SpeedUnit => SettingsDataView.SystemParamsStaticData.SpeedUnitPref ? "mph" : "km/h";
        public string DistanceUnit => SettingsDataView.SystemParamsStaticData.DistanceUnitPref ? "feet" : "metres";

        public bool IsSelectedChannelMath => SelectedChannel?.IsMathChannel == true;

        public bool IsSelectedChannelNonMath => !IsSelectedChannelMath;

        public bool HasMathValidationError => !string.IsNullOrWhiteSpace(MathValidationErrorMessage);

        public string MathValidationStatusMessage => HasMathValidationError
            ? MathValidationErrorMessage
            : "No validation errors detected.";

        public IBrush MathValidationStatusBrush => HasMathValidationError
            ? new SolidColorBrush(Color.Parse("#FFD8A56D"))
            : new SolidColorBrush(Color.Parse("#FF78C29A"));

        public string MathValidationStatusIcon => HasMathValidationError ? "▲" : "●";

        public string SelectedMathRuntimeRootValueText
        {
            get
            {
                OutputChannel? channel = SelectedLiveChannel;
                if (channel is null)
                {
                    return "Output effect: --";
                }

                if (channel.ChanType != OutputChannel.ChannelType.MATH)
                {
                    return "Output effect: channel is not in MATH mode";
                }

                if (channel.MathError != 0)
                {
                    return $"Output effect: error {channel.MathError}";
                }

                OutputChannel.MathRule? rootRule = channel.MathRules.ElementAtOrDefault(channel.MathRootRule);
                if (rootRule is null || rootRule.RuntimeError != 0 || !rootRule.IsEvaluated)
                {
                    return rootRule is not null && rootRule.RuntimeError != 0
                        ? $"Output effect: {MathRuntimeDisplayFormatter.FormatRuleError(rootRule, rootRule.RuntimeError, LiveDataView)}"
                        : "Output effect: -- (root rule not evaluated)";
                }

                return MathRuntimeDisplayFormatter.FormatOutputEffect(
                    channel.MathCalculatedRootValue,
                    channel.MathOutputMode);
            }
        }

        public string SelectedMathRuntimeRootStatusText => SelectedLiveChannel is null
            ? "No live root result yet."
            : SelectedLiveChannel.ChanType != OutputChannel.ChannelType.MATH
                ? "Channel is not in MATH mode."
                : $"Root rule {SelectedLiveChannel.MathRootRule + 1} directly controls this output.";

        public IBrush SelectedMathRuntimeRootStatusBrush => SelectedLiveChannel is null
            ? new SolidColorBrush(Color.Parse("#FF8A7E99"))
            : new SolidColorBrush(Color.Parse("#FFB7A7D1"));

        public string SelectedMathRuntimeChannelErrorText => BuildMathRuntimeErrorMessage("Channel", SelectedLiveChannel?.MathError ?? 0);

        public IBrush SelectedMathRuntimeChannelStatusBrush => SelectedLiveChannel == null
            ? new SolidColorBrush(Color.Parse("#FF8A7E99"))
            : SelectedLiveChannel.MathError == 0
                ? new SolidColorBrush(Color.Parse("#FF78C29A"))
                : new SolidColorBrush(Color.Parse("#FFD9899A"));

        public string SelectedMathRuntimeChannelStatusIcon => SelectedLiveChannel == null
            ? "●"
            : SelectedLiveChannel.MathError == 0 ? "●" : "▲";

        public string SelectedMathRuntimeRuleValueText
        {
            get
            {
                if (SelectedLiveMathRule == null)
                {
                    return "Rule output: --";
                }

                OutputChannel? channel = SelectedLiveChannel;
                if (channel is null)
                {
                    return "Rule output: --";
                }

                if (channel.ChanType != OutputChannel.ChannelType.MATH)
                {
                    return "Rule output: channel is not in MATH mode";
                }

                if (SelectedMathRuleSlotIndex == channel.MathRootRule)
                {
                    if (channel.MathError != 0)
                    {
                        if (SelectedLiveMathRule.RuntimeError != 0)
                        {
                            return $"Output effect: {MathRuntimeDisplayFormatter.FormatRuleError(SelectedMathRule ?? SelectedLiveMathRule, SelectedLiveMathRule.RuntimeError, LiveDataView)}";
                        }

                        return $"Output effect: error {channel.MathError}";
                    }

                    if (SelectedLiveMathRule.RuntimeError != 0 || !SelectedLiveMathRule.IsEvaluated)
                    {
                        return SelectedLiveMathRule.RuntimeError != 0
                            ? $"Output effect: {MathRuntimeDisplayFormatter.FormatRuleError(SelectedMathRule ?? SelectedLiveMathRule, SelectedLiveMathRule.RuntimeError, LiveDataView)}"
                            : "Output effect: -- (root rule not evaluated)";
                    }

                    return $"Rule result: {MathRuntimeDisplayFormatter.FormatValue(SelectedLiveMathRule.RuntimeValue)}; "
                        + MathRuntimeDisplayFormatter.FormatOutputEffect(
                            channel.MathCalculatedRootValue,
                            channel.MathOutputMode);
                }

                OutputChannel.MathOperation operation = (OutputChannel.MathOperation)SelectedLiveMathRule.Operation;
                bool isBoolean = IsBooleanMathOperation(operation);
                string unitSuffix = GetSelectedMathRuleUnitSuffix(SelectedLiveMathRule);
                string result = SelectedLiveMathRule.RuntimeError != 0
                    ? MathRuntimeDisplayFormatter.FormatRuleError(SelectedMathRule ?? SelectedLiveMathRule, SelectedLiveMathRule.RuntimeError, LiveDataView)
                    : !SelectedLiveMathRule.IsEvaluated
                        ? "--"
                        : FormatMathRuntimeValue(SelectedLiveMathRule.RuntimeValue, isBoolean, unitSuffix);
                string usage = GetMathRuleUsageDescription(channel, SelectedMathRuleSlotIndex);
                return $"Rule result: {result}; {usage}";
            }
        }

        public string SelectedMathRuntimeRuleEvaluationText
        {
            get
            {
                if (SelectedMathRule == null || SelectedLiveMathRule == null || SelectedChannel?.ChanType != OutputChannel.ChannelType.MATH)
                {
                    return "Evaluation: --";
                }

                return MathRuntimeDisplayFormatter.FormatRuleEvaluation(
                    SelectedMathRule,
                    SelectedLiveMathRule,
                    LiveDataView,
                    SelectedChannel.ChannelNumber,
                    ruleIndex => ruleIndex);
            }
        }

        public string SelectedMathRuntimeOperandAText => SelectedMathRule == null || SelectedChannel == null
            ? "--"
            : MathRuntimeDisplayFormatter.FormatOperand(SelectedMathRule, true, LiveDataView, SelectedChannel.ChannelNumber);

        public string SelectedMathRuntimeOperandBText => SelectedMathRule == null || SelectedChannel == null
            ? "--"
            : MathRuntimeDisplayFormatter.FormatOperand(SelectedMathRule, false, LiveDataView, SelectedChannel.ChannelNumber);

        public string SelectedMathRuntimeResultText => SelectedMathRule == null || SelectedLiveMathRule == null
            ? "--"
            : MathRuntimeDisplayFormatter.FormatRuleResult(SelectedMathRule, SelectedLiveMathRule, LiveDataView);

        public string SelectedMathRuntimeAllRuleValuesText
        {
            get
            {
                OutputChannel? liveChannel = SelectedLiveChannel;
                if (liveChannel?.ChanType != OutputChannel.ChannelType.MATH)
                {
                    return "Live rule results: --";
                }

                int ruleCount = Math.Min(liveChannel.MathRuleCount, liveChannel.MathRules.Count);
                if (ruleCount == 0)
                {
                    return "Live rule results: --";
                }

                IEnumerable<string> values = Enumerable.Range(0, ruleCount).Select(index =>
                {
                    OutputChannel.MathRule rule = liveChannel.MathRules[index];
                    OutputChannel.MathRule configuredRule = SelectedChannel?.MathRules.ElementAtOrDefault(index) ?? rule;
                    string evaluation = MathRuntimeDisplayFormatter.FormatRuleEvaluation(
                        configuredRule,
                        rule,
                        LiveDataView,
                        liveChannel.ChannelNumber,
                        ruleIndex => ruleIndex);
                    string result;
                    if (index == liveChannel.MathRootRule)
                    {
                        result = liveChannel.MathError != 0
                            ? rule.RuntimeError != 0
                                ? $"Output effect: {MathRuntimeDisplayFormatter.FormatRuleError(configuredRule, rule.RuntimeError, LiveDataView)}"
                                : $"Output effect: error {liveChannel.MathError}"
                            : rule.RuntimeError != 0
                                ? $"Output effect: {MathRuntimeDisplayFormatter.FormatRuleError(configuredRule, rule.RuntimeError, LiveDataView)}"
                                : rule.Evaluated == 0
                                    ? "Output effect: -- (root rule not evaluated)"
                                    : MathRuntimeDisplayFormatter.FormatOutputEffect(
                                        liveChannel.MathCalculatedRootValue,
                                        liveChannel.MathOutputMode);
                        result = $"{evaluation}; {result}";
                    }
                    else
                    {
                        result = $"{evaluation}; {GetMathRuleUsageDescription(liveChannel, index)}";
                    }

                    return $"Rule {index + 1}: {result}";
                });

                return $"Live rule results: {string.Join("  |  ", values)}";
            }
        }

        public string SelectedMathRuntimeRuleErrorText => SelectedMathRule is not null && SelectedLiveMathRule is not null && SelectedLiveMathRule.RuntimeError != 0
            ? MathRuntimeDisplayFormatter.FormatRuleError(SelectedMathRule, SelectedLiveMathRule.RuntimeError, LiveDataView)
            : BuildMathRuntimeErrorMessage("Rule", SelectedLiveMathRule?.RuntimeError ?? 0);

        public IBrush SelectedMathRuntimeRuleStatusBrush => SelectedLiveMathRule == null
            ? new SolidColorBrush(Color.Parse("#FF8A7E99"))
            : SelectedLiveMathRule.RuntimeError == 0
                ? new SolidColorBrush(Color.Parse("#FF78C29A"))
                : new SolidColorBrush(Color.Parse("#FFD9899A"));

        public string SelectedMathRuntimeRuleStatusIcon => SelectedLiveMathRule == null
            ? "●"
            : SelectedLiveMathRule.RuntimeError == 0 ? "●" : "▲";

        public string SelectedMathRuntimeEvaluatedText => SelectedLiveMathRule is null
            ? "Evaluated: --"
            : SelectedLiveMathRule.IsEvaluated
                ? "Evaluated: Yes"
                : "Evaluated: No";

        public IBrush SelectedMathRuntimeEvaluatedStatusBrush => SelectedLiveMathRule == null
            ? new SolidColorBrush(Color.Parse("#FF8A7E99"))
            : SelectedLiveMathRule.IsEvaluated
                ? new SolidColorBrush(Color.Parse("#FF78C29A"))
                : new SolidColorBrush(Color.Parse("#FFD8A56D"));

        public string SelectedMathRuntimeEvaluatedStatusIcon => SelectedLiveMathRule == null
            ? "●"
            : SelectedLiveMathRule.IsEvaluated ? "●" : "▲";

        public IEnumerable<int> MathRuleCountOptions => Enumerable.Range(1, Constants.NUM_MATH_RULE_SLOTS);

        public IEnumerable<MathRuleOption> MathRuleSlotOptions => _mathRuleSlotOptions;

        public IEnumerable<MathOperandSourceOption> SelectedMathOperandAIndexOptions => _selectedMathOperandAIndexOptions;

        public IEnumerable<MathOperandSourceOption> SelectedMathOperandBIndexOptions => _selectedMathOperandBIndexOptions;

        public bool CanAddMathRule => IsSelectedChannelMath && SelectedMathRuleCount < Constants.NUM_MATH_RULE_SLOTS;

        public bool CanRemoveMathRule => IsSelectedChannelMath && SelectedMathRuleCount > 1;

        public OutputChannel.MathRule? SelectedMathRule =>
            SelectedChannel?.MathRules is { Count: > 0 } rules &&
            SelectedMathRuleSlotIndex >= 0 &&
            SelectedMathRuleSlotIndex < rules.Count
                ? rules[SelectedMathRuleSlotIndex]
                : null;

        private IReadOnlyList<MathRuleOption> _mathRuleSlotOptions = [new MathRuleOption(0, "Rule 1")];
        private IReadOnlyList<MathOperandSourceOption> _selectedMathOperandAIndexOptions = Array.Empty<MathOperandSourceOption>();
        private IReadOnlyList<MathOperandSourceOption> _selectedMathOperandBIndexOptions = Array.Empty<MathOperandSourceOption>();

        public OutputChannel? SelectedLiveChannel =>
            SelectedChannelIndex >= 0 && SelectedChannelIndex < LiveDataView.ChannelsLiveData.Count
                ? LiveDataView.ChannelsLiveData[SelectedChannelIndex]
                : null;

        public OutputChannel.MathRule? SelectedLiveMathRule =>
            SelectedLiveChannel?.MathRules is { Count: > 0 } rules &&
            SelectedMathRuleSlotIndex >= 0 &&
            SelectedMathRuleSlotIndex < rules.Count
                ? rules[SelectedMathRuleSlotIndex]
                : null;

        public int SelectedMathRuleCount
        {
            get => SelectedChannel?.MathRuleCount ?? 1;
            set
            {
                if (SelectedChannel == null)
                {
                    return;
                }

                SetSelectedMathRuleCount(value);
            }
        }

        public bool SelectedMathRuleIsRoot
        {
            get => SelectedChannel?.MathRootRule == SelectedMathRuleSlotIndex;
            set
            {
                if (SelectedChannel == null || SelectedMathRuleCount <= 0)
                {
                    return;
                }

                int currentRootIndex = SelectedChannel.MathRootRule;
                int selectedRuleIndex = Math.Clamp(SelectedMathRuleSlotIndex, 0, SelectedMathRuleCount - 1);
                int newRootIndex = value
                    ? selectedRuleIndex
                    : currentRootIndex == selectedRuleIndex && SelectedMathRuleCount > 1
                        ? (selectedRuleIndex + 1) % SelectedMathRuleCount
                        : currentRootIndex;

                if (currentRootIndex == newRootIndex)
                {
                    return;
                }

                SelectedChannel.MathRootRule = (byte)newRootIndex;
                OnPropertyChanged();
            }
        }

        public bool CanToggleSelectedMathRuleRoot => SelectedMathRuleCount > 1;

        public bool SelectedMathRuleUsesOperandB =>
            SelectedMathOperationIndex != (int)OutputChannel.MathOperation.NOT &&
            SelectedMathOperationIndex != (int)OutputChannel.MathOperation.ABS &&
            SelectedMathOperationIndex != (int)OutputChannel.MathOperation.TOGGLE;

        public bool SelectedMathRuleSupportsHysteresis =>
            SelectedMathOperationIndex >= (int)OutputChannel.MathOperation.GREATER_THAN &&
            SelectedMathOperationIndex <= (int)OutputChannel.MathOperation.LESS_EQUAL;

        public bool SelectedMathRuleHysteresisEnabled => SelectedMathRule?.HysteresisEnabled != 0;

        public string SelectedMathOperationDescription => GetMathOperationDescription(SelectedMathOperationIndex);

        public bool SelectedMathOperandAIsConstant => SelectedMathOperandATypeIndex == (int)OutputChannel.MathOperandType.Constant;

        public bool SelectedMathOperandAUsesIndex => !SelectedMathOperandAIsConstant;

        public bool SelectedMathOperandBIsConstant => SelectedMathOperandBTypeIndex == (int)OutputChannel.MathOperandType.Constant;

        public bool SelectedMathOperandBUsesIndex => !SelectedMathOperandBIsConstant;

        public string SelectedMathOperandAIndexLabel => GetMathOperandIndexLabel(SelectedMathOperandATypeIndex);

        public string SelectedMathOperandBIndexLabel => GetMathOperandIndexLabel(SelectedMathOperandBTypeIndex);

        public int SelectedMathOperationIndex
        {
            get => SelectedMathRule?.Operation ?? 0;
            set
            {
                if (SelectedMathRule == null || SelectedMathRule.Operation == value)
                {
                    return;
                }

                SelectedMathRule.Operation = (byte)value;
                RaiseMathRuleUiPropertiesChanged(includeRuleSelectorOptions: false, includeOperandASelectorOptions: false, includeOperandBSelectorOptions: false);
                OnPropertyChanged(nameof(SelectedMathOperationDescription));
            }
        }

        public int SelectedMathOutputModeIndex
        {
            get => SelectedChannel?.MathOutputMode ?? 0;
            set
            {
                if (SelectedChannel == null || SelectedChannel.MathOutputMode == value)
                {
                    return;
                }

                SelectedChannel.MathOutputMode = (byte)value;
                OnPropertyChanged();
            }
        }

        public int SelectedMathOperandATypeIndex
        {
            get => SelectedMathRule?.OperandAType ?? 0;
            set
            {
                if (SelectedMathRule == null || SelectedMathRule.OperandAType == value)
                {
                    return;
                }

                SelectedMathRule.OperandAType = (byte)value;
                NormalizeMathOperandIndex(SelectedMathRule, operandA: true);
                RaiseMathRuleUiPropertiesChanged(includeRuleSelectorOptions: false, includeOperandASelectorOptions: true, includeOperandBSelectorOptions: false);
            }
        }

        public int SelectedMathOperandBTypeIndex
        {
            get => SelectedMathRule?.OperandBType ?? 0;
            set
            {
                if (SelectedMathRule == null || SelectedMathRule.OperandBType == value)
                {
                    return;
                }

                SelectedMathRule.OperandBType = (byte)value;
                NormalizeMathOperandIndex(SelectedMathRule, operandA: false);
                RaiseMathRuleUiPropertiesChanged(includeRuleSelectorOptions: false, includeOperandASelectorOptions: false, includeOperandBSelectorOptions: true);
            }
        }

        public int SelectedMathOperandAIndexValue
        {
            get => SelectedMathRule?.OperandAIndex ?? 0;
            set
            {
                if (SelectedMathRule == null || SelectedMathRule.OperandAIndex == value)
                {
                    return;
                }

                SelectedMathRule.OperandAIndex = (byte)value;
                OnPropertyChanged();
            }
        }

        public int SelectedMathOperandBIndexValue
        {
            get => SelectedMathRule?.OperandBIndex ?? 0;
            set
            {
                if (SelectedMathRule == null || SelectedMathRule.OperandBIndex == value)
                {
                    return;
                }

                SelectedMathRule.OperandBIndex = (byte)value;
                OnPropertyChanged();
            }
        }

        public MathOperandSourceOption? SelectedMathOperandAIndexOption
        {
            get => _selectedMathOperandAIndexOptions
                .FirstOrDefault(option => option.Value == SelectedMathOperandAIndexValue);
            set
            {
                if (value != null)
                {
                    SelectedMathOperandAIndexValue = value.Value;
                }
            }
        }

        public MathOperandSourceOption? SelectedMathOperandBIndexOption
        {
            get => _selectedMathOperandBIndexOptions
                .FirstOrDefault(option => option.Value == SelectedMathOperandBIndexValue);
            set
            {
                if (value != null)
                {
                    SelectedMathOperandBIndexValue = value.Value;
                }
            }
        }

        public IEnumerable<AnalogueUnitDisplay> FilteredAnalogueUnits
        {
            get
            {
                if (SelectedAnalogueInput == null)
                {
                    return AnalogueUnits;
                }

                return SelectedAnalogueInput.ChanType switch
                {
                    AnalogueInput.AnalogueChannelType.RawVoltage => AnalogueUnits.Where(unit => unit.Units == AnalogueInput.AnalogueUnits.Volts),
                    AnalogueInput.AnalogueChannelType.Digital => AnalogueUnits.Where(unit => unit.Units == AnalogueInput.AnalogueUnits.Volts),
                    AnalogueInput.AnalogueChannelType.NTC => AnalogueUnits.Where(unit => unit.Units == AnalogueInput.AnalogueUnits.Celsius || unit.Units == AnalogueInput.AnalogueUnits.Fahrenheit),
                    _ => AnalogueUnits,
                };
            }
        }

        private bool refreshStaticData = true;

        private readonly object _chartLock = new();

        private bool _pendingRevertLog;
        private bool _suppressDirtyTracking;
        private bool _suppressTimeZoneSelectionWriteBack;
        private bool _awaitingInitialMathSettingsHydration;
        private DataStructures? _controllerConfigBaseline;
        private DataStructures? _configurationBeingSaved;
        private IoFlowGraphSnapshot? _ioFlowSnapshotBeingSaved;
        private TaskCompletionSource<bool>? _configSaveCompletionTcs;
        private DateTime _nextCellularHealthPollUtc = DateTime.MinValue;
        private DateTime _suppressCellularNeedsAttentionUntilUtc = DateTime.MinValue;
        private int _isCellularHealthPollInProgress;
        private string _lastLoggedCellularHealthStatus = string.Empty;
        private double _activeLogFilterStartMs = double.NaN;
        private double _activeLogFilterEndMs = double.NaN;
        private double _lastRenderedLogViewportStartMs = double.NaN;
        private double _lastRenderedLogViewportEndMs = double.NaN;

        public InputDisplayItem? SelectedInputItem
        {
            get => _selectedInputItem;
            set
            {
                if (SetProperty(ref _selectedInputItem, value) && value != null)
                {
                    // Keep SelectedChannel.ControlPin in sync
                    if (SelectedChannel != null)
                    {
                        SelectedChannel.InputControlPin = value.Pin;
                    }
                }
            }
        }

        partial void OnSelectedPinNumberChanged(byte value)
        {
            SelectedInputLabel = InputDisplayList.FirstOrDefault(input => input.Pin == value);
        }

        partial void OnSystemDateTimeChanged(string value)
        {
            OnPropertyChanged(nameof(HasControllerSaveTimestamp));
            OnPropertyChanged(nameof(HasControllerSaveStatus));
            OnPropertyChanged(nameof(ControllerSaveStatusText));
        }

        partial void OnHasPendingConfigChangesChanged(bool value)
        {
            OnPropertyChanged(nameof(CanSendConfig));
        }

        partial void OnLiveDataViewChanged(DataStructures value)
        {
            RebuildConnectorPins();
            RefreshCustomCanBindings();
        }

        partial void OnSettingsDataViewChanged(DataStructures value)
        {
            RebuildConnectorPins();
            RefreshCustomCanBindings();
        }

        partial void OnIsInternetAvailableChanged(bool value)
        {
            OnPropertyChanged(nameof(CanUseOpenRemoteSettings));
            OnPropertyChanged(nameof(CanProvisionOpenRemote));
            OnPropertyChanged(nameof(OpenRemoteAvailabilityMessage));
        }

        partial void OnIsSendingConfigChanged(bool value)
        {
            OnPropertyChanged(nameof(CanSendConfig));
            OnPropertyChanged(nameof(CanTestCellularConnection));
            OnPropertyChanged(nameof(CanProvisionOpenRemote));
            OnPropertyChanged(nameof(HasControllerSaveStatus));
            OnPropertyChanged(nameof(ControllerSaveStatusText));
        }

        partial void OnIsLiveChartPausedChanged(bool value)
        {
            OnPropertyChanged(nameof(LiveChartPauseButtonText));

            if (value)
            {
                return;
            }

            if (!_hasReceivedLiveData || !IsConnected || !CommsEstablished)
            {
                return;
            }

            RestoreLiveChartSeriesFromHistory();
        }

        partial void OnControllerRtcDateChanged(DateTimeOffset? value)
        {
            UpdateSelectedTimeZoneRule();
        }

        partial void OnSelectedTimeZoneDisplayChanged(TimeZoneDisplay? value)
        {
            if (_suppressTimeZoneSelectionWriteBack)
            {
                return;
            }

            SettingsDataView.SystemParamsStaticData.TimeZoneId = value?.Id;
            UpdateSelectedTimeZoneRule();
        }

        partial void OnSelectedChannelIndexChanged(int oldValue, int newValue)
        {
            OnPropertyChanged(nameof(SelectedChannel));

            SelectedChannelLabel = ChannelDisplayList.FirstOrDefault(c => c.Index == newValue) ?? ChannelDisplayList.FirstOrDefault() ?? new ChannelLabel(newValue);
            SelectedChannel = SettingsDataView.ChannelsStaticData.ElementAtOrDefault(SelectedChannelIndex);

            if (SelectedChannel != null)
            {
                SelectedChannel.EnsureMathRuleSlots();
                SelectedChannelTypeDisplay = ChannelTypes.FirstOrDefault(ctd => ctd.ChannelType == SelectedChannel.ChanType);
                OnPropertyChanged(nameof(IsManualPwmDutyChannel));
                SelectedChannelCategoryDisplay = ChannelCategories.FirstOrDefault(category => category.Category == SelectedChannel.Category);
                RefreshInputDisplayList(SelectedChannel.ChanType);
                SelectedPinNumber = SelectedChannel.InputControlPin;
                SelectedChannelName = new string(SelectedChannel.Name).TrimEnd('\0');

                int maxActiveRuleIndex = Math.Max(0, SelectedChannel.MathRuleCount - 1);
                SelectedChannel.MathRootRule = (byte)Math.Clamp(SelectedChannel.MathRootRule, 0, maxActiveRuleIndex);
                SelectedMathRuleSlotIndex = 0;

                if (SelectedChannel.MathRuleCount <= 1)
                {
                    SelectedChannel.MathRootRule = 0;
                }

                // Update the IsPWMChannel property when channel changes
                IsPWMChannel = SelectedChannel.IsPWMChannel;
            }

            RaiseMathRuleUiPropertiesChanged();
            UpdateMathStatusPollingSelection();
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
                bool typeChanged = SelectedChannel.ChanType != value.ChannelType;

                // Update the ChanType property of the SelectedChannel with the new enum value
                SelectedChannel.ChanType = value.ChannelType;

                if (typeChanged && value.ChannelType == OutputChannel.ChannelType.Intermittent)
                {
                    SelectedChannel.IntermittentOnTime = 1.0f;
                    SelectedChannel.IntermittentOffTime = 1.0f;
                }

                RefreshInputDisplayList(value.ChannelType);
                SelectedPinNumber = SelectedChannel.InputControlPin;

                // Update IsPWMChannel when channel type changes
                IsPWMChannel = SelectedChannel.IsPWMChannel;
                NotifyAnalogueChannelUiContextChanged();
                OnPropertyChanged(nameof(IsManualPwmDutyChannel));
                RaiseMathRuleUiPropertiesChanged();
                UpdateMathStatusPollingSelection();
            }
        }

        partial void OnSelectedChannelCategoryDisplayChanged(ChannelCategoryDisplay? value)
        {
            if (SelectedChannel != null && value != null)
            {
                SelectedChannel.Category = value.Category;
            }
        }

        partial void OnSelectedChannelChanged(OutputChannel? value)
        {
            value?.RefreshDelayUiUnitsFromStoredValues();
            value?.EnsureMathRuleSlots();
            NotifyAnalogueChannelUiContextChanged();
            RaiseMathRuleUiPropertiesChanged();
            UpdateMathStatusPollingSelection();
            NotifySelectedChannelCanTxChanged();
        }

        partial void OnSelectedMathRuleSlotIndexChanged(int value)
        {
            if (SelectedChannel != null)
            {
                int clamped = Math.Clamp(value, 0, Math.Max(0, SelectedMathRuleCount - 1));
                if (clamped != value)
                {
                    SelectedMathRuleSlotIndex = clamped;
                    return;
                }
            }

            RaiseMathRuleUiPropertiesChanged(includeRuleSelectorOptions: false, includeOperandASelectorOptions: true, includeOperandBSelectorOptions: true);
        }

        partial void OnSelectedAnalogueInputChanged(AnalogueInput? value)
        {
            NotifyAnalogueChannelUiContextChanged();
            OnPropertyChanged(nameof(FilteredAnalogueUnits));
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
                SelectedChannel.InputControlPin = value.Pin;
                var desiredChannelType = SelectedChannelTypeDisplay?.ChannelType ?? SelectedChannel.ChanType;
                var compatibleChannelType = GetCompatibleChannelTypeForInput(value.Pin, desiredChannelType);

                if (SelectedChannel.ChanType != compatibleChannelType)
                {
                    SelectedChannel.ChanType = compatibleChannelType;
                }

                IsPWMChannel = SelectedChannel.IsPWMChannel;
                NotifyAnalogueChannelUiContextChanged();
            }
        }

        partial void OnMathValidationErrorMessageChanged(string value)
        {
            OnPropertyChanged(nameof(HasMathValidationError));
            OnPropertyChanged(nameof(MathValidationStatusMessage));
            OnPropertyChanged(nameof(MathValidationStatusBrush));
            OnPropertyChanged(nameof(MathValidationStatusIcon));
        }

        private void RaiseMathRuleUiPropertiesChanged(bool includeRuleSelectorOptions = true, bool includeOperandASelectorOptions = true, bool includeOperandBSelectorOptions = true)
        {
            OnPropertyChanged(nameof(IsSelectedChannelMath));
            OnPropertyChanged(nameof(IsSelectedChannelNonMath));
            OnPropertyChanged(nameof(SelectedLiveChannel));
            OnPropertyChanged(nameof(SelectedLiveMathRule));
            OnPropertyChanged(nameof(SelectedMathRule));
            OnPropertyChanged(nameof(SelectedMathRuleIsRoot));
            OnPropertyChanged(nameof(CanToggleSelectedMathRuleRoot));
            OnPropertyChanged(nameof(SelectedMathRuntimeRootValueText));
            OnPropertyChanged(nameof(SelectedMathRuntimeAllRuleValuesText));
            OnPropertyChanged(nameof(SelectedMathRuntimeRootStatusText));
            OnPropertyChanged(nameof(SelectedMathRuntimeRootStatusBrush));
            OnPropertyChanged(nameof(SelectedMathRuntimeChannelErrorText));
            OnPropertyChanged(nameof(SelectedMathRuntimeChannelStatusBrush));
            OnPropertyChanged(nameof(SelectedMathRuntimeChannelStatusIcon));
            OnPropertyChanged(nameof(SelectedMathRuntimeRuleValueText));
            OnPropertyChanged(nameof(SelectedMathRuntimeRuleEvaluationText));
            OnPropertyChanged(nameof(SelectedMathRuntimeOperandAText));
            OnPropertyChanged(nameof(SelectedMathRuntimeOperandBText));
            OnPropertyChanged(nameof(SelectedMathRuntimeResultText));
            OnPropertyChanged(nameof(SelectedMathRuntimeRuleErrorText));
            OnPropertyChanged(nameof(SelectedMathRuntimeRuleStatusBrush));
            OnPropertyChanged(nameof(SelectedMathRuntimeRuleStatusIcon));
            OnPropertyChanged(nameof(SelectedMathRuntimeEvaluatedText));
            OnPropertyChanged(nameof(SelectedMathRuntimeEvaluatedStatusBrush));
            OnPropertyChanged(nameof(SelectedMathRuntimeEvaluatedStatusIcon));
            OnPropertyChanged(nameof(SelectedMathRuleCount));
            OnPropertyChanged(nameof(SelectedMathOperationIndex));
            OnPropertyChanged(nameof(SelectedMathOperationDescription));
            OnPropertyChanged(nameof(SelectedMathOutputModeIndex));
            OnPropertyChanged(nameof(SelectedMathOperandATypeIndex));
            OnPropertyChanged(nameof(SelectedMathOperandBTypeIndex));
            OnPropertyChanged(nameof(SelectedMathOperandAIndexLabel));
            OnPropertyChanged(nameof(SelectedMathOperandBIndexLabel));
            OnPropertyChanged(nameof(SelectedMathOperandAIsConstant));
            OnPropertyChanged(nameof(SelectedMathOperandAUsesIndex));
            OnPropertyChanged(nameof(SelectedMathOperandBIsConstant));
            OnPropertyChanged(nameof(SelectedMathOperandBUsesIndex));
            OnPropertyChanged(nameof(SelectedMathRuleUsesOperandB));
            OnPropertyChanged(nameof(SelectedMathRuleSupportsHysteresis));
            OnPropertyChanged(nameof(SelectedMathRuleHysteresisEnabled));
            OnPropertyChanged(nameof(SelectedMathOperandAIndexValue));
            OnPropertyChanged(nameof(SelectedMathOperandBIndexValue));
            OnPropertyChanged(nameof(SelectedMathOperandAIndexOption));
            OnPropertyChanged(nameof(SelectedMathOperandBIndexOption));
            OnPropertyChanged(nameof(CanAddMathRule));
            OnPropertyChanged(nameof(CanRemoveMathRule));

            if (includeRuleSelectorOptions)
            {
                OnPropertyChanged(nameof(MathRuleCountOptions));
                RefreshMathRuleSelectorOptions();
            }

            RefreshMathOperandSourceOptions(includeOperandASelectorOptions, includeOperandBSelectorOptions);
        }

        private void RefreshMathRuleSelectorOptions()
        {
            int optionCount = Math.Max(1, SelectedMathRuleCount);
            _mathRuleSlotOptions = Enumerable.Range(0, optionCount)
                .Select(index => new MathRuleOption(index, $"Rule {index + 1}"))
                .ToList();

            OnPropertyChanged(nameof(MathRuleSlotOptions));

            if (SelectedChannel == null)
            {
                return;
            }

            int maxRuleIndex = optionCount - 1;
            if (SelectedMathRuleSlotIndex < 0 || SelectedMathRuleSlotIndex > maxRuleIndex)
            {
                SelectedMathRuleSlotIndex = Math.Clamp(SelectedMathRuleSlotIndex, 0, maxRuleIndex);
            }

            if (SelectedChannel.MathRootRule > maxRuleIndex)
            {
                SelectedChannel.MathRootRule = (byte)maxRuleIndex;
            }

            OnPropertyChanged(nameof(SelectedMathRuleSlotIndex));
            OnPropertyChanged(nameof(SelectedMathRuleIsRoot));
            OnPropertyChanged(nameof(CanToggleSelectedMathRuleRoot));
        }

        private void RefreshMathOperandSourceOptions(bool refreshOperandAOptions = true, bool refreshOperandBOptions = true)
        {
            if (refreshOperandAOptions)
            {
                _selectedMathOperandAIndexOptions = GetMathOperandSourceOptions((OutputChannel.MathOperandType)SelectedMathOperandATypeIndex).ToList();
                OnPropertyChanged(nameof(SelectedMathOperandAIndexOptions));
                OnPropertyChanged(nameof(SelectedMathOperandAIndexOption));
            }

            if (refreshOperandBOptions)
            {
                _selectedMathOperandBIndexOptions = GetMathOperandSourceOptions((OutputChannel.MathOperandType)SelectedMathOperandBTypeIndex).ToList();
                OnPropertyChanged(nameof(SelectedMathOperandBIndexOptions));
                OnPropertyChanged(nameof(SelectedMathOperandBIndexOption));
            }
        }

        private IEnumerable<MathOperandSourceOption> GetMathOperandSourceOptions(OutputChannel.MathOperandType operandType)
        {
            return operandType switch
            {
                OutputChannel.MathOperandType.Channel => ChannelDisplayList.Select(channel => new MathOperandSourceOption(channel.Index, channel.Label)),
                OutputChannel.MathOperandType.AnalogueInput => SettingsDataView.AnalogueInputsStaticData
                    .Where(input => input.InputNumber > 0 && input.InputNumber <= byte.MaxValue)
                    .Select(input => new MathOperandSourceOption(input.InputNumber - 1, $"Analogue input {input.InputNumber}")),
                OutputChannel.MathOperandType.DigitalInput => SettingsDataView.DigitalInputsStaticData
                    .Where(input => input.InputNumber > 0 && input.InputNumber <= byte.MaxValue)
                    .Select(input => new MathOperandSourceOption(input.InputNumber - 1, $"Digital input {input.InputNumber}")),
                OutputChannel.MathOperandType.CanSignal => SettingsDataView.CustomCanSignalsStaticData
                    .Where(signal => signal.IsEnabled && SettingsDataView.CustomCanMessagesStaticData
                        .ElementAtOrDefault(signal.MessageIndex) is { IsTx: false })
                    .OrderBy(signal => signal.Index)
                    .Select(signal =>
                    {
                        int slotIndex = signal.Index;
                        string name = string.IsNullOrWhiteSpace(signal.Name)
                            ? $"CAN signal {slotIndex + 1}"
                            : signal.Name.Trim();
                        return new MathOperandSourceOption(slotIndex, name);
                    }),
                OutputChannel.MathOperandType.SystemValue => Enum.GetValues<OutputChannel.MathSystemValue>()
                    .Select(value => new MathOperandSourceOption((int)value, FormatMathSystemValueLabel(value))),
                OutputChannel.MathOperandType.Rule => Enumerable.Range(0, Math.Max(1, SelectedMathRuleCount))
                    .Select(index => new MathOperandSourceOption(index, $"Rule {index + 1}")),
                _ => []
            };
        }

        private void SetSelectedMathRuleCount(int value)
        {
            if (SelectedChannel == null)
            {
                return;
            }

            int clamped = Math.Clamp(value, 1, Constants.NUM_MATH_RULE_SLOTS);
            if (SelectedChannel.MathRuleCount == clamped)
            {
                return;
            }

            SelectedChannel.MathRuleCount = (byte)clamped;
            if (SelectedChannel.MathRuleCount <= 1)
            {
                SelectedChannel.MathRootRule = 0;
                SelectedMathRuleSlotIndex = 0;
            }
            else
            {
                if (SelectedChannel.MathRootRule >= SelectedChannel.MathRuleCount)
                {
                    SelectedChannel.MathRootRule = (byte)(SelectedChannel.MathRuleCount - 1);
                }

                if (SelectedMathRuleSlotIndex >= SelectedChannel.MathRuleCount)
                {
                    SelectedMathRuleSlotIndex = SelectedChannel.MathRuleCount - 1;
                }
            }

            RaiseMathRuleUiPropertiesChanged();
        }

        private void NormalizeMathOperandIndex(OutputChannel.MathRule rule, bool operandA)
        {
            OutputChannel.MathOperandType operandType = (OutputChannel.MathOperandType)(operandA ? rule.OperandAType : rule.OperandBType);
            List<MathOperandSourceOption> options = GetMathOperandSourceOptions(operandType).ToList();
            if (options.Count == 0)
            {
                return;
            }

            int currentValue = operandA ? rule.OperandAIndex : rule.OperandBIndex;
            int normalizedValue = options.Any(option => option.Value == currentValue)
                ? currentValue
                : options[0].Value;

            if (operandA)
            {
                rule.OperandAIndex = (byte)normalizedValue;
            }
            else
            {
                rule.OperandBIndex = (byte)normalizedValue;
            }
        }

        private static string BuildMathRuntimeErrorMessage(string scope, byte errorCode)
        {
            return errorCode == 0
                ? $"{scope} status: OK"
                : $"{scope} status: Error code {errorCode}";
        }

        private static bool IsBooleanMathOperation(OutputChannel.MathOperation operation)
        {
            return operation == OutputChannel.MathOperation.GREATER_THAN ||
                   operation == OutputChannel.MathOperation.GREATER_EQUAL ||
                   operation == OutputChannel.MathOperation.LESS_THAN ||
                   operation == OutputChannel.MathOperation.LESS_EQUAL ||
                   operation == OutputChannel.MathOperation.EQUAL ||
                   operation == OutputChannel.MathOperation.NOT_EQUAL ||
                   operation == OutputChannel.MathOperation.AND ||
                   operation == OutputChannel.MathOperation.OR ||
                   operation == OutputChannel.MathOperation.XOR ||
                   operation == OutputChannel.MathOperation.NOT ||
                   operation == OutputChannel.MathOperation.TOGGLE;
        }

        private string FormatMathRuntimeValue(float value, bool isBoolean, string unitSuffix)
        {
            if (isBoolean)
            {
                return value != 0f ? "True" : "False";
            }

            string formattedValue = Math.Abs(value - MathF.Round(value)) < 0.0001f
                ? MathF.Round(value).ToString("F0", CultureInfo.InvariantCulture)
                : value.ToString("F1", CultureInfo.InvariantCulture);

            return string.IsNullOrWhiteSpace(unitSuffix)
                ? formattedValue
                : $"{formattedValue} {unitSuffix}";
        }

        private static string GetMathRuleUsageDescription(OutputChannel channel, int ruleIndex)
        {
            int ruleCount = Math.Min(channel.MathRuleCount, channel.MathRules.Count);
            string[] consumers = Enumerable.Range(0, ruleCount)
                .Where(index => index != ruleIndex && UsesMathRule(channel.MathRules[index], ruleIndex))
                .Select(index => $"Rule {index + 1}")
                .ToArray();
            return consumers.Length == 0
                ? "not connected to the output root"
                : $"feeds {string.Join(", ", consumers)} (intermediate, not directly driving output)";
        }

        private static bool UsesMathRule(OutputChannel.MathRule rule, int ruleIndex)
        {
            return (rule.OperandAType == (byte)OutputChannel.MathOperandType.Rule && rule.OperandAIndex == ruleIndex)
                || (rule.OperandBType == (byte)OutputChannel.MathOperandType.Rule && rule.OperandBIndex == ruleIndex);
        }

        private static string GetSystemValueUnit(OutputChannel.MathSystemValue systemValue)
        {
            return systemValue switch
            {
                OutputChannel.MathSystemValue.SystemTemperature => "°C",
                OutputChannel.MathSystemValue.SIMModuleTemperature => "°C",
                OutputChannel.MathSystemValue.IMUTemperature => "°C",
                OutputChannel.MathSystemValue.BatteryVoltage => "V",
                OutputChannel.MathSystemValue.SystemCurrent => "A",
                OutputChannel.MathSystemValue.AccelerationX => "G",
                OutputChannel.MathSystemValue.AccelerationY => "G",
                OutputChannel.MathSystemValue.AccelerationZ => "G",
                OutputChannel.MathSystemValue.GyroscopeX => "°/s",
                OutputChannel.MathSystemValue.GyroscopeY => "°/s",
                OutputChannel.MathSystemValue.GyroscopeZ => "°/s",
                OutputChannel.MathSystemValue.MagneticFieldX => "µT",
                OutputChannel.MathSystemValue.MagneticFieldY => "µT",
                OutputChannel.MathSystemValue.MagneticFieldZ => "µT",
                _ => string.Empty,
            };
        }

        private string GetSelectedMathRuleUnitSuffix(OutputChannel.MathRule? rule)
        {
            if (rule == null)
            {
                return string.Empty;
            }

            OutputChannel.MathOperation operation = (OutputChannel.MathOperation)rule.Operation;
            if (IsBooleanMathOperation(operation))
            {
                return string.Empty;
            }

            OutputChannel.MathOperandType operandType = (OutputChannel.MathOperandType)rule.OperandAType;
            return operandType switch
            {
                OutputChannel.MathOperandType.SystemValue => GetSystemValueUnit((OutputChannel.MathSystemValue)rule.OperandAIndex),
                OutputChannel.MathOperandType.AnalogueInput => GetAnalogueInputUnitSuffix(rule.OperandAIndex),
                _ => string.Empty,
            };
        }

        private string GetAnalogueInputUnitSuffix(int analogueInputIndex)
        {
            if (analogueInputIndex < 0 || analogueInputIndex >= SettingsDataView.AnalogueInputsStaticData.Count)
            {
                return string.Empty;
            }

            AnalogueInput input = SettingsDataView.AnalogueInputsStaticData[analogueInputIndex];
            return input.Units switch
            {
                AnalogueInput.AnalogueUnits.Volts => "V",
                AnalogueInput.AnalogueUnits.Amps => "A",
                AnalogueInput.AnalogueUnits.Celsius => "°C",
                AnalogueInput.AnalogueUnits.Fahrenheit => "°F",
                AnalogueInput.AnalogueUnits.Percent => "%",
                AnalogueInput.AnalogueUnits.RPM => "RPM",
                AnalogueInput.AnalogueUnits.KPH => "km/h",
                AnalogueInput.AnalogueUnits.MPH => "mph",
                AnalogueInput.AnalogueUnits.Bar => "bar",
                AnalogueInput.AnalogueUnits.PSI => "psi",
                _ => string.Empty,
            };
        }

        private static string FormatEnumLabel(string value)
        {
            return value.Replace("_", " ");
        }

        private static string FormatOperandTypeLabel(OutputChannel.MathOperandType operandType)
        {
            return operandType switch
            {
                OutputChannel.MathOperandType.AnalogueInput => "Analogue input",
                OutputChannel.MathOperandType.DigitalInput => "Digital input",
                OutputChannel.MathOperandType.CanSignal => "CAN signal",
                _ => FormatEnumLabel(operandType.ToString())
            };
        }

        private static string GetMathOperationDescription(int operationIndex)
        {
            return (OutputChannel.MathOperation)operationIndex switch
            {
                OutputChannel.MathOperation.ADD => "Operand A plus Operand B.",
                OutputChannel.MathOperation.SUBTRACT => "Operand A minus Operand B.",
                OutputChannel.MathOperation.MULTIPLY => "Operand A multiplied by Operand B.",
                OutputChannel.MathOperation.DIVIDE => "Operand A divided by Operand B.",
                OutputChannel.MathOperation.GREATER_THAN => "True when Operand A is greater than Operand B.",
                OutputChannel.MathOperation.GREATER_EQUAL => "True when Operand A is greater than or equal to Operand B.",
                OutputChannel.MathOperation.LESS_THAN => "True when Operand A is less than Operand B.",
                OutputChannel.MathOperation.LESS_EQUAL => "True when Operand A is less than or equal to Operand B.",
                OutputChannel.MathOperation.EQUAL => "True when Operand A equals Operand B.",
                OutputChannel.MathOperation.NOT_EQUAL => "True when Operand A does not equal Operand B.",
                OutputChannel.MathOperation.AND => "Bitwise/logic AND of Operand A and Operand B.",
                OutputChannel.MathOperation.OR => "Bitwise/logic OR of Operand A and Operand B.",
                OutputChannel.MathOperation.XOR => "Bitwise/logic XOR of Operand A and Operand B.",
                OutputChannel.MathOperation.NOT => "Unary operator. Operand B is not used.",
                OutputChannel.MathOperation.MIN => "Returns the smaller of Operand A and Operand B.",
                OutputChannel.MathOperation.MAX => "Returns the larger of Operand A and Operand B.",
                OutputChannel.MathOperation.ABS => "Absolute value of Operand A. Operand B is not used.",
                OutputChannel.MathOperation.TOGGLE => "Unary latch: each rising edge of Operand A flips the output between 0 and 1 (push-on/push-off). Operand B is not used.",
                _ => "Select an operator for this rule."
            };
        }

        private static string FormatMathSystemValueLabel(OutputChannel.MathSystemValue value)
        {
            return value switch
            {
                OutputChannel.MathSystemValue.SystemTemperature => "System temperature (°C)",
                OutputChannel.MathSystemValue.SIMModuleTemperature => "SIM module temperature (°C)",
                OutputChannel.MathSystemValue.IMUTemperature => "IMU temperature (°C)",
                OutputChannel.MathSystemValue.BatteryVoltage => "Battery voltage (V)",
                OutputChannel.MathSystemValue.SystemCurrent => "System current (A)",
                OutputChannel.MathSystemValue.AccelerationX => "Acceleration X (G)",
                OutputChannel.MathSystemValue.AccelerationY => "Acceleration Y (G)",
                OutputChannel.MathSystemValue.AccelerationZ => "Acceleration Z (G)",
                OutputChannel.MathSystemValue.GyroscopeX => "Gyroscope X (degrees/second)",
                OutputChannel.MathSystemValue.GyroscopeY => "Gyroscope Y (degrees/second)",
                OutputChannel.MathSystemValue.GyroscopeZ => "Gyroscope Z (degrees/second)",
                OutputChannel.MathSystemValue.MagneticFieldX => "Magnetic field X (microtesla)",
                OutputChannel.MathSystemValue.MagneticFieldY => "Magnetic field Y (microtesla)",
                OutputChannel.MathSystemValue.MagneticFieldZ => "Magnetic field Z (microtesla)",
                _ => FormatEnumLabel(value.ToString())
            };
        }

        private void UpdateMathStatusPollingSelection()
        {
            if (_portService == null)
            {
                return;
            }

            int pollChannelIndex = LiveDataView.ChannelsLiveData
                .ToList().FindIndex(channel => channel.IsMathChannel);

            _portService.SetMathStatusPollingChannel(pollChannelIndex);
        }

        private static string GetMathOperandIndexLabel(int operandTypeIndex)
        {
            return (OutputChannel.MathOperandType)operandTypeIndex switch
            {
                OutputChannel.MathOperandType.Channel => "Channel index",
                OutputChannel.MathOperandType.AnalogueInput => "Analogue input index",
                OutputChannel.MathOperandType.DigitalInput => "Digital input index",
                OutputChannel.MathOperandType.CanSignal => "CAN signal",
                OutputChannel.MathOperandType.SystemValue => "System value",
                OutputChannel.MathOperandType.Rule => "Rule index",
                _ => "Index",
            };
        }

        private static bool IsAnalogueChannelType(OutputChannel.ChannelType channelType)
        {
            return channelType == OutputChannel.ChannelType.Analogue ||
                   channelType == OutputChannel.ChannelType.AnalogueScaled;
        }

        private static OutputChannel.ChannelType DefaultToAnalogueType(OutputChannel.ChannelType currentType)
        {
            return currentType switch
            {
                OutputChannel.ChannelType.Digital => OutputChannel.ChannelType.Analogue,
                OutputChannel.ChannelType.PWM => OutputChannel.ChannelType.AnalogueScaled,
                OutputChannel.ChannelType.Intermittent => OutputChannel.ChannelType.Analogue,
                _ => currentType,
            };
        }

        private static OutputChannel.ChannelType DefaultToDigitalType(OutputChannel.ChannelType currentType)
        {
            return currentType switch
            {
                OutputChannel.ChannelType.Analogue => OutputChannel.ChannelType.Digital,
                OutputChannel.ChannelType.AnalogueScaled => OutputChannel.ChannelType.PWM,
                _ => currentType,
            };
        }

        private OutputChannel.ChannelType GetCompatibleChannelTypeForInput(byte inputControlPin, OutputChannel.ChannelType desiredType)
        {
            int analogueInputIndex = Array.IndexOf(ANAChannelInputPins, inputControlPin);
            if (analogueInputIndex < 0 || analogueInputIndex >= SettingsDataView.AnalogueInputsStaticData.Count)
            {
                return InputPinCatalog.DIChannelInputPins.Contains(inputControlPin)
                    || inputControlPin == InputPinCatalog.IgnitionInputPin
                    ? IsDigitalOutputChannelType(desiredType) ? desiredType : OutputChannel.ChannelType.Digital
                    : desiredType;
            }

            var analogueInput = SettingsDataView.AnalogueInputsStaticData[analogueInputIndex];
            if (analogueInput.ChanType == AnalogueInput.AnalogueChannelType.Digital)
            {
                OutputChannel.ChannelType digitalType = DefaultToDigitalType(desiredType);
                return IsDigitalOutputChannelType(digitalType)
                    ? digitalType
                    : OutputChannel.ChannelType.Digital;
            }

            OutputChannel.ChannelType analogueType = IsDigitalOutputChannelType(desiredType)
                ? DefaultToAnalogueType(desiredType)
                : desiredType;
            return analogueType is OutputChannel.ChannelType.Analogue or OutputChannel.ChannelType.AnalogueScaled
                ? analogueType
                : OutputChannel.ChannelType.Analogue;
        }

        private static bool IsDigitalOutputChannelType(OutputChannel.ChannelType channelType)
        {
            return channelType is OutputChannel.ChannelType.Digital or OutputChannel.ChannelType.PWM or OutputChannel.ChannelType.Intermittent;
        }

        private void SyncChannelTypeForAssignedInput(OutputChannel channel)
        {
            var syncedType = GetCompatibleChannelTypeForInput(channel.InputControlPin, channel.ChanType);

            if (syncedType != channel.ChanType)
            {
                channel.ChanType = syncedType;
            }
        }

        private void DefaultChannelsForAnalogueInput(int analogueInputIndex)
        {
            if (analogueInputIndex < 0 || analogueInputIndex >= ANAChannelInputPins.Length)
            {
                return;
            }

            byte analoguePin = ANAChannelInputPins[analogueInputIndex];
            foreach (var channel in SettingsDataView.ChannelsStaticData)
            {
                if (channel.InputControlPin != analoguePin)
                {
                    continue;
                }

                SyncChannelTypeForAssignedInput(channel);
            }

            if (SelectedChannel != null)
            {
                SelectedChannelTypeDisplay = ChannelTypes.FirstOrDefault(ctd => ctd.ChannelType == SelectedChannel.ChanType);
                RefreshInputDisplayList(SelectedChannel.ChanType);
                SelectedPinNumber = SelectedChannel.InputControlPin;
                IsPWMChannel = SelectedChannel.IsPWMChannel;
            }
        }

        private AnalogueInput? GetAssociatedAnalogueInputConfig(byte inputControlPin)
        {
            int analogueInputIndex = Array.IndexOf(ANAChannelInputPins, inputControlPin);
            if (analogueInputIndex < 0 || analogueInputIndex >= SettingsDataView.AnalogueInputsStaticData.Count)
            {
                return null;
            }

            return SettingsDataView.AnalogueInputsStaticData[analogueInputIndex];
        }

        private static string GetUnitsSuffix(AnalogueInput.AnalogueUnits units)
        {
            return units switch
            {
                AnalogueInput.AnalogueUnits.Volts => "V",
                AnalogueInput.AnalogueUnits.Amps => "A",
                AnalogueInput.AnalogueUnits.Celsius => "°C",
                AnalogueInput.AnalogueUnits.Fahrenheit => "°F",
                AnalogueInput.AnalogueUnits.Percent => "%",
                AnalogueInput.AnalogueUnits.RPM => "RPM",
                AnalogueInput.AnalogueUnits.KPH => "kph",
                AnalogueInput.AnalogueUnits.MPH => "mph",
                AnalogueInput.AnalogueUnits.Bar => "bar",
                AnalogueInput.AnalogueUnits.PSI => "psi",
                _ => ""
            };
        }

        private static bool UseDecimalPrecision(AnalogueInput.AnalogueUnits units)
        {
            return units == AnalogueInput.AnalogueUnits.Volts ||
                   units == AnalogueInput.AnalogueUnits.Amps ||
                   units == AnalogueInput.AnalogueUnits.Bar;
        }

        private static (double Min, double Max) GetInputRangeForAnalogueConfig(AnalogueInput input)
        {
            if (input.ChanType == AnalogueInput.AnalogueChannelType.NTC)
            {
                if (input.Units == AnalogueInput.AnalogueUnits.Fahrenheit)
                {
                    return (-22.0, 302.0);
                }

                return (-30.0, 150.0);
            }

            if (input.ReportsConnectorVoltage)
            {
                return (AnalogueInput.ConnectorVoltageMinimum, AnalogueInput.ConnectorVoltageMaximum);
            }

            double min = Math.Min(input.ConfigRangeMin, input.ConfigRangeMax);
            double max = Math.Max(input.ConfigRangeMin, input.ConfigRangeMax);
            if (Math.Abs(max - min) < 0.001)
            {
                max = min + 1.0;
            }

            return (min, max);
        }

        public string SelectedChannelInputUnits
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return "V";
                }

                var input = GetAssociatedAnalogueInputConfig(SelectedChannel.InputControlPin);
                if (input == null)
                {
                    return "V";
                }

                return GetUnitsSuffix(input.Units);
            }
        }

        public int SelectedChannelInputDecimalPlaces
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return 1;
                }

                var input = GetAssociatedAnalogueInputConfig(SelectedChannel.InputControlPin);
                if (input == null)
                {
                    return 1;
                }

                return UseDecimalPrecision(input.Units) ? 1 : 0;
            }
        }

        public double SelectedChannelInputTickFrequency => SelectedChannelInputDecimalPlaces == 0 ? 1.0 : 0.1;

        private string FormatSelectedChannelValue(float value)
        {
            return value.ToString(SelectedChannelInputDecimalPlaces == 0 ? "F0" : "F1");
        }

        public string SelectedChannelOnThresholdDisplay
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return "0";
                }

                return FormatSelectedChannelValue(SelectedChannel.OnThreshold);
            }
        }

        public bool SelectedChannelUsesNegativeGoingThreshold
        {
            get => SelectedChannel != null && SelectedChannel.OnThreshold < SelectedChannel.OffThreshold;
            set
            {
                if (SelectedChannel == null)
                {
                    return;
                }

                bool currentValue = SelectedChannel.OnThreshold < SelectedChannel.OffThreshold;
                if (currentValue == value)
                {
                    return;
                }

                float previousOnThreshold = SelectedChannel.OnThreshold;
                SelectedChannel.OnThreshold = SelectedChannel.OffThreshold;
                SelectedChannel.OffThreshold = previousOnThreshold;
                NotifyAnalogueChannelUiContextChanged();
            }
        }

        public string SelectedChannelOffThresholdDisplay
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return "0";
                }

                return FormatSelectedChannelValue(SelectedChannel.OffThreshold);
            }
        }

        public string SelectedChannelScaleMinDisplay
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return "0";
                }

                return FormatSelectedChannelValue(SelectedChannel.ScaleMin);
            }
        }

        public string SelectedChannelScaleMaxDisplay
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return "0";
                }

                return FormatSelectedChannelValue(SelectedChannel.ScaleMax);
            }
        }

        public double SelectedChannelInputMinimum
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return 0.0;
                }

                var input = GetAssociatedAnalogueInputConfig(SelectedChannel.InputControlPin);
                if (input == null)
                {
                    return 0.0;
                }

                return GetInputRangeForAnalogueConfig(input).Min;
            }
        }

        public double SelectedChannelInputMaximum
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return 5.0;
                }

                var input = GetAssociatedAnalogueInputConfig(SelectedChannel.InputControlPin);
                if (input == null)
                {
                    return 5.0;
                }

                return GetInputRangeForAnalogueConfig(input).Max;
            }
        }

        public double SelectedChannelOnThresholdMinimum
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return 0.0;
                }

                return SelectedChannelUsesNegativeGoingThreshold
                    ? SelectedChannelInputMinimum
                    : SelectedChannel.OffThreshold;
            }
        }

        public double SelectedChannelOnThresholdMaximum
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return 5.0;
                }

                return SelectedChannelUsesNegativeGoingThreshold
                    ? SelectedChannel.OffThreshold
                    : SelectedChannelInputMaximum;
            }
        }

        public double SelectedChannelOffThresholdMinimum
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return 0.0;
                }

                return SelectedChannelUsesNegativeGoingThreshold
                    ? SelectedChannel.OnThreshold
                    : SelectedChannelInputMinimum;
            }
        }

        public double SelectedChannelOffThresholdMaximum
        {
            get
            {
                if (SelectedChannel == null)
                {
                    return 5.0;
                }

                return SelectedChannelUsesNegativeGoingThreshold
                    ? SelectedChannelInputMaximum
                    : SelectedChannel.OnThreshold;
            }
        }

        public string SelectedChannelThresholdDirectionSummary => SelectedChannelUsesNegativeGoingThreshold
            ? "Turns ON below the lower threshold and OFF above the upper threshold"
            : "Turns ON above the upper threshold and OFF below the lower threshold";

        public string SelectedChannelOnThresholdLabel => SelectedChannelUsesNegativeGoingThreshold
            ? $"On below ({SelectedChannelInputUnits})"
            : $"On above ({SelectedChannelInputUnits})";

        public string SelectedChannelOffThresholdLabel => SelectedChannelUsesNegativeGoingThreshold
            ? $"Off above ({SelectedChannelInputUnits})"
            : $"Off below ({SelectedChannelInputUnits})";

        public string SelectedChannelOnThresholdTooltip => SelectedChannelUsesNegativeGoingThreshold
            ? "Channel turns ON below this value"
            : "Channel turns ON above this value";

        public string SelectedChannelOffThresholdTooltip => SelectedChannelUsesNegativeGoingThreshold
            ? "Channel turns OFF above this value"
            : "Channel turns OFF below this value";

        public string SelectedChannelScaleLabel => $"Analogue input range ({SelectedChannelInputUnits})";

        public CanIdOption? SelectedSystemDataCanId
        {
            get => GetCanIdOption(SettingsDataView.SystemParamsStaticData.SystemDataCANID);
            set
            {
                if (value != null)
                {
                    SettingsDataView.SystemParamsStaticData.SystemDataCANID = value.Value;
                }
            }
        }

        public CanIdOption? SelectedDigitalInputDataCanId
        {
            get => GetCanIdOption(SettingsDataView.SystemParamsStaticData.DigitalInputDataCANID);
            set
            {
                if (value != null)
                {
                    SettingsDataView.SystemParamsStaticData.DigitalInputDataCANID = value.Value;
                }
            }
        }

        public CanIdOption? SelectedAnalogueInputDataCanId
        {
            get => GetCanIdOption(SettingsDataView.SystemParamsStaticData.AnalogueInputDataCANID);
            set
            {
                if (value != null)
                {
                    SettingsDataView.SystemParamsStaticData.AnalogueInputDataCANID = value.Value;
                }
            }
        }

        public CanIdOption? SelectedSystemConfigCanId
        {
            get => GetCanIdOption(SettingsDataView.SystemParamsStaticData.SystemConfigCANID);
            set
            {
                if (value != null)
                {
                    SettingsDataView.SystemParamsStaticData.SystemConfigCANID = value.Value;
                }
            }
        }

        public CanIdOption? SelectedChannelDataCanId
        {
            get => GetCanIdOption(SettingsDataView.SystemParamsStaticData.ChannelDataCANID);
            set
            {
                if (value != null)
                {
                    SettingsDataView.SystemParamsStaticData.ChannelDataCANID = value.Value;
                }
            }
        }

        public CanIdOption? SelectedConfigDataCanId
        {
            get => GetCanIdOption(SettingsDataView.SystemParamsStaticData.ConfigDataCANID);
            set
            {
                if (value != null)
                {
                    SettingsDataView.SystemParamsStaticData.ConfigDataCANID = value.Value;
                }
            }
        }

        public CanBitrateOption? SelectedCanBusBitrate
        {
            get => GetCanBitrateOption(SettingsDataView.SystemParamsStaticData.CANBusBitrate);
            set
            {
                if (value != null)
                {
                    SettingsDataView.SystemParamsStaticData.CANBusBitrate = value.Value;
                }
            }
        }

        /// <summary>
        /// Called by the view when the user taps a GPS point on the map.
        /// Populates the inspection grid with the 10 Hz rows associated with that second.
        /// </summary>
        public void SelectLogMapPoint(LogMapGridRow row)
        {
            var columns = GetSelectedLogMapParameterColumns();

            var newRows = new ObservableCollection<LogMapInspectionRow>();
            foreach (var parsedRow in row.AssociatedRows)
            {
                var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                values["_ts"] = parsedRow.Timestamp
                    .LocalDateTime.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

                foreach (var col in columns)
                {
                    if (parsedRow.NumericValues.TryGetValue(col.Key, out double value))
                    {
                        string text = FormatLogMapNumericValue(value);
                        if (TryGetLogSeriesUnit(col.Key, out string? unit) && !string.IsNullOrWhiteSpace(unit))
                            text = $"{text} {unit}";
                        values[col.ColumnId] = text;
                    }
                }

                newRows.Add(new LogMapInspectionRow(values));
            }

            LogMapInspectionRows = newRows;
        }

        private void NotifyAnalogueChannelUiContextChanged()
        {
            OnPropertyChanged(nameof(SelectedChannelInputUnits));
            OnPropertyChanged(nameof(SelectedChannelInputMinimum));
            OnPropertyChanged(nameof(SelectedChannelInputMaximum));
            OnPropertyChanged(nameof(SelectedChannelInputDecimalPlaces));
            OnPropertyChanged(nameof(SelectedChannelInputTickFrequency));
            OnPropertyChanged(nameof(SelectedChannelUsesNegativeGoingThreshold));
            OnPropertyChanged(nameof(SelectedChannelThresholdDirectionSummary));
            OnPropertyChanged(nameof(SelectedChannelOnThresholdLabel));
            OnPropertyChanged(nameof(SelectedChannelOffThresholdLabel));
            OnPropertyChanged(nameof(SelectedChannelOnThresholdTooltip));
            OnPropertyChanged(nameof(SelectedChannelOffThresholdTooltip));
            OnPropertyChanged(nameof(SelectedChannelOnThresholdMinimum));
            OnPropertyChanged(nameof(SelectedChannelOnThresholdMaximum));
            OnPropertyChanged(nameof(SelectedChannelOffThresholdMinimum));
            OnPropertyChanged(nameof(SelectedChannelOffThresholdMaximum));
            OnPropertyChanged(nameof(SelectedChannelScaleLabel));
            OnPropertyChanged(nameof(SelectedChannelOnThresholdDisplay));
            OnPropertyChanged(nameof(SelectedChannelOffThresholdDisplay));
            OnPropertyChanged(nameof(SelectedChannelScaleMinDisplay));
            OnPropertyChanged(nameof(SelectedChannelScaleMaxDisplay));
        }

        private float? GetAssociatedAnalogueInputVoltage(byte inputControlPin)
        {
            int analogueInputIndex = Array.IndexOf(ANAChannelInputPins, inputControlPin);
            if (analogueInputIndex < 0 || analogueInputIndex >= LiveDataView.AnalogueInputsLiveData.Count)
            {
                return null;
            }

            return LiveDataView.AnalogueInputsLiveData[analogueInputIndex].InputVoltage;
        }

        private void UpdateChannelAnalogueVoltageDisplayValues()
        {
            foreach (var channel in LiveDataView.ChannelsLiveData)
            {
                if (channel == null)
                {
                    continue;
                }

                string displayValue = "-";
                if (channel.ChanType == OutputChannel.ChannelType.Analogue ||
                    channel.ChanType == OutputChannel.ChannelType.AnalogueScaled)
                {
                    int analogueInputIndex = Array.IndexOf(ANAChannelInputPins, channel.InputControlPin);
                    if (analogueInputIndex >= 0 && analogueInputIndex < LiveDataView.AnalogueInputsLiveData.Count)
                    {
                        var anaInput = LiveDataView.AnalogueInputsLiveData[analogueInputIndex];
                        float? liveValue = anaInput.InputValue ?? anaInput.InputVoltage;
                        if (liveValue.HasValue)
                        {
                            string valueText = UseDecimalPrecision(anaInput.Units)
                                ? liveValue.Value.ToString("F1")
                                : liveValue.Value.ToString("F0");
                            displayValue = $"{valueText} {GetUnitsSuffix(anaInput.Units)}";
                        }
                    }
                }

                if (channel.AnalogueInputVoltageDisplay != displayValue)
                {
                    channel.AnalogueInputVoltageDisplay = displayValue;
                }
            }
        }

        private static string GetInputLabelForPin(byte pin)
        {
            if (pin == InputPinCatalog.IgnitionInputPin)
            {
                return "Ignition";
            }

            var digitalIndex = Array.IndexOf(InputPinCatalog.DIChannelInputPins, pin);
            if (digitalIndex >= 0)
            {
                return $"Digital {digitalIndex + 1}";
            }

            var analogueIndex = Array.IndexOf(InputPinCatalog.ANAChannelInputPins, pin);
            if (analogueIndex >= 0)
            {
                return $"Ana/Dig {analogueIndex + 1}";
            }

            return $"Pin {pin}";
        }

        private void RefreshInputDisplayList(OutputChannel.ChannelType channelType)
        {
            if (channelType == OutputChannel.ChannelType.MATH)
            {
                InputDisplayList = [];
                SelectedInputLabel = null;
                if (SelectedChannel is OutputChannel channel && channel.InputControlPin != 0)
                {
                    channel.InputControlPin = 0;
                }

                return;
            }

            var allowedPins = IsAnalogueChannelType(channelType)
                ? InputPinCatalog.ANAChannelInputPins
                : InputPinCatalog.AllInputPins;

            InputDisplayList = new ObservableCollection<InputLabel>(
                allowedPins.Select(pin => new InputLabel(pin, GetInputLabelForPin(pin))));

            if (SelectedChannel == null)
            {
                SelectedInputLabel = null;
                return;
            }

            var selectedInput = InputDisplayList.FirstOrDefault(input => input.Pin == SelectedChannel.InputControlPin);

            if (selectedInput == null && InputDisplayList.Count > 0)
            {
                selectedInput = InputDisplayList[0];
                SelectedChannel.InputControlPin = selectedInput.Pin;
            }

            SelectedInputLabel = selectedInput;
        }

        public MainWindowViewModel(
            IAppCloser appCloser,
            MainWindowViewModelDependencies? dependencies = null)
        {
            IsConnected = false;
            SdOK = false;
            OverCurrent = false;
            OverTemperature = false;
            UnderVoltage = true;
            CrcFailed = false;
            isPWMChannel = false;
            GpsOK = false;
            ResetCellularTestStatus();

            ChannelIndices = new ObservableCollection<int>(
            Enumerable.Range(0, SettingsDataView.ChannelsStaticData.Count));

            ChannelDisplayList = new ObservableCollection<ChannelLabel>(
    Enumerable.Range(0, SettingsDataView.ChannelsStaticData.Count)
    .Select(i => new ChannelLabel(i)));

            InputDisplayList = new ObservableCollection<InputLabel>(
                InputPinCatalog.AllInputPins.Select(pin => new InputLabel(pin, GetInputLabelForPin(pin))));

            ChannelTypes = new ObservableCollection<ChannelTypeDisplay>
    {
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.Digital, Label = "Digital Input" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.PWM, Label = "Digital PWM" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.Intermittent, Label = "Digital intermittent" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.Analogue, Label = "Analogue threshold" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.AnalogueScaled, Label = "Analogue scaled PWM" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.CAN, Label = "CAN Digital" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.CAN_PWM, Label = "CAN PWM" },
        new ChannelTypeDisplay { ChannelType = OutputChannel.ChannelType.MATH, Label = "MATH" },
    };

            MathOutputModes = new ObservableCollection<string>
            {
                "Digital",
                "PWM",
            };

            MathOperations = new ObservableCollection<string>(
                Enum.GetValues<OutputChannel.MathOperation>().Select(value => FormatEnumLabel(value.ToString())));

            MathOperandTypes = new ObservableCollection<string>(
                Enum.GetValues<OutputChannel.MathOperandType>().Select(FormatOperandTypeLabel));

            MathSystemValues = new ObservableCollection<string>(
                Enum.GetValues<OutputChannel.MathSystemValue>().Select(FormatMathSystemValueLabel));

            ChannelCategories = new ObservableCollection<ChannelCategoryDisplay>
            {
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.ECUPower, Label = "ECU Power" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.IgnitionCoils, Label = "Ignition Coils" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.FuelPump, Label = "Fuel Pump" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.FuelInjectors, Label = "Fuel Injectors" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.EngineSensorsSupply, Label = "Engine Sensors Supply" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.DriveByWire, Label = "Drive-by-Wire" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.Headlights, Label = "Headlights" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.BrakeLights, Label = "Brake Lights" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.Indicators, Label = "Indicators" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.HazardLights, Label = "Hazard Lights" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.Horn, Label = "Horn" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.Wipers, Label = "Wipers" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.WasherPump, Label = "Washer Pump" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.ABSBrakeSystem, Label = "ABS / Brake System" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.PowerSteering, Label = "Power Steering" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.CoolingFan, Label = "Cooling Fan" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.OilCoolerFan, Label = "Oil Cooler Fan" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.WaterPump, Label = "Water Pump" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.IntercoolerPump, Label = "Intercooler Pump" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.TransmissionPump, Label = "Transmission Pump" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.TailLights, Label = "Tail Lights" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.DRL, Label = "DRL" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.ReverseLights, Label = "Reverse Lights" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.InteriorLights, Label = "Interior Lights" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.DashCluster, Label = "Dash / Cluster" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.GearSelector, Label = "Gear Selector" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.HeatedSeats, Label = "Heated Seats" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.HeatedSteeringWheel, Label = "Heated Steering Wheel" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.HVACBlower, Label = "HVAC Blower" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.ACClutch, Label = "AC Clutch" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.Infotainment, Label = "Infotainment" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.USBAccessoryPower, Label = "USB / Accessory Power" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.DataLogger, Label = "Data Logger" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.Telemetry, Label = "Telemetry" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.CameraSystem, Label = "Camera System" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.LapTimer, Label = "Lap Timer" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.CoolSuitPump, Label = "Cool Suit Pump" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.FireSuppression, Label = "Fire Suppression" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.RainLight, Label = "Rain Light" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.PitLimiter, Label = "Pit Limiter" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.Auxiliary, Label = "Auxiliary" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.Spare, Label = "Spare" },
                new ChannelCategoryDisplay { Category = OutputChannel.ChannelCategory.Custom, Label = "Custom" },
            };

            SelectedChannelLabel = ChannelDisplayList.FirstOrDefault() ?? new ChannelLabel(0);

            SelectedDigitalInput = SettingsDataView.DigitalInputsStaticData.FirstOrDefault();

            SelectedAnalogueInput = SettingsDataView.AnalogueInputsStaticData.FirstOrDefault();

            SelectedChannel = new OutputChannel();

            UpperAnalogueTH = 5.0;
            UpperPWMRange = 100;

            AnalogueChannelTypes = new ObservableCollection<AnalogueTypeDisplay>
            {
                new AnalogueTypeDisplay { Type = AnalogueInput.AnalogueChannelType.RawVoltage, Label = "Raw voltage" },
                new AnalogueTypeDisplay { Type = AnalogueInput.AnalogueChannelType.Digital, Label = "Digital" },
                new AnalogueTypeDisplay { Type = AnalogueInput.AnalogueChannelType.Active, Label = "Active sensor" },
                new AnalogueTypeDisplay { Type = AnalogueInput.AnalogueChannelType.Passive, Label = "Passive sensor" },
                new AnalogueTypeDisplay { Type = AnalogueInput.AnalogueChannelType.NTC, Label = "NTC thermistor" },
            };

            AnalogueUnits = new ObservableCollection<AnalogueUnitDisplay>
            {
                new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Volts, Label = "Volts (V)" },
                new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Amps, Label = "Amps (A)" },
                new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Celsius, Label = "Celsius (°C)" },
                new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Fahrenheit, Label = "Fahrenheit (°F)" },
                new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Percent, Label = "Percent (%)" },
                new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.RPM, Label = "RPM" },
                new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.KPH, Label = "KPH" },
                new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.MPH, Label = "MPH" },
                new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Bar, Label = "Bar" },
                new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.PSI, Label = "PSI" },
            };

            AnalogueCalibrationPointOptions = new ObservableCollection<byte> { 2, 3 };

            AvailableCanIds = new ObservableCollection<CanIdOption>(
                Enumerable.Range(0, 0x800)
                    .Select(value => new CanIdOption((ushort)value, $"0x{value:X3}")));

            AvailableCanBitrates = new ObservableCollection<CanBitrateOption>
            {
                new CanBitrateOption(Constants.CAN_BITRATE_125K, "125 kbit/s"),
                new CanBitrateOption(Constants.CAN_BITRATE_250K, "250 kbit/s"),
                new CanBitrateOption(Constants.CAN_BITRATE_500K, "500 kbit/s"),
                new CanBitrateOption(Constants.CAN_BITRATE_1M, "1 Mbit/s"),
            };

            TimeZones = new ObservableCollection<TimeZoneDisplay>(BuildTimeZoneOptions());

            dependencies ??= new MainWindowViewModelDependencies();

            _appCloser = appCloser;
            _appUpdateService = dependencies.AppUpdateService;
            _firmwareUpdateService = dependencies.FirmwareUpdateService;
            _provisioningApiClient = dependencies.ProvisioningApiClient;
            _provisioningSessionStore = dependencies.ProvisioningSessionStore;
            _ioFlowGraphStore = dependencies.IoFlowGraphStore;
            ExitCommand = new RelayCommand(OnExit);

            SystemDateTime = string.Empty;
            var now = DateTimeOffset.Now;
            ControllerRtcDate = now.Date;
            ControllerRtcTime = now.TimeOfDay;
            SyncSelectedTimeZoneFromSettings();

            LoadSerialPorts();

            _pollTimer.Elapsed += (s, e) => LoadSerialPorts();
            _pollTimer.Start();

            _commsTimer.Elapsed += (s, e) => HandleComms();
            _commsTimer.Start();
            AttachSettingsTracking(SettingsDataView);
            InitializeIoFlowGraph();
            InitializeCustomCanFeature();
            RebuildConnectorPins();
            SelectedChannel.PropertyChanged += SelectedChannel_PropertyChanged;

            // Create a LineSeries for each channel
            int channelNumber = 1;
            foreach (var ch in LiveDataView.ChannelsLiveData)
            {
                var points = new ObservableCollection<ObservablePoint>();
                int seriesIndex = channelNumber - 1;
                SKColor seriesColor = GetLiveSeriesColor(seriesIndex);

                var series = new LineSeries<ObservablePoint>
                {
                    Values = points,
                    Name = "CH" + channelNumber++,
                    GeometrySize = 0,
                    GeometryStroke = null,
                    GeometryFill = null,
                    Fill = null,
                    Stroke = new SolidColorPaint(seriesColor) { StrokeThickness = 1.0f },
                    LineSmoothness = 0,
                    AnimationsSpeed = LiveChartSeriesAnimationSpeed,
                    EasingFunction = EasingFunctions.Lineal,
                };

                seriesCollection.Add(series);
                var toggle = new ChartSeriesToggleItem(
                    GetLiveSeriesDisplayName(ch, seriesIndex),
                    CreateLiveSeriesBrush(seriesColor),
                    series,
                    onVisibilityChanged: _ => RefreshLiveChartAxes());
                LiveSeriesToggles.Add(toggle);
                ChannelLiveSeriesToggles.Add(toggle);

                foreach (var srs in seriesCollection)
                {
                    if (srs is LineSeries<ObservablePoint> lineSeries)
                    {
                        lineSeries.LineSmoothness = 0;
                        lineSeries.AnimationsSpeed = LiveChartSeriesAnimationSpeed;
                        lineSeries.EasingFunction = EasingFunctions.Lineal;
                        if (lineSeries.Stroke is SolidColorPaint paint)
                        {
                            paint.StrokeThickness = 1.0f;
                        }
                    }
                }
            }

            for (int i = 0; i < LiveDataView.AnalogueInputsLiveData.Count; i++)
            {
                var analogueInput = LiveDataView.AnalogueInputsLiveData[i];
                int seriesIndex = LiveDataView.ChannelsLiveData.Count + i;
                SKColor seriesColor = GetLiveSeriesColor(seriesIndex);

                var series = new LineSeries<ObservablePoint>
                {
                    Values = new ObservableCollection<ObservablePoint>(),
                    Name = $"ANA{i + 1}",
                    GeometrySize = 0,
                    GeometryStroke = null,
                    GeometryFill = null,
                    Fill = null,
                    Stroke = new SolidColorPaint(seriesColor) { StrokeThickness = 1.0f },
                    LineSmoothness = 0,
                    AnimationsSpeed = LiveChartSeriesAnimationSpeed,
                    EasingFunction = EasingFunctions.Lineal,
                };

                seriesCollection.Add(series);
                var toggle = new ChartSeriesToggleItem(
                    GetLiveAnalogueSeriesDisplayName(analogueInput, i),
                    CreateLiveSeriesBrush(seriesColor),
                    series,
                    onVisibilityChanged: _ => RefreshLiveChartAxes());
                LiveSeriesToggles.Add(toggle);
                AnalogueLiveSeriesToggles.Add(toggle);
            }

            UpdateLiveSeriesToggleLabels();
            ApplySavedLiveChartPreferences();
            RefreshLiveChartAxes();
            UpdateLiveCrosshairState();

            _uiUpdateTimer = new System.Timers.Timer(50);
            _uiUpdateTimer.Elapsed += OnUIUpdateTimerElapsed;
            _uiUpdateTimer.AutoReset = true;
            _uiUpdateTimer.Start();

            _logViewportMonitorTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250),
            };
            _logViewportMonitorTimer.Tick += (_, _) => RefreshLogSeriesForViewportIfNeeded();
            _logViewportMonitorTimer.Start();

            _liveHoverClearTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1400),
            };
            _liveHoverClearTimer.Tick += (_, _) =>
            {
                _liveHoverClearTimer.Stop();
                ClearLiveChartSelectionState();
            };

            BuildLogParameterSelections();
            InitializeDefaultLogRange();
            if (LogXAxes.FirstOrDefault() is Axis logXAxis)
            {
                logXAxis.Labeler = value => FormatLogTimestampLabel(value);
            }
            UpdateLogCrosshairState();
            UpdateApplicationUpdateButtonPresentation();
            NotifyApplicationUpdateInfoChanged();
            StartBackgroundTask(RefreshApplicationUpdateStateAsync(), "Application update check");
            StartBackgroundTask(RefreshAvailableLogFilesAsync(), "Log file refresh");
        }

        private void ConfigSaved(object? sender, EventArgs e)
        {
            IsSendingConfig = false;
            _controllerConfigBaseline = _configurationBeingSaved ?? DeepCopyDataStructures(SettingsDataView);
            _configurationBeingSaved = null;
            _portService?.UpdateSettingsData(SettingsDataView);
            RecalculatePendingConfigChanges();
            if (HasPendingConfigChanges)
            {
                _ioFlowBaselineSnapshot = _ioFlowSnapshotBeingSaved;
                _ioFlowHasUnsavedControllerChanges = true;
                PersistIoFlowGraphSnapshotLocally();
                UpdateIoFlowControllerMismatchWarning();
            }
            else
            {
                PersistIoFlowGraphSnapshotForController();
            }
            _ioFlowSnapshotBeingSaved = null;
            SystemDateTime = $"Last updated: {DateTime.Now:yyyy/MM/dd HH:mm:ss}";
            StartBackgroundTask(FlashLastUpdatedAsync(), "Last-updated highlight");
        }

        private void ConfigSaveCompleted(object? sender, ConfigurationSaveCompletedEventArgs e)
        {
            if (!e.Succeeded)
            {
                IsSendingConfig = false;
                _configurationBeingSaved = null;
                _ioFlowSnapshotBeingSaved = null;
                _portService?.UpdateSettingsData(SettingsDataView);
            }

            _configSaveCompletionTcs?.TrySetResult(e.Succeeded);
            _configSaveCompletionTcs = null;
            OnPropertyChanged(nameof(CanTestCellularConnection));
        }

        public async Task FlashLastUpdatedAsync()
        {
            LastUpdatedHighlightOpacity = 1;
            await Task.Delay(1200);
            LastUpdatedHighlightOpacity = 0;
        }

        public ICartesianAxis[] YAxes { get; set; } =
        [
            CreateLiveAxis("Current (Amps)", AxisPosition.Start, value => value.ToString("F2"))
        ];

        public ICartesianAxis[] XAxes { get; set; } = [
        new Axis
        {
            Name = "Time",
            Labeler = value =>
            {
                try
                {
                    // Convert Unix timestamp (seconds since epoch) to DateTime
                    var timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)value).LocalDateTime;
                    return timestamp.ToString("HH:mm:ss");
                }
                catch
                {
                    return string.Empty;
                }
            },
            ShowSeparatorLines = true,
            SeparatorsPaint = new SolidColorPaint
            {
                StrokeThickness = 1,
                Color = new SKColor(120, 120, 120),
            },
            SubseparatorsPaint = new SolidColorPaint
            {
                Color = new SKColor(50, 50, 50),
                StrokeThickness = 0.5f,
            },
            SubseparatorsCount = 9,
            ZeroPaint = new SolidColorPaint
            {
                Color = new SKColor(150, 150, 150),
                StrokeThickness = 2,
            },
            TicksPaint = new SolidColorPaint
            {
                Color = new SKColor(140, 140, 140),
                StrokeThickness = 1.5f,
            },
            SubticksPaint = new SolidColorPaint
            {
                Color = new SKColor(50, 50, 50),
                StrokeThickness = 1
            },
        }
    ];

        private void SelectedChannel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            Debug.WriteLine("Channel property changed: " + e.PropertyName);
        }

        private void SettingsModelItem_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (sender is AnalogueInput analogueInput && e.PropertyName == nameof(AnalogueInput.ChanType))
            {
                int analogueIndex = SettingsDataView.AnalogueInputsStaticData.IndexOf(analogueInput);
                DefaultChannelsForAnalogueInput(analogueIndex);
            }

            if (sender is OutputChannel outputChannel &&
                e.PropertyName == nameof(OutputChannel.InputControlPin))
            {
                if (outputChannel.ChanType == OutputChannel.ChannelType.MATH
                    && outputChannel.InputControlPin != 0)
                {
                    outputChannel.InputControlPin = 0;
                }
                else
                {
                    SyncChannelTypeForAssignedInput(outputChannel);
                }

                RefreshIoFlowConnections();
            }

            if (sender is OutputChannel changedChannel &&
                e.PropertyName == nameof(OutputChannel.ChanType)
                && changedChannel.InputControlPin != 0
                && !OutputNodeViewModel.IsInputTypeCompatible(
                    changedChannel.InputControlPin,
                    changedChannel.ChanType,
                    SettingsDataView.AnalogueInputsStaticData))
            {
                changedChannel.InputControlPin = 0;
            }

            if (!IoFlowGraph.IsApplyingMathRulesFromGraph)
            {
                if (sender is OutputChannel mathChannel &&
                    (e.PropertyName == nameof(OutputChannel.ChanType)
                        || e.PropertyName == nameof(OutputChannel.MathRootRule)
                        || e.PropertyName == nameof(OutputChannel.MathRuleCount)))
                {
                    IoFlowGraph.SynchronizeMathChannelFromSettings(mathChannel);
                }
                else if (sender is OutputChannel.MathRule mathRule)
                {
                    OutputChannel? ownerChannel = SettingsDataView.ChannelsStaticData
                        .FirstOrDefault(channel => channel.MathRules.Contains(mathRule));
                    if (ownerChannel != null)
                    {
                        IoFlowGraph.SynchronizeMathChannelFromSettings(ownerChannel);
                    }
                }
            }

            if (SelectedChannel != null && ReferenceEquals(sender, SelectedChannel))
            {
                RaiseMathRuleUiPropertiesChanged();
            }
            else if (SelectedChannel != null
                && sender is OutputChannel.MathRule selectedRule
                && SelectedChannel.MathRules.Contains(selectedRule))
            {
                RaiseMathRuleUiPropertiesChanged(
                    includeRuleSelectorOptions: false,
                    includeOperandASelectorOptions: e.PropertyName == nameof(OutputChannel.MathRule.OperandAType),
                    includeOperandBSelectorOptions: e.PropertyName == nameof(OutputChannel.MathRule.OperandBType));
            }

            if (sender is OutputChannel && e.PropertyName == nameof(OutputChannel.ChanType))
            {
                ConnectorLegendItems = BuildConnectorLegendItems(ConnectorPins);
            }

            if (!_suppressDirtyTracking && _controllerConfigBaseline != null)
            {
                _awaitingInitialMathSettingsHydration = false;
                HasPendingConfigChanges = true;
                _ioFlowHasUnsavedControllerChanges = true;
                UpdateIoFlowControllerMismatchWarning();
                if (!IoFlowGraph.IsApplyingMathRulesFromGraph)
                    PersistIoFlowGraphSnapshotLocally();
            }

            NotifyAnalogueChannelUiContextChanged();

            if (sender is SystemParameters)
            {
                OnPropertyChanged(nameof(SelectedSystemDataCanId));
                OnPropertyChanged(nameof(SelectedDigitalInputDataCanId));
                OnPropertyChanged(nameof(SelectedAnalogueInputDataCanId));
                OnPropertyChanged(nameof(SelectedSystemConfigCanId));
                OnPropertyChanged(nameof(SelectedChannelDataCanId));
                OnPropertyChanged(nameof(SelectedConfigDataCanId));
                OnPropertyChanged(nameof(SelectedCanBusBitrate));
                OnPropertyChanged(nameof(CanUseOpenRemoteSettings));
                OnPropertyChanged(nameof(CanProvisionOpenRemote));
                OnPropertyChanged(nameof(OpenRemoteAvailabilityMessage));
            }

            if (e.PropertyName == nameof(AnalogueInput.ChanType) || e.PropertyName == nameof(AnalogueInput.Units))
            {
                OnPropertyChanged(nameof(FilteredAnalogueUnits));
            }
        }

        private void AttachSettingsTracking(DataStructures data)
        {
            data.PropertyChanged += SettingsDataView_PropertyChanged;

            foreach (var channel in data.ChannelsStaticData)
            {
                channel.PropertyChanged += SettingsModelItem_PropertyChanged;
                channel.EnsureMathRuleSlots();
                foreach (var rule in channel.MathRules)
                {
                    rule.PropertyChanged += SettingsModelItem_PropertyChanged;
                }
            }

            foreach (var digitalInput in data.DigitalInputsStaticData)
            {
                digitalInput.PropertyChanged += SettingsModelItem_PropertyChanged;
            }

            foreach (var analogueInput in data.AnalogueInputsStaticData)
            {
                analogueInput.PropertyChanged += SettingsModelItem_PropertyChanged;
            }

            data.SystemParamsStaticData.PropertyChanged += SettingsModelItem_PropertyChanged;
            data.CellularParamsStaticData.PropertyChanged += SettingsModelItem_PropertyChanged;
        }

        private void DetachSettingsTracking(DataStructures data)
        {
            data.PropertyChanged -= SettingsDataView_PropertyChanged;

            foreach (var channel in data.ChannelsStaticData)
            {
                channel.PropertyChanged -= SettingsModelItem_PropertyChanged;
                channel.EnsureMathRuleSlots();
                foreach (var rule in channel.MathRules)
                {
                    rule.PropertyChanged -= SettingsModelItem_PropertyChanged;
                }
            }

            foreach (var digitalInput in data.DigitalInputsStaticData)
            {
                digitalInput.PropertyChanged -= SettingsModelItem_PropertyChanged;
            }

            foreach (var analogueInput in data.AnalogueInputsStaticData)
            {
                analogueInput.PropertyChanged -= SettingsModelItem_PropertyChanged;
            }

            data.SystemParamsStaticData.PropertyChanged -= SettingsModelItem_PropertyChanged;
            data.CellularParamsStaticData.PropertyChanged -= SettingsModelItem_PropertyChanged;
        }

        private void ReplaceSettingsData(DataStructures newData)
        {
            List<CustomCanSignalMetadata> currentCustomCanMetadata = SettingsDataView.CustomCanSignalsMetadata?
                .Select(metadata => metadata.Clone())
                .ToList() ?? [];

            DetachSettingsTracking(SettingsDataView);
            SettingsDataView = newData;

            // Custom CAN names/limits are a local store, not controller config; never let a settings
            // replacement (reconnect, discard, reload) drop entries. Current entries win on conflict.
            List<CustomCanSignalMetadata> incomingCustomCanMetadata = newData.CustomCanSignalsMetadata?.ToList() ?? [];
            newData.CustomCanSignalsMetadata ??= [];
            ReplaceCustomCanSignalMetadata(incomingCustomCanMetadata.Concat(currentCustomCanMetadata));

            AttachSettingsTracking(SettingsDataView);
            RefreshIoFlowGraphFromSettings();
            SyncSelectedTimeZoneFromSettings();
            RebuildConnectorPins();
        }

        private void RebuildConnectorPins()
        {
            foreach (var item in ConnectorPins)
            {
                item.Dispose();
            }

            var items = new ObservableCollection<ConnectorPinDisplayItem>(
                ConnectorPinCatalog.Pins.Select(definition => CreateConnectorPinDisplayItem(definition)));

            ConnectorPins = items;
            ConnectorLegendItems = BuildConnectorLegendItems(items);
        }

        private ObservableCollection<ConnectorLegendItem> BuildConnectorLegendItems(IEnumerable<ConnectorPinDisplayItem> pins)
        {
            var legendItems = new List<ConnectorLegendItem>();
            var outputTypes = pins
                .Where(pin => pin.Role == ConnectorPinRole.Output && pin.OutputChannelNumber.HasValue)
                .Select(pin => pin.OutputChannelNumber is int outputChannelNumber
                    ? SettingsDataView.ChannelsStaticData.ElementAtOrDefault(outputChannelNumber - 1)?.ChanType
                    : null)
                .OfType<OutputChannel.ChannelType>()
                .Distinct()
                .Select(CreateOutputLegendItem);

            legendItems.AddRange(outputTypes);
            legendItems.Add(CreateLegendItem("Digital inputs", ConnectorPinPalette.DigitalInput));
            legendItems.Add(CreateLegendItem("Analogue inputs", ConnectorPinPalette.AnalogueInput));
            legendItems.Add(CreateLegendItem("Ignition", ConnectorPinPalette.Ignition, ConnectorPinPalette.PinTextLight));
            legendItems.Add(CreateLegendItem("CAN", ConnectorPinPalette.CanHigh));
            legendItems.Add(CreateLegendItem("5V ref", ConnectorPinPalette.Reference5V));
            legendItems.Add(CreateLegendItem("Ground", ConnectorPinPalette.Ground));

            return new ObservableCollection<ConnectorLegendItem>(legendItems);
        }

        private static ConnectorLegendItem CreateOutputLegendItem(OutputChannel.ChannelType channelType)
        {
            return CreateLegendItem(
                ConnectorPinDisplayItem.FormatOutputChannelType(channelType),
                ConnectorPinDisplayItem.GetOutputColorHex(channelType),
                channelType == OutputChannel.ChannelType.MATH ? ConnectorPinPalette.PinTextLight : null);
        }

        private static ConnectorLegendItem CreateLegendItem(string text, string backgroundHex, string? foregroundHex = null)
        {
            return new ConnectorLegendItem
            {
                Text = text,
                Background = new SolidColorBrush(Color.Parse(backgroundHex)),
                Foreground = new SolidColorBrush(Color.Parse(foregroundHex ?? ConnectorPinPalette.PinTextDark)),
            };
        }

        private ConnectorPinDisplayItem CreateConnectorPinDisplayItem(ConnectorPinDefinition definition)
        {
            var item = new ConnectorPinDisplayItem(definition);
            item.AttachSources(
                settingsChannel: definition.OutputChannelNumber is int outputNumber && outputNumber - 1 < SettingsDataView.ChannelsStaticData.Count
                    ? SettingsDataView.ChannelsStaticData[outputNumber - 1]
                    : null,
                liveChannel: definition.OutputChannelNumber is int liveOutputNumber && liveOutputNumber - 1 < LiveDataView.ChannelsLiveData.Count
                    ? LiveDataView.ChannelsLiveData[liveOutputNumber - 1]
                    : null,
                settingsDigitalInput: definition.DigitalInputNumber is int digitalNumber && digitalNumber - 1 < SettingsDataView.DigitalInputsStaticData.Count
                    ? SettingsDataView.DigitalInputsStaticData[digitalNumber - 1]
                    : null,
                liveDigitalInput: definition.DigitalInputNumber is int liveDigitalNumber && liveDigitalNumber - 1 < LiveDataView.DigitalInputsLiveData.Count
                    ? LiveDataView.DigitalInputsLiveData[liveDigitalNumber - 1]
                    : null,
                settingsAnalogueInput: definition.AnalogueInputNumber is int analogueNumber && analogueNumber - 1 < SettingsDataView.AnalogueInputsStaticData.Count
                    ? SettingsDataView.AnalogueInputsStaticData[analogueNumber - 1]
                    : null,
                liveAnalogueInput: definition.AnalogueInputNumber is int liveAnalogueNumber && liveAnalogueNumber - 1 < LiveDataView.AnalogueInputsLiveData.Count
                    ? LiveDataView.AnalogueInputsLiveData[liveAnalogueNumber - 1]
                    : null);
            return item;
        }

        private void RecalculatePendingConfigChanges()
        {
            if (_controllerConfigBaseline == null)
            {
                HasPendingConfigChanges = false;
                return;
            }

            string current = ConfigFileSerializer.SerializeSettings(SettingsDataView, includeCustomCanMetadata: false);
            string baseline = ConfigFileSerializer.SerializeSettings(_controllerConfigBaseline, includeCustomCanMetadata: false);
            HasPendingConfigChanges = !string.Equals(current, baseline, StringComparison.Ordinal) || HasPendingCustomCanChanges();
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
                SelectedDigitalInput = SettingsDataView.DigitalInputsStaticData.FirstOrDefault();
            }
        }

        public ISeries[] Series { get; set; }
            = new ISeries[]
            {
                    new LineSeries<double>
                    {
                        Values = new double[] { 2, 1, 3, 5, 3, 4, 6 },
                        Fill = null,
                    },
                    new LineSeries<double>
                    {
                        Values = new double[] { 20, 10, 30, 50, 30, 40, 60 },
                        Fill = null
                    },
            };



        private void OnExit() => _appCloser.CloseApp();

        public void OnWindowClosing()
        {
            Dispose();
        }

        public void Dispose()
        {
            if (_isDisposed)
            {
                return;
            }

            _isDisposed = true;

            SaveLiveChartPreferences();

            _pollTimer.Stop();
            _pollTimer.Dispose();

            _commsTimer.Stop();
            _commsTimer.Dispose();

            _uiUpdateTimer.Stop();
            _uiUpdateTimer.Elapsed -= OnUIUpdateTimerElapsed;
            _uiUpdateTimer.Dispose();

            _logViewportMonitorTimer.Stop();
            _liveHoverClearTimer.Stop();

            _logLoadCts?.Cancel();
            _logLoadCts?.Dispose();
            _logLoadCts = null;

            _applicationUpdateCheckCts?.Cancel();
            _applicationUpdateCheckCts?.Dispose();
            _applicationUpdateCheckCts = null;

            _firmwareCheckCts?.Cancel();
            _firmwareCheckCts?.Dispose();
            _firmwareCheckCts = null;

            if (SelectedChannel != null)
            {
                SelectedChannel.PropertyChanged -= SelectedChannel_PropertyChanged;
            }

            Disconnect(disposePortServiceInBackground: true);

            foreach (var connectorPin in ConnectorPins)
            {
                connectorPin.Dispose();
            }

            _provisioningApiClient.Dispose();
            GC.SuppressFinalize(this);
        }



        [RelayCommand]
        private async Task ShowAbout()
        {
            await _appCloser.ShowAboutAsync();
        }

        [RelayCommand]
        private void OpenDetailedLogFolder()
        {
            try
            {
                Directory.CreateDirectory(LoggingService.DetailedLogDirectoryPath);
                Process.Start(new ProcessStartInfo
                {
                    FileName = LoggingService.DetailedLogDirectoryPath,
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                LoggingService.AddLog($"Failed to open detailed log folder: {ex.Message}");
            }
        }

        [RelayCommand]
        private void EnableChannelLiveSeries()
        {
            SetChannelLiveSeriesEnabled(true);
        }

        [RelayCommand]
        private void DisableChannelLiveSeries()
        {
            SetChannelLiveSeriesEnabled(false);
        }

        [RelayCommand]
        private void EnableAnalogueLiveSeries()
        {
            SetAnalogueLiveSeriesEnabled(true);
        }

        [RelayCommand]
        private void DisableAnalogueLiveSeries()
        {
            SetAnalogueLiveSeriesEnabled(false);
        }

        [RelayCommand]
        private void ToggleLiveChartPause()
        {
            IsLiveChartPaused = !IsLiveChartPaused;
        }

        [RelayCommand]
        private void HandleLiveHoveredPointsChanged(object? parameter)
        {
            if (!TryGetChartPoints(parameter, out List<ChartPoint> points))
            {
                return;
            }

            _liveHoverClearTimer.Stop();

            double hoveredX = points[0].Coordinate.SecondaryValue;
            _liveHoverAnchorTimestampMs = hoveredX;
            _hasLiveHoverAnchor = true;

            UpdateLiveChartSelection(points, pinSelection: false);
        }

        [RelayCommand]
        private void HandleLiveChartPointPointerDown(object? parameter)
        {
            if (!TryGetChartPoints(parameter, out List<ChartPoint> points))
            {
                return;
            }

            UpdateLiveChartSelection(points, pinSelection: true);
        }

        [RelayCommand]
        private void ToggleLiveChartPin()
        {
            if (!IsLiveChartSelectionPinned)
            {
                if (_lastHoveredLiveChartPoints.Count > 0)
                {
                    IsLiveChartSelectionPinned = true;
                    OnPropertyChanged(nameof(LiveChartPinButtonText));
                }

                return;
            }

            IsLiveChartSelectionPinned = false;
            OnPropertyChanged(nameof(LiveChartPinButtonText));

            LiveChartHoverSummary = _lastHoveredLiveChartPoints.Count > 0
                ? BuildLiveChartHoverSummary(_lastHoveredLiveChartPoints)
                : "Hover the chart to inspect live values. Click a point to pin the readout.";
        }

        [RelayCommand]
        private void ClearLiveChartSelection()
        {
            _lastHoveredLiveChartPoints.Clear();
            _hasLiveHoverAnchor = false;
            _liveHoverAnchorTimestampMs = 0;
            IsLiveChartSelectionPinned = false;
            LiveChartHoverSummary = "Hover the chart to inspect live values. Click a point to pin the readout.";
            ClearLiveSeriesToggleValues();
            OnPropertyChanged(nameof(HasLiveChartSelection));
            OnPropertyChanged(nameof(LiveChartPinButtonText));
        }



        [RelayCommand]
        private void AddMathRule()
        {
            if (SelectedChannel == null || !SelectedChannel.IsMathChannel)
            {
                return;
            }

            int nextCount = Math.Min(Constants.NUM_MATH_RULE_SLOTS, SelectedChannel.MathRuleCount + 1);
            if (nextCount == SelectedChannel.MathRuleCount)
            {
                return;
            }

            SetSelectedMathRuleCount(nextCount);
            SelectedMathRuleSlotIndex = nextCount - 1;
            RaiseMathRuleUiPropertiesChanged();
        }

        [RelayCommand]
        private void RemoveMathRule()
        {
            if (SelectedChannel == null || !SelectedChannel.IsMathChannel)
            {
                return;
            }

            int nextCount = Math.Max(1, SelectedChannel.MathRuleCount - 1);
            if (nextCount == SelectedChannel.MathRuleCount)
            {
                return;
            }

            IoFlowGraph.RemoveChannelMathRules(SelectedChannel, new HashSet<int> { nextCount });
            SelectedMathRuleSlotIndex = Math.Min(SelectedMathRuleSlotIndex, SelectedChannel.MathRuleCount - 1);
            RaiseMathRuleUiPropertiesChanged();
        }

        [RelayCommand]
        private async Task SendConfig()
        {
            if (IsSendingConfig)
            {
                return;
            }

            if (!IsConnected || _portService == null)
            {
                return;
            }

            if (!IoFlowGraph.ReconcileMathConfiguration())
            {
                MathValidationErrorMessage = IoFlowGraph.AddNodeWarning;
                AddLog("Cannot save maths canvas.", MathValidationErrorMessage);
                return;
            }
            RecalculatePendingConfigChanges();

            if (!TryValidateMathConfiguration(out string validationError))
            {
                MathValidationErrorMessage = validationError;
                AddLog("Cannot save MATH configuration.", validationError);
                return;
            }

            MathValidationErrorMessage = string.Empty;
            PrepareCellularSettingsForSave();

            bool customCanSaved = true;
            if (HasPendingCustomCanChanges())
            {
                customCanSaved = await PersistPendingCustomCanChangesAsync();
                if (!customCanSaved)
                {
                    return;
                }

                SyncCustomCanBaselineSnapshot();
                RecalculatePendingConfigChanges();
            }

            if (!HasPendingConfigChanges)
            {
                if (customCanSaved)
                {
                    PersistIoFlowGraphSnapshotForController();
                    SystemDateTime = $"Last updated: {DateTime.Now:yyyy/MM/dd HH:mm:ss}";
                    StartBackgroundTask(FlashLastUpdatedAsync(), "Last-updated highlight");
                }

                return;
            }

            IsSendingConfig = true;
            _configurationBeingSaved = DeepCopyDataStructures(SettingsDataView);
            _ioFlowSnapshotBeingSaved = IoFlowGraph.CreateSnapshot();
            _portService.UpdateSettingsData(_configurationBeingSaved);
            _portService.StartSendConfig();
        }

        private bool TryValidateMathConfiguration(out string validationError)
        {
            for (int channelIndex = 0; channelIndex < SettingsDataView.ChannelsStaticData.Count; channelIndex++)
            {
                OutputChannel channel = SettingsDataView.ChannelsStaticData[channelIndex];
                if (!channel.IsMathChannel)
                {
                    continue;
                }

                channel.EnsureMathRuleSlots();

                if (channel.MathRuleCount < 1 || channel.MathRuleCount > Constants.NUM_MATH_RULE_SLOTS)
                {
                    validationError = $"Channel {channelIndex + 1}: Rule count must be between 1 and {Constants.NUM_MATH_RULE_SLOTS}.";
                    return false;
                }

                if (channel.MathRootRule >= channel.MathRuleCount)
                {
                    validationError = $"Channel {channelIndex + 1}: Root rule must be within active rule slots (0 to {channel.MathRuleCount - 1}).";
                    return false;
                }

                if (channel.MathOutputMode > (byte)OutputChannel.MathOutputModeType.PWM)
                {
                    validationError = $"Channel {channelIndex + 1}: Output mode is invalid.";
                    return false;
                }

                int activeRuleCount = channel.MathRuleCount;
                for (int ruleIndex = 0; ruleIndex < activeRuleCount; ruleIndex++)
                {
                    OutputChannel.MathRule rule = channel.MathRules[ruleIndex];
                    if (rule.Operation > (byte)OutputChannel.MathOperation.TOGGLE)
                    {
                        validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Operator is invalid.";
                        return false;
                    }

                    bool unary = rule.Operation == (byte)OutputChannel.MathOperation.NOT ||
                                 rule.Operation == (byte)OutputChannel.MathOperation.ABS ||
                                 rule.Operation == (byte)OutputChannel.MathOperation.TOGGLE;

                    bool supportsHysteresis = rule.Operation >= (byte)OutputChannel.MathOperation.GREATER_THAN &&
                                              rule.Operation <= (byte)OutputChannel.MathOperation.LESS_EQUAL;
                    if (rule.HysteresisEnabled != 0 && !supportsHysteresis)
                    {
                        validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Hysteresis is only supported for comparison operators GREATER/LESS variants.";
                        return false;
                    }

                    if (rule.HysteresisEnabled != 0)
                    {
                        if (!float.IsFinite(rule.OnThreshold) || !float.IsFinite(rule.OffThreshold))
                        {
                            validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Hysteresis thresholds must be finite numbers.";
                            return false;
                        }
                    }

                    if (!TryValidateMathOperand(channelIndex, ruleIndex, "A", rule.OperandAType, rule.OperandAIndex, rule.OperandAConstant, activeRuleCount, out validationError))
                    {
                        return false;
                    }

                    if (!unary && !TryValidateMathOperand(channelIndex, ruleIndex, "B", rule.OperandBType, rule.OperandBIndex, rule.OperandBConstant, activeRuleCount, out validationError))
                    {
                        return false;
                    }
                }

                if (HasMathRuleCycle(channel, activeRuleCount, out int cycleRule))
                {
                    validationError = $"Channel {channelIndex + 1}: Rule graph contains a cycle at rule {cycleRule}. Rule references must be acyclic.";
                    return false;
                }
            }

            validationError = string.Empty;
            return true;
        }

        private bool TryValidateMathOperand(
            int channelIndex,
            int ruleIndex,
            string operandName,
            byte operandType,
            byte operandIndex,
            float operandConstant,
            int activeRuleCount,
            out string validationError)
        {
            if (!Enum.IsDefined(typeof(OutputChannel.MathOperandType), operandType))
            {
                validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Operand {operandName} type is invalid.";
                return false;
            }

            switch ((OutputChannel.MathOperandType)operandType)
            {
                case OutputChannel.MathOperandType.Constant:
                    if (!float.IsFinite(operandConstant))
                    {
                        validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Operand {operandName} constant must be finite.";
                        return false;
                    }
                    break;
                case OutputChannel.MathOperandType.Channel:
                    if (operandIndex >= Constants.NUM_OUTPUT_CHANNELS)
                    {
                        validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Operand {operandName} channel index must be 0..{Constants.NUM_OUTPUT_CHANNELS - 1}.";
                        return false;
                    }
                    break;
                case OutputChannel.MathOperandType.AnalogueInput:
                    if (operandIndex >= Constants.NUM_ANALOGUE_INPUTS)
                    {
                        validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Operand {operandName} analogue-input index must be 0..{Constants.NUM_ANALOGUE_INPUTS - 1}.";
                        return false;
                    }
                    break;
                case OutputChannel.MathOperandType.DigitalInput:
                    if (operandIndex >= Constants.NUM_DIGITAL_INPUTS)
                    {
                        validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Operand {operandName} digital-input index must be 0..{Constants.NUM_DIGITAL_INPUTS - 1}.";
                        return false;
                    }
                    break;
                case OutputChannel.MathOperandType.SystemValue:
                    if (operandIndex > (byte)OutputChannel.MathSystemValue.MagneticFieldZ)
                    {
                        validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Operand {operandName} system-value index must be 0..{(byte)OutputChannel.MathSystemValue.MagneticFieldZ}.";
                        return false;
                    }
                    break;
                case OutputChannel.MathOperandType.CanSignal:
                    if (operandIndex >= Constants.MAX_CUSTOM_CAN_SIGNALS)
                    {
                        validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Operand {operandName} CAN-signal index must be 0..{Constants.MAX_CUSTOM_CAN_SIGNALS - 1}.";
                        return false;
                    }
                    CustomCanSignal? signal = SettingsDataView.CustomCanSignalsStaticData.ElementAtOrDefault(operandIndex);
                    if (signal?.IsEnabled != true || SettingsDataView.CustomCanMessagesStaticData
                        .ElementAtOrDefault(signal.MessageIndex) is not { IsTx: false })
                    {
                        validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Operand {operandName} requires an enabled decoded CAN RX signal.";
                        return false;
                    }
                    break;
                case OutputChannel.MathOperandType.Rule:
                    if (operandIndex >= activeRuleCount)
                    {
                        validationError = $"Channel {channelIndex + 1}, rule {ruleIndex}: Operand {operandName} rule reference must be 0..{activeRuleCount - 1}.";
                        return false;
                    }
                    break;
            }

            validationError = string.Empty;
            return true;
        }

        private static bool HasMathRuleCycle(OutputChannel channel, int activeRuleCount, out int cycleRule)
        {
            var visiting = new bool[activeRuleCount];
            var visited = new bool[activeRuleCount];

            for (int ruleIndex = 0; ruleIndex < activeRuleCount; ruleIndex++)
            {
                if (visited[ruleIndex])
                {
                    continue;
                }

                if (VisitMathRule(channel, activeRuleCount, ruleIndex, visiting, visited, out cycleRule))
                {
                    return true;
                }
            }

            cycleRule = -1;
            return false;
        }

        private static bool VisitMathRule(OutputChannel channel, int activeRuleCount, int ruleIndex, bool[] visiting, bool[] visited, out int cycleRule)
        {
            if (visiting[ruleIndex])
            {
                cycleRule = ruleIndex;
                return true;
            }

            if (visited[ruleIndex])
            {
                cycleRule = -1;
                return false;
            }

            visiting[ruleIndex] = true;
            OutputChannel.MathRule rule = channel.MathRules[ruleIndex];

            if (TryGetRuleDependency(rule.OperandAType, rule.OperandAIndex, activeRuleCount, out int dependencyA) &&
                VisitMathRule(channel, activeRuleCount, dependencyA, visiting, visited, out cycleRule))
            {
                return true;
            }

            bool unary = rule.Operation == (byte)OutputChannel.MathOperation.NOT ||
                         rule.Operation == (byte)OutputChannel.MathOperation.ABS ||
                         rule.Operation == (byte)OutputChannel.MathOperation.TOGGLE;
            if (!unary &&
                TryGetRuleDependency(rule.OperandBType, rule.OperandBIndex, activeRuleCount, out int dependencyB) &&
                VisitMathRule(channel, activeRuleCount, dependencyB, visiting, visited, out cycleRule))
            {
                return true;
            }

            visiting[ruleIndex] = false;
            visited[ruleIndex] = true;
            cycleRule = -1;
            return false;
        }

        private static bool TryGetRuleDependency(byte operandType, byte operandIndex, int activeRuleCount, out int dependency)
        {
            if ((OutputChannel.MathOperandType)operandType == OutputChannel.MathOperandType.Rule && operandIndex < activeRuleCount)
            {
                dependency = operandIndex;
                return true;
            }

            dependency = -1;
            return false;
        }

        private void PrepareCellularSettingsForSave()
        {
            SettingsDataView.CellularParamsStaticData.ConfigVersion = Constants.CELLULAR_CONFIG_VERSION;
            SettingsDataView.CellularParamsStaticData.EnsurePublishTopicFromOpenRemoteFields();
        }

        private Task<bool> SavePendingConfigForCellularTestAsync()
        {
            if (_portService == null)
            {
                return Task.FromResult(true);
            }

            PrepareCellularSettingsForSave();
            return Task.FromResult(true);
        }

        [RelayCommand]
        private async Task TestCellularConnectionAsync()
        {
            bool completed = await RunCellularConnectionTestAsync(isAutomaticRun: false, maxAttempts: 18);
            if (completed || !LooksLikeConnectionTestFailure(CellularTestStatusMessage))
            {
                return;
            }

            SetCellularTestStatus(
                "The telemetry connection is still settling. Retrying automatically...",
                [.. CreatePendingCellularTestItems(includeSaveHint: false)]);
            await Task.Delay(ManualCellularRetryDelayMs);
            await RunCellularConnectionTestAsync(isAutomaticRun: false, maxAttempts: 18);
        }

        private async Task<bool> RunCellularConnectionTestAsync(bool isAutomaticRun, int maxAttempts)
        {
            if (!CanTestCellularConnection || _portService == null)
            {
                AddLog("Cellular connection test unavailable. Check connection and controller comms.");
                return false;
            }

            IsTestingCellularConnection = true;

            try
            {
                if (!await SavePendingConfigForCellularTestAsync())
                {
                    return false;
                }

                SetCellularTestStatus(
                    "Checking connection...",
                    [.. CreatePendingCellularTestItems(includeSaveHint: !isAutomaticRun)]);

                string? lastDiagnostic = null;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    CellularParameters? testSettings = attempt == 0 ? SettingsDataView.CellularParamsStaticData : null;
                    string? diagnostic = await _portService.RequestCellularTestAsync(testSettings, 20000);
                    if (string.IsNullOrWhiteSpace(diagnostic))
                    {
                        SetCellularTestStatus(
                            "The PDM did not respond. Check the connection and try again.",
                            new CellularTestStatusItem("Test", "No response", ""));
                        AddLog(CellularTestStatusMessage);
                        return false;
                    }

                    lastDiagnostic = diagnostic;
                    Debug.WriteLine("Cellular test result:\n" + diagnostic);
                    UpdateCellularTestStatus(diagnostic, includeSaveHint: !isAutomaticRun);
                    if (diagnostic.Contains("Internet: Failed", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("Settings: Blocked", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("Data: Failed", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("Data: Skipped", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("MQTT: Blocked", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("MQTT: Connected", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("MQTT: Recovering", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("MQTT: Failed", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("Telemetry: OK", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("Telemetry: Warning", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("Health: OK", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("Health: Failed", StringComparison.OrdinalIgnoreCase) ||
                        diagnostic.Contains("Health: Warning", StringComparison.OrdinalIgnoreCase))
                    {
                        AddLog(CellularTestStatusMessage);
                        return true;
                    }

                    await Task.Delay(2500);
                }

                List<CellularTestStatusItem> timeoutItems = [.. CellularTestStatusItems];
                timeoutItems.Add(new CellularTestStatusItem("Test", "Timeout", "The PDM did not report a final internet result before Cortex stopped waiting."));
                SetCellularTestStatus(
                    isAutomaticRun
                        ? "Automatic connection test is still waiting for telemetry updates. You can run a manual test when ready."
                        : "The connection test took too long. Try again in a moment.",
                    [.. timeoutItems]);
                Debug.WriteLine("Cellular test timed out before the data connection became ready." +
                       (string.IsNullOrWhiteSpace(lastDiagnostic) ? string.Empty : "\nLast PDM cellular diagnostic:\n" + lastDiagnostic));
                AddLog(CellularTestStatusMessage);
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cellular test failed: {ex}");
                SetCellularTestStatus(
                    "Connection test failed. Check the PDM connection and try again.",
                    new CellularTestStatusItem("Test", "Problem", ""));
                AddLog(CellularTestStatusMessage);
                return false;
            }
            finally
            {
                IsTestingCellularConnection = false;
            }
        }

        private void ScheduleCellularHealthPoll(bool immediate)
        {
            _nextCellularHealthPollUtc = immediate
                ? DateTime.UtcNow
                : DateTime.UtcNow.AddMilliseconds(CellularHealthPollIntervalMs);
        }

        private async Task PollCellularHealthStatusAsync()
        {
            if (_portService == null || !IsConnected || !CommsEstablished)
            {
                SetCellularConnectionHealthStatus("Offline", shouldLog: true);
                return;
            }

            if (!IsConnectedPdmRegistered)
            {
                SetCellularConnectionHealthStatus("Offline", shouldLog: true);
                return;
            }

            if (IsSendingConfig || IsOpenRemoteProvisioningInProgress)
            {
                SetCellularConnectionHealthStatus("Checking", shouldLog: true);
                return;
            }

            if (Interlocked.Exchange(ref _isCellularHealthPollInProgress, 1) == 1)
            {
                return;
            }

            try
            {
                CellularParameters? testSettings = SettingsDataView.CellularParamsStaticData;
                string? diagnostic = await _portService.RequestCellularTestAsync(testSettings, 12000);
                string status = ClassifyCellularConnectionHealthStatus(diagnostic, out string? reason);

                if (status.Equals("Needs attention", StringComparison.OrdinalIgnoreCase) &&
                    DateTime.UtcNow < _suppressCellularNeedsAttentionUntilUtc)
                {
                    status = "Checking";
                    reason = null;
                }

                SetCellularConnectionHealthStatus(status, shouldLog: true, reason: reason);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Cellular health poll failed: {ex}");
                SetCellularConnectionHealthStatus("Needs attention", shouldLog: true, reason: "status check failed");
            }
            finally
            {
                Interlocked.Exchange(ref _isCellularHealthPollInProgress, 0);
            }
        }

        private void SetCellularConnectionHealthStatus(string status, bool shouldLog, string? reason = null)
        {
            bool changed = !string.Equals(CellularConnectionHealthStatus, status, StringComparison.OrdinalIgnoreCase);
            CellularConnectionHealthStatus = status;

            if (!shouldLog || !SettingsDataView.SystemParamsStaticData.AllowData)
            {
                return;
            }

            if (changed || !string.Equals(_lastLoggedCellularHealthStatus, status, StringComparison.OrdinalIgnoreCase))
            {
                _lastLoggedCellularHealthStatus = status;
                if (status.Equals("Needs attention", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(reason))
                {
                    AddLog($"Connectivity status: {status} ({reason}).");
                    return;
                }

                AddLog($"Connectivity status: {status}.");
            }
        }

        private static string ClassifyCellularConnectionHealthStatus(string? diagnostic, out string? reason)
        {
            reason = null;

            if (string.IsNullOrWhiteSpace(diagnostic))
            {
                reason = "no response from modem test";
                return "Offline";
            }

            if (diagnostic.Contains("Telemetry: OK", StringComparison.OrdinalIgnoreCase) &&
                diagnostic.Contains("Health: OK", StringComparison.OrdinalIgnoreCase))
            {
                return "Healthy";
            }

            foreach (string line in diagnostic.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Problem", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Blocked", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Skipped", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("No response", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Needs setup", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Legacy", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Update needed", StringComparison.OrdinalIgnoreCase))
                {
                    reason = line.Trim();
                    return "Needs attention";
                }
            }

            return "Checking";
        }

        private static List<CellularTestStatusItem> CreatePendingCellularTestItems(bool includeSaveHint)
        {
            return
            [
                new CellularTestStatusItem("Settings", "Pending", includeSaveHint ? "Save settings, then run the test." : "Settings synced to the PDM."),
                new CellularTestStatusItem("Mobile data", "Pending", string.Empty),
                new CellularTestStatusItem("Internet", "Pending", string.Empty),
                new CellularTestStatusItem(TelemetryServiceStage, "Pending", string.Empty),
                new CellularTestStatusItem("Health", "Pending", string.Empty)
            ];
        }

        private void ResetCellularTestStatus()
        {
            SetCellularTestStatus(
                DefaultCellularTestStatusMessage,
                [.. CreatePendingCellularTestItems(includeSaveHint: true)]);
        }

        private void SetCellularTestStatus(string summary, params CellularTestStatusItem[] items)
        {
            CellularTestStatusMessage = summary;
            CellularTestStatusItems.Clear();

            foreach (CellularTestStatusItem item in items)
            {
                CellularTestStatusItems.Add(item);
            }

            UpdateCellularTestProgress(items);
        }

        private void UpdateCellularTestProgress(IEnumerable<CellularTestStatusItem> items)
        {
            if (!IsCellularTestInProgress)
            {
                CellularTestProgressValue = 0;
                return;
            }

            int completed = items.Count(item =>
                CellularTestStageOrder.Contains(item.Stage, StringComparer.OrdinalIgnoreCase) &&
                !item.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase) &&
                !item.Status.Equals("Checking", StringComparison.OrdinalIgnoreCase) &&
                !item.Status.Equals("Retrying", StringComparison.OrdinalIgnoreCase));

            CellularTestProgressValue = Math.Clamp(
                (double)completed / CellularTestStageOrder.Length,
                0,
                1);
        }

        private void UpdateCellularTestStatus(string diagnostic, bool includeSaveHint)
        {
            if (diagnostic.Contains(LegacyCellularEnableDataMessage, StringComparison.OrdinalIgnoreCase))
            {
                SetCellularTestStatus(
                    "The PDM is reporting an old cellular settings format.",
                    new CellularTestStatusItem("Firmware", "Update needed", "Flash the latest PDM firmware so Mobile data becomes the only connection enable setting."),
                    new CellularTestStatusItem("Settings", "Legacy", "The controller still reports the removed Enable connection setting."));
                return;
            }

            List<CellularTestStatusItem> items = CreatePendingCellularTestItems(includeSaveHint);
            bool hasParsedLine = false;

            foreach (string line in diagnostic.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
            {
                int colonIndex = line.IndexOf(':');
                if (colonIndex <= 0 || colonIndex >= line.Length - 1)
                {
                    continue;
                }

                hasParsedLine = true;
                string stage = line[..colonIndex].Trim();
                string remainder = line[(colonIndex + 1)..].Trim();
                string status = remainder;
                string message = string.Empty;

                if (stage.Equals("SIM", StringComparison.OrdinalIgnoreCase) ||
                    stage.Equals("Storage", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int separatorIndex = remainder.IndexOf(" - ", StringComparison.Ordinal);
                if (separatorIndex >= 0)
                {
                    status = remainder[..separatorIndex].Trim();
                    message = remainder[(separatorIndex + 3)..].Trim();
                }

                string displayStage = SimplifyCellularTestStage(stage);
                string displayStatus = SimplifyCellularTestStatus(status);
                message = SimplifyCellularTestMessage(displayStage, displayStatus, message);

                int existingIndex = items.FindIndex(item => item.Stage.Equals(displayStage, StringComparison.OrdinalIgnoreCase));
                if (existingIndex >= 0)
                {
                    items[existingIndex] = new CellularTestStatusItem(displayStage, displayStatus, message);
                }
                else
                {
                    items.Add(new CellularTestStatusItem(displayStage, displayStatus, message));
                }
            }

            if (!hasParsedLine)
            {
                SetCellularTestStatus(
                    "The PDM returned an unrecognised cellular test response.",
                    new CellularTestStatusItem("Response", "Raw", diagnostic));
                return;
            }

            ReconcileCellularTestStatusItems(items);
            SetCellularTestStatus(BuildCellularTestSummary(items), [.. items]);
        }

        private static void ReconcileCellularTestStatusItems(List<CellularTestStatusItem> items)
        {
            int openRemoteIndex = items.FindIndex(item => item.Stage.Equals(TelemetryServiceStage, StringComparison.OrdinalIgnoreCase));
            if (openRemoteIndex < 0)
            {
                return;
            }

            CellularTestStatusItem? telemetry = items.FirstOrDefault(item => item.Stage.Equals("Telemetry", StringComparison.OrdinalIgnoreCase));
            CellularTestStatusItem? health = items.FirstOrDefault(item => item.Stage.Equals("Health", StringComparison.OrdinalIgnoreCase));
            bool telemetryIsReady = telemetry?.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) == true;
            bool healthHasProblem = health?.Status.Equals("Problem", StringComparison.OrdinalIgnoreCase) == true;

            string currentOpenRemoteStatus = items[openRemoteIndex].Status;
            bool openRemoteIsInProgress =
                currentOpenRemoteStatus.Equals("Retrying", StringComparison.OrdinalIgnoreCase) ||
                currentOpenRemoteStatus.Equals("Checking", StringComparison.OrdinalIgnoreCase);

            if (openRemoteIsInProgress && telemetryIsReady && !healthHasProblem)
            {
                items[openRemoteIndex] = new CellularTestStatusItem(TelemetryServiceStage, "Ready", string.Empty);
            }
        }

        private static string SimplifyCellularTestMessage(string stage, string status, string message)
        {
            if (status.Equals("Ready", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (stage.Equals(TelemetryServiceStage, StringComparison.OrdinalIgnoreCase) && status.Equals("Retrying", StringComparison.OrdinalIgnoreCase))
            {
                return "Trying again automatically.";
            }

            if (stage.Equals("Settings", StringComparison.OrdinalIgnoreCase) && status.Equals("Needs setup", StringComparison.OrdinalIgnoreCase))
            {
                return "Enable Mobile data and check APN settings.";
            }

            if (stage.Equals("Internet", StringComparison.OrdinalIgnoreCase) && status.Equals("Problem", StringComparison.OrdinalIgnoreCase))
            {
                return "Check signal, SIM data, or APN settings.";
            }

            if (stage.Equals(TelemetryServiceStage, StringComparison.OrdinalIgnoreCase) && status.Equals("Problem", StringComparison.OrdinalIgnoreCase))
            {
                return "Check the telemetry service settings.";
            }

            if (stage.Equals("Telemetry", StringComparison.OrdinalIgnoreCase) && status.Equals("Problem", StringComparison.OrdinalIgnoreCase))
            {
                return "Telemetry has not updated yet.";
            }

            return message;
        }

        private static string SimplifyCellularTestStage(string stage)
        {
            if (stage.Equals("Data", StringComparison.OrdinalIgnoreCase))
            {
                return "Mobile data";
            }

            if (stage.Equals("MQTT", StringComparison.OrdinalIgnoreCase))
            {
                return TelemetryServiceStage;
            }

            return stage;
        }

        private static string SimplifyCellularTestStatus(string status)
        {
            if (status.Equals("OK", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Connected", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Fix", StringComparison.OrdinalIgnoreCase))
            {
                return "Ready";
            }

            if (status.Equals("Recovering", StringComparison.OrdinalIgnoreCase))
            {
                return "Retrying";
            }

            if (status.Equals("Blocked", StringComparison.OrdinalIgnoreCase))
            {
                return "Needs setup";
            }

            if (status.Equals("Failed", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Skipped", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Warning", StringComparison.OrdinalIgnoreCase))
            {
                return "Problem";
            }

            if (status.Equals("Timeout", StringComparison.OrdinalIgnoreCase))
            {
                return "Timed out";
            }

            if (status.Equals("Sent", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Testing", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Connecting", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Queued", StringComparison.OrdinalIgnoreCase) ||
                status.Equals("Waiting", StringComparison.OrdinalIgnoreCase))
            {
                return "Checking";
            }

            return status;
        }

        private static string BuildCellularTestSummary(IReadOnlyList<CellularTestStatusItem> items)
        {
            CellularTestStatusItem? settings = items.FirstOrDefault(item => item.Stage.Equals("Settings", StringComparison.OrdinalIgnoreCase));
            CellularTestStatusItem? internet = items.FirstOrDefault(item => item.Stage.Equals("Internet", StringComparison.OrdinalIgnoreCase));
            CellularTestStatusItem? mqtt = items.FirstOrDefault(item => item.Stage.Equals(TelemetryServiceStage, StringComparison.OrdinalIgnoreCase));
            CellularTestStatusItem? health = items.FirstOrDefault(item => item.Stage.Equals("Health", StringComparison.OrdinalIgnoreCase));

            if (settings?.Status.Equals("Needs setup", StringComparison.OrdinalIgnoreCase) == true)
            {
                return string.IsNullOrWhiteSpace(settings.Message)
                    ? "Settings need attention before the connection can start."
                    : settings.Message;
            }

            if (mqtt?.Status.Equals("Needs setup", StringComparison.OrdinalIgnoreCase) == true)
            {
                return string.IsNullOrWhiteSpace(mqtt.Message)
                    ? "Telemetry service settings need attention before publishing can start."
                    : mqtt.Message;
            }

            if (mqtt?.Status.Equals("Problem", StringComparison.OrdinalIgnoreCase) == true)
            {
                return string.IsNullOrWhiteSpace(mqtt.Message)
                    ? "The telemetry service needs attention."
                    : mqtt.Message;
            }

            if (health?.Status.Equals("Problem", StringComparison.OrdinalIgnoreCase) == true)
            {
                return string.IsNullOrWhiteSpace(health.Message)
                    ? "Remote telemetry needs attention."
                    : health.Message;
            }

            if (mqtt?.Status.Equals("Retrying", StringComparison.OrdinalIgnoreCase) == true)
            {
                return string.IsNullOrWhiteSpace(mqtt.Message)
                    ? "The telemetry service is trying again."
                    : mqtt.Message;
            }

            if (mqtt?.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) == true)
            {
                bool healthReady = health?.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) == true;
                return healthReady
                    ? "Connection test passed. Telemetry is live."
                    : (string.IsNullOrWhiteSpace(mqtt.Message) ? "The telemetry service is ready." : mqtt.Message);
            }

            if (internet?.Status.Equals("Ready", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "Internet is ready. Checking the telemetry service...";
            }

            if (internet?.Status.Equals("Problem", StringComparison.OrdinalIgnoreCase) == true)
            {
                return "Internet access needs attention.";
            }

            if (items.Any(item => item.Status.Equals("Checking", StringComparison.OrdinalIgnoreCase) ||
                                  item.Status.Equals("Retrying", StringComparison.OrdinalIgnoreCase)))
            {
                return "Checking connection...";
            }

            if (items.Any(item => item.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase)))
            {
                return "Waiting for test updates from the PDM...";
            }

            return "Review the connection test details below.";
        }

        [RelayCommand]
        private async Task SetControllerDateTimeAsync()
        {
            if (!CanSetControllerRtc || _portService == null)
            {
                AddLog("Controller clock update unavailable. Check connection and controller comms.");
                return;
            }

            if (!TryComposeControllerDateTime(ControllerRtcDate, ControllerRtcTime, SelectedTimeZoneDisplay?.TimeZone, out DateTimeOffset controllerDateTime, out string? dateTimeError))
            {
                AddLog(dateTimeError ?? "Select a controller date before setting the clock.");
                return;
            }

            if (controllerDateTime.Year < 2000 || controllerDateTime.Year > 2099)
            {
                AddLog("Controller clock year must be between 2000 and 2099.");
                return;
            }

            if (!TryBuildTimeZoneRuleBlob(SelectedTimeZoneDisplay, controllerDateTime, out byte[] timeZoneRule, out string? timeZoneError))
            {
                AddLog(timeZoneError ?? "Selected time zone is not supported for controller DST automation.");
                return;
            }

            IsSettingControllerRtc = true;
            try
            {
                AddLog($"Applying controller time zone {SelectedTimeZoneDisplay?.Label ?? "Local"}...");
                bool timeZoneSuccess = await _portService.SetControllerTimeZoneRuleAsync(timeZoneRule);
                if (!timeZoneSuccess)
                {
                    AddLog("Controller rejected the time zone update or did not acknowledge it.");
                    return;
                }

                AddLog($"Setting controller clock to {controllerDateTime:yyyy/MM/dd HH:mm:ss}...");
                bool success = await _portService.SetControllerRtcAsync(controllerDateTime);
                if (success)
                {
                    AddLog($"Controller clock set to {controllerDateTime:yyyy/MM/dd HH:mm:ss} ({SelectedTimeZoneDisplay?.Label ?? "Local"}).");
                }
                else
                {
                    AddLog("Controller rejected the clock update or did not acknowledge it.");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Failed to set controller clock: {ex.Message}");
            }
            finally
            {
                IsSettingControllerRtc = false;
            }
        }

        [RelayCommand]
        private async Task FactoryResetAsync()
        {
            if (!CanFactoryReset || _portService == null)
            {
                AddLog("Factory reset unavailable. Check connection and controller comms.");
                return;
            }

            bool confirmed = await _appCloser.ConfirmAsync(
                "Factory Reset",
                "Proceed with factory reset? This will erase controller EEPROM settings and restore defaults.",
                "PROCEED",
                "CANCEL");

            if (!confirmed)
            {
                return;
            }

            try
            {
                AddLog("Factory reset requested. Restoring controller defaults...");
                IsFactoryResetInProgress = true;
                FactoryResetStatusMessage = "Resetting controller and refreshing settings...";
                bool success = await _portService.FactoryResetAsync();
                if (!success)
                {
                    IsFactoryResetInProgress = false;
                    FactoryResetStatusMessage = string.Empty;
                    AddLog("Factory reset was rejected by the controller or timed out.");
                    return;
                }

                refreshStaticData = true;
                _portService.RequestStaticSnapshot();
                AddLog("Factory reset complete. Controller defaults restored.");
            }
            catch (Exception ex)
            {
                IsFactoryResetInProgress = false;
                FactoryResetStatusMessage = string.Empty;
                AddLog($"Factory reset failed: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task ConfigurePinLockAsync()
        {
            if (!CanConfigurePinLock || _portService == null)
            {
                AddLog("PIN lock setup unavailable. Check connection and controller comms.");
                return;
            }

            IsPinLockBusy = true;
            try
            {
                bool secureSessionReady = await _portService.EnsureSecureSessionAsync();
                if (!secureSessionReady && _portService.HasPendingControllerIdentityMismatch)
                {
                    bool rePairConfirmed = await _appCloser.ConfirmAsync(
                        "Controller Identity Changed",
                        $"Controller identity has changed for {_portService.ControllerPairingPortName}. If this is your controller, replace its saved pairing and retry secure session setup?",
                        "RE-PAIR",
                        "CANCEL");

                    if (rePairConfirmed && _portService.ReplacePendingControllerPairing())
                    {
                        secureSessionReady = await _portService.EnsureSecureSessionAsync();
                    }
                }

                if (!secureSessionReady)
                {
                    string secureError = _portService.LastSecureSessionError ?? "Secure session could not be established.";
                    PinLockStatusMessage = secureError;
                    AddLog($"PIN lock setup failed: {secureError}");
                    return;
                }

                PinSetupDialogResult? dialogResult = await _appCloser.ShowPinSetupWizardAsync(IsPinLockEnabled);
                if (dialogResult == null || !dialogResult.Confirmed)
                {
                    return;
                }

                PinSetupResult result = await _portService.SecureSetPinAsync(dialogResult.Pin);
                if (!result.Success || string.IsNullOrWhiteSpace(result.RecoveryKey))
                {
                    PinLockRetryAfterMs = result.RetryAfterMs;
                    PinLockStatusMessage = result.ErrorMessage ?? "PIN setup failed.";
                    AddLog($"PIN setup failed: {PinLockStatusMessage}");
                    return;
                }

                bool acknowledged = await _appCloser.ShowRecoveryCodeAsync(result.RecoveryKey);
                if (!acknowledged)
                {
                    PinLockStatusMessage = "PIN was set but recovery key confirmation was dismissed.";
                    AddLog("PIN set successfully, but recovery key confirmation was not completed.");
                }
                else
                {
                    PinLockStatusMessage = "PIN lock enabled. Keep your recovery key in a safe place.";
                    AddLog("PIN lock enabled.");
                }

                await RefreshPinLockStateAsync();
            }
            catch (Exception ex)
            {
                PinLockStatusMessage = "PIN setup failed.";
                AddLog($"PIN setup failed: {ex.Message}");
            }
            finally
            {
                IsPinLockBusy = false;
            }
        }

        [RelayCommand]
        private async Task DisablePinLockAsync()
        {
            if (!CanDisablePinLock || _portService == null)
            {
                return;
            }

            bool confirmed = await _appCloser.ConfirmAsync(
                "Disable PIN Lock",
                "Disable PIN lock for this PDM? This requires an active authorized secure session.",
                "DISABLE",
                "CANCEL");

            if (!confirmed)
            {
                return;
            }

            IsPinLockBusy = true;
            try
            {
                SecureManagementResponse? response = await _portService.SecureDisablePinAsync();
                if (response == null || response.Result != SecureControlResult.Ok)
                {
                    PinLockStatusMessage = response == null
                        ? "Disable PIN failed."
                        : $"Disable PIN returned {response.Result}.";
                    PinLockRetryAfterMs = response?.RetryAfterMs ?? 0;
                    AddLog(PinLockStatusMessage);
                    return;
                }

                PinLockStatusMessage = "PIN lock disabled.";
                PinLockRetryAfterMs = 0;
                IsPinLockEnabled = false;
                AddLog("PIN lock disabled.");
                await RefreshPinLockStateAsync();
            }
            catch (Exception ex)
            {
                PinLockStatusMessage = "Disable PIN failed.";
                AddLog($"Disable PIN failed: {ex.Message}");
            }
            finally
            {
                IsPinLockBusy = false;
            }
        }

        private async Task RefreshPinLockStateAsync()
        {
            if (_portService == null || !IsConnected)
            {
                IsPinLockSupported = false;
                IsPinLockEnabled = false;
                PinLockRetryAfterMs = 0;
                PinLockStatusMessage = "PIN lock unavailable.";
                return;
            }

            if (_portService.IsSecureSessionOpen && _portService.IsSecureProtocolEnabled)
            {
                SecureManagementResponse? status = await _portService.GetSecureStatusAsync();
                if (status != null)
                {
                    IsPinLockSupported = true;
                    IsPinLockEnabled = status.PinEnabled;
                    PinLockRetryAfterMs = status.RetryAfterMs;
                    PinLockStatusMessage = status.StorageFault
                        ? "PIN lock storage fault detected on controller."
                        : status.PinEnabled
                            ? "PIN lock is enabled on this PDM."
                            : "PIN lock is not enabled on this PDM.";
                    return;
                }
            }

            SecureInfoResponse? info = await _portService.GetSecureInfoAsync();
            if (info == null || info.ProtocolVersion != Constants.SECURE_PROTOCOL_VERSION)
            {
                IsPinLockSupported = false;
                IsPinLockEnabled = false;
                PinLockRetryAfterMs = 0;
                PinLockStatusMessage = "Controller does not report secure PIN protocol support.";
                return;
            }

            IsPinLockSupported = true;
            IsPinLockEnabled = info.PinEnabled;
            PinLockRetryAfterMs = info.RetryAfterMs;
            PinLockStatusMessage = info.StorageFault
                ? "PIN lock storage fault detected on controller."
                : info.PinEnabled
                    ? "PIN lock is enabled on this PDM."
                    : "PIN lock is not enabled on this PDM.";
        }

        private void RefreshInternetAvailability()
        {
            IsInternetAvailable = NetworkInterface.GetIsNetworkAvailable();
        }

        [RelayCommand]
        private async Task SaveConfigFile()
        {
            try
            {
                PrepareCellularSettingsForSave();
                string content = ConfigFileSerializer.SerializeSettings(SettingsDataView, CreateIoFlowConfigSnapshot());
                bool saved = await _appCloser.SavePdmFileContentAsync(content);

                if (saved)
                {
                    AddLog("Configuration saved to .pdm file.");
                }
            }
            catch (Exception ex)
            {
                AddLog($"Failed to save configuration file: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task OpenConfigFile()
        {
            try
            {
                string? content = await _appCloser.OpenPdmFileContentAsync();
                if (string.IsNullOrWhiteSpace(content))
                {
                    return;
                }

                if (!ConfigFileSerializer.TryDeserialize(content, out var snapshot, out var error) || snapshot == null)
                {
                    AddLog(error ?? "Failed to load configuration file.");
                    return;
                }

                ConfigFileSerializer.ApplySnapshot(SettingsDataView, snapshot);
                ForceRefreshSettingsBindings();
                _ioFlowDraftAwaitingReconciliation = null;
                _ioFlowSnapshotMismatchWarning = string.Empty;

                IoFlowGraphSnapshot? ioFlowSnapshot = snapshot.IoFlowGraph == null
                    ? null
                    : ConvertIoFlowConfigSnapshot(snapshot.IoFlowGraph);
                ApplyIoFlowSnapshotFromConfig(ioFlowSnapshot);
                PersistIoFlowGraphSnapshotLocally();

                AddLog("Configuration loaded from .pdm file. Save to controller to apply permanently.");
            }
            catch (Exception ex)
            {
                AddLog($"Failed to open configuration file: {ex.Message}");
            }
        }

        private void ForceRefreshSettingsBindings()
        {
            int channelIndex = SelectedChannelIndex;
            int digitalIndex = (SelectedDigitalInput?.InputNumber ?? 1) - 1;
            int analogueIndex = (SelectedAnalogueInput?.InputNumber ?? 1) - 1;

            ReplaceSettingsData(DeepCopyDataStructures(SettingsDataView));
            _portService?.UpdateSettingsData(SettingsDataView);

            if (SettingsDataView.DigitalInputsStaticData.Count > 0)
            {
                int clampedDigitalIndex = Math.Clamp(digitalIndex, 0, SettingsDataView.DigitalInputsStaticData.Count - 1);
                SelectedDigitalInput = SettingsDataView.DigitalInputsStaticData.ElementAtOrDefault(clampedDigitalIndex)
                    ?? SettingsDataView.DigitalInputsStaticData.FirstOrDefault();
            }
            else
            {
                SelectedDigitalInput = null;
            }

            if (SettingsDataView.AnalogueInputsStaticData.Count > 0)
            {
                int clampedAnalogueIndex = Math.Clamp(analogueIndex, 0, SettingsDataView.AnalogueInputsStaticData.Count - 1);
                SelectedAnalogueInput = SettingsDataView.AnalogueInputsStaticData.ElementAtOrDefault(clampedAnalogueIndex)
                    ?? SettingsDataView.AnalogueInputsStaticData.FirstOrDefault();
            }
            else
            {
                SelectedAnalogueInput = new AnalogueInput(1, false, false, null);
            }

            if (SettingsDataView.ChannelsStaticData.Count > 0)
            {
                SelectedChannelIndex = Math.Clamp(channelIndex, 0, SettingsDataView.ChannelsStaticData.Count - 1);
                OnSelectedChannelIndexChanged(SelectedChannelIndex, SelectedChannelIndex);
            }
            else
            {
                SelectedChannel = null;
            }

            OnPropertyChanged(nameof(SettingsDataView));
            OnPropertyChanged(nameof(SelectedChannel));
            RecalculatePendingConfigChanges();
        }

        private void RestoreSettingsFromBaseline(DataStructures snapshot)
        {
            int channelIndex = SelectedChannelIndex;
            int digitalIndex = (SelectedDigitalInput?.InputNumber ?? 1) - 1;
            int analogueIndex = (SelectedAnalogueInput?.InputNumber ?? 1) - 1;

            _suppressDirtyTracking = true;
            try
            {
                ReplaceSettingsData(DeepCopyDataStructures(snapshot));
                _portService?.UpdateSettingsData(SettingsDataView);

                if (SettingsDataView.DigitalInputsStaticData.Count > 0)
                {
                    int clampedDigitalIndex = Math.Clamp(digitalIndex, 0, SettingsDataView.DigitalInputsStaticData.Count - 1);
                    SelectedDigitalInput = SettingsDataView.DigitalInputsStaticData.ElementAtOrDefault(clampedDigitalIndex)
                        ?? SettingsDataView.DigitalInputsStaticData.FirstOrDefault();
                }
                else
                {
                    SelectedDigitalInput = null;
                }

                if (SettingsDataView.AnalogueInputsStaticData.Count > 0)
                {
                    int clampedAnalogueIndex = Math.Clamp(analogueIndex, 0, SettingsDataView.AnalogueInputsStaticData.Count - 1);
                    SelectedAnalogueInput = SettingsDataView.AnalogueInputsStaticData.ElementAtOrDefault(clampedAnalogueIndex)
                        ?? SettingsDataView.AnalogueInputsStaticData.FirstOrDefault();
                }
                else
                {
                    SelectedAnalogueInput = new AnalogueInput(1, false, false, null);
                }

                if (SettingsDataView.ChannelsStaticData.Count > 0)
                {
                    SelectedChannelIndex = Math.Clamp(channelIndex, 0, SettingsDataView.ChannelsStaticData.Count - 1);
                    OnSelectedChannelIndexChanged(SelectedChannelIndex, SelectedChannelIndex);
                }
                else
                {
                    SelectedChannel = null;
                }

                HasPendingConfigChanges = false;
                OnPropertyChanged(nameof(SettingsDataView));
                OnPropertyChanged(nameof(SelectedChannel));
            }
            finally
            {
                _suppressDirtyTracking = false;
            }
        }

        [RelayCommand]
        public void RevertChanges()
        {
            _ioFlowDraftAwaitingReconciliation = null;
            if (_controllerConfigBaseline != null)
            {
                var nodeLocations = IoFlowGraph.CreateSnapshot();
                RestoreSettingsFromBaseline(_controllerConfigBaseline);
                _ioFlowDraftAwaitingReconciliation = null;
                RevertIoFlowGraphToBaseline(nodeLocations);
                AddLog("Pending changes reverted.");
                return;
            }

            if (!IsConnected || _portService == null)
            {
                AddLog("Connect to PDM to restore controller parameters.");
                return;
            }

            refreshStaticData = true;
            _portService.RequestStaticSnapshot();
            _pendingRevertLog = true;
            AddLog("Restoring parameters from controller...");
        }



        private void OnUIUpdateTimerElapsed(object? sender, ElapsedEventArgs e)
        {
            if (_pauseLiveUiUpdates)
            {
                return;
            }

            DataStructures? dataToProcess = null;
            bool hasPendingData;

            lock (_pendingDataLock)
            {
                hasPendingData = _hasPendingData;
                if (hasPendingData)
                {
                    dataToProcess = _pendingLiveData;
                    _hasPendingData = false;
                }
            }

            if (!hasPendingData && (!_hasReceivedLiveData || !IsConnected || !CommsEstablished))
            {
                return;
            }

            // Now marshal to UI thread ONCE per timer tick
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                if (hasPendingData && dataToProcess != null)
                {
                    UpdateUIWithData(dataToProcess);
                    return;
                }

                if (IsLiveChartPaused)
                {
                    return;
                }

                UpdateCharts(LiveDataView, appendNewSamples: false);
            });
        }

        private void UpdateUIWithData(DataStructures data)
        {
            // Update live data in place
            UpdateLiveDataInPlace(data);
            UpdateMathStatusPollingSelection();
            HydrateMathSettingsFromLiveDataIfClean();

            CaptureLiveChartHistory(LiveDataView);

            // Update charts
            if (!IsLiveChartPaused)
            {
                UpdateCharts(LiveDataView, appendNewSamples: true);  // Use existing LiveDataView
            }

            // Update error flags
            UpdateErrorFlags();
            RaiseMathRuleUiPropertiesChanged(includeRuleSelectorOptions: false, includeOperandASelectorOptions: false, includeOperandBSelectorOptions: false);

            // Handle static data refresh
            bool staticSnapshotReady = _portService != null && _portService.HasLoadedStaticSnapshot && !_portService.UpdateStaticData;
            if (refreshStaticData && staticSnapshotReady)
            {
                if (HasPendingConfigChanges)
                {
                    RememberIoFlowDraft(IoFlowGraph.CreateSnapshot());
                }
                DataStructures confirmedControllerSettings = DeepCopyDataStructures(data);
                refreshStaticData = false;
                _suppressDirtyTracking = true;
                try
                {
                    string? preservedTimeZoneId = SettingsDataView.SystemParamsStaticData.TimeZoneId;
                    byte[] preservedTimeZoneRule = SettingsDataView.SystemParamsStaticData.TimeZoneRule?.ToArray() ?? Array.Empty<byte>();
                    CellularParameters preservedCellular = DeepCopyDataStructures(SettingsDataView).CellularParamsStaticData;
                    List<CustomCanSignalMetadata> preservedCustomCanMetadata = SettingsDataView.CustomCanSignalsMetadata
                        .Select(metadata => metadata.Clone())
                        .ToList();

                    ReplaceSettingsData(DeepCopyDataStructures(data));
                    SettingsDataView.CellularParamsStaticData.PreserveOpenRemoteFieldsFrom(preservedCellular);
                    if (!HasTimeZoneRuleBlob(SettingsDataView.SystemParamsStaticData.TimeZoneRule)
                        && string.IsNullOrWhiteSpace(SettingsDataView.SystemParamsStaticData.TimeZoneId)
                        && !string.IsNullOrWhiteSpace(preservedTimeZoneId))
                    {
                        SettingsDataView.SystemParamsStaticData.TimeZoneId = preservedTimeZoneId;
                    }

                    if (!HasTimeZoneRuleBlob(SettingsDataView.SystemParamsStaticData.TimeZoneRule)
                        && HasTimeZoneRuleBlob(preservedTimeZoneRule))
                    {
                        SettingsDataView.SystemParamsStaticData.TimeZoneRule = preservedTimeZoneRule;
                    }

                    if (preservedCustomCanMetadata.Count > 0)
                    {
                        SettingsDataView.CustomCanSignalsMetadata.Clear();
                        foreach (CustomCanSignalMetadata metadata in preservedCustomCanMetadata)
                        {
                            SettingsDataView.CustomCanSignalsMetadata.Add(metadata);
                        }

                        RefreshCustomCanBindings();
                    }

                    SyncSelectedTimeZoneFromSettings();
                    _controllerConfigBaseline = DeepCopyDataStructures(SettingsDataView);
                    _controllerConfigBaseline.ChannelsStaticData = confirmedControllerSettings.ChannelsStaticData;
                    CaptureIoFlowBaselineSnapshot();
                    RecalculatePendingConfigChanges();
                    _awaitingInitialMathSettingsHydration = _portService?.HasLoadedMathSnapshot != true;
                    _portService?.UpdateSettingsData(SettingsDataView);
                    // If the CAN messages tab is already open, kick polling again now
                    // that static settings hydration is complete.
                    UpdateCustomCanStatusPollingSelection();
                    OnSelectedChannelIndexChanged(SelectedChannelIndex, SelectedChannelIndex);
                    SelectedAnalogueInput = SettingsDataView.AnalogueInputsStaticData.FirstOrDefault();
                    SelectedDigitalInput = SettingsDataView.DigitalInputsStaticData.FirstOrDefault();
                    RefreshInternetAvailability();
                }
                finally
                {
                    _suppressDirtyTracking = false;
                }

                IsInitialSnapshotLoaded = true;

                if (IsFactoryResetInProgress)
                {
                    IsFactoryResetInProgress = false;
                    FactoryResetStatusMessage = string.Empty;
                }

                if (_pendingRevertLog)
                {
                    _pendingRevertLog = false;
                    AddLog("PDM values restored to UI");
                }

                StartBackgroundTask(RefreshFirmwareUpdateStateAsync(), "Firmware update state refresh");
            }
            if (IsInitialSnapshotLoaded && (_portService == null
                || (_portService.HasLoadedCustomCanSnapshot && _portService.HasLoadedMathSnapshot)))
                StartBackgroundTask(ReconcileIoFlowDraftAsync(), "I/O Flow settings reconciliation");
        }

        private void HydrateMathSettingsFromLiveDataIfClean()
        {
            if (!_awaitingInitialMathSettingsHydration || HasPendingConfigChanges)
            {
                return;
            }

            if (_portService != null ? !_portService.HasLoadedMathSnapshot : !LiveMathSnapshotLooksReady())
            {
                return;
            }

            int channelCount = Math.Min(LiveDataView.ChannelsLiveData.Count, SettingsDataView.ChannelsStaticData.Count);
            bool anyUpdated = false;

            _suppressDirtyTracking = true;
            try
            {
                for (int channelIndex = 0; channelIndex < channelCount; channelIndex++)
                {
                    OutputChannel liveChannel = LiveDataView.ChannelsLiveData[channelIndex];
                    OutputChannel staticChannel = SettingsDataView.ChannelsStaticData[channelIndex];

                    if (!liveChannel.IsMathChannel)
                    {
                        continue;
                    }

                    liveChannel.EnsureMathRuleSlots();
                    staticChannel.EnsureMathRuleSlots();

                    if (staticChannel.MathRuleCount != liveChannel.MathRuleCount)
                    {
                        staticChannel.MathRuleCount = liveChannel.MathRuleCount;
                        anyUpdated = true;
                    }

                    if (staticChannel.MathRootRule != liveChannel.MathRootRule)
                    {
                        staticChannel.MathRootRule = liveChannel.MathRootRule;
                        anyUpdated = true;
                    }

                    if (staticChannel.MathOutputMode != liveChannel.MathOutputMode)
                    {
                        staticChannel.MathOutputMode = liveChannel.MathOutputMode;
                        anyUpdated = true;
                    }

                    for (int ruleIndex = 0; ruleIndex < Constants.NUM_MATH_RULE_SLOTS; ruleIndex++)
                    {
                        OutputChannel.MathRule liveRule = liveChannel.MathRules[ruleIndex];
                        OutputChannel.MathRule staticRule = staticChannel.MathRules[ruleIndex];

                        if (staticRule.Operation != liveRule.Operation)
                        {
                            staticRule.Operation = liveRule.Operation;
                            anyUpdated = true;
                        }

                        if (staticRule.HysteresisEnabled != liveRule.HysteresisEnabled)
                        {
                            staticRule.HysteresisEnabled = liveRule.HysteresisEnabled;
                            anyUpdated = true;
                        }

                        if (staticRule.OnThreshold != liveRule.OnThreshold)
                        {
                            staticRule.OnThreshold = liveRule.OnThreshold;
                            anyUpdated = true;
                        }

                        if (staticRule.OffThreshold != liveRule.OffThreshold)
                        {
                            staticRule.OffThreshold = liveRule.OffThreshold;
                            anyUpdated = true;
                        }

                        if (staticRule.OperandAType != liveRule.OperandAType)
                        {
                            staticRule.OperandAType = liveRule.OperandAType;
                            anyUpdated = true;
                        }

                        if (staticRule.OperandAIndex != liveRule.OperandAIndex)
                        {
                            staticRule.OperandAIndex = liveRule.OperandAIndex;
                            anyUpdated = true;
                        }

                        if (staticRule.OperandAConstant != liveRule.OperandAConstant)
                        {
                            staticRule.OperandAConstant = liveRule.OperandAConstant;
                            anyUpdated = true;
                        }

                        if (staticRule.OperandBType != liveRule.OperandBType)
                        {
                            staticRule.OperandBType = liveRule.OperandBType;
                            anyUpdated = true;
                        }

                        if (staticRule.OperandBIndex != liveRule.OperandBIndex)
                        {
                            staticRule.OperandBIndex = liveRule.OperandBIndex;
                            anyUpdated = true;
                        }

                        if (staticRule.OperandBConstant != liveRule.OperandBConstant)
                        {
                            staticRule.OperandBConstant = liveRule.OperandBConstant;
                            anyUpdated = true;
                        }
                    }
                }
            }
            finally
            {
                _suppressDirtyTracking = false;
            }

            _awaitingInitialMathSettingsHydration = false;

            if (!anyUpdated)
            {
                return;
            }

            _portService?.UpdateSettingsData(SettingsDataView);
            _controllerConfigBaseline = DeepCopyDataStructures(SettingsDataView);
            CaptureIoFlowBaselineSnapshot();
            HasPendingConfigChanges = false;
            RaiseMathRuleUiPropertiesChanged();
        }

        private bool LiveMathSnapshotLooksReady()
        {
            foreach (OutputChannel channel in LiveDataView.ChannelsLiveData)
            {
                if (!channel.IsMathChannel)
                {
                    continue;
                }

                channel.EnsureMathRuleSlots();

                if (channel.MathRuleCount != 1 || channel.MathRootRule != 0 || channel.MathOutputMode != 0)
                {
                    return true;
                }

                for (int ruleIndex = 0; ruleIndex < Constants.NUM_MATH_RULE_SLOTS; ruleIndex++)
                {
                    OutputChannel.MathRule rule = channel.MathRules[ruleIndex];
                    if (rule.Operation != 0 ||
                        rule.HysteresisEnabled != 0 ||
                        rule.OnThreshold != 0f ||
                        rule.OffThreshold != 0f ||
                        rule.OperandAType != 0 ||
                        rule.OperandAIndex != 0 ||
                        rule.OperandAConstant != 0f ||
                        rule.OperandBType != 0 ||
                        rule.OperandBIndex != 0 ||
                        rule.OperandBConstant != 0f)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private void UpdateLiveDataInPlace(DataStructures newData)
        {
            if (newData == null)
            {
                return;
            }

            // Update channel data without replacing collections
            int channelCount = Math.Min(newData.ChannelsLiveData.Count, LiveDataView.ChannelsLiveData.Count);
            for (int i = 0; i < channelCount; i++)
            {
                var target = LiveDataView.ChannelsLiveData[i];
                var source = newData.ChannelsLiveData[i];

                // Update all OutputChannel properties
                if (target.ChanType != source.ChanType)
                {
                    target.ChanType = source.ChanType;
                }

                if (target.Override != source.Override)
                {
                    target.Override = source.Override;
                }

                if (target.CurrentSensePin != source.CurrentSensePin)
                {
                    target.CurrentSensePin = source.CurrentSensePin;
                }

                if (target.CurrentThresholdHigh != source.CurrentThresholdHigh)
                {
                    target.CurrentThresholdHigh = source.CurrentThresholdHigh;
                }

                if (target.CurrentThresholdLow != source.CurrentThresholdLow)
                {
                    target.CurrentThresholdLow = source.CurrentThresholdLow;
                }

                if (target.CurrentValue != source.CurrentValue)
                {
                    target.CurrentValue = source.CurrentValue;
                }

                if (target.Enabled != source.Enabled)
                {
                    target.Enabled = source.Enabled;
                }

                if (target.ErrorFlags != source.ErrorFlags)
                {
                    target.ErrorFlags = source.ErrorFlags;
                }

                if (target.GroupNumber != source.GroupNumber)
                {
                    target.GroupNumber = source.GroupNumber;
                }

                if (target.InputControlPin != source.InputControlPin)
                {
                    target.InputControlPin = source.InputControlPin;
                }

                if (target.MultiChannel != source.MultiChannel)
                {
                    target.MultiChannel = source.MultiChannel;
                }

                if (target.RetryCount != source.RetryCount)
                {
                    target.RetryCount = source.RetryCount;
                }

                if (target.InrushDelay != source.InrushDelay)
                {
                    target.InrushDelay = source.InrushDelay;
                }

                // Update Name array if different
                if (source.Name != null && (target.Name == null || !target.Name.SequenceEqual(source.Name)))
                {
                    target.Name = (char[])source.Name.Clone();
                }

                if (target.PWMSetDuty != source.PWMSetDuty)
                {
                    target.PWMSetDuty = source.PWMSetDuty;
                }

                if (target.SoftStartEnabled != source.SoftStartEnabled)
                {
                    target.SoftStartEnabled = source.SoftStartEnabled;
                }

                if (target.SoftStartTime != source.SoftStartTime)
                {
                    target.SoftStartTime = source.SoftStartTime;
                }

                if (target.SoftStopEnabled != source.SoftStopEnabled)
                {
                    target.SoftStopEnabled = source.SoftStopEnabled;
                }

                if (target.SoftStopTime != source.SoftStopTime)
                {
                    target.SoftStopTime = source.SoftStopTime;
                }

                if (target.InrushCurrentLimit != source.InrushCurrentLimit)
                {
                    target.InrushCurrentLimit = source.InrushCurrentLimit;
                }

                if (target.OnThreshold != source.OnThreshold)
                {
                    target.OnThreshold = source.OnThreshold;
                }

                if (target.OffThreshold != source.OffThreshold)
                {
                    target.OffThreshold = source.OffThreshold;
                }

                if (target.ScaleMin != source.ScaleMin)
                {
                    target.ScaleMin = source.ScaleMin;
                }

                if (target.ScaleMax != source.ScaleMax)
                {
                    target.ScaleMax = source.ScaleMax;
                }

                if (target.PWMMin != source.PWMMin)
                {
                    target.PWMMin = source.PWMMin;
                }

                if (target.PWMMax != source.PWMMax)
                {
                    target.PWMMax = source.PWMMax;
                }

                if (target.IntermittentOnTime != source.IntermittentOnTime)
                {
                    target.IntermittentOnTime = source.IntermittentOnTime;
                }

                if (target.IntermittentOffTime != source.IntermittentOffTime)
                {
                    target.IntermittentOffTime = source.IntermittentOffTime;
                }

                if (target.MathRuleCount != source.MathRuleCount)
                {
                    target.MathRuleCount = source.MathRuleCount;
                }

                if (target.MathRootRule != source.MathRootRule)
                {
                    target.MathRootRule = source.MathRootRule;
                }

                if (target.MathOutputMode != source.MathOutputMode)
                {
                    target.MathOutputMode = source.MathOutputMode;
                }

                if (target.MathCalculatedRootValue != source.MathCalculatedRootValue)
                {
                    target.MathCalculatedRootValue = source.MathCalculatedRootValue;
                }

                if (target.MathError != source.MathError)
                {
                    target.MathError = source.MathError;
                }

                target.EnsureMathRuleSlots();
                source.EnsureMathRuleSlots();
                for (int ruleIndex = 0; ruleIndex < Constants.NUM_MATH_RULE_SLOTS; ruleIndex++)
                {
                    OutputChannel.MathRule targetRule = target.MathRules[ruleIndex];
                    OutputChannel.MathRule sourceRule = source.MathRules[ruleIndex];

                    if (targetRule.Operation != sourceRule.Operation)
                    {
                        targetRule.Operation = sourceRule.Operation;
                    }

                    if (targetRule.HysteresisEnabled != sourceRule.HysteresisEnabled)
                    {
                        targetRule.HysteresisEnabled = sourceRule.HysteresisEnabled;
                    }

                    if (targetRule.OnThreshold != sourceRule.OnThreshold)
                    {
                        targetRule.OnThreshold = sourceRule.OnThreshold;
                    }

                    if (targetRule.OffThreshold != sourceRule.OffThreshold)
                    {
                        targetRule.OffThreshold = sourceRule.OffThreshold;
                    }

                    if (targetRule.OperandAType != sourceRule.OperandAType)
                    {
                        targetRule.OperandAType = sourceRule.OperandAType;
                    }

                    if (targetRule.OperandAIndex != sourceRule.OperandAIndex)
                    {
                        targetRule.OperandAIndex = sourceRule.OperandAIndex;
                    }

                    if (targetRule.OperandAConstant != sourceRule.OperandAConstant)
                    {
                        targetRule.OperandAConstant = sourceRule.OperandAConstant;
                    }

                    if (targetRule.OperandBType != sourceRule.OperandBType)
                    {
                        targetRule.OperandBType = sourceRule.OperandBType;
                    }

                    if (targetRule.OperandBIndex != sourceRule.OperandBIndex)
                    {
                        targetRule.OperandBIndex = sourceRule.OperandBIndex;
                    }

                    if (targetRule.OperandBConstant != sourceRule.OperandBConstant)
                    {
                        targetRule.OperandBConstant = sourceRule.OperandBConstant;
                    }

                    if (targetRule.RuntimeValue != sourceRule.RuntimeValue)
                    {
                        targetRule.RuntimeValue = sourceRule.RuntimeValue;
                    }

                    if (targetRule.RuntimeError != sourceRule.RuntimeError)
                    {
                        targetRule.RuntimeError = sourceRule.RuntimeError;
                    }

                    if (targetRule.Evaluated != sourceRule.Evaluated)
                    {
                        targetRule.Evaluated = sourceRule.Evaluated;
                    }
                }

                if (target.DelayedOn != source.DelayedOn)
                {
                    target.DelayedOn = source.DelayedOn;
                }

                if (target.DelayedOnTime != source.DelayedOnTime)
                {
                    target.DelayedOnTime = source.DelayedOnTime;
                }

                if (target.DelayedOff != source.DelayedOff)
                {
                    target.DelayedOff = source.DelayedOff;
                }

                if (target.DelayedOffTime != source.DelayedOffTime)
                {
                    target.DelayedOffTime = source.DelayedOffTime;
                }

                if (target.DelayedOffTrigger != source.DelayedOffTrigger)
                {
                    target.DelayedOffTrigger = source.DelayedOffTrigger;
                }

                target.RefreshDelayUiUnitsFromStoredValues();
            }

            // Update analogue inputs
            int analogueCount = Math.Min(newData.AnalogueInputsLiveData.Count, LiveDataView.AnalogueInputsLiveData.Count);
            bool liveAnalogueMetadataChanged = false;
            for (int i = 0; i < analogueCount; i++)
            {
                var target = LiveDataView.AnalogueInputsLiveData[i];
                var source = newData.AnalogueInputsLiveData[i];

                if (target.ChanType != source.ChanType)
                {
                    target.ChanType = source.ChanType;
                    liveAnalogueMetadataChanged = true;
                }

                if (target.Units != source.Units)
                {
                    target.Units = source.Units;
                    liveAnalogueMetadataChanged = true;
                }

                if (target.CalibrationPoints != source.CalibrationPoints)
                {
                    target.CalibrationPoints = source.CalibrationPoints;
                }

                if (target.PullUpEnable != source.PullUpEnable)
                {
                    target.PullUpEnable = source.PullUpEnable;
                }

                if (target.PullDownEnable != source.PullDownEnable)
                {
                    target.PullDownEnable = source.PullDownEnable;
                }

                if (target.InputVoltage != source.InputVoltage)
                {
                    target.InputVoltage = source.InputVoltage;
                }

                if (target.InputValue != source.InputValue)
                {
                    target.InputValue = source.InputValue;
                }

                if (target.CalibrationVolt1 != source.CalibrationVolt1)
                {
                    target.CalibrationVolt1 = source.CalibrationVolt1;
                }

                if (target.CalibrationValue1 != source.CalibrationValue1)
                {
                    target.CalibrationValue1 = source.CalibrationValue1;
                }

                if (target.CalibrationVolt2 != source.CalibrationVolt2)
                {
                    target.CalibrationVolt2 = source.CalibrationVolt2;
                }

                if (target.CalibrationValue2 != source.CalibrationValue2)
                {
                    target.CalibrationValue2 = source.CalibrationValue2;
                }

                if (target.CalibrationVolt3 != source.CalibrationVolt3)
                {
                    target.CalibrationVolt3 = source.CalibrationVolt3;
                }

                if (target.CalibrationValue3 != source.CalibrationValue3)
                {
                    target.CalibrationValue3 = source.CalibrationValue3;
                }

                if (target.NtcBeta != source.NtcBeta)
                {
                    target.NtcBeta = source.NtcBeta;
                }

                if (target.NtcNominalResistance != source.NtcNominalResistance)
                {
                    target.NtcNominalResistance = source.NtcNominalResistance;
                }
            }

            UpdateChannelAnalogueVoltageDisplayValues();
            if (liveAnalogueMetadataChanged)
            {
                _lastLiveChartAxisSignature = string.Empty;
                UpdateLiveSeriesToggleLabels();
                RefreshLiveChartAxes();
            }

            // Update digital inputs
            int digitalCount = Math.Min(newData.DigitalInputsStaticData.Count, LiveDataView.DigitalInputsLiveData.Count);
            for (int i = 0; i < digitalCount; i++)
            {
                var target = LiveDataView.DigitalInputsLiveData[i];
                var source = newData.DigitalInputsLiveData[i];

                if (target.IsActiveHigh != source.IsActiveHigh)
                {
                    target.IsActiveHigh = source.IsActiveHigh;
                }

                if (target.LiveElectricalState != source.LiveElectricalState)
                {
                    target.LiveElectricalState = source.LiveElectricalState;
                }
            }

            // Update system params
            var targetSys = LiveDataView.SystemParams;
            var sourceSys = newData.SystemParams;

            targetSys.AccelerationX = sourceSys.AccelerationX;
            targetSys.AccelerationY = sourceSys.AccelerationY;
            targetSys.AccelerationZ = sourceSys.AccelerationZ;
            targetSys.GyroscopeX = sourceSys.GyroscopeX;
            targetSys.GyroscopeY = sourceSys.GyroscopeY;
            targetSys.GyroscopeZ = sourceSys.GyroscopeZ;
            targetSys.MagneticFieldX = sourceSys.MagneticFieldX;
            targetSys.MagneticFieldY = sourceSys.MagneticFieldY;
            targetSys.MagneticFieldZ = sourceSys.MagneticFieldZ;

            if (targetSys.SystemTemperature != sourceSys.SystemTemperature)
            {
                targetSys.SystemTemperature = sourceSys.SystemTemperature;
            }

            if (targetSys.SIMModuleTemp != sourceSys.SIMModuleTemp)
            {
                targetSys.SIMModuleTemp = sourceSys.SIMModuleTemp;
            }

            if (targetSys.IMUTemp != sourceSys.IMUTemp)
            {
                targetSys.IMUTemp = sourceSys.IMUTemp;
            }

            if (targetSys.CANResEnabled != sourceSys.CANResEnabled)
            {
                targetSys.CANResEnabled = sourceSys.CANResEnabled;
            }

            if (targetSys.VBatt != sourceSys.VBatt)
            {
                targetSys.VBatt = sourceSys.VBatt;
            }

            if (targetSys.SystemCurrent != sourceSys.SystemCurrent)
            {
                targetSys.SystemCurrent = sourceSys.SystemCurrent;
            }

            if (targetSys.SystemCurrentLimit != sourceSys.SystemCurrentLimit)
            {
                targetSys.SystemCurrentLimit = sourceSys.SystemCurrentLimit;
            }

            if (targetSys.ErrorFlags != sourceSys.ErrorFlags)
            {
                targetSys.ErrorFlags = sourceSys.ErrorFlags;
            }

            if (targetSys.ChannelDataCANID != sourceSys.ChannelDataCANID)
            {
                targetSys.ChannelDataCANID = sourceSys.ChannelDataCANID;
            }

            if (targetSys.DigitalInputDataCANID != sourceSys.DigitalInputDataCANID)
            {
                targetSys.DigitalInputDataCANID = sourceSys.DigitalInputDataCANID;
            }

            if (targetSys.AnalogueInputDataCANID != sourceSys.AnalogueInputDataCANID)
            {
                targetSys.AnalogueInputDataCANID = sourceSys.AnalogueInputDataCANID;
            }

            if (targetSys.SystemDataCANID != sourceSys.SystemDataCANID)
            {
                targetSys.SystemDataCANID = sourceSys.SystemDataCANID;
            }

            if (targetSys.ConfigDataCANID != sourceSys.ConfigDataCANID)
            {
                targetSys.ConfigDataCANID = sourceSys.ConfigDataCANID;
            }

            if (targetSys.SystemConfigCANID != sourceSys.SystemConfigCANID)
            {
                targetSys.SystemConfigCANID = sourceSys.SystemConfigCANID;
            }

            if (targetSys.IMUWakeWindow != sourceSys.IMUWakeWindow)
            {
                targetSys.IMUWakeWindow = sourceSys.IMUWakeWindow;
            }

            if (targetSys.SpeedUnitPref != sourceSys.SpeedUnitPref)
            {
                targetSys.SpeedUnitPref = sourceSys.SpeedUnitPref;
            }

            if (targetSys.DistanceUnitPref != sourceSys.DistanceUnitPref)
            {
                targetSys.DistanceUnitPref = sourceSys.DistanceUnitPref;
            }

            if (targetSys.AllowData != sourceSys.AllowData)
            {
                targetSys.AllowData = sourceSys.AllowData;
            }

            if (targetSys.AllowGPS != sourceSys.AllowGPS)
            {
                targetSys.AllowGPS = sourceSys.AllowGPS;
            }

            if (targetSys.MobileSignalPercent != sourceSys.MobileSignalPercent)
            {
                targetSys.MobileSignalPercent = sourceSys.MobileSignalPercent;
            }

            int customCanLiveMessageCount = Math.Min(newData.CustomCanMessagesLiveData.Count, LiveDataView.CustomCanMessagesLiveData.Count);
            for (int i = 0; i < customCanLiveMessageCount; i++)
            {
                CustomCanMessage source = newData.CustomCanMessagesLiveData[i];
                CustomCanMessage target = LiveDataView.CustomCanMessagesLiveData[i];
                target.Index = source.Index;
                target.Id = source.Id;
                target.TimeoutMs = source.TimeoutMs;
                target.Dlc = source.Dlc;
                target.Flags = source.Flags;
                target.TxTrigger = source.TxTrigger;
                target.Status = source.Status;
            }

            int customCanLiveSignalCount = Math.Min(newData.CustomCanSignalsLiveData.Count, LiveDataView.CustomCanSignalsLiveData.Count);
            for (int i = 0; i < customCanLiveSignalCount; i++)
            {
                CustomCanSignal source = newData.CustomCanSignalsLiveData[i];
                CustomCanSignal target = LiveDataView.CustomCanSignalsLiveData[i];
                CustomCanSignal? storedSignal = i < SettingsDataView.CustomCanSignalsStaticData.Count
                    ? SettingsDataView.CustomCanSignalsStaticData[i]
                    : null;
                target.Index = source.Index;
                // The controller does not store names; prefer the locally tracked name over its placeholder.
                target.Name = !ShouldApplyMetadataName(source.Name, i) || storedSignal == null
                    ? source.Name
                    : storedSignal.Name;
                target.MessageName = !IsPlaceholderMessageName(source.MessageName) || storedSignal == null
                    ? source.MessageName
                    : storedSignal.MessageName;
                target.MessageIndex = source.MessageIndex;
                target.StartBit = source.StartBit;
                target.BitLength = source.BitLength;
                target.Flags = source.Flags;
                target.ValueSource = source.ValueSource;
                target.Factor = source.Factor;
                target.Offset = source.Offset;
                target.RuntimeValue = source.RuntimeValue;
                target.Status = source.Status;
            }

            bool preserveEditableCustomCan = HasPendingConfigChanges;
            if (!preserveEditableCustomCan)
            {
                string?[] previousSignalIdentities = SettingsDataView.CustomCanSignalsStaticData
                    .Select(BuildStoredCustomCanSignalIdentityKey)
                    .ToArray();

                int customCanStaticMessageCount = Math.Min(newData.CustomCanMessagesStaticData.Count, SettingsDataView.CustomCanMessagesStaticData.Count);
                bool customCanStaticDefinitionsChanged =
                    newData.CustomCanMessagesStaticData.Count != SettingsDataView.CustomCanMessagesStaticData.Count
                    || newData.CustomCanSignalsStaticData.Count != SettingsDataView.CustomCanSignalsStaticData.Count;

                for (int i = 0; i < customCanStaticMessageCount; i++)
                {
                    CustomCanMessage source = newData.CustomCanMessagesStaticData[i];
                    CustomCanMessage target = SettingsDataView.CustomCanMessagesStaticData[i];

                    ushort sourceTimeoutMs = source.TimeoutMs;
                    byte sourceFlags = source.Flags;
                    byte sourceTxTrigger = source.TxTrigger;
                    if (TryGetPendingCustomCanMessageWrite(i, out ushort pendingTimeoutMs, out byte pendingFlags, out byte pendingTxTrigger))
                    {
                        sourceTimeoutMs = pendingTimeoutMs;
                        sourceFlags = pendingFlags;
                        sourceTxTrigger = pendingTxTrigger;
                    }

                    if (target.Index != source.Index
                        || target.Id != source.Id
                        || target.TimeoutMs != sourceTimeoutMs
                        || target.Dlc != source.Dlc
                        || target.Flags != sourceFlags
                        || target.TxTrigger != sourceTxTrigger)
                    {
                        customCanStaticDefinitionsChanged = true;
                    }

                    target.Index = source.Index;
                    target.Id = source.Id;
                    target.TimeoutMs = sourceTimeoutMs;
                    target.Dlc = source.Dlc;
                    target.Flags = sourceFlags;
                    target.TxTrigger = sourceTxTrigger;
                    target.Status = source.Status;
                }

                int customCanStaticSignalCount = Math.Min(newData.CustomCanSignalsStaticData.Count, SettingsDataView.CustomCanSignalsStaticData.Count);
                _customCanMetadataUpsertSuppression++;
                try
                {
                    for (int i = 0; i < customCanStaticSignalCount; i++)
                    {
                        CustomCanSignal source = newData.CustomCanSignalsStaticData[i];
                        CustomCanSignal target = SettingsDataView.CustomCanSignalsStaticData[i];

                        byte sourceValueSource = source.ValueSource;
                        if (TryGetPendingCustomCanSignalValueSourceWrite(i, out byte pendingValueSource))
                        {
                            sourceValueSource = pendingValueSource;
                        }

                        bool sourceLooksEmptyDisabled =
                            source.Flags == 0 &&
                            source.StartBit == 0 &&
                            source.BitLength <= 1 &&
                            source.MessageIndex == 0 &&
                            source.Factor == 1.0f &&
                            source.Offset == 0.0f &&
                            string.IsNullOrWhiteSpace(source.MessageName);
                        if (target.IsEnabled && sourceLooksEmptyDisabled)
                        {
                            continue;
                        }

                        bool sourceHasName = !ShouldApplyMetadataName(source.Name, i);
                        bool sourceHasMessageName = !IsPlaceholderMessageName(source.MessageName);

                        if (target.Index != source.Index
                            || (sourceHasName && target.Name != source.Name)
                            || (sourceHasMessageName && target.MessageName != source.MessageName)
                            || target.MessageIndex != source.MessageIndex
                            || target.StartBit != source.StartBit
                            || target.BitLength != source.BitLength
                            || target.Flags != source.Flags
                            || target.ValueSource != sourceValueSource
                            || target.Factor != source.Factor
                            || target.Offset != source.Offset)
                        {
                            customCanStaticDefinitionsChanged = true;
                        }

                        target.Index = source.Index;
                        target.MessageIndex = source.MessageIndex;
                        target.StartBit = source.StartBit;
                        target.BitLength = source.BitLength;
                        target.Flags = source.Flags;
                        target.ValueSource = sourceValueSource;
                        target.Factor = source.Factor;
                        target.Offset = source.Offset;
                        target.RuntimeValue = source.RuntimeValue;
                        target.Status = source.Status;

                        // Names, limits and units only exist locally. When the slot now holds a different
                        // signal, drop the stale local details so they are re-resolved from metadata.
                        string? previousIdentity = i < previousSignalIdentities.Length ? previousSignalIdentities[i] : null;
                        if (!string.Equals(previousIdentity, BuildStoredCustomCanSignalIdentityKey(target), StringComparison.Ordinal))
                        {
                            target.Name = $"Signal {i + 1}";
                            target.MessageName = string.Empty;
                            target.MinValue = null;
                            target.MaxValue = null;
                            target.Units = string.Empty;
                            customCanStaticDefinitionsChanged = true;
                        }

                        if (sourceHasName)
                        {
                            target.Name = source.Name;
                        }

                        if (sourceHasMessageName)
                        {
                            target.MessageName = source.MessageName;
                        }
                    }
                }
                finally
                {
                    _customCanMetadataUpsertSuppression--;
                }

                if (customCanStaticDefinitionsChanged || SettingsDataView.CustomCanSignalsMetadata.Count > 0)
                {
                    RefreshCustomCanBindings();
                }

                SyncCustomCanBaselineSnapshot();
                if (customCanStaticDefinitionsChanged)
                {
                    SyncIoFlowCanTxBaseline();
                }
            }

            RefreshIoFlowLiveSamples();
        }

        private void SyncCustomCanBaselineSnapshot()
        {
            if (_controllerConfigBaseline == null)
            {
                return;
            }

            int messageCount = Math.Min(SettingsDataView.CustomCanMessagesStaticData.Count, _controllerConfigBaseline.CustomCanMessagesStaticData.Count);
            for (int i = 0; i < messageCount; i++)
            {
                CustomCanMessage source = SettingsDataView.CustomCanMessagesStaticData[i];
                CustomCanMessage target = _controllerConfigBaseline.CustomCanMessagesStaticData[i];
                target.Index = source.Index;
                target.Id = source.Id;
                target.TimeoutMs = source.TimeoutMs;
                target.Dlc = source.Dlc;
                target.Flags = source.Flags;
                target.TxTrigger = source.TxTrigger;
                target.Status = source.Status;
            }

            int signalCount = Math.Min(SettingsDataView.CustomCanSignalsStaticData.Count, _controllerConfigBaseline.CustomCanSignalsStaticData.Count);
            for (int i = 0; i < signalCount; i++)
            {
                CustomCanSignal source = SettingsDataView.CustomCanSignalsStaticData[i];
                CustomCanSignal target = _controllerConfigBaseline.CustomCanSignalsStaticData[i];
                target.Index = source.Index;
                target.Name = source.Name;
                target.MessageName = source.MessageName;
                target.MessageIndex = source.MessageIndex;
                target.StartBit = source.StartBit;
                target.BitLength = source.BitLength;
                target.Flags = source.Flags;
                target.ValueSource = source.ValueSource;
                target.Factor = source.Factor;
                target.Offset = source.Offset;
                target.RuntimeValue = source.RuntimeValue;
                target.Status = source.Status;
            }
        }

        public double LastUpdatedHighlightOpacity
        {
            get => _lastUpdatedHighlightOpacity;
            set
            {
                if (_lastUpdatedHighlightOpacity != value)
                {
                    _lastUpdatedHighlightOpacity = value;
                    OnPropertyChanged();
                }
            }
        }


        private const int MAX_CHART_POINTS = 2000;
        private const int LiveChartHistoryRetentionSeconds = 300;
        private const int LiveChartHistoryMaxPoints = 6000;
        private static readonly TimeSpan LiveChartSeriesAnimationSpeed = TimeSpan.FromMilliseconds(90);
        private const double LiveChartYAxisHeadroomAmps = 1.0;
        private const int LiveChartAnalogueAxisSpacing = 6;
        private static readonly string LiveChartPreferencesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cortex",
            "live-chart-preferences.json");

        private static SKColor GetLiveSeriesColor(int index) => LiveSeriesPalette[index % LiveSeriesPalette.Length];

        private static IBrush CreateLiveSeriesBrush(SKColor color) =>
            new SolidColorBrush(Color.FromArgb(color.Alpha, color.Red, color.Green, color.Blue));

        private sealed class LiveChartPreferences
        {
            public List<bool>? ChannelSeriesEnabled { get; set; }

            public List<bool>? AnalogueSeriesEnabled { get; set; }
        }

        private bool HasAnyVisibleCurrentSeries => ChannelLiveSeriesToggles.Any(toggle => toggle.IsEnabled);

        private bool HasVisibleSeriesUsingUnit(AnalogueInput.AnalogueUnits units)
        {
            int analogueCount = Math.Min(LiveDataView.AnalogueInputsLiveData.Count, AnalogueLiveSeriesToggles.Count);
            for (int i = 0; i < analogueCount; i++)
            {
                if (!AnalogueLiveSeriesToggles[i].IsEnabled)
                {
                    continue;
                }

                if (GetLiveAnalogueSeriesUnit(LiveDataView.AnalogueInputsLiveData[i]) == units)
                {
                    return true;
                }
            }

            return false;
        }

        private void ApplySavedLiveChartPreferences()
        {
            LiveChartPreferences? preferences = LoadLiveChartPreferences();
            if (preferences == null)
            {
                return;
            }

            _suppressLiveChartAxisRefresh = true;
            try
            {
                ApplyToggleStates(ChannelLiveSeriesToggles, preferences.ChannelSeriesEnabled);
                ApplyToggleStates(AnalogueLiveSeriesToggles, preferences.AnalogueSeriesEnabled);
            }
            finally
            {
                _suppressLiveChartAxisRefresh = false;
            }
        }

        private static void ApplyToggleStates(IReadOnlyList<ChartSeriesToggleItem> toggles, IReadOnlyList<bool>? states)
        {
            if (states == null)
            {
                return;
            }

            int count = Math.Min(toggles.Count, states.Count);
            for (int i = 0; i < count; i++)
            {
                toggles[i].IsEnabled = states[i];
            }
        }

        private static LiveChartPreferences? LoadLiveChartPreferences()
        {
            try
            {
                if (!File.Exists(LiveChartPreferencesPath))
                {
                    return null;
                }

                string json = File.ReadAllText(LiveChartPreferencesPath);
                return JsonSerializer.Deserialize<LiveChartPreferences>(json);
            }
            catch
            {
                return null;
            }
        }

        private void SaveLiveChartPreferences()
        {
            try
            {
                string? directory = Path.GetDirectoryName(LiveChartPreferencesPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var preferences = new LiveChartPreferences
                {
                    ChannelSeriesEnabled = ChannelLiveSeriesToggles.Select(toggle => toggle.IsEnabled).ToList(),
                    AnalogueSeriesEnabled = AnalogueLiveSeriesToggles.Select(toggle => toggle.IsEnabled).ToList(),
                };

                string json = JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(LiveChartPreferencesPath, json);
            }
            catch
            {
                // Ignore local preference persistence failures.
            }
        }

        private static Axis CreateLiveAxis(string name, AxisPosition position, Func<double, string>? labeler = null, int outerPadding = 0, bool showGridLines = true)
        {
            return new Axis
            {
                Name = name,
                Position = position,
                Padding = position == AxisPosition.End
                    ? new LiveChartsCore.Drawing.Padding(10, 0, outerPadding + 4, 0)
                    : new LiveChartsCore.Drawing.Padding(10, 0, 10, 0),
                Labeler = labeler ?? (value => value.ToString("F1")),
                ShowSeparatorLines = showGridLines,
                SeparatorsPaint = showGridLines
                    ? new SolidColorPaint
                    {
                        StrokeThickness = 1,
                        Color = new SKColor(120, 120, 120),
                    }
                    : null,
                SubseparatorsPaint = showGridLines
                    ? new SolidColorPaint
                    {
                        Color = new SKColor(34, 34, 34),
                        StrokeThickness = 0.5f,
                    }
                    : null,
                SubseparatorsCount = showGridLines ? 9 : 0,
                ZeroPaint = showGridLines
                    ? new SolidColorPaint
                    {
                        Color = new SKColor(150, 150, 150),
                        StrokeThickness = 2,
                    }
                    : null,
                TicksPaint = showGridLines
                    ? new SolidColorPaint
                    {
                        Color = new SKColor(140, 140, 140),
                        StrokeThickness = 1.5f,
                    }
                    : null,
                SubticksPaint = showGridLines
                    ? new SolidColorPaint
                    {
                        Color = new SKColor(34, 34, 34),
                        StrokeThickness = 1,
                    }
                    : null,
                NameTextSize = 13,
                TextSize = 13,
            };
        }

        private static string GetAnalogueUnitAxisName(AnalogueInput.AnalogueUnits units)
        {
            string suffix = GetUnitsSuffix(units);
            return string.IsNullOrWhiteSpace(suffix)
                ? "Analogue"
                : $"Analogue ({suffix})";
        }

        private static Func<double, string> GetLiveAxisLabeler(AnalogueInput.AnalogueUnits units)
        {
            return UseDecimalPrecision(units)
                ? value => value.ToString("F1")
                : value => value.ToString("F0");
        }

        private static AnalogueInput.AnalogueUnits GetLiveAnalogueSeriesUnit(AnalogueInput input)
        {
            return input.ReportsConnectorVoltage
                ? AnalogueInput.AnalogueUnits.Volts
                : input.Units;
        }

        private static float? GetLiveAnalogueSeriesValue(AnalogueInput input)
        {
            return input.InputValue ?? input.InputVoltage;
        }

        private static string GetLiveAnalogueSeriesDisplayName(AnalogueInput input, int index)
        {
            string suffix = GetUnitsSuffix(GetLiveAnalogueSeriesUnit(input));
            return string.IsNullOrWhiteSpace(suffix)
                ? $"Ana/Dig {index + 1}"
                : $"Ana/Dig {index + 1} ({suffix})";
        }

        private static bool TryGetChartPoints(object? parameter, out List<ChartPoint> points)
        {
            points = [];

            if (parameter is IEnumerable<ChartPoint> manyPoints)
            {
                points = manyPoints.Where(point => point != null && !point.IsEmpty).ToList();
                return points.Count > 0;
            }

            if (parameter is ChartPoint singlePoint && !singlePoint.IsEmpty)
            {
                points = [singlePoint];
                return true;
            }

            return false;
        }

        private string BuildLiveChartHoverSummary(IReadOnlyList<ChartPoint> points)
        {
            if (points.Count == 0)
            {
                return string.Empty;
            }

            double timestampMs = points[0].Coordinate.SecondaryValue;
            string timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(timestampMs))
                .LocalDateTime
                .ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

            var values = new List<string>();
            foreach (ChartPoint point in points.OrderBy(point => point.Context.Series?.Name, StringComparer.Ordinal))
            {
                string seriesName = point.Context.Series?.Name ?? "Series";
                double value = point.Coordinate.PrimaryValue;
                values.Add($"{seriesName}: {value:F1}");
            }

            return values.Count == 0
                ? timestamp
                : $"{timestamp}  |  {string.Join("  |  ", values)}";
        }

        private static bool TryGetNearestSeriesValue(IReadOnlyList<ObservablePoint> points, double targetX, out double value)
        {
            value = 0;

            if (points.Count == 0)
            {
                return false;
            }

            double? previousX = null;
            double? previousY = null;

            foreach (ObservablePoint point in points)
            {
                if (!point.X.HasValue || !point.Y.HasValue)
                {
                    continue;
                }

                double currentX = point.X.Value;
                double currentY = point.Y.Value;

                if (currentX < targetX)
                {
                    previousX = currentX;
                    previousY = currentY;
                    continue;
                }

                if (!previousX.HasValue || !previousY.HasValue)
                {
                    value = currentY;
                    return true;
                }

                if (currentX <= previousX.Value)
                {
                    value = currentY;
                    return true;
                }

                double ratio = Math.Clamp((targetX - previousX.Value) / (currentX - previousX.Value), 0d, 1d);
                value = previousY.Value + ((currentY - previousY.Value) * ratio);
                return true;
            }

            if (previousY.HasValue)
            {
                value = previousY.Value;
                return true;
            }

            return false;
        }

        private void RefreshLiveChartSelectionFromAnchor()
        {
            if (IsLiveChartSelectionPinned || !_hasLiveHoverAnchor)
            {
                return;
            }

            double anchorTimestampMs = _liveHoverAnchorTimestampMs;

            for (int i = 0; i < ChannelLiveSeriesToggles.Count; i++)
            {
                ChannelLiveSeriesToggles[i].SetCurrentValue(TryGetLiveSeriesValueAtAnchor(i, anchorTimestampMs, requireVisible: true, out double value)
                    ? value
                    : null);
            }

            int analogueOffset = LiveDataView.ChannelsLiveData.Count;
            for (int i = 0; i < AnalogueLiveSeriesToggles.Count; i++)
            {
                AnalogueLiveSeriesToggles[i].SetCurrentValue(TryGetLiveSeriesValueAtAnchor(analogueOffset + i, anchorTimestampMs, requireVisible: true, out double value)
                    ? value
                    : null);
            }

            OnPropertyChanged(nameof(HasLiveChartSelection));
        }

        public void UpdateLiveHoverAnchorFromRelativeX(double relativeX)
        {
            if (IsLiveChartSelectionPinned)
            {
                return;
            }

            if (XAxes is not { Length: > 0 } || !XAxes[0].MaxLimit.HasValue)
            {
                return;
            }

            double maxX = XAxes[0].MaxLimit!.Value;
            double minX = XAxes[0].MinLimit ?? maxX;
            double range = Math.Max(1, maxX - minX);
            double clampedRelativeX = Math.Clamp(relativeX, 0d, 1d);
            double anchorX = minX + (range * clampedRelativeX);

            _liveHoverAnchorTimestampMs = anchorX;
            _hasLiveHoverAnchor = true;
            RefreshLiveChartSelectionFromAnchor();
            OnPropertyChanged(nameof(HasLiveChartSelection));
        }

        private void UpdateLiveChartSelection(IReadOnlyList<ChartPoint> points, bool pinSelection)
        {
            _lastHoveredLiveChartPoints = points.Where(point => point != null && !point.IsEmpty).ToList();
            if (_lastHoveredLiveChartPoints.Count == 0)
            {
                if (!_hasLiveHoverAnchor)
                {
                    ClearLiveChartSelectionState();
                }

                return;
            }

            if (pinSelection)
            {
                IsLiveChartSelectionPinned = true;
            }

            LiveChartHoverSummary = BuildLiveChartHoverSummary(_lastHoveredLiveChartPoints);
            OnPropertyChanged(nameof(HasLiveChartSelection));
            OnPropertyChanged(nameof(LiveChartPinButtonText));
        }

        private void ScheduleLiveChartSelectionClear()
        {
            if (_lastHoveredLiveChartPoints.Count == 0)
            {
                return;
            }

            _liveHoverClearTimer.Stop();
            _liveHoverClearTimer.Start();
        }

        private bool TryGetLiveSeriesValueAtAnchor(int seriesIndex, double anchorTimestampMs, bool requireVisible, out double value)
        {
            value = 0;

            if (seriesIndex < 0 || seriesIndex >= SeriesCollection.Count)
            {
                return false;
            }

            if (SeriesCollection[seriesIndex] is not LineSeries<ObservablePoint> series)
            {
                return false;
            }

            if (requireVisible && !series.IsVisible)
            {
                return false;
            }

            EnsureLiveSeriesStateCapacity();
            if (seriesIndex < _liveSeriesHistory.Length)
            {
                List<ObservablePoint> history = _liveSeriesHistory[seriesIndex];
                if (history.Count > 0 && TryGetNearestSeriesValue(history, anchorTimestampMs, out value))
                {
                    return true;
                }
            }

            if (series.Values is not ObservableCollection<ObservablePoint> points || points.Count == 0)
            {
                return false;
            }

            return TryGetNearestSeriesValue(points, anchorTimestampMs, out value);
        }

        private void ClearLiveSeriesToggleValues()
        {
            foreach (ChartSeriesToggleItem toggle in ChannelLiveSeriesToggles)
            {
                toggle.SetCurrentValue(null);
            }

            foreach (ChartSeriesToggleItem toggle in AnalogueLiveSeriesToggles)
            {
                toggle.SetCurrentValue(null);
            }
        }

        private void ClearLiveChartSelectionState()
        {
            _lastHoveredLiveChartPoints.Clear();
            _hasLiveHoverAnchor = false;
            _liveHoverAnchorTimestampMs = 0;
            LiveChartHoverSummary = string.Empty;
            ClearLiveSeriesToggleValues();
            OnPropertyChanged(nameof(HasLiveChartSelection));
            OnPropertyChanged(nameof(LiveChartPinButtonText));
        }

        private string BuildLiveChartAxisSignature()
        {
            var parts = new List<string>();
            if (HasAnyVisibleCurrentSeries)
            {
                parts.Add("current");
            }

            foreach (AnalogueInput.AnalogueUnits units in Enum.GetValues(typeof(AnalogueInput.AnalogueUnits)))
            {
                if (HasVisibleSeriesUsingUnit(units))
                {
                    parts.Add($"ana:{(int)units}:{UsesConnectorVoltageAxis(units)}");
                }
            }

            return string.Join("|", parts);
        }

        private void RefreshLiveChartAxes()
        {
            if (_suppressLiveChartAxisRefresh)
            {
                return;
            }

            string axisSignature = BuildLiveChartAxisSignature();
            if (string.Equals(_lastLiveChartAxisSignature, axisSignature, StringComparison.Ordinal))
            {
                UpdateLiveSeriesAxisAssignments();
                UpdateLiveCrosshairState();
                return;
            }

            var axes = new List<ICartesianAxis>();
            if (HasAnyVisibleCurrentSeries)
            {
                axes.Add(CreateLiveAxis("Current (Amps)", AxisPosition.Start, value => value.ToString("F2")));
            }

            int analogueAxisCount = 0;
            foreach (AnalogueInput.AnalogueUnits units in Enum.GetValues(typeof(AnalogueInput.AnalogueUnits)))
            {
                if (!HasVisibleSeriesUsingUnit(units))
                {
                    continue;
                }

                axes.Add(CreateLiveAxis(
                    GetAnalogueUnitAxisName(units),
                    AxisPosition.End,
                    GetLiveAxisLabeler(units),
                    outerPadding: analogueAxisCount * LiveChartAnalogueAxisSpacing,
                    showGridLines: false));
                analogueAxisCount++;
            }

            if (axes.Count == 0)
            {
                axes.Add(CreateLiveAxis("Current (Amps)", AxisPosition.Start, value => value.ToString("F2")));
            }

            YAxes = axes.ToArray();
            _lastLiveChartAxisSignature = axisSignature;
            OnPropertyChanged(nameof(YAxes));
            UpdateLiveSeriesAxisAssignments();
            ApplyLiveChartAxisLimits(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), null);
            UpdateLiveCrosshairState();
        }

        private bool UsesConnectorVoltageAxis(AnalogueInput.AnalogueUnits units)
        {
            if (units != AnalogueInput.AnalogueUnits.Volts)
            {
                return false;
            }

            int count = Math.Min(LiveDataView.AnalogueInputsLiveData.Count, AnalogueLiveSeriesToggles.Count);
            bool hasConnectorVoltage = false;
            for (int i = 0; i < count; i++)
            {
                AnalogueInput input = LiveDataView.AnalogueInputsLiveData[i];
                if (!AnalogueLiveSeriesToggles[i].IsEnabled || GetLiveAnalogueSeriesUnit(input) != units)
                {
                    continue;
                }
                if (!input.ReportsConnectorVoltage)
                {
                    return false;
                }
                hasConnectorVoltage = true;
            }
            return hasConnectorVoltage;
        }

        private void UpdateLiveSeriesAxisAssignments()
        {
            int currentAxisIndex = HasAnyVisibleCurrentSeries ? 0 : -1;
            int analogueAxisStartIndex = currentAxisIndex >= 0 ? 1 : 0;
            var analogueAxisMap = new Dictionary<AnalogueInput.AnalogueUnits, int>();
            int analogueAxisIndex = analogueAxisStartIndex;

            foreach (AnalogueInput.AnalogueUnits units in Enum.GetValues(typeof(AnalogueInput.AnalogueUnits)))
            {
                if (!HasVisibleSeriesUsingUnit(units))
                {
                    continue;
                }

                analogueAxisMap[units] = analogueAxisIndex++;
            }

            for (int i = 0; i < LiveDataView.ChannelsLiveData.Count && i < SeriesCollection.Count; i++)
            {
                if (SeriesCollection[i] is LineSeries<ObservablePoint> channelSeries)
                {
                    channelSeries.ScalesYAt = Math.Max(currentAxisIndex, 0);
                }
            }

            int offset = LiveDataView.ChannelsLiveData.Count;
            for (int i = 0; i < LiveDataView.AnalogueInputsLiveData.Count && offset + i < SeriesCollection.Count; i++)
            {
                if (SeriesCollection[offset + i] is not LineSeries<ObservablePoint> analogueSeries)
                {
                    continue;
                }

                AnalogueInput.AnalogueUnits units = GetLiveAnalogueSeriesUnit(LiveDataView.AnalogueInputsLiveData[i]);
                analogueSeries.ScalesYAt = analogueAxisMap.TryGetValue(units, out int axisIndex)
                    ? axisIndex
                    : Math.Max(currentAxisIndex, 0);
            }
        }

        private void SetChannelLiveSeriesEnabled(bool isEnabled)
        {
            _suppressLiveChartAxisRefresh = true;
            try
            {
                foreach (var toggle in ChannelLiveSeriesToggles)
                {
                    toggle.IsEnabled = isEnabled;
                }
            }
            finally
            {
                _suppressLiveChartAxisRefresh = false;
            }

            _lastLiveChartAxisSignature = string.Empty;
            RefreshLiveChartAxes();
        }

        private void SetAnalogueLiveSeriesEnabled(bool isEnabled)
        {
            _suppressLiveChartAxisRefresh = true;
            try
            {
                int offset = LiveDataView.ChannelsLiveData.Count;
                for (int i = offset; i < LiveSeriesToggles.Count; i++)
                {
                    LiveSeriesToggles[i].IsEnabled = isEnabled;
                }
            }
            finally
            {
                _suppressLiveChartAxisRefresh = false;
            }

            _lastLiveChartAxisSignature = string.Empty;
            RefreshLiveChartAxes();
        }

        private static string GetLiveSeriesDisplayName(OutputChannel channel, int fallbackIndex)
        {
            string channelName = channel.Name == null
                ? string.Empty
                : new string(channel.Name).TrimEnd('\0', ' ');

            int channelNumber = channel.ChannelNumber > 0 ? channel.ChannelNumber : fallbackIndex + 1;
            return string.IsNullOrWhiteSpace(channelName)
                ? $"CH{channelNumber}"
                : $"CH{channelNumber} {channelName}";
        }

        private void UpdateLiveSeriesToggleLabels()
        {
            int currentCount = Math.Min(LiveDataView.ChannelsLiveData.Count, LiveSeriesToggles.Count);
            for (int i = 0; i < currentCount; i++)
            {
                string label = GetLiveSeriesDisplayName(LiveDataView.ChannelsLiveData[i], i);
                if (!string.Equals(LiveSeriesToggles[i].DisplayName, label, StringComparison.Ordinal))
                {
                    LiveSeriesToggles[i].DisplayName = label;
                }
            }

            int analogueOffset = LiveDataView.ChannelsLiveData.Count;
            int analogueCount = Math.Min(LiveDataView.AnalogueInputsLiveData.Count, Math.Max(0, LiveSeriesToggles.Count - analogueOffset));
            for (int i = 0; i < analogueCount; i++)
            {
                string label = GetLiveAnalogueSeriesDisplayName(LiveDataView.AnalogueInputsLiveData[i], i);
                ChartSeriesToggleItem toggle = LiveSeriesToggles[analogueOffset + i];
                if (!string.Equals(toggle.DisplayName, label, StringComparison.Ordinal))
                {
                    toggle.DisplayName = label;
                }
            }
        }

        private void EnsureLiveSeriesStateCapacity()
        {
            if (_liveSeriesHasSyntheticTail.Length == SeriesCollection.Count && _liveSeriesHistory.Length == SeriesCollection.Count)
            {
                return;
            }

            int previousHistoryLength = _liveSeriesHistory.Length;
            Array.Resize(ref _liveSeriesHasSyntheticTail, SeriesCollection.Count);
            Array.Resize(ref _liveSeriesHistory, SeriesCollection.Count);

            for (int i = previousHistoryLength; i < _liveSeriesHistory.Length; i++)
            {
                _liveSeriesHistory[i] = new List<ObservablePoint>();
            }

            for (int i = 0; i < _liveSeriesHistory.Length; i++)
            {
                _liveSeriesHistory[i] ??= new List<ObservablePoint>();
            }
        }

        private void ResetLiveChartSeries()
        {
            EnsureLiveSeriesStateCapacity();

            for (int i = 0; i < _liveSeriesHasSyntheticTail.Length; i++)
            {
                _liveSeriesHasSyntheticTail[i] = false;
            }

            foreach (ISeries series in SeriesCollection)
            {
                if (series is not LineSeries<ObservablePoint> lineSeries)
                {
                    continue;
                }

                if (lineSeries.Values is ObservableCollection<ObservablePoint> values)
                {
                    values.Clear();
                }
                else
                {
                    lineSeries.Values = new ObservableCollection<ObservablePoint>();
                }
            }
        }

        private void ResetLiveChartHistory()
        {
            EnsureLiveSeriesStateCapacity();

            foreach (List<ObservablePoint> history in _liveSeriesHistory)
            {
                history.Clear();
            }
        }

        private static void TrimLiveSeriesHistory(List<ObservablePoint> history, long cutoffMs)
        {
            while (history.Count > 0 && history[0].X < cutoffMs)
            {
                history.RemoveAt(0);
            }

            while (history.Count > LiveChartHistoryMaxPoints)
            {
                history.RemoveAt(0);
            }
        }

        private void AppendLiveChartHistoryPoint(int seriesIndex, long nowMs, double currentValue)
        {
            EnsureLiveSeriesStateCapacity();

            List<ObservablePoint> history = _liveSeriesHistory[seriesIndex];
            long x = nowMs;
            if (history.Count > 0 && history[^1].X.HasValue)
            {
                long lastX = (long)history[^1].X!.Value;
                if (x <= lastX)
                {
                    x = lastX + 1;
                }
            }

            history.Add(new ObservablePoint(x, currentValue));
            TrimLiveSeriesHistory(history, nowMs - (LiveChartHistoryRetentionSeconds * 1000L));
        }

        private void CaptureLiveChartHistory(DataStructures data)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            for (int i = 0; i < data.ChannelsLiveData.Count && i < SeriesCollection.Count; i++)
            {
                double current = data.ChannelsLiveData[i].CurrentValue;
                if (double.IsNaN(current) || double.IsInfinity(current))
                {
                    continue;
                }

                AppendLiveChartHistoryPoint(i, nowMs, current);
            }

            int analogueSeriesOffset = data.ChannelsLiveData.Count;
            for (int i = 0; i < data.AnalogueInputsLiveData.Count && analogueSeriesOffset + i < SeriesCollection.Count; i++)
            {
                float? analogueValue = GetLiveAnalogueSeriesValue(data.AnalogueInputsLiveData[i]);
                if (!analogueValue.HasValue || float.IsNaN(analogueValue.Value) || float.IsInfinity(analogueValue.Value))
                {
                    continue;
                }

                AppendLiveChartHistoryPoint(analogueSeriesOffset + i, nowMs, analogueValue.Value);
            }
        }

        private Dictionary<int, double> CalculateLiveChartAxisMaxima()
        {
            var maxVisibleByAxis = new Dictionary<int, double>();

            foreach (ISeries seriesBase in SeriesCollection)
            {
                if (seriesBase is not LineSeries<ObservablePoint> series || !series.IsVisible)
                {
                    continue;
                }

                if (series.Values is not ObservableCollection<ObservablePoint> values)
                {
                    continue;
                }

                foreach (ObservablePoint point in values)
                {
                    if (!point.Y.HasValue)
                    {
                        continue;
                    }

                    double y = point.Y.Value;
                    if (double.IsNaN(y) || double.IsInfinity(y))
                    {
                        continue;
                    }

                    if (maxVisibleByAxis.TryGetValue(series.ScalesYAt, out double existingAxisMax))
                    {
                        maxVisibleByAxis[series.ScalesYAt] = Math.Max(existingAxisMax, y);
                    }
                    else
                    {
                        maxVisibleByAxis[series.ScalesYAt] = y;
                    }
                }
            }

            return maxVisibleByAxis;
        }

        private void ApplyLiveChartAxisLimits(long nowMs, Dictionary<int, double>? maxVisibleByAxis)
        {
            long cutoffMs = nowMs - (SelectedTimeWindowSeconds * 1000L);

            if (XAxes is { Length: > 0 })
            {
                XAxes[0].MinLimit = cutoffMs;
                XAxes[0].MaxLimit = nowMs;
            }

            if (YAxes is { Length: > 0 })
            {
                int connectorVoltageAxis = -1;
                if (UsesConnectorVoltageAxis(AnalogueInput.AnalogueUnits.Volts))
                {
                    int offset = LiveDataView.ChannelsLiveData.Count;
                    int count = Math.Min(LiveDataView.AnalogueInputsLiveData.Count, AnalogueLiveSeriesToggles.Count);
                    for (int i = 0; i < count && offset + i < SeriesCollection.Count; i++)
                    {
                        if (AnalogueLiveSeriesToggles[i].IsEnabled &&
                            LiveDataView.AnalogueInputsLiveData[i].ReportsConnectorVoltage &&
                            SeriesCollection[offset + i] is LineSeries<ObservablePoint> series)
                        {
                            connectorVoltageAxis = series.ScalesYAt;
                            break;
                        }
                    }
                }
                for (int i = 0; i < YAxes.Length; i++)
                {
                    if (i == connectorVoltageAxis)
                    {
                        YAxes[i].MinLimit = AnalogueInput.ConnectorVoltageMinimum;
                        YAxes[i].MaxLimit = AnalogueInput.ConnectorVoltageMaximum;
                    }
                    else if (maxVisibleByAxis != null)
                    {
                        YAxes[i].MinLimit = null;
                        YAxes[i].MaxLimit = maxVisibleByAxis.TryGetValue(i, out double axisMax)
                            ? axisMax + LiveChartYAxisHeadroomAmps
                            : null;
                    }
                }
            }
        }

        private void RestoreLiveChartSeriesFromHistory()
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long cutoffMs = nowMs - (SelectedTimeWindowSeconds * 1000L);

            ResetLiveChartSeries();

            for (int seriesIndex = 0; seriesIndex < SeriesCollection.Count; seriesIndex++)
            {
                if (SeriesCollection[seriesIndex] is not LineSeries<ObservablePoint> lineSeries)
                {
                    continue;
                }

                if (lineSeries.Values is not ObservableCollection<ObservablePoint> values)
                {
                    values = new ObservableCollection<ObservablePoint>();
                    lineSeries.Values = values;
                }

                foreach (ObservablePoint point in _liveSeriesHistory[seriesIndex])
                {
                    if (!point.X.HasValue || point.X.Value < cutoffMs)
                    {
                        continue;
                    }

                    values.Add(new ObservablePoint(point.X.Value, point.Y));
                }
            }

            ApplyLiveChartAxisLimits(nowMs, CalculateLiveChartAxisMaxima());
        }

        private static void TrimLiveSeriesValues(ObservableCollection<ObservablePoint> values, long cutoffMs)
        {
            while (values.Count > 0 && values[0].X < cutoffMs)
            {
                values.RemoveAt(0);
            }

            while (values.Count > MAX_CHART_POINTS)
            {
                values.RemoveAt(0);
            }
        }

        private void UpdateLiveSeriesPoints(int seriesIndex, ObservableCollection<ObservablePoint> values, long nowMs, long cutoffMs, double currentValue, bool appendNewSample)
        {
            EnsureLiveSeriesStateCapacity();

            if (appendNewSample)
            {
                if (_liveSeriesHasSyntheticTail[seriesIndex] && values.Count > 0)
                {
                    ObservablePoint tailPoint = values[^1];
                    tailPoint.X = nowMs;
                    tailPoint.Y = currentValue;
                    _liveSeriesHasSyntheticTail[seriesIndex] = false;
                }
                else
                {
                    long x = nowMs;
                    if (values.Count > 0 && values[^1].X.HasValue)
                    {
                        long lastX = (long)values[^1].X!.Value;
                        if (x <= lastX)
                        {
                            x = lastX + 1;
                        }
                    }

                    values.Add(new ObservablePoint(x, currentValue));
                }
            }
            else
            {
                if (values.Count == 0)
                {
                    return;
                }

                if (_liveSeriesHasSyntheticTail[seriesIndex])
                {
                    ObservablePoint tailPoint = values[^1];
                    tailPoint.X = nowMs;
                    tailPoint.Y = currentValue;
                }
                else
                {
                    long x = nowMs;
                    if (values[^1].X.HasValue)
                    {
                        long lastX = (long)values[^1].X!.Value;
                        if (x <= lastX)
                        {
                            x = lastX + 1;
                        }
                    }

                    values.Add(new ObservablePoint(x, currentValue));
                    _liveSeriesHasSyntheticTail[seriesIndex] = true;
                }
            }

            TrimLiveSeriesValues(values, cutoffMs);
        }

        private void UpdateCharts(DataStructures data, bool appendNewSamples)
        {
            long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            long cutoffMs = nowMs - (SelectedTimeWindowSeconds * 1000L);
            Dictionary<int, double>? maxVisibleByAxis = appendNewSamples ? new Dictionary<int, double>() : null;

            EnsureLiveSeriesStateCapacity();

            for (int i = 0; i < data.ChannelsLiveData.Count && i < SeriesCollection.Count; i++)
            {
                if (SeriesCollection[i] is not LineSeries<ObservablePoint> series)
                    continue;

                if (series.Values is not ObservableCollection<ObservablePoint> values)
                {
                    values = new ObservableCollection<ObservablePoint>();
                    series.Values = values;
                }

                if (series.Stroke is SolidColorPaint paint)
                {
                    paint.StrokeThickness = 1.0f;
                }

                double current = data.ChannelsLiveData[i].CurrentValue;
                if (double.IsNaN(current) || double.IsInfinity(current))
                    continue;

                UpdateLiveSeriesPoints(i, values, nowMs, cutoffMs, current, appendNewSamples);

                if (!series.IsVisible)
                {
                    continue;
                }

                if (maxVisibleByAxis == null)
                {
                    continue;
                }

                foreach (var point in values)
                {
                    if (!point.Y.HasValue)
                        continue;

                    double y = point.Y.Value;
                    if (double.IsNaN(y) || double.IsInfinity(y))
                        continue;

                    if (maxVisibleByAxis.TryGetValue(series.ScalesYAt, out double existingCurrentMax))
                    {
                        maxVisibleByAxis[series.ScalesYAt] = Math.Max(existingCurrentMax, y);
                    }
                    else
                    {
                        maxVisibleByAxis[series.ScalesYAt] = y;
                    }
                }
            }

            int analogueSeriesOffset = data.ChannelsLiveData.Count;
            for (int i = 0; i < data.AnalogueInputsLiveData.Count && analogueSeriesOffset + i < SeriesCollection.Count; i++)
            {
                if (SeriesCollection[analogueSeriesOffset + i] is not LineSeries<ObservablePoint> series)
                {
                    continue;
                }

                if (series.Values is not ObservableCollection<ObservablePoint> values)
                {
                    values = new ObservableCollection<ObservablePoint>();
                    series.Values = values;
                }

                if (series.Stroke is SolidColorPaint paint)
                {
                    paint.StrokeThickness = 1.0f;
                }

                float? analogueValue = GetLiveAnalogueSeriesValue(data.AnalogueInputsLiveData[i]);
                if (!analogueValue.HasValue || float.IsNaN(analogueValue.Value) || float.IsInfinity(analogueValue.Value))
                {
                    continue;
                }

                UpdateLiveSeriesPoints(analogueSeriesOffset + i, values, nowMs, cutoffMs, analogueValue.Value, appendNewSamples);

                if (!series.IsVisible)
                {
                    continue;
                }

                if (maxVisibleByAxis == null)
                {
                    continue;
                }

                foreach (var point in values)
                {
                    if (!point.Y.HasValue)
                    {
                        continue;
                    }

                    double y = point.Y.Value;
                    if (double.IsNaN(y) || double.IsInfinity(y))
                    {
                        continue;
                    }

                    if (maxVisibleByAxis.TryGetValue(series.ScalesYAt, out double existingAnalogueMax))
                    {
                        maxVisibleByAxis[series.ScalesYAt] = Math.Max(existingAnalogueMax, y);
                    }
                    else
                    {
                        maxVisibleByAxis[series.ScalesYAt] = y;
                    }
                }
            }

            ApplyLiveChartAxisLimits(nowMs, appendNewSamples ? maxVisibleByAxis : null);
            RefreshLiveChartSelectionFromAnchor();
        }

        private void UpdateErrorFlags()
        {
            ushort errorFlags = LiveDataView.SystemParams.ErrorFlags;

            OverCurrent = (errorFlags & 0x0001) == 0;
            OverTemperature = (errorFlags & 0x0002) == 0;
            UnderVoltage = (errorFlags & 0x0004) != 0;
            CrcFailed = (errorFlags & 0x0008) != 0;
            SdOK = (errorFlags & 0x0010) == 0;
            GpsOK = (errorFlags & 0x0040) == 0;
        }


        private DataStructures DeepCopyDataStructures(DataStructures source)
        {
            var json = JsonSerializer.Serialize(source);
            return JsonSerializer.Deserialize<DataStructures>(json) ?? new DataStructures();
        }



        public void SendOverrideCommand(OutputChannel channel)
        {
            int channelIndex = LiveDataView.ChannelsLiveData.IndexOf(channel);
            if (channelIndex >= 0)
            {
                _portService?.SendOverrideCommand(channelIndex, channel.Override);
            }
        }



        public void AddLog(
            string message,
            string? details = null,
            Exception? exception = null,
            [CallerMemberName] string callerMemberName = "",
            [CallerFilePath] string callerFilePath = "",
            [CallerLineNumber] int callerLineNumber = 0)
        {
            LoggingService.AddLog(message, details, exception, callerMemberName, callerFilePath, callerLineNumber);
        }

        private void StartBackgroundTask(Task task, string operationName)
        {
            _ = ObserveBackgroundTaskAsync(task, operationName);
        }

        private async Task ObserveBackgroundTaskAsync(Task task, string operationName)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                AddLog($"{operationName} failed: {ex.Message}", exception: ex);
            }
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

        partial void OnIsConnectedChanged(bool value)
        {
            OnPropertyChanged(nameof(IsControllerUiReady));
            OnPropertyChanged(nameof(CanResetLogs));
            OnPropertyChanged(nameof(CanAccessOperationalTabs));
            OnPropertyChanged(nameof(CanOpenFirmwareUpdateDialog));
            OnPropertyChanged(nameof(CanOpenLocalFirmwareUpdateDialog));
            OnPropertyChanged(nameof(CanSetControllerRtc));
            OnPropertyChanged(nameof(CanFactoryReset));
            OnPropertyChanged(nameof(CanManagePinLock));
            OnPropertyChanged(nameof(CanConfigurePinLock));
            OnPropertyChanged(nameof(CanDisablePinLock));
            OnPropertyChanged(nameof(CanTestCellularConnection));
            OnPropertyChanged(nameof(CanProvisionOpenRemote));
            UpdateCustomCanStatusPollingSelection();

            if (!value)
            {
                IsSendingConfig = false;
                IsFactoryResetInProgress = false;
                IsTestingCellularConnection = false;
                IsAutomaticCellularTestInProgress = false;
                IsOpenRemoteProvisioningInProgress = false;
                FactoryResetStatusMessage = string.Empty;
                IsPinLockBusy = false;
                IsPinLockSupported = false;
                IsPinLockEnabled = false;
                PinLockRetryAfterMs = 0;
                PinLockStatusMessage = "PIN lock unavailable.";
                ResetCellularTestStatus();
                _suppressCellularNeedsAttentionUntilUtc = DateTime.MinValue;
                SetCellularConnectionHealthStatus("Offline", shouldLog: true);
                _nextCellularHealthPollUtc = DateTime.MinValue;
                ResetFirmwareUpdateState();
            }
            else
            {
                if (IsConnectedPdmRegistered)
                {
                    SetCellularConnectionHealthStatus("Checking", shouldLog: true);
                    ScheduleCellularHealthPoll(immediate: true);
                }
                else
                {
                    SetCellularConnectionHealthStatus("Offline", shouldLog: false);
                    _nextCellularHealthPollUtc = DateTime.MinValue;
                }

                UpdateFirmwareButtonPresentation();
                StartBackgroundTask(RefreshPinLockStateAsync(), "PIN lock status refresh");
            }
        }

        partial void OnCommsEstablishedChanged(bool value)
        {
            OnPropertyChanged(nameof(IsControllerUiReady));
            OnPropertyChanged(nameof(CanOpenFirmwareUpdateDialog));
            OnPropertyChanged(nameof(CanOpenLocalFirmwareUpdateDialog));
            OnPropertyChanged(nameof(CanSetControllerRtc));
            OnPropertyChanged(nameof(CanFactoryReset));
            OnPropertyChanged(nameof(CanManagePinLock));
            OnPropertyChanged(nameof(CanConfigurePinLock));
            OnPropertyChanged(nameof(CanDisablePinLock));
            OnPropertyChanged(nameof(CanTestCellularConnection));
            OnPropertyChanged(nameof(CanProvisionOpenRemote));
            UpdateCustomCanStatusPollingSelection();

            if (!value)
            {
                _latestFirmwareRelease = null;
                _availableFirmwareRelease = null;
                IsFirmwareUpdateAvailable = false;
                IsCheckingFirmwareUpdate = false;
                _suppressCellularNeedsAttentionUntilUtc = DateTime.MinValue;
                SetCellularConnectionHealthStatus("Offline", shouldLog: true);
                _nextCellularHealthPollUtc = DateTime.MinValue;
                NotifyFirmwareInfoChanged();
                UpdateFirmwareButtonPresentation();
                return;
            }

            if (IsConnectedPdmRegistered)
            {
                SetCellularConnectionHealthStatus("Checking", shouldLog: true);
                ScheduleCellularHealthPoll(immediate: true);
                StartBackgroundTask(PollCellularHealthStatusAsync(), "Cellular health poll");
            }
            else
            {
                SetCellularConnectionHealthStatus("Offline", shouldLog: false);
                _nextCellularHealthPollUtc = DateTime.MinValue;
            }


        }

        partial void OnIsInitialSnapshotLoadedChanged(bool value)
        {
            OnPropertyChanged(nameof(IsControllerUiReady));
            OnPropertyChanged(nameof(CanAccessOperationalTabs));

            if (value && IsConnected && CommsEstablished)
            {
                AddLog("PDM connected.");
            }
        }

        partial void OnIsSettingControllerRtcChanged(bool value)
        {
            OnPropertyChanged(nameof(CanSetControllerRtc));
            OnPropertyChanged(nameof(SetControllerRtcButtonText));
        }

        partial void OnIsFactoryResetInProgressChanged(bool value)
        {
            OnPropertyChanged(nameof(CanFactoryReset));
            OnPropertyChanged(nameof(FactoryResetButtonText));
        }

        partial void OnIsTestingCellularConnectionChanged(bool value)
        {
            OnPropertyChanged(nameof(CanTestCellularConnection));
            OnPropertyChanged(nameof(CellularTestButtonText));
            OnPropertyChanged(nameof(IsCellularTestInProgress));
            UpdateCellularTestProgress(CellularTestStatusItems);
        }

        partial void OnIsAutomaticCellularTestInProgressChanged(bool value)
        {
            OnPropertyChanged(nameof(CanTestCellularConnection));
            OnPropertyChanged(nameof(CellularTestButtonText));
            OnPropertyChanged(nameof(IsCellularTestInProgress));
            UpdateCellularTestProgress(CellularTestStatusItems);
        }

        partial void OnIsOpenRemoteProvisioningInProgressChanged(bool value)
        {
            OnPropertyChanged(nameof(CanProvisionOpenRemote));
            OnPropertyChanged(nameof(OpenRemoteProvisioningButtonText));
            OnPropertyChanged(nameof(CanRenamePdm));
            OnPropertyChanged(nameof(CanUnregisterPdm));
        }

        partial void OnLogRangeStartDateChanged(DateTimeOffset? value)
        {
            ApplyLogFilters();
            NotifyLogMapDataChanged();
            NotifyLogMapRouteChanged();
        }

        partial void OnLogRangeStartTimeChanged(TimeSpan? value)
        {
            ApplyLogFilters();
            NotifyLogMapDataChanged();
            NotifyLogMapRouteChanged();
        }

        partial void OnLogRangeEndDateChanged(DateTimeOffset? value)
        {
            ApplyLogFilters();
            NotifyLogMapDataChanged();
            NotifyLogMapRouteChanged();
        }

        partial void OnLogRangeEndTimeChanged(TimeSpan? value)
        {
            ApplyLogFilters();
            NotifyLogMapDataChanged();
            NotifyLogMapRouteChanged();
        }

        partial void OnIsLiveCrosshairEnabledChanged(bool value)
        {
            UpdateLiveCrosshairState();
        }

        [RelayCommand]
        private void ApplyLogFilters()
        {
            if (_parsedLogRows.Count == 0)
            {
                LogMetricRows = new ObservableCollection<LogMetricRow>();
                ClearActiveLogSeries();
                return;
            }

            DateTimeOffset start = ComposeDateTime(LogRangeStartDate, LogRangeStartTime) ?? _parsedLogRows.First().Timestamp;
            DateTimeOffset end = ComposeDateTime(LogRangeEndDate, LogRangeEndTime) ?? _parsedLogRows.Last().Timestamp;

            if (end < start)
            {
                LogMetricRows = new ObservableCollection<LogMetricRow>();
                LogStatusMessage = "End time is before start time.";
                ClearActiveLogSeries();
                return;
            }

            var filteredRows = _parsedLogRows
                .Where(row => row.Timestamp >= start && row.Timestamp <= end)
                .ToList();

            UpdateLogMetrics(filteredRows);

            var selectedSystemKeys = SystemParameterSelections
                .Where(p => p.IsSelected)
                .Select(p => p.Key)
                .ToList();

            var selectedChannels = ChannelSelections
                .Where(selection => selection.IsSelected)
                .Select(selection => selection.ChannelNumber)
                .ToList();

            var selectedChannelFields = ChannelFieldSelections
                .Where(selection => selection.IsSelected)
                .Select(selection => selection.Key)
                .ToList();

            var selectedChannelKeys = new List<string>();
            foreach (int channelNumber in selectedChannels)
            {
                foreach (string fieldName in selectedChannelFields)
                {
                    selectedChannelKeys.Add($"CH{channelNumber}.{fieldName}");
                }
            }

            var selectedInputKeys = DigitalInputSelections
                .Where(selection => selection.IsSelected)
                .Select(selection => selection.Key)
                .Concat(AnalogueInputSelections
                    .Where(selection => selection.IsSelected)
                    .Select(selection => selection.Key))
                .ToList();

            var selectedKeys = selectedSystemKeys
                .Concat(selectedChannelKeys)
                .Concat(selectedInputKeys)
                .ToList();

            if (selectedKeys.Count == 0)
            {
                LogStatusMessage = "Select at least one parameter to chart.";
                ClearActiveLogSeries();
                return;
            }

            double startMs = start.ToUnixTimeMilliseconds();
            double endMs = end.ToUnixTimeMilliseconds();
            var filteredSeries = new Dictionary<string, List<LogSeriesPoint>>(StringComparer.OrdinalIgnoreCase);

            foreach (string key in selectedKeys)
            {
                if (!_parsedLogSeries.TryGetValue(key, out var sourcePoints) || sourcePoints.Count == 0)
                {
                    continue;
                }

                var visiblePoints = SliceLogSeries(sourcePoints, startMs, endMs);
                if (visiblePoints.Count > 0)
                {
                    filteredSeries[key] = visiblePoints;
                }
            }

            if (filteredSeries.Count == 0)
            {
                LogStatusMessage = "No data points in selected range.";
                ClearActiveLogSeries();
                return;
            }

            var systemDisplayNames = SystemParameterSelections.ToDictionary(
                selection => selection.Key,
                selection => selection.DisplayName);

            _activeFilteredLogSeries = filteredSeries;
            _activeLogSeriesKeys = selectedKeys.Where(filteredSeries.ContainsKey).ToList();
            _activeSystemDisplayNames = systemDisplayNames;
            _activeLogFilterStartMs = startMs;
            _activeLogFilterEndMs = endMs;
            _lastRenderedLogViewportStartMs = double.NaN;
            _lastRenderedLogViewportEndMs = double.NaN;

            if (LogXAxes is { Length: > 0 })
            {
                LogXAxes[0].MinLimit = startMs;
                LogXAxes[0].MaxLimit = endMs;
            }

            RebuildDisplayedLogSeries(startMs, endMs);
        }

        private void UpdateLogMetrics(IReadOnlyList<ParsedLogRow> filteredRows)
        {
            if (filteredRows.Count == 0)
            {
                LogMetricRows = new ObservableCollection<LogMetricRow>();
                return;
            }

            string speedUnit = GetLogMetricUnit("System.Speed", SpeedUnit);
            string distanceUnit = GetDistanceMetricUnit(speedUnit);
            string currentUnit = GetLogMetricUnit("System.System Current", "A");
            string voltageUnit = GetLogMetricUnit("System.System Voltage", "V");
            string temperatureUnit = GetLogMetricUnit("System.System Temp", "°C");
            string simTemperatureUnit = GetLogMetricUnit("System.SIM Module Temp", "°C");
            string imuTemperatureUnit = GetLogMetricUnit("System.IMU Temp", "°C");

            var metrics = new List<LogMetricRow>();

            AddLogMetricText(metrics, "Log duration", FormatLogDuration(filteredRows));
            AddLogMetricText(metrics, "Data points", filteredRows.Count.ToString("N0", CultureInfo.InvariantCulture));

            AddLogMetric(metrics, "Max. speed", GetMaxMetricValue(filteredRows, "System.Speed"), speedUnit, 1);
            AddLogMetric(metrics, "Average speed", GetAverageMetricValue(filteredRows, "System.Speed"), speedUnit, 1);
            AddLogMetric(metrics, "Distance travelled", CalculateDistanceTravelled(filteredRows), distanceUnit, 2);

            AddLogMetric(metrics, "Max. system current", GetMaxMetricValue(filteredRows, "System.System Current"), currentUnit, 1);
            AddLogMetric(metrics, "Average system current", GetAverageMetricValue(filteredRows, "System.System Current"), currentUnit, 1);
            AddLogMetric(metrics, "Min. system voltage", GetMinMetricValue(filteredRows, "System.System Voltage"), voltageUnit, 2);
            AddLogMetric(metrics, "Max. system voltage", GetMaxMetricValue(filteredRows, "System.System Voltage"), voltageUnit, 2);
            AddLogMetric(metrics, "Average system voltage", GetAverageMetricValue(filteredRows, "System.System Voltage"), voltageUnit, 2);

            AddLogMetric(metrics, "Max. system temp.", GetMaxMetricValue(filteredRows, "System.System Temp"), temperatureUnit, 1);
            AddLogMetric(metrics, "Min. system temp.", GetMinMetricValue(filteredRows, "System.System Temp"), temperatureUnit, 1);
            AddLogMetric(metrics, "Average system temp.", GetAverageMetricValue(filteredRows, "System.System Temp"), temperatureUnit, 1);
            AddLogMetric(metrics, "Max. SIM module temp.", GetMaxMetricValue(filteredRows, "System.SIM Module Temp"), simTemperatureUnit, 1);
            AddLogMetric(metrics, "Min. SIM module temp.", GetMinMetricValue(filteredRows, "System.SIM Module Temp"), simTemperatureUnit, 1);
            AddLogMetric(metrics, "Average SIM module temp.", GetAverageMetricValue(filteredRows, "System.SIM Module Temp"), simTemperatureUnit, 1);
            AddLogMetric(metrics, "Max. IMU temp.", GetMaxMetricValue(filteredRows, "System.IMU Temp"), imuTemperatureUnit, 1);
            AddLogMetric(metrics, "Min. IMU temp.", GetMinMetricValue(filteredRows, "System.IMU Temp"), imuTemperatureUnit, 1);
            AddLogMetric(metrics, "Average IMU temp.", GetAverageMetricValue(filteredRows, "System.IMU Temp"), imuTemperatureUnit, 1);

            LogMetricRows = new ObservableCollection<LogMetricRow>(metrics);
        }

        private static void AddLogMetric(ICollection<LogMetricRow> metrics, string metric, double? value, string unit, int decimals)
        {
            metrics.Add(new LogMetricRow
            {
                Metric = metric,
                Value = FormatLogMetricValue(value, unit, decimals),
            });
        }

        private static void AddLogMetricText(ICollection<LogMetricRow> metrics, string metric, string value)
        {
            metrics.Add(new LogMetricRow
            {
                Metric = metric,
                Value = value,
            });
        }

        private static string FormatLogMetricValue(double? value, string unit, int decimals)
        {
            if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
            {
                return "—";
            }

            string number = value.Value.ToString($"F{decimals}", CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(unit)
                ? number
                : $"{number} {unit}";
        }

        private static double? GetMaxMetricValue(IEnumerable<ParsedLogRow> rows, string key)
        {
            var values = GetMetricValues(rows, key).ToList();
            return values.Count == 0 ? null : values.Max();
        }

        private static double? GetMinMetricValue(IEnumerable<ParsedLogRow> rows, string key)
        {
            var values = GetMetricValues(rows, key).ToList();
            return values.Count == 0 ? null : values.Min();
        }

        private static double? GetAverageMetricValue(IEnumerable<ParsedLogRow> rows, string key)
        {
            var values = GetMetricValues(rows, key).ToList();
            return values.Count == 0 ? null : values.Average();
        }

        private static IEnumerable<double> GetMetricValues(IEnumerable<ParsedLogRow> rows, string key)
        {
            foreach (var row in rows)
            {
                if (row.NumericValues.TryGetValue(key, out double value))
                {
                    yield return value;
                }
            }
        }

        private string GetLogMetricUnit(string key, string fallbackUnit)
        {
            return TryGetLogSeriesUnit(key, out string? unit) && !string.IsNullOrWhiteSpace(unit)
                ? unit
                : fallbackUnit;
        }

        private static string GetDistanceMetricUnit(string speedUnit)
        {
            if (speedUnit.Contains("mph", StringComparison.OrdinalIgnoreCase))
            {
                return "miles";
            }

            if (speedUnit.Contains("km", StringComparison.OrdinalIgnoreCase) ||
                speedUnit.Contains("kph", StringComparison.OrdinalIgnoreCase))
            {
                return "km";
            }

            return string.Empty;
        }

        private static string FormatLogDuration(IReadOnlyList<ParsedLogRow> rows)
        {
            if (rows.Count == 0)
            {
                return "—";
            }

            TimeSpan duration = rows[^1].Timestamp - rows[0].Timestamp;
            if (duration < TimeSpan.Zero)
            {
                duration = TimeSpan.Zero;
            }

            return duration.TotalDays >= 1
                ? $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}"
                : duration.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
        }

        private static double? CalculateDistanceTravelled(IReadOnlyList<ParsedLogRow> rows)
        {
            double distance = 0;
            bool hasDistance = false;
            double? previousSpeed = null;
            DateTimeOffset previousTimestamp = default;

            foreach (var row in rows)
            {
                if (!row.NumericValues.TryGetValue("System.Speed", out double speed))
                {
                    continue;
                }

                if (previousSpeed.HasValue)
                {
                    double elapsedHours = (row.Timestamp - previousTimestamp).TotalHours;
                    if (elapsedHours > 0)
                    {
                        distance += ((previousSpeed.Value + speed) / 2.0) * elapsedHours;
                        hasDistance = true;
                    }
                }

                previousSpeed = speed;
                previousTimestamp = row.Timestamp;
            }

            return hasDistance ? distance : null;
        }

        private void BuildLogParameterSelections()
        {
            SystemParameterSelections = new ObservableCollection<LogParameterSelection>(
                SystemHeaderFields
                .Skip(2)
                .Select(field => new LogParameterSelection
                {
                    Key = $"System.{field}",
                    DisplayName = field,
                    IsSelected = false,
                }));

            var selectableChannels = new List<LogChannelSelection>();
            for (int i = 0; i < Constants.NUM_OUTPUT_CHANNELS; i++)
            {
                selectableChannels.Add(new LogChannelSelection
                {
                    ChannelNumber = i + 1,
                    IsSelected = false,
                });
            }

            ChannelSelections = new ObservableCollection<LogChannelSelection>(selectableChannels);

            var channelFields = ChannelHeaderFields
                .Where(field => field != "Channel Type" && field != "Analogue Input")
                .Select(field => new LogParameterSelection
                {
                    Key = field,
                    DisplayName = field,
                    IsSelected = false,
                })
                .ToList();

            ChannelFieldSelections = new ObservableCollection<LogParameterSelection>(channelFields);

            DigitalInputSelections = new ObservableCollection<LogParameterSelection>(
                DigitalInputHeaderFields
                    .Select((field, index) => new LogParameterSelection
                    {
                        Key = $"DI{index + 1}",
                        DisplayName = field,
                        IsSelected = false,
                    }));

            AnalogueInputSelections = new ObservableCollection<LogParameterSelection>(
                AnalogueInputHeaderFields
                    .Select((field, index) => new LogParameterSelection
                    {
                        Key = $"AI{index + 1}",
                        DisplayName = field,
                        IsSelected = false,
                    }));

            foreach (var selection in SystemParameterSelections)
            {
                selection.PropertyChanged += LogSelection_PropertyChanged;
            }

            foreach (var selection in ChannelFieldSelections)
            {
                selection.PropertyChanged += LogSelection_PropertyChanged;
            }

            foreach (var selection in DigitalInputSelections)
            {
                selection.PropertyChanged += LogSelection_PropertyChanged;
            }

            foreach (var selection in AnalogueInputSelections)
            {
                selection.PropertyChanged += LogSelection_PropertyChanged;
            }

            foreach (var selection in ChannelSelections)
            {
                selection.PropertyChanged += LogChannelSelection_PropertyChanged;
            }

            RefreshLogSelectionMasterStates();
        }

        private void LogSelection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LogParameterSelection.IsSelected))
            {
                if (_bulkUpdatingLogSelections)
                {
                    return;
                }

                RefreshLogSelectionMasterStates();
                ApplyLogFilters();
                NotifyLogMapDataChanged();
            }
        }

        private void LogChannelSelection_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LogChannelSelection.IsSelected))
            {
                if (_bulkUpdatingLogSelections)
                {
                    return;
                }

                RefreshLogSelectionMasterStates();
                ApplyLogFilters();
                NotifyLogMapDataChanged();
            }
        }

        private void RefreshLogSelectionMasterStates()
        {
            _updatingLogSelectionMasterState = true;
            try
            {
                AreAllSystemParametersSelected = SystemParameterSelections.Count > 0 && SystemParameterSelections.All(selection => selection.IsSelected);
                AreAllChannelsSelected = ChannelSelections.Count > 0 && ChannelSelections.All(selection => selection.IsSelected);
                AreAllChannelFieldsSelected = ChannelFieldSelections.Count > 0 && ChannelFieldSelections.All(selection => selection.IsSelected);
                AreAllDigitalInputsSelected = DigitalInputSelections.Count > 0 && DigitalInputSelections.All(selection => selection.IsSelected);
                AreAllAnalogueInputsSelected = AnalogueInputSelections.Count > 0 && AnalogueInputSelections.All(selection => selection.IsSelected);
            }
            finally
            {
                _updatingLogSelectionMasterState = false;
            }
        }

        private void SetAllSelections(IEnumerable<LogParameterSelection> selections, bool isSelected)
        {
            _bulkUpdatingLogSelections = true;
            try
            {
                foreach (var selection in selections)
                {
                    selection.IsSelected = isSelected;
                }
            }
            finally
            {
                _bulkUpdatingLogSelections = false;
            }

            RefreshLogSelectionMasterStates();
            ApplyLogFilters();
            NotifyLogMapDataChanged();
        }

        private void SetAllSelections(IEnumerable<LogChannelSelection> selections, bool isSelected)
        {
            _bulkUpdatingLogSelections = true;
            try
            {
                foreach (var selection in selections)
                {
                    selection.IsSelected = isSelected;
                }
            }
            finally
            {
                _bulkUpdatingLogSelections = false;
            }

            RefreshLogSelectionMasterStates();
            ApplyLogFilters();
            NotifyLogMapDataChanged();
        }

        private string ResolveDisplayNameForKey(string key, IReadOnlyDictionary<string, string> systemDisplayNames)
        {
            if (systemDisplayNames.TryGetValue(key, out var systemName))
            {
                return systemName;
            }

            if (TryParseIndexedInputKey(key, 'D', out int digitalInputNumber))
            {
                return $"Digital Input {digitalInputNumber}";
            }

            if (TryParseIndexedInputKey(key, 'A', out int analogueInputNumber))
            {
                return $"Analogue Input {analogueInputNumber}";
            }

            var parts = key.Split('.', 2);
            if (parts.Length == 2)
            {
                return $"{parts[0]} {parts[1]}";
            }

            return key;
        }

        private void InitializeDefaultLogRange()
        {
            var now = DateTimeOffset.Now;
            LogRangeStartDate = now.Date;
            LogRangeStartTime = TimeSpan.Zero;
            LogRangeEndDate = now.Date;
            LogRangeEndTime = now.TimeOfDay;
        }

        private void ParseLogContent(string csvContent)
        {
            _parsedLogRows.Clear();
            _parsedLogSeries.Clear();
            _parsedLogSeriesUnits.Clear();
            ClearActiveLogSeries(resetAxisLimits: false);
            LogMapGridRows = new ObservableCollection<LogMapGridRow>();
            LogMapInspectionRows = new ObservableCollection<LogMapInspectionRow>();

            if (string.IsNullOrWhiteSpace(csvContent))
            {
                NotifyLogMapDataChanged();
                NotifyLogMapRouteChanged();
                return;
            }

            var lines = csvContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length <= 1)
            {
                NotifyLogMapDataChanged();
                NotifyLogMapRouteChanged();
                return;
            }

            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                var columns = lines[lineIndex].Split(',');
                int expectedColumnCount = SystemHeaderFields.Length +
                                          (Constants.NUM_OUTPUT_CHANNELS * ChannelHeaderFields.Length) +
                                          DigitalInputHeaderFields.Length +
                                          AnalogueInputHeaderFields.Length;

                if (columns.Length < expectedColumnCount)
                {
                    continue;
                }

                if (!TryParseTimestamp(columns[0], columns[1], out var timestamp))
                {
                    continue;
                }

                var row = new ParsedLogRow
                {
                    Timestamp = timestamp,
                };

                for (int i = 2; i < SystemHeaderFields.Length && i < columns.Length; i++)
                {
                    if (TryParseNumeric(columns[i], out double value, out string? unit))
                    {
                        string key = $"System.{SystemHeaderFields[i]}";
                        row.NumericValues[key] = value;
                        AddParsedLogPoint(key, timestamp, value, unit);
                    }
                }

                int channelStartIndex = SystemHeaderFields.Length;
                for (int channel = 0; channel < Constants.NUM_OUTPUT_CHANNELS; channel++)
                {
                    for (int field = 0; field < ChannelHeaderFields.Length; field++)
                    {
                        if (ChannelHeaderFields[field] == "Channel Type" || ChannelHeaderFields[field] == "Analogue Input")
                        {
                            continue;
                        }

                        int columnIndex = channelStartIndex + (channel * ChannelHeaderFields.Length) + field;
                        if (columnIndex >= columns.Length)
                        {
                            break;
                        }

                        if (TryParseNumeric(columns[columnIndex], out double value, out string? unit))
                        {
                            string key = $"CH{channel + 1}.{ChannelHeaderFields[field]}";
                            row.NumericValues[key] = value;
                            AddParsedLogPoint(key, timestamp, value, unit);
                        }
                    }
                }

                int digitalInputStartIndex = channelStartIndex + (Constants.NUM_OUTPUT_CHANNELS * ChannelHeaderFields.Length);
                for (int digitalInput = 0; digitalInput < DigitalInputHeaderFields.Length; digitalInput++)
                {
                    int columnIndex = digitalInputStartIndex + digitalInput;
                    if (columnIndex >= columns.Length)
                    {
                        break;
                    }

                    if (TryParseNumeric(columns[columnIndex], out double value, out string? unit))
                    {
                        string key = $"DI{digitalInput + 1}";
                        row.NumericValues[key] = value;
                        AddParsedLogPoint(key, timestamp, value, unit);
                    }
                }

                int analogueInputStartIndex = digitalInputStartIndex + DigitalInputHeaderFields.Length;
                for (int analogueInput = 0; analogueInput < AnalogueInputHeaderFields.Length; analogueInput++)
                {
                    int columnIndex = analogueInputStartIndex + analogueInput;
                    if (columnIndex >= columns.Length)
                    {
                        break;
                    }

                    if (TryParseNumeric(columns[columnIndex], out double value, out string? unit))
                    {
                        string key = $"AI{analogueInput + 1}";
                        row.NumericValues[key] = value;
                        AddParsedLogPoint(key, timestamp, value, unit);
                    }
                }

                _parsedLogRows.Add(row);
            }

            if (_parsedLogRows.Count > 0)
            {
                LogRangeStartDate = _parsedLogRows.First().Timestamp.Date;
                LogRangeStartTime = _parsedLogRows.First().Timestamp.TimeOfDay;
                LogRangeEndDate = _parsedLogRows.Last().Timestamp.Date;
                LogRangeEndTime = _parsedLogRows.Last().Timestamp.TimeOfDay;
            }

            NotifyLogMapDataChanged();
            NotifyLogMapRouteChanged();
        }

        private static IReadOnlyList<string> BuildExpectedLogHeaderColumns()
        {
            var columns = new List<string>(
                SystemHeaderFields.Length +
                (Constants.NUM_OUTPUT_CHANNELS * ChannelHeaderFields.Length) +
                DigitalInputHeaderFields.Length +
                AnalogueInputHeaderFields.Length);

            columns.AddRange(SystemHeaderFields);
            for (int channel = 0; channel < Constants.NUM_OUTPUT_CHANNELS; channel++)
            {
                columns.AddRange(ChannelHeaderFields);
            }

            columns.AddRange(DigitalInputHeaderFields);
            columns.AddRange(AnalogueInputHeaderFields);
            return columns;
        }

        private static bool MatchesExpectedLogHeader(IReadOnlyList<string> actualHeader)
        {
            IReadOnlyList<string> expectedHeader = BuildExpectedLogHeaderColumns();
            if (actualHeader.Count < expectedHeader.Count)
            {
                return false;
            }

            for (int index = 0; index < expectedHeader.Count; index++)
            {
                if (!string.Equals(actualHeader[index].Trim(), expectedHeader[index], StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }

        private static void EnsureValidLogContent(string csvContent)
        {
            if (!TryValidateLogContent(csvContent, out string validationError))
            {
                throw new InvalidDataException(validationError);
            }
        }

        private static bool TryValidateLogContent(string csvContent, out string validationError)
        {
            if (string.IsNullOrWhiteSpace(csvContent))
            {
                validationError = "Selected file is empty or not a valid Synapse PDM log.";
                return false;
            }

            string[] lines = csvContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0)
            {
                validationError = "Selected file is empty or not a valid Synapse PDM log.";
                return false;
            }

            string[] actualHeader = lines[0].Split(',');
            if (!MatchesExpectedLogHeader(actualHeader))
            {
                validationError = "Selected file is not a valid Synapse PDM log format.";
                return false;
            }

            validationError = string.Empty;
            return true;
        }

        private static bool TryParseTimestamp(string datePart, string timePart, out DateTimeOffset timestamp)
        {
            string combined = $"{datePart.Trim()} {timePart.Trim()}";
            if (DateTimeOffset.TryParse(combined, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out timestamp))
            {
                return true;
            }

            return DateTimeOffset.TryParse(combined, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out timestamp);
        }

        private static bool TryParseNumeric(string input, out double value, out string? unit)
        {
            unit = null;
            if (double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                double.TryParse(input, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                return true;
            }

            Match match = NumericWithUnitRegex.Match(input);
            if (!match.Success)
            {
                value = default;
                return false;
            }

            string numericValue = match.Groups["value"].Value;
            if (!double.TryParse(numericValue, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !double.TryParse(numericValue, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                return false;
            }

            string parsedUnit = match.Groups["unit"].Value.Trim();
            unit = string.IsNullOrWhiteSpace(parsedUnit) || parsedUnit == "-"
                ? null
                : NormalizeLoggedUnit(parsedUnit);
            return true;
        }

        private static DateTimeOffset? ComposeDateTime(DateTimeOffset? date, TimeSpan? time)
        {
            if (date == null)
            {
                return null;
            }

            return date.Value.Date + (time ?? TimeSpan.Zero);
        }

        private static bool TryComposeControllerDateTime(DateTimeOffset? date, TimeSpan? time, TimeZoneInfo? timeZone, out DateTimeOffset controllerDateTime, out string? error)
        {
            controllerDateTime = default;
            error = null;

            if (date == null)
            {
                error = "Select a controller date before setting the clock.";
                return false;
            }

            DateTime localDateTime = date.Value.DateTime.Date + (time ?? TimeSpan.Zero);
            TimeZoneInfo effectiveTimeZone = timeZone ?? TimeZoneInfo.Local;

            if (effectiveTimeZone.IsInvalidTime(localDateTime))
            {
                error = "The selected local time does not exist in the chosen time zone because of a DST transition.";
                return false;
            }

            TimeSpan offset = effectiveTimeZone.GetUtcOffset(localDateTime);
            if (effectiveTimeZone.IsAmbiguousTime(localDateTime))
            {
                offset = effectiveTimeZone.GetAmbiguousTimeOffsets(localDateTime)
                    .OrderByDescending(value => value)
                    .First();
            }

            controllerDateTime = new DateTimeOffset(localDateTime, offset);
            return true;
        }

        private static IReadOnlyList<TimeZoneDisplay> BuildTimeZoneOptions()
        {
            var options = new List<TimeZoneDisplay>();

            try
            {
                foreach (TimeZoneInfo timeZone in TimeZoneInfo.GetSystemTimeZones())
                {
                    try
                    {
                        options.Add(CreateTimeZoneDisplay(timeZone, DateTimeOffset.Now));
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"Skipping invalid time zone '{timeZone.Id}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to enumerate system time zones: {ex.Message}");
            }

            if (options.Count == 0)
            {
                TimeZoneInfo fallbackZone = TimeZoneInfo.Local;
                options.Add(new TimeZoneDisplay(
                    fallbackZone.Id,
                    $"(UTC{FormatOffsetLabel((int)Math.Round(fallbackZone.BaseUtcOffset.TotalMinutes))}) {fallbackZone.DisplayName}",
                    fallbackZone,
                    (int)Math.Round(fallbackZone.BaseUtcOffset.TotalMinutes),
                    false,
                    "Fell back to the local system time zone."));
            }

            return options
                .OrderBy(option => option.BaseOffsetMinutes)
                .ThenBy(option => option.Label, StringComparer.CurrentCulture)
                .ToList();
        }

        private static TimeZoneDisplay CreateTimeZoneDisplay(TimeZoneInfo timeZone, DateTimeOffset reference)
        {
            bool supported = TryBuildTimeZoneRuleBlob(timeZone, reference, out _, out string? error);
            int baseOffsetMinutes = (int)Math.Round(timeZone.BaseUtcOffset.TotalMinutes);
            string offsetLabel = FormatOffsetLabel(baseOffsetMinutes);
            string label = $"(UTC{offsetLabel}) {timeZone.DisplayName}";
            if (!supported)
            {
                label += " [DST rule unsupported]";
            }

            return new TimeZoneDisplay(timeZone.Id, label, timeZone, baseOffsetMinutes, supported, error);
        }

        private static string FormatOffsetLabel(int offsetMinutes)
        {
            int absoluteMinutes = Math.Abs(offsetMinutes);
            int hours = absoluteMinutes / 60;
            int minutes = absoluteMinutes % 60;
            return $"{(offsetMinutes >= 0 ? "+" : "-")}{hours:00}:{minutes:00}";
        }

        private void SyncSelectedTimeZoneFromSettings()
        {
            string? timeZoneId = SettingsDataView.SystemParamsStaticData.TimeZoneId;
            byte[] timeZoneRule = SettingsDataView.SystemParamsStaticData.TimeZoneRule ?? Array.Empty<byte>();
            TimeZoneDisplay? target = null;
            bool hasRule = HasTimeZoneRuleBlob(timeZoneRule);

            if (!string.IsNullOrWhiteSpace(timeZoneId))
            {
                TimeZoneDisplay? idMatch = TimeZones.FirstOrDefault(option => string.Equals(option.Id, timeZoneId, StringComparison.OrdinalIgnoreCase));
                if (idMatch != null && (!hasRule || DoesTimeZoneMatchRule(idMatch, timeZoneRule)))
                {
                    target = idMatch;
                }
            }

            if (target == null && hasRule)
            {
                target = FindTimeZoneDisplayByRule(timeZoneRule, timeZoneId);
                if (target == null)
                {
                    ApplyResolvedTimeZoneSelection(null);
                    return;
                }
            }

            if (target == null)
            {
                string? localTimeZoneId = TryGetLocalTimeZoneId();
                if (!string.IsNullOrWhiteSpace(localTimeZoneId))
                {
                    target = TimeZones.FirstOrDefault(option => string.Equals(option.Id, localTimeZoneId, StringComparison.OrdinalIgnoreCase));
                }
            }

            target ??= TimeZones.FirstOrDefault();
            ApplyResolvedTimeZoneSelection(target);
        }

        private void ApplyResolvedTimeZoneSelection(TimeZoneDisplay? target)
        {
            try
            {
                _suppressTimeZoneSelectionWriteBack = true;
                if (!ReferenceEquals(SelectedTimeZoneDisplay, target))
                {
                    SelectedTimeZoneDisplay = target;
                }
            }
            finally
            {
                _suppressTimeZoneSelectionWriteBack = false;
            }

            SettingsDataView.SystemParamsStaticData.TimeZoneId = target?.Id;
            if (target != null)
            {
                UpdateSelectedTimeZoneRule();
            }
        }

        private void UpdateSelectedTimeZoneRule()
        {
            if (SelectedTimeZoneDisplay == null)
            {
                if (!HasTimeZoneRuleBlob(SettingsDataView.SystemParamsStaticData.TimeZoneRule))
                {
                    SettingsDataView.SystemParamsStaticData.TimeZoneRule = Array.Empty<byte>();
                }

                return;
            }

            DateTimeOffset reference = ControllerRtcDate ?? DateTimeOffset.Now;
            if (TryBuildTimeZoneRuleBlob(SelectedTimeZoneDisplay, reference, out byte[] timeZoneRule, out _))
            {
                SettingsDataView.SystemParamsStaticData.TimeZoneRule = timeZoneRule;
            }
            else
            {
                SettingsDataView.SystemParamsStaticData.TimeZoneRule = Array.Empty<byte>();
            }
        }

        private bool DoesTimeZoneMatchRule(TimeZoneDisplay timeZoneDisplay, byte[] expectedRule)
        {
            DateTimeOffset reference = ControllerRtcDate ?? DateTimeOffset.Now;
            return TryBuildTimeZoneRuleBlob(timeZoneDisplay, reference, out byte[] candidateRule, out _)
                && ByteArraysEqual(candidateRule, expectedRule);
        }

        private TimeZoneDisplay? FindTimeZoneDisplayByRule(byte[] expectedRule, string? preferredId)
        {
            List<TimeZoneDisplay> matches = TimeZones
                .Where(option => DoesTimeZoneMatchRule(option, expectedRule))
                .ToList();

            if (matches.Count == 0)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(preferredId))
            {
                TimeZoneDisplay? preferredMatch = matches.FirstOrDefault(option => string.Equals(option.Id, preferredId, StringComparison.OrdinalIgnoreCase));
                if (preferredMatch != null)
                {
                    return preferredMatch;
                }
            }

            string? localTimeZoneId = TryGetLocalTimeZoneId();
            if (!string.IsNullOrWhiteSpace(localTimeZoneId))
            {
                TimeZoneDisplay? localMatch = matches.FirstOrDefault(option => string.Equals(option.Id, localTimeZoneId, StringComparison.OrdinalIgnoreCase));
                if (localMatch != null)
                {
                    return localMatch;
                }
            }

            return matches.FirstOrDefault();
        }

        private static string? TryGetLocalTimeZoneId()
        {
            try
            {
                return TimeZoneInfo.Local.Id;
            }
            catch
            {
                return null;
            }
        }

        private static bool HasTimeZoneRuleBlob(byte[]? timeZoneRule)
        {
            return timeZoneRule != null && timeZoneRule.Length == Constants.TIME_ZONE_RULE_LENGTH;
        }

        private static bool ByteArraysEqual(byte[]? left, byte[]? right)
        {
            if (ReferenceEquals(left, right))
            {
                return true;
            }

            if (left == null || right == null || left.Length != right.Length)
            {
                return false;
            }

            for (int i = 0; i < left.Length; i++)
            {
                if (left[i] != right[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static bool TryBuildTimeZoneRuleBlob(TimeZoneDisplay? selectedTimeZone, DateTimeOffset reference, out byte[] timeZoneRule, out string? error)
        {
            if (selectedTimeZone == null)
            {
                timeZoneRule = Array.Empty<byte>();
                error = "Select a time zone before setting the controller clock.";
                return false;
            }

            return TryBuildTimeZoneRuleBlob(selectedTimeZone.TimeZone, reference, out timeZoneRule, out error);
        }

        private static bool TryBuildTimeZoneRuleBlob(TimeZoneInfo timeZone, DateTimeOffset reference, out byte[] timeZoneRule, out string? error)
        {
            timeZoneRule = new byte[Constants.TIME_ZONE_RULE_LENGTH];
            error = null;

            try
            {
                int standardOffsetMinutes = (int)Math.Round(timeZone.BaseUtcOffset.TotalMinutes);
                if (standardOffsetMinutes < -720 || standardOffsetMinutes > 840)
                {
                    error = $"Time zone {timeZone.DisplayName} uses an unsupported UTC offset.";
                    return false;
                }

                BitConverter.GetBytes((short)standardOffsetMinutes).CopyTo(timeZoneRule, 0);

                TimeZoneInfo.AdjustmentRule? adjustmentRule = timeZone.GetAdjustmentRules()
                    .LastOrDefault(rule => reference.Date >= rule.DateStart && reference.Date <= rule.DateEnd);

                if (!timeZone.SupportsDaylightSavingTime || adjustmentRule == null || adjustmentRule.DaylightDelta == TimeSpan.Zero)
                {
                    return true;
                }

                int daylightDeltaMinutes = (int)Math.Round(adjustmentRule.DaylightDelta.TotalMinutes);
                if (daylightDeltaMinutes <= 0 || daylightDeltaMinutes > 180)
                {
                    error = $"Time zone {timeZone.DisplayName} uses an unsupported DST offset.";
                    return false;
                }

                BitConverter.GetBytes((short)daylightDeltaMinutes).CopyTo(timeZoneRule, 2);
                timeZoneRule[4] = 1;
                EncodeTransition(adjustmentRule.DaylightTransitionStart, timeZoneRule, 5);
                EncodeTransition(adjustmentRule.DaylightTransitionEnd, timeZoneRule, 10);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Time zone {timeZone.Id} could not be read: {ex.Message}";
                Array.Clear(timeZoneRule, 0, timeZoneRule.Length);
                return false;
            }
        }

        private static void EncodeTransition(TimeZoneInfo.TransitionTime transition, byte[] destination, int startIndex)
        {
            destination[startIndex] = (byte)transition.Month;
            if (transition.IsFixedDateRule)
            {
                destination[startIndex + 1] = (byte)(TimeZoneFixedDateFlag | (transition.Day & TimeZoneDayMask));
                destination[startIndex + 2] = 0;
            }
            else
            {
                destination[startIndex + 1] = (byte)transition.Week;
                destination[startIndex + 2] = (byte)transition.DayOfWeek;
            }

            destination[startIndex + 3] = (byte)transition.TimeOfDay.Hour;
            destination[startIndex + 4] = (byte)transition.TimeOfDay.Minute;
        }

        private static string BuildLogCacheKey(LogFile file)
        {
            string identity = file.IsControllerFile
                ? $"controller:{file.FileName}"
                : $"local:{(string.IsNullOrWhiteSpace(file.FullPath) ? file.FileName : file.FullPath)}";

            return $"{identity}|{file.FileSizeBytes}";
        }

        private CanIdOption? GetCanIdOption(ushort value)
        {
            int index = Math.Clamp((int)value, 0, 0x7FF);
            return AvailableCanIds.ElementAtOrDefault(index);
        }

        private CanBitrateOption? GetCanBitrateOption(uint value)
        {
            CanBitrateOption? match = AvailableCanBitrates.FirstOrDefault(option => option.Value == value);
            return match ?? AvailableCanBitrates.FirstOrDefault(option => option.Value == Constants.DEFAULT_CAN_BITRATE);
        }

        private bool HasDownloadedControllerLog(string fileName, long fileSizeBytes)
        {
            var file = new LogFile
            {
                FileName = fileName,
                FullPath = fileName,
                FileSizeBytes = fileSizeBytes,
                IsControllerFile = true,
            };

            return _downloadedLogCache.ContainsKey(BuildLogCacheKey(file)) || TryGetStoredControllerLogCopy(file, out _);
        }

        private static string GetStoredControllerLogPath(LogFile file)
        {
            string safeName = Path.GetFileName(string.IsNullOrWhiteSpace(file.FileName) ? "log.csv" : file.FileName);
            return Path.Combine(PreferredDownloadedLogDirectory, safeName);
        }

        private static bool TryGetStoredControllerLogCopy(LogFile file, out string? storedPath)
        {
            storedPath = GetStoredControllerLogPath(file);
            if (!File.Exists(storedPath))
            {
                storedPath = null;
                return false;
            }

            if (file.FileSizeBytes > 0 && new FileInfo(storedPath).Length != file.FileSizeBytes)
            {
                storedPath = null;
                return false;
            }

            return true;
        }

        private static async Task PersistDownloadedControllerLogAsync(LogFile file, string content, CancellationToken token)
        {
            Directory.CreateDirectory(PreferredDownloadedLogDirectory);
            string destinationPath = GetStoredControllerLogPath(file);
            await File.WriteAllTextAsync(destinationPath, content, token);
        }

        public IReadOnlyList<LogMapGridRow> BuildLogMapRows(int maxPointCount, LogMapViewport? viewport = null)
        {
            if (_parsedLogRows.Count == 0)
                return [];

            DateTimeOffset start = ComposeDateTime(LogRangeStartDate, LogRangeStartTime) ?? _parsedLogRows.First().Timestamp;
            DateTimeOffset end = ComposeDateTime(LogRangeEndDate, LogRangeEndTime) ?? _parsedLogRows.Last().Timestamp;
            if (end < start)
                return [];

            var selectedKeys = GetSelectedLogParameterKeys();
            var systemDisplayNames = SystemParameterSelections.ToDictionary(
                selection => selection.Key,
                selection => selection.DisplayName);

            // Group all rows by whole-second bucket — GPS is 1Hz so one unique
            // coordinate exists per second; all 10Hz rows in that second are associated.
            var secondGroups = _parsedLogRows
                .Where(row => row.Timestamp >= start && row.Timestamp <= end)
                .GroupBy(row => new DateTimeOffset(
                    row.Timestamp.Year, row.Timestamp.Month, row.Timestamp.Day,
                    row.Timestamp.Hour, row.Timestamp.Minute, row.Timestamp.Second,
                    row.Timestamp.Offset))
                .OrderBy(g => g.Key)
                .ToList();

            var result = new List<LogMapGridRow>();

            foreach (var group in secondGroups)
            {
                // Use the first row in this second that has a valid coordinate
                var gpsRow = group.FirstOrDefault(row => TryGetLogMapCoordinate(row, out _, out _));
                if (gpsRow == null)
                    continue;

                TryGetLogMapCoordinate(gpsRow, out double latitude, out double longitude);
                var associatedRows = group.OrderBy(row => row.Timestamp).ToList();

                var parameterValuesByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string key in selectedKeys)
                {
                    if (!gpsRow.NumericValues.TryGetValue(key, out double value))
                        continue;

                    string valueText = FormatLogMapNumericValue(value);
                    if (TryGetLogSeriesUnit(key, out string? unit) && !string.IsNullOrWhiteSpace(unit))
                        valueText = $"{valueText} {unit}";

                    parameterValuesByKey[key] = valueText;
                }

                result.Add(new LogMapGridRow
                {
                    Timestamp = gpsRow.Timestamp,
                    Latitude = latitude,
                    Longitude = longitude,
                    ParameterValuesByKey = parameterValuesByKey,
                    AssociatedRows = associatedRows
                });
            }

            return result;
        }

        public IReadOnlyList<LogMapParameterColumn> GetSelectedLogMapParameterColumns()
        {
            var selectedKeys = GetSelectedLogParameterKeys();
            var systemDisplayNames = SystemParameterSelections.ToDictionary(
                selection => selection.Key,
                selection => selection.DisplayName);

            var columns = new List<LogMapParameterColumn>(selectedKeys.Count);
            for (int index = 0; index < selectedKeys.Count; index++)
            {
                string key = selectedKeys[index];
                columns.Add(new LogMapParameterColumn
                {
                    ColumnId = $"P{index}",
                    Key = key,
                    Header = BuildLogSeriesDisplayName(key, systemDisplayNames),
                });
            }

            return columns;
        }

        private bool IsSelectedLogLoadCurrent(LogFile selectedFile, int loadVersion)
        {
            return loadVersion == _selectedLogLoadVersion &&
                   SelectedLogFile != null &&
                   string.Equals(BuildLogCacheKey(SelectedLogFile), BuildLogCacheKey(selectedFile), StringComparison.OrdinalIgnoreCase);
        }

        private void ResetParsedLogContent()
        {
            _parsedLogRows.Clear();
            _parsedLogSeries.Clear();
            _parsedLogSeriesUnits.Clear();
            ClearActiveLogSeries();
            LogMetricRows = new ObservableCollection<LogMetricRow>();
            LogMapGridRows = new ObservableCollection<LogMapGridRow>();
            LogMapInspectionRows = new ObservableCollection<LogMapInspectionRow>();
            NotifyLogMapDataChanged();
            NotifyLogMapRouteChanged();
            _logSeriesColorRegistry.Clear();
        }

        private static DateTime? TryParseLogFileTimestampUtc(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return null;
            }

            Match match = LogFileTimestampRegex.Match(fileName);
            if (match.Success)
            {
                if (DateTime.TryParseExact(
                    match.Groups["timestamp"].Value,
                    "yyyy-MM-dd_HH-mm-ss",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,
                    out DateTime parsedTimestamp))
                {
                    return parsedTimestamp.ToUniversalTime();
                }
            }

            match = LegacyLogFileTimestampRegex.Match(fileName);
            if (!match.Success)
            {
                return null;
            }

            string timestampText = $"{match.Groups["date"].Value}{match.Groups["time"].Value}";
            if (!DateTime.TryParseExact(
                timestampText,
                "yyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out DateTime localTimestamp))
            {
                return null;
            }

            return localTimestamp.ToUniversalTime();
        }

        private void AddParsedLogPoint(string key, DateTimeOffset timestamp, double value, string? unit = null)
        {
            if (!_parsedLogSeries.TryGetValue(key, out var points))
            {
                points = [];
                _parsedLogSeries[key] = points;
            }

            points.Add(new LogSeriesPoint(timestamp.ToUnixTimeMilliseconds(), value));

            if (!string.IsNullOrWhiteSpace(unit) && !_parsedLogSeriesUnits.ContainsKey(key))
            {
                _parsedLogSeriesUnits[key] = unit;
            }
        }

        private void RefreshLogSeriesForViewportIfNeeded()
        {
            if (_activeFilteredLogSeries.Count == 0 || IsLogBusy)
            {
                return;
            }

            if (!TryGetCurrentLogViewport(out double viewportStartMs, out double viewportEndMs))
            {
                return;
            }

            if (Math.Abs(viewportStartMs - _lastRenderedLogViewportStartMs) < 1.0 &&
                Math.Abs(viewportEndMs - _lastRenderedLogViewportEndMs) < 1.0)
            {
                return;
            }

            RebuildDisplayedLogSeries(viewportStartMs, viewportEndMs);
        }

        private bool TryGetCurrentLogViewport(out double viewportStartMs, out double viewportEndMs)
        {
            viewportStartMs = _activeLogFilterStartMs;
            viewportEndMs = _activeLogFilterEndMs;

            if (double.IsNaN(_activeLogFilterStartMs) || double.IsNaN(_activeLogFilterEndMs))
            {
                return false;
            }

            if (LogXAxes is not { Length: > 0 })
            {
                return true;
            }

            double? minLimit = LogXAxes[0].MinLimit;
            double? maxLimit = LogXAxes[0].MaxLimit;

            viewportStartMs = minLimit.HasValue
                ? minLimit.Value
                : _activeLogFilterStartMs;
            viewportEndMs = maxLimit.HasValue
                ? maxLimit.Value
                : _activeLogFilterEndMs;

            if (double.IsNaN(viewportStartMs) || double.IsInfinity(viewportStartMs) ||
                double.IsNaN(viewportEndMs) || double.IsInfinity(viewportEndMs))
            {
                return false;
            }

            viewportStartMs = Math.Max(_activeLogFilterStartMs, viewportStartMs);
            viewportEndMs = Math.Min(_activeLogFilterEndMs, viewportEndMs);

            if (viewportEndMs <= viewportStartMs)
            {
                viewportStartMs = _activeLogFilterStartMs;
                viewportEndMs = _activeLogFilterEndMs;
            }

            return true;
        }

        private void RebuildDisplayedLogSeries(double viewportStartMs, double viewportEndMs)
        {
            var newSeries = new ObservableCollection<ISeries>();
            var newLegendItems = new ObservableCollection<ChartSeriesToggleItem>();
            var displayedSeriesKeys = new List<string>();
            double filterSpanMs = Math.Max(1.0, _activeLogFilterEndMs - _activeLogFilterStartMs);
            double viewportSpanMs = Math.Max(1.0, viewportEndMs - viewportStartMs);
            double zoomFactor = Math.Max(1.0, filterSpanMs / viewportSpanMs);

            foreach (string key in _activeLogSeriesKeys)
            {
                if (!_activeFilteredLogSeries.TryGetValue(key, out var sourcePoints) || sourcePoints.Count == 0)
                {
                    continue;
                }

                int desiredPointCount = Math.Max(
                    MinimumViewportRenderedPointsPerSeries,
                    (int)Math.Ceiling(MaxRenderedLogPointsPerSeries * zoomFactor));

                var renderedPoints = BuildRenderedLogPoints(sourcePoints, desiredPointCount);
                if (renderedPoints.Count == 0)
                {
                    continue;
                }

                string displayName = BuildLogSeriesDisplayName(key, _activeSystemDisplayNames);
                SKColor seriesColor = GetOrAssignLogSeriesColor(key, _activeLogSeriesKeys);
                var lineSeries = new LineSeries<ObservablePoint>
                {
                    Values = renderedPoints,
                    Name = displayName,
                    GeometrySize = 0,
                    GeometryStroke = null,
                    GeometryFill = null,
                    Fill = null,
                    LineSmoothness = 0,
                    AnimationsSpeed = TimeSpan.Zero,
                    Stroke = new SolidColorPaint(seriesColor)
                    {
                        StrokeThickness = 1.0f,
                    },
                };

                newSeries.Add(lineSeries);
                newLegendItems.Add(new ChartSeriesToggleItem(displayName, CreateLiveSeriesBrush(seriesColor), lineSeries));
                displayedSeriesKeys.Add(key);
            }

            LogSeriesCollection = newSeries;
            LogChartLegendItems = newLegendItems;
            _activeDisplayedLogSeriesKeys = displayedSeriesKeys;
            _lastRenderedLogViewportStartMs = viewportStartMs;
            _lastRenderedLogViewportEndMs = viewportEndMs;
            LogStatusMessage = $"Showing {newSeries.Count} parameter series.";
        }

        private void ClearActiveLogSeries(bool resetAxisLimits = true)
        {
            _activeFilteredLogSeries = new Dictionary<string, List<LogSeriesPoint>>(StringComparer.OrdinalIgnoreCase);
            _activeLogSeriesKeys = [];
            _activeSystemDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _activeDisplayedLogSeriesKeys = [];
            _activeLogFilterStartMs = double.NaN;
            _activeLogFilterEndMs = double.NaN;
            _lastRenderedLogViewportStartMs = double.NaN;
            _lastRenderedLogViewportEndMs = double.NaN;
            LogSeriesCollection = new ObservableCollection<ISeries>();
            LogChartLegendItems = new ObservableCollection<ChartSeriesToggleItem>();

            if (resetAxisLimits && LogXAxes is { Length: > 0 })
            {
                LogXAxes[0].MinLimit = null;
                LogXAxes[0].MaxLimit = null;
            }
        }

        public void UpdateLogHoverAnchorFromRelativeX(double relativeX)
        {
            if (LogChartLegendItems.Count == 0 || _activeDisplayedLogSeriesKeys.Count == 0)
            {
                return;
            }

            if (!TryGetCurrentLogViewport(out double viewportStartMs, out double viewportEndMs))
            {
                return;
            }

            double span = Math.Max(1.0, viewportEndMs - viewportStartMs);
            double anchorTimestampMs = viewportStartMs + (span * Math.Clamp(relativeX, 0d, 1d));

            for (int i = 0; i < LogChartLegendItems.Count; i++)
            {
                if (i >= _activeDisplayedLogSeriesKeys.Count)
                {
                    LogChartLegendItems[i].SetCurrentValue(null);
                    continue;
                }

                string key = _activeDisplayedLogSeriesKeys[i];
                if (!_activeFilteredLogSeries.TryGetValue(key, out List<LogSeriesPoint>? sourcePoints) ||
                    !TryGetNearestLogSeriesValue(sourcePoints, anchorTimestampMs, out double value))
                {
                    LogChartLegendItems[i].SetCurrentValue(null);
                    continue;
                }

                LogChartLegendItems[i].SetCurrentValue(value);
            }
        }

        public void ClearLogHoverValues()
        {
            foreach (ChartSeriesToggleItem legendItem in LogChartLegendItems)
            {
                legendItem.SetCurrentValue(null);
            }
        }

        private static bool TryGetNearestLogSeriesValue(IReadOnlyList<LogSeriesPoint> points, double targetX, out double value)
        {
            value = 0;
            double bestDistance = double.MaxValue;
            bool found = false;

            for (int i = 0; i < points.Count; i++)
            {
                LogSeriesPoint point = points[i];
                double distance = Math.Abs(point.TimestampMs - targetX);
                if (distance >= bestDistance)
                {
                    continue;
                }

                bestDistance = distance;
                value = point.Value;
                found = true;
            }

            return found;
        }

        private void UpdateLogCrosshairState()
        {
            var crosshairPaint = IsLogCrosshairEnabled
                ? new SolidColorPaint(new SKColor(235, 235, 235, 180))
                {
                    StrokeThickness = 1,
                }
                : null;

            foreach (var axis in LogXAxes.OfType<Axis>())
            {
                axis.CrosshairPaint = crosshairPaint;
                axis.CrosshairSnapEnabled = false;
            }

            foreach (var axis in LogYAxes.OfType<Axis>())
            {
                axis.CrosshairPaint = null;
                axis.CrosshairSnapEnabled = false;
            }
        }

        private void UpdateLiveCrosshairState()
        {
            var crosshairPaint = IsLiveCrosshairEnabled
                ? new SolidColorPaint(new SKColor(235, 235, 235, 180))
                {
                    StrokeThickness = 1,
                }
                : null;

            foreach (var axis in XAxes.OfType<Axis>())
            {
                axis.CrosshairPaint = crosshairPaint;
                axis.CrosshairSnapEnabled = false;
            }

            foreach (var axis in YAxes.OfType<Axis>())
            {
                axis.CrosshairPaint = null;
                axis.CrosshairSnapEnabled = false;
            }
        }

        private string BuildLogSeriesDisplayName(string key, IReadOnlyDictionary<string, string> systemDisplayNames)
        {
            string displayName = ResolveDisplayNameForKey(key, systemDisplayNames);
            if (!TryGetLogSeriesUnit(key, out string? unit) || string.IsNullOrWhiteSpace(unit))
            {
                return displayName;
            }

            return $"{displayName} ({unit})";
        }

        private SKColor GetOrAssignLogSeriesColor(string key, IReadOnlyList<string> activeKeys)
        {
            if (_logSeriesColorRegistry.TryGetValue(key, out SKColor existing))
                return existing;

            // Find which palette indices are already taken by active series
            var usedIndices = activeKeys
                .Where(k => _logSeriesColorRegistry.ContainsKey(k))
                .Select(k => Array.IndexOf(LogSeriesPalette, _logSeriesColorRegistry[k]))
                .ToHashSet();

            // Pick the first palette index not currently in use
            int chosen = Enumerable.Range(0, LogSeriesPalette.Length)
                .FirstOrDefault(i => !usedIndices.Contains(i), 0);

            SKColor color = LogSeriesPalette[chosen];
            _logSeriesColorRegistry[key] = color;
            return color;
        }

        internal bool TryGetLogSeriesUnit(string key, out string? unit)
        {
            if (_parsedLogSeriesUnits.TryGetValue(key, out string? parsedUnit) && !string.IsNullOrWhiteSpace(parsedUnit))
            {
                unit = parsedUnit;
                return true;
            }

            if (key.Equals("System.System Temp", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("System.SIM Module Temp", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("System.IMU Temp", StringComparison.OrdinalIgnoreCase))
            {
                unit = "°C";
                return true;
            }

            if (key.Equals("System.System Voltage", StringComparison.OrdinalIgnoreCase))
            {
                unit = "V";
                return true;
            }

            if (key.Equals("System.System Current", StringComparison.OrdinalIgnoreCase))
            {
                unit = "A";
                return true;
            }

            if (key.EndsWith(".Current Value", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith(".Current Threshold High", StringComparison.OrdinalIgnoreCase) ||
                key.EndsWith(".Current Threshold Low", StringComparison.OrdinalIgnoreCase))
            {
                unit = "A";
                return true;
            }

            if (key.Equals("System.IMU Accel X", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("System.IMU Accel Y", StringComparison.OrdinalIgnoreCase) ||
                 key.Equals("System.IMU Accel Z", StringComparison.OrdinalIgnoreCase))
            {
                unit = "g";
                return true;
            }

            if (key.Equals("System.IMU Gyro X", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("System.IMU Gyro Y", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("System.IMU Gyro Z", StringComparison.OrdinalIgnoreCase))
            {
                unit = "°/s";
                return true;
            }

            if (key.Equals("System.Speed", StringComparison.OrdinalIgnoreCase))
            {
                unit = SpeedUnit;
                return true;
            }

            if (key.Equals("System.Distance", StringComparison.OrdinalIgnoreCase))
            {
                unit = DistanceUnit;
                return true;
            }

            if (key.Equals("System.Alt", StringComparison.OrdinalIgnoreCase))
            {
                unit = "m";
                return true;
            }

            unit = null;
            return false;
        }

        private static string NormalizeLoggedUnit(string unit)
        {
            return unit switch
            {
                "C" => "°C",
                "F" => "°F",
                _ => unit,
            };
        }

        private string FormatLogTimestampLabel(double value)
        {
            try
            {
                var timestamp = DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(value)).LocalDateTime;
                double viewportSpanMs = GetCurrentLogViewportSpanMs();

                if (viewportSpanMs <= 30_000)
                {
                    return timestamp.ToString("HH:mm:ss.fff");
                }

                if (viewportSpanMs <= 43_200_000)
                {
                    return timestamp.ToString("HH:mm:ss");
                }

                return timestamp.ToString("yyyy-MM-dd HH:mm:ss");
            }
            catch
            {
                return string.Empty;
            }
        }

        private double GetCurrentLogViewportSpanMs()
        {
            if (LogXAxes is { Length: > 0 })
            {
                double? minLimit = LogXAxes[0].MinLimit;
                double? maxLimit = LogXAxes[0].MaxLimit;
                if (minLimit.HasValue && maxLimit.HasValue && maxLimit.Value > minLimit.Value)
                {
                    return maxLimit.Value - minLimit.Value;
                }
            }

            if (!double.IsNaN(_activeLogFilterStartMs) &&
                !double.IsNaN(_activeLogFilterEndMs) &&
                _activeLogFilterEndMs > _activeLogFilterStartMs)
            {
                return _activeLogFilterEndMs - _activeLogFilterStartMs;
            }

            if (_parsedLogRows.Count > 1)
            {
                return (_parsedLogRows[^1].Timestamp - _parsedLogRows[0].Timestamp).TotalMilliseconds;
            }

            return 0;
        }

        private static bool TryParseIndexedInputKey(string key, char inputPrefix, out int inputNumber)
        {
            inputNumber = 0;
            if (key.Length < 3 || key[0] != inputPrefix || key[1] != 'I')
            {
                return false;
            }

            return int.TryParse(key.AsSpan(2), NumberStyles.Integer, CultureInfo.InvariantCulture, out inputNumber);
        }

        private static List<LogSeriesPoint> SliceLogSeries(List<LogSeriesPoint> sourcePoints, double startMs, double endMs)
        {
            if (sourcePoints.Count == 0)
            {
                return [];
            }

            int startIndex = FindFirstIndexAtOrAfter(sourcePoints, startMs);
            if (startIndex >= sourcePoints.Count)
            {
                return [];
            }

            int endIndex = FindLastIndexAtOrBefore(sourcePoints, endMs);
            if (endIndex < startIndex)
            {
                return [];
            }

            return sourcePoints.GetRange(startIndex, endIndex - startIndex + 1);
        }

        private static int FindFirstIndexAtOrAfter(List<LogSeriesPoint> sourcePoints, double targetMs)
        {
            int low = 0;
            int high = sourcePoints.Count - 1;
            int result = sourcePoints.Count;

            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (sourcePoints[middle].TimestampMs >= targetMs)
                {
                    result = middle;
                    high = middle - 1;
                }
                else
                {
                    low = middle + 1;
                }
            }

            return result;
        }

        private static int FindLastIndexAtOrBefore(List<LogSeriesPoint> sourcePoints, double targetMs)
        {
            int low = 0;
            int high = sourcePoints.Count - 1;
            int result = -1;

            while (low <= high)
            {
                int middle = low + ((high - low) / 2);
                if (sourcePoints[middle].TimestampMs <= targetMs)
                {
                    result = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return result;
        }

        private static List<ObservablePoint> BuildRenderedLogPoints(List<LogSeriesPoint> sourcePoints, int maxPointCount)
        {
            if (sourcePoints.Count <= maxPointCount)
            {
                return sourcePoints
                    .Select(point => new ObservablePoint(point.TimestampMs, point.Value))
                    .ToList();
            }

            int bucketCount = Math.Max(1, maxPointCount - 2);
            double pointsPerBucket = (double)(sourcePoints.Count - 2) / bucketCount;
            var renderedPoints = new List<ObservablePoint>(Math.Min(sourcePoints.Count, bucketCount + 2));

            AppendRenderedPoint(renderedPoints, sourcePoints[0]);

            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                int bucketStart = 1 + (int)Math.Floor(bucket * pointsPerBucket);
                int bucketEndExclusive = 1 + (int)Math.Floor((bucket + 1) * pointsPerBucket);
                bucketStart = Math.Clamp(bucketStart, 1, sourcePoints.Count - 1);
                bucketEndExclusive = Math.Clamp(bucketEndExclusive, bucketStart + 1, sourcePoints.Count);

                AppendRenderedPoint(renderedPoints, SelectRepresentativeLogPoint(sourcePoints, bucketStart, bucketEndExclusive));
            }

            AppendRenderedPoint(renderedPoints, sourcePoints[^1]);
            return renderedPoints;
        }

        private static LogSeriesPoint SelectRepresentativeLogPoint(List<LogSeriesPoint> sourcePoints, int bucketStart, int bucketEndExclusive)
        {
            if (bucketEndExclusive <= bucketStart + 1)
            {
                return sourcePoints[bucketStart];
            }

            int previousIndex = Math.Max(0, bucketStart - 1);
            int nextIndex = Math.Min(sourcePoints.Count - 1, bucketEndExclusive);
            LogSeriesPoint previousPoint = sourcePoints[previousIndex];
            LogSeriesPoint nextPoint = sourcePoints[nextIndex];

            double interpolationSpan = nextPoint.TimestampMs - previousPoint.TimestampMs;
            LogSeriesPoint representativePoint = sourcePoints[bucketStart];
            double maxDeviation = double.MinValue;

            for (int pointIndex = bucketStart; pointIndex < bucketEndExclusive; pointIndex++)
            {
                LogSeriesPoint candidatePoint = sourcePoints[pointIndex];
                double expectedValue;
                if (Math.Abs(interpolationSpan) < double.Epsilon)
                {
                    expectedValue = (previousPoint.Value + nextPoint.Value) / 2.0;
                }
                else
                {
                    double position = (candidatePoint.TimestampMs - previousPoint.TimestampMs) / interpolationSpan;
                    expectedValue = previousPoint.Value + ((nextPoint.Value - previousPoint.Value) * position);
                }

                double deviation = Math.Abs(candidatePoint.Value - expectedValue);
                if (deviation > maxDeviation)
                {
                    maxDeviation = deviation;
                    representativePoint = candidatePoint;
                }
            }

            return representativePoint;
        }

        private static void AppendRenderedPoint(List<ObservablePoint> renderedPoints, LogSeriesPoint point)
        {
            if (renderedPoints.Count > 0)
            {
                var lastPoint = renderedPoints[^1];
                if (lastPoint.X.HasValue && lastPoint.Y.HasValue)
                {
                    double lastX = Convert.ToDouble(lastPoint.X.Value, CultureInfo.InvariantCulture);
                    double lastY = Convert.ToDouble(lastPoint.Y.Value, CultureInfo.InvariantCulture);
                    if (Math.Abs(lastX - point.TimestampMs) < 0.5 && Math.Abs(lastY - point.Value) < double.Epsilon)
                    {
                        return;
                    }
                }
            }

            renderedPoints.Add(new ObservablePoint(point.TimestampMs, point.Value));
        }

        private void NotifyLogMapDataChanged()
        {
            LogMapDataVersion++;
            LogMapInspectionRows = new ObservableCollection<LogMapInspectionRow>(); // clear stale inspection
        }

        private void NotifyLogMapRouteChanged()
        {
            LogMapRouteVersion++;
        }

        private List<string> GetSelectedLogParameterKeys()
        {
            var selectedSystemKeys = SystemParameterSelections
                .Where(p => p.IsSelected)
                .Select(p => p.Key);

            var selectedChannels = ChannelSelections
                .Where(selection => selection.IsSelected)
                .Select(selection => selection.ChannelNumber)
                .ToList();

            var selectedChannelFields = ChannelFieldSelections
                .Where(selection => selection.IsSelected)
                .Select(selection => selection.Key)
                .ToList();

            var selectedChannelKeys = new List<string>();
            foreach (int channelNumber in selectedChannels)
            {
                foreach (string fieldName in selectedChannelFields)
                {
                    selectedChannelKeys.Add($"CH{channelNumber}.{fieldName}");
                }
            }

            var selectedInputKeys = DigitalInputSelections
                .Where(selection => selection.IsSelected)
                .Select(selection => selection.Key)
                .Concat(AnalogueInputSelections
                    .Where(selection => selection.IsSelected)
                    .Select(selection => selection.Key));

            return selectedSystemKeys
                .Concat(selectedChannelKeys)
                .Concat(selectedInputKeys)
                .ToList();
        }

        private LogMapGridRow CreateLogMapGridRow(
            LogMapSample sample,
            IReadOnlyList<string> selectedKeys,
            IReadOnlyDictionary<string, string> systemDisplayNames)
        {
            var parameterValuesByKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in selectedKeys)
            {
                if (!sample.Row.NumericValues.TryGetValue(key, out double value))
                {
                    continue;
                }

                string displayName = ResolveDisplayNameForKey(key, systemDisplayNames);
                string valueText = FormatLogMapNumericValue(value);
                if (TryGetLogSeriesUnit(key, out string? unit) && !string.IsNullOrWhiteSpace(unit))
                {
                    valueText = $"{valueText} {unit}";
                }

                parameterValuesByKey[key] = valueText;
            }

            return new LogMapGridRow
            {
                Timestamp = sample.Row.Timestamp,
                Latitude = sample.Latitude,
                Longitude = sample.Longitude,
                ParameterValuesByKey = parameterValuesByKey,
            };
        }

        private static string FormatLogMapNumericValue(double value)
        {
            if (Math.Abs(value - Math.Round(value)) < 0.0001)
            {
                return Math.Round(value).ToString(CultureInfo.InvariantCulture);
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static bool TryGetLogMapCoordinate(ParsedLogRow row, out double latitude, out double longitude)
        {
            latitude = 0;
            longitude = 0;

            if (!row.NumericValues.TryGetValue("System.Lat", out latitude) ||
                !row.NumericValues.TryGetValue("System.Lon", out longitude))
            {
                return false;
            }

            if (double.IsNaN(latitude) || double.IsInfinity(latitude) ||
                double.IsNaN(longitude) || double.IsInfinity(longitude))
            {
                return false;
            }

            if (Math.Abs(latitude) < double.Epsilon && Math.Abs(longitude) < double.Epsilon)
            {
                return false;
            }

            return latitude is >= -90 and <= 90 && longitude is >= -180 and <= 180;
        }

        private static List<LogMapSample> BuildRenderedLogMapSamples(List<LogMapSample> sourceSamples, int maxPointCount)
        {
            if (sourceSamples.Count <= maxPointCount)
            {
                return sourceSamples;
            }

            int bucketCount = Math.Max(1, maxPointCount - 2);
            double samplesPerBucket = (double)(sourceSamples.Count - 2) / bucketCount;
            var renderedSamples = new List<LogMapSample>(Math.Min(sourceSamples.Count, bucketCount + 2))
            {
                sourceSamples[0],
            };

            for (int bucket = 0; bucket < bucketCount; bucket++)
            {
                int bucketStart = 1 + (int)Math.Floor(bucket * samplesPerBucket);
                int bucketEndExclusive = 1 + (int)Math.Floor((bucket + 1) * samplesPerBucket);
                bucketStart = Math.Clamp(bucketStart, 1, sourceSamples.Count - 1);
                bucketEndExclusive = Math.Clamp(bucketEndExclusive, bucketStart + 1, sourceSamples.Count);

                AppendRenderedLogMapSample(renderedSamples, SelectRepresentativeLogMapSample(sourceSamples, bucketStart, bucketEndExclusive));
            }

            AppendRenderedLogMapSample(renderedSamples, sourceSamples[^1]);
            return renderedSamples;
        }

        private static LogMapSample SelectRepresentativeLogMapSample(List<LogMapSample> sourceSamples, int bucketStart, int bucketEndExclusive)
        {
            if (bucketEndExclusive <= bucketStart + 1)
            {
                return sourceSamples[bucketStart];
            }

            int previousIndex = Math.Max(0, bucketStart - 1);
            int nextIndex = Math.Min(sourceSamples.Count - 1, bucketEndExclusive);
            LogMapSample previousSample = sourceSamples[previousIndex];
            LogMapSample nextSample = sourceSamples[nextIndex];

            double deltaLongitude = nextSample.Longitude - previousSample.Longitude;
            double deltaLatitude = nextSample.Latitude - previousSample.Latitude;
            double denominator = (deltaLongitude * deltaLongitude) + (deltaLatitude * deltaLatitude);

            LogMapSample representativeSample = sourceSamples[bucketStart];
            double maxDeviation = double.MinValue;

            for (int sampleIndex = bucketStart; sampleIndex < bucketEndExclusive; sampleIndex++)
            {
                LogMapSample candidateSample = sourceSamples[sampleIndex];
                double projectedLongitude;
                double projectedLatitude;

                if (denominator < double.Epsilon)
                {
                    projectedLongitude = previousSample.Longitude;
                    projectedLatitude = previousSample.Latitude;
                }
                else
                {
                    double position = ((candidateSample.Longitude - previousSample.Longitude) * deltaLongitude) +
                                      ((candidateSample.Latitude - previousSample.Latitude) * deltaLatitude);
                    position = Math.Clamp(position / denominator, 0, 1);
                    projectedLongitude = previousSample.Longitude + (deltaLongitude * position);
                    projectedLatitude = previousSample.Latitude + (deltaLatitude * position);
                }

                double longitudeDeviation = candidateSample.Longitude - projectedLongitude;
                double latitudeDeviation = candidateSample.Latitude - projectedLatitude;
                double deviation = (longitudeDeviation * longitudeDeviation) + (latitudeDeviation * latitudeDeviation);

                if (deviation > maxDeviation)
                {
                    maxDeviation = deviation;
                    representativeSample = candidateSample;
                }
            }

            return representativeSample;
        }

        private static void AppendRenderedLogMapSample(List<LogMapSample> renderedSamples, LogMapSample sample)
        {
            if (renderedSamples.Count > 0)
            {
                LogMapSample lastSample = renderedSamples[^1];
                if (lastSample.Row.Timestamp == sample.Row.Timestamp &&
                    Math.Abs(lastSample.Latitude - sample.Latitude) < 0.0000001 &&
                    Math.Abs(lastSample.Longitude - sample.Longitude) < 0.0000001)
                {
                    return;
                }
            }

            renderedSamples.Add(sample);
        }

        public sealed class ParsedLogRow
        {
            public DateTimeOffset Timestamp { get; set; }

            public Dictionary<string, double> NumericValues { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        private readonly record struct LogSeriesPoint(long TimestampMs, double Value);
        private readonly record struct LogMapSample(ParsedLogRow Row, double Latitude, double Longitude);

    }
}

public sealed class LogMapViewport
{
    public double MinLongitude { get; init; }
    public double MinLatitude { get; init; }
    public double MaxLongitude { get; init; }
    public double MaxLatitude { get; init; }

    public bool Contains(double longitude, double latitude)
    {
        return longitude >= MinLongitude && longitude <= MaxLongitude && latitude >= MinLatitude && latitude <= MaxLatitude;
    }
}

public sealed class LogMapGridRow
{
    public DateTimeOffset Timestamp { get; init; }

    public double Latitude { get; init; }

    public double Longitude { get; init; }

    public IReadOnlyDictionary<string, string> ParameterValuesByKey { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string TimestampDisplay => Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public string LatitudeDisplay => Latitude.ToString("0.000000", CultureInfo.InvariantCulture);

    public string LongitudeDisplay => Longitude.ToString("0.000000", CultureInfo.InvariantCulture);

    public IReadOnlyList<ParsedLogRow> AssociatedRows { get; init; } = [];
}

public sealed class LogMapParameterColumn
{
    public string ColumnId { get; init; } = string.Empty;

    public string Key { get; init; } = string.Empty;

    public string Header { get; init; } = string.Empty;
}

public sealed class LogMapInspectionRow
{
    private readonly IReadOnlyDictionary<string, string> _values;

    public LogMapInspectionRow(IReadOnlyDictionary<string, string> values)
    {
        _values = values;
    }

    public string this[string columnId] => _values.TryGetValue(columnId, out string? value) ? value : string.Empty;
}

public sealed class MathRuleOption
{
    public MathRuleOption(int value, string label)
    {
        Value = value;
        Label = label;
    }

    public int Value { get; }

    public string Label { get; }
}

public sealed class MathOperandSourceOption
{
    public MathOperandSourceOption(int value, string label)
    {
        Value = value;
        Label = label;
    }

    public int Value { get; }

    public string Label { get; }
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
    public byte Pin { get; }
    public string Label { get; }

    public InputLabel(byte pin, string label)
    {
        Pin = pin;
        Label = label;
    }
}

public class InputDisplayItem
{
    public byte Pin { get; set; }
    public string Label { get; set; } = string.Empty;
}

public class ChannelTypeDisplay
{
    public OutputChannel.ChannelType ChannelType { get; set; }
    public required string Label { get; set; }
}

public class ChannelCategoryDisplay
{
    public OutputChannel.ChannelCategory Category { get; set; }
    public required string Label { get; set; }
}

public class AnalogueTypeDisplay
{
    public AnalogueInput.AnalogueChannelType Type { get; set; }
    public required string Label { get; set; }
}

public class AnalogueUnitDisplay
{
    public AnalogueInput.AnalogueUnits Units { get; set; }
    public required string Label { get; set; }
}

public sealed class CanIdOption
{
    public CanIdOption(ushort value, string label)
    {
        Value = value;
        Label = label;
    }

    public ushort Value { get; }

    public string Label { get; }
}

public sealed class CanBitrateOption
{
    public CanBitrateOption(uint value, string label)
    {
        Value = value;
        Label = label;
    }

    public uint Value { get; }

    public string Label { get; }
}

public sealed class TimeZoneDisplay
{
    public TimeZoneDisplay(string id, string label, TimeZoneInfo timeZone, int baseOffsetMinutes, bool isSupported, string? unsupportedReason)
    {
        Id = id;
        Label = label;
        TimeZone = timeZone;
        BaseOffsetMinutes = baseOffsetMinutes;
        IsSupported = isSupported;
        UnsupportedReason = unsupportedReason;
    }

    public string Id { get; }

    public string Label { get; }

    public TimeZoneInfo TimeZone { get; }

    public int BaseOffsetMinutes { get; }

    public bool IsSupported { get; }

    public string? UnsupportedReason { get; }
}
