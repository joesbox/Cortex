using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.ComponentModel;
using System.Globalization;

namespace Cortex.Models
{
    public partial class ConnectorPinDisplayItem : ObservableObject, IDisposable
    {
        private readonly ConnectorPinDefinition _definition;
        private OutputChannel? _settingsChannel;
        private OutputChannel? _liveChannel;
        private DigitalInput? _settingsDigitalInput;
        private DigitalInput? _liveDigitalInput;
        private AnalogueInput? _settingsAnalogueInput;
        private AnalogueInput? _liveAnalogueInput;

        private string _configuredType = string.Empty;
        private string _settingsSummary = string.Empty;
        private string _activeState = string.Empty;
        private string _liveValue = string.Empty;
        private string _limitsSummary = string.Empty;
        private IBrush _pinBrush = Brushes.Transparent;
        private IBrush _pinOutlineBrush = Brushes.Transparent;
        private IBrush _pinTextBrush = Brushes.Transparent;
        private IBrush _badgeBrush = Brushes.Transparent;
        private IBrush _statusBrush = Brushes.Transparent;
        private bool _isHighlighted;

        public string ConfiguredType
        {
            get => _configuredType;
            private set => SetProperty(ref _configuredType, value);
        }

        public string SettingsSummary
        {
            get => _settingsSummary;
            private set => SetProperty(ref _settingsSummary, value);
        }

        public string ActiveState
        {
            get => _activeState;
            private set => SetProperty(ref _activeState, value);
        }

        public string LiveValue
        {
            get => _liveValue;
            private set => SetProperty(ref _liveValue, value);
        }

        public string LimitsSummary
        {
            get => _limitsSummary;
            private set => SetProperty(ref _limitsSummary, value);
        }

        public IBrush PinBrush
        {
            get => _pinBrush;
            private set => SetProperty(ref _pinBrush, value);
        }

        public IBrush PinOutlineBrush
        {
            get => _pinOutlineBrush;
            private set => SetProperty(ref _pinOutlineBrush, value);
        }

        public IBrush PinTextBrush
        {
            get => _pinTextBrush;
            private set => SetProperty(ref _pinTextBrush, value);
        }

        public IBrush BadgeBrush
        {
            get => _badgeBrush;
            private set => SetProperty(ref _badgeBrush, value);
        }

        public IBrush StatusBrush
        {
            get => _statusBrush;
            private set => SetProperty(ref _statusBrush, value);
        }

        public bool IsHighlighted
        {
            get => _isHighlighted;
            private set => SetProperty(ref _isHighlighted, value);
        }

        public string PinNumberText => PinNumber.ToString(CultureInfo.InvariantCulture);

        public string RoleText => Role switch
        {
            ConnectorPinRole.Output => "Output",
            ConnectorPinRole.DigitalInput => "Digital input",
            ConnectorPinRole.AnalogueInput => "Analogue input",
            ConnectorPinRole.Ignition => "Ignition",
            ConnectorPinRole.CanLow => "CAN L",
            ConnectorPinRole.CanHigh => "CAN H",
            ConnectorPinRole.Reference5V => "5V ref",
            ConnectorPinRole.Ground => "Ground",
            _ => Role.ToString(),
        };

        public string BadgeText => OutputChannelNumber is int outputChannelNumber
            ? $"OUT {outputChannelNumber}"
            : DigitalInputNumber is int digitalInputNumber
                ? $"DIN {digitalInputNumber}"
                : AnalogueInputNumber is int analogueInputNumber
                    ? $"AIN {analogueInputNumber}"
                    : RoleText;

        public string PinTooltip => $"Pin {PinNumber}: {PhysicalFunction}";

        public bool HasLiveValue => LiveValue != "-";

        public bool HasSettingsSummary => !string.IsNullOrWhiteSpace(SettingsSummary);

        public bool HasLimitsSummary => !string.IsNullOrWhiteSpace(LimitsSummary) && LimitsSummary != "-";

        public double OverlayLeft => X - ConnectorPinCatalog.PinRadius;

        public double OverlayTop => Y - ConnectorPinCatalog.PinRadius;

        public double OverlayDiameter => ConnectorPinCatalog.PinRadius * 2;

        public ConnectorPinDisplayItem(ConnectorPinDefinition definition)
        {
            _definition = definition;
            Refresh();
        }

        public int PinNumber => _definition.PinNumber;

        public string PhysicalFunction => _definition.PhysicalFunction;

        public ConnectorPinRole Role => _definition.Role;

        public double X => _definition.X;

        public double Y => _definition.Y;

        public int? OutputChannelNumber => _definition.OutputChannelNumber;

        public int? DigitalInputNumber => _definition.DigitalInputNumber;

        public int? AnalogueInputNumber => _definition.AnalogueInputNumber;

        public void AttachSources(
            OutputChannel? settingsChannel,
            OutputChannel? liveChannel,
            DigitalInput? settingsDigitalInput,
            DigitalInput? liveDigitalInput,
            AnalogueInput? settingsAnalogueInput,
            AnalogueInput? liveAnalogueInput)
        {
            DetachAllSources();

            _settingsChannel = settingsChannel;
            _liveChannel = liveChannel;
            _settingsDigitalInput = settingsDigitalInput;
            _liveDigitalInput = liveDigitalInput;
            _settingsAnalogueInput = settingsAnalogueInput;
            _liveAnalogueInput = liveAnalogueInput;

            AttachSource(_settingsChannel);
            AttachSource(_liveChannel);
            AttachSource(_settingsDigitalInput);
            AttachSource(_liveDigitalInput);
            AttachSource(_settingsAnalogueInput);
            AttachSource(_liveAnalogueInput);

            Refresh();
        }

        public void Dispose()
        {
            DetachAllSources();
        }

        public void SetHighlighted(bool isHighlighted)
        {
            IsHighlighted = isHighlighted;
        }

        private void DetachAllSources()
        {
            DetachSource(_settingsChannel);
            DetachSource(_liveChannel);
            DetachSource(_settingsDigitalInput);
            DetachSource(_liveDigitalInput);
            DetachSource(_settingsAnalogueInput);
            DetachSource(_liveAnalogueInput);
        }

        private void AttachSource(INotifyPropertyChanged? source)
        {
            if (source != null)
            {
                source.PropertyChanged += Source_PropertyChanged;
            }
        }

        private void DetachSource(INotifyPropertyChanged? source)
        {
            if (source != null)
            {
                source.PropertyChanged -= Source_PropertyChanged;
            }
        }

        private void Source_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            Refresh();
        }

        private void Refresh()
        {
            ConfiguredType = GetConfiguredType();
            SettingsSummary = GetSettingsSummary();
            ActiveState = GetActiveState();
            LiveValue = GetLiveValue();
            LimitsSummary = GetLimitsSummary();

            Color baseColor = Color.Parse(GetColorHex());
            Color inactiveColor = Color.Parse(ConnectorPinPalette.InactiveFill);
            bool active = IsVisuallyActive();

            PinBrush = new SolidColorBrush(baseColor);
            PinOutlineBrush = new SolidColorBrush(Color.Parse(ConnectorPinPalette.Outline));
            PinTextBrush = new SolidColorBrush(GetPinTextColor(baseColor));
            BadgeBrush = new SolidColorBrush(baseColor);
            StatusBrush = new SolidColorBrush(Color.Parse(active ? ConnectorPinPalette.ActiveStatus : ConnectorPinPalette.InactiveStatus));
        }

        private bool IsVisuallyActive()
        {
            return Role switch
            {
                ConnectorPinRole.Output => _liveChannel != null &&
                    (_liveChannel.Override || _liveChannel.Enabled != 0 || _liveChannel.CurrentValue > 0.05f || _liveChannel.PWMSetDuty > 0),
                ConnectorPinRole.DigitalInput => _liveDigitalInput?.LiveElectricalState == true,
                ConnectorPinRole.AnalogueInput => _liveAnalogueInput?.InputVoltage is float voltage && Math.Abs(voltage) > 0.01f,
                _ => false,
            };
        }

        private static Color GetPinTextColor(Color backgroundColor)
        {
            double luminance = (0.299 * backgroundColor.R) + (0.587 * backgroundColor.G) + (0.114 * backgroundColor.B);
            return luminance >= 170
                ? Color.Parse(ConnectorPinPalette.PinTextDark)
                : Color.Parse(ConnectorPinPalette.PinTextLight);
        }

        private string GetColorHex()
        {
            return Role switch
            {
                ConnectorPinRole.Output => GetOutputColorHex(_settingsChannel?.ChanType ?? OutputChannel.ChannelType.Digital),
                ConnectorPinRole.DigitalInput => ConnectorPinPalette.DigitalInput,
                ConnectorPinRole.AnalogueInput => ConnectorPinPalette.AnalogueInput,
                ConnectorPinRole.Ignition => ConnectorPinPalette.Ignition,
                ConnectorPinRole.CanLow => ConnectorPinPalette.CanLow,
                ConnectorPinRole.CanHigh => ConnectorPinPalette.CanHigh,
                ConnectorPinRole.Reference5V => ConnectorPinPalette.Reference5V,
                ConnectorPinRole.Ground => ConnectorPinPalette.Ground,
                _ => ConnectorPinPalette.Outline,
            };
        }

        public static string GetOutputColorHex(OutputChannel.ChannelType channelType)
        {
            return channelType switch
            {
                OutputChannel.ChannelType.Digital => ConnectorPinPalette.OutputDigital,
                OutputChannel.ChannelType.PWM => ConnectorPinPalette.OutputPwm,
                OutputChannel.ChannelType.Intermittent => ConnectorPinPalette.OutputIntermittent,
                OutputChannel.ChannelType.Analogue => ConnectorPinPalette.OutputAnalogueThreshold,
                OutputChannel.ChannelType.AnalogueScaled => ConnectorPinPalette.OutputAnalogueScaled,
                OutputChannel.ChannelType.CAN => ConnectorPinPalette.OutputCanDigital,
                OutputChannel.ChannelType.CAN_PWM => ConnectorPinPalette.OutputCanPwm,
                OutputChannel.ChannelType.MATH => ConnectorPinPalette.OutputMath,
                _ => ConnectorPinPalette.OutputDigital,
            };
        }

        private string GetConfiguredType()
        {
            return Role switch
            {
                ConnectorPinRole.Output => _settingsChannel == null ? "Output" : FormatOutputChannelType(_settingsChannel.ChanType),
                ConnectorPinRole.DigitalInput => "Digital input",
                ConnectorPinRole.AnalogueInput => _settingsAnalogueInput == null ? "Analogue input" : FormatAnalogueType(_settingsAnalogueInput.ChanType),
                ConnectorPinRole.Ignition => "Ignition sense",
                ConnectorPinRole.CanLow => "CAN low",
                ConnectorPinRole.CanHigh => "CAN high",
                ConnectorPinRole.Reference5V => "5V reference",
                ConnectorPinRole.Ground => "Ground",
                _ => PhysicalFunction,
            };
        }

        private string GetSettingsSummary()
        {
            return Role switch
            {
                ConnectorPinRole.Output => GetOutputSettingsSummary(),
                ConnectorPinRole.DigitalInput => "No configurable settings",
                ConnectorPinRole.AnalogueInput => GetAnalogueSettingsSummary(),
                ConnectorPinRole.Ignition => "Fixed ignition input",
                ConnectorPinRole.CanLow => "Fixed CAN bus pin",
                ConnectorPinRole.CanHigh => "Fixed CAN bus pin",
                ConnectorPinRole.Reference5V => "Fixed 5V sensor reference",
                ConnectorPinRole.Ground => "Fixed chassis/sensor ground",
                _ => PhysicalFunction,
            };
        }

        private string GetActiveState()
        {
            return Role switch
            {
                ConnectorPinRole.Output => _liveChannel == null
                    ? "No live data"
                    : _liveChannel.Override
                        ? "Override"
                        : (_liveChannel.Enabled != 0 || _liveChannel.CurrentValue > 0.05f || _liveChannel.PWMSetDuty > 0)
                            ? "Active"
                            : _settingsChannel?.Enabled != 0
                                ? "Inactive"
                                : "Disabled",
                ConnectorPinRole.DigitalInput => _liveDigitalInput?.LiveElectricalState switch
                {
                    true => "High",
                    false => "Low",
                    _ => "Unavailable",
                },
                ConnectorPinRole.AnalogueInput => _liveAnalogueInput?.InputVoltage is float voltage && Math.Abs(voltage) > 0.01f ? "Active" : "Idle",
                ConnectorPinRole.Ignition => "Fixed input",
                ConnectorPinRole.CanLow => "Bus pin",
                ConnectorPinRole.CanHigh => "Bus pin",
                ConnectorPinRole.Reference5V => "Reference",
                ConnectorPinRole.Ground => "Ground",
                _ => string.Empty,
            };
        }

        private string GetLiveValue()
        {
            return Role switch
            {
                ConnectorPinRole.Output => _liveChannel == null
                    ? "-"
                    : string.Format(
                        CultureInfo.InvariantCulture,
                        "{0:0.0} A / {1}%",
                        _liveChannel.CurrentValue,
                        _liveChannel.PWMSetDuty),
                ConnectorPinRole.DigitalInput => _liveDigitalInput?.LiveElectricalState switch
                {
                    true => "Electrical HIGH",
                    false => "Electrical LOW",
                    _ => "-",
                },
                ConnectorPinRole.AnalogueInput => GetAnalogueLiveValue(),
                ConnectorPinRole.Ignition => "-",
                ConnectorPinRole.CanLow => "-",
                ConnectorPinRole.CanHigh => "-",
                ConnectorPinRole.Reference5V => "5 V nominal",
                ConnectorPinRole.Ground => "0 V",
                _ => "-",
            };
        }

        private string GetLimitsSummary()
        {
            return Role switch
            {
                ConnectorPinRole.Output => GetOutputLimitsSummary(),
                ConnectorPinRole.DigitalInput => "Raw electrical input",
                ConnectorPinRole.AnalogueInput => GetAnalogueLimitsSummary(),
                ConnectorPinRole.Ignition => "Vehicle ignition sense",
                ConnectorPinRole.CanLow => "Differential bus",
                ConnectorPinRole.CanHigh => "Differential bus",
                ConnectorPinRole.Reference5V => "Sensor supply only",
                ConnectorPinRole.Ground => "Return path",
                _ => string.Empty,
            };
        }

        private string GetOutputSettingsSummary()
        {
            if (_settingsChannel == null)
            {
                return "No settings";
            }

            string name = string.IsNullOrWhiteSpace(GetChannelName(_settingsChannel))
                ? $"Channel {_settingsChannel.ChannelNumber}"
                : GetChannelName(_settingsChannel);

            return string.Format(
                CultureInfo.InvariantCulture,
                "Name: {0}; Control input: {1}",
                name,
                InputPinCatalog.GetLabelForPin(_settingsChannel.InputControlPin));
        }

        private string GetAnalogueSettingsSummary()
        {
            if (_settingsAnalogueInput == null)
            {
                return "No settings";
            }

            return string.Format(
                CultureInfo.InvariantCulture,
                "Units: {0}; Pull-up {1}; Pull-down {2}",
                FormatAnalogueUnits(_settingsAnalogueInput.Units),
                _settingsAnalogueInput.PullUpEnable ? "On" : "Off",
                _settingsAnalogueInput.PullDownEnable ? "On" : "Off");
        }

        private string GetAnalogueLiveValue()
        {
            if (_liveAnalogueInput == null)
            {
                return "-";
            }

            if (_liveAnalogueInput.InputValue is float value && _settingsAnalogueInput != null && _settingsAnalogueInput.ChanType != AnalogueInput.AnalogueChannelType.RawVoltage)
            {
                return string.Format(
                    CultureInfo.InvariantCulture,
                    "{0:0.0} {1}",
                    value,
                    FormatAnalogueUnits(_settingsAnalogueInput.Units));
            }

            return _liveAnalogueInput.InputVoltage is float voltage
                ? string.Format(CultureInfo.InvariantCulture, "{0:0.0} V", voltage)
                : "-";
        }

        private string GetOutputLimitsSummary()
        {
            if (_settingsChannel == null)
            {
                return "-";
            }

            return _settingsChannel.ChanType switch
            {
                OutputChannel.ChannelType.PWM or OutputChannel.ChannelType.CAN_PWM or OutputChannel.ChannelType.AnalogueScaled or OutputChannel.ChannelType.MATH => string.Format(
                    CultureInfo.InvariantCulture,
                    "PWM {0}-{1}% ; Current {2:0.00}-{3:0.00} A",
                    _settingsChannel.PWMMin,
                    _settingsChannel.PWMMax,
                    _settingsChannel.CurrentThresholdLow,
                    _settingsChannel.CurrentThresholdHigh),
                OutputChannel.ChannelType.Analogue => string.Format(
                    CultureInfo.InvariantCulture,
                    "On {0:0.00} V / Off {1:0.00} V",
                    _settingsChannel.OnThreshold,
                    _settingsChannel.OffThreshold),
                _ => string.Format(
                    CultureInfo.InvariantCulture,
                    "Current {0:0.00}-{1:0.00} A",
                    _settingsChannel.CurrentThresholdLow,
                    _settingsChannel.CurrentThresholdHigh),
            };
        }

        private string GetAnalogueLimitsSummary()
        {
            if (_settingsAnalogueInput == null)
            {
                return "-";
            }

            return _settingsAnalogueInput.ChanType switch
            {
                AnalogueInput.AnalogueChannelType.Active or AnalogueInput.AnalogueChannelType.Passive => string.Format(
                    CultureInfo.InvariantCulture,
                    "Range {0:0.00} to {1:0.00}",
                    _settingsAnalogueInput.ConfigRangeMin,
                    _settingsAnalogueInput.ConfigRangeMax),
                AnalogueInput.AnalogueChannelType.NTC => string.Format(
                    CultureInfo.InvariantCulture,
                    "Beta {0:0} / Nom {1:0} Ω",
                    _settingsAnalogueInput.NtcBeta,
                    _settingsAnalogueInput.NtcNominalResistance),
                AnalogueInput.AnalogueChannelType.Digital => "Digital threshold input",
                _ => "0-5 V input",
            };
        }

        private static string GetChannelName(OutputChannel channel)
        {
            if (channel.Name == null || channel.Name.Length == 0)
            {
                return string.Empty;
            }

            return new string(channel.Name).Trim('\0', ' ');
        }

        public static string FormatOutputChannelType(OutputChannel.ChannelType channelType)
        {
            return channelType switch
            {
                OutputChannel.ChannelType.Digital => "Digital output",
                OutputChannel.ChannelType.PWM => "PWM output",
                OutputChannel.ChannelType.Intermittent => "Intermittent output",
                OutputChannel.ChannelType.Analogue => "Analogue threshold output",
                OutputChannel.ChannelType.AnalogueScaled => "Analogue scaled PWM",
                OutputChannel.ChannelType.CAN => "CAN digital output",
                OutputChannel.ChannelType.CAN_PWM => "CAN PWM output",
                OutputChannel.ChannelType.MATH => "RULE BASED output",
                _ => channelType.ToString(),
            };
        }

        private static string FormatAnalogueType(AnalogueInput.AnalogueChannelType channelType)
        {
            return channelType switch
            {
                AnalogueInput.AnalogueChannelType.RawVoltage => "Raw voltage input",
                AnalogueInput.AnalogueChannelType.Active => "Active sensor input",
                AnalogueInput.AnalogueChannelType.Passive => "Passive sensor input",
                AnalogueInput.AnalogueChannelType.NTC => "NTC sensor input",
                AnalogueInput.AnalogueChannelType.Digital => "Digital threshold input",
                _ => channelType.ToString(),
            };
        }

        private static string FormatAnalogueUnits(AnalogueInput.AnalogueUnits units)
        {
            return units switch
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
                _ => units.ToString(),
            };
        }
    }
}
