using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Cortex.Models
{
    [Serializable]
    public partial class OutputChannel : ObservableObject
    {
        [ObservableProperty]
        public ChannelType _ChanType;           // Channel type

        [ObservableProperty]
        public ChannelCategory _Category = ChannelCategory.Auxiliary;       // Output category

        [ObservableProperty]
        public byte _PWMSetDuty;                // Current duty set percentage

        [ObservableProperty]
        public byte _Enabled;                   // Channel enabled flag        

        [ObservableProperty]
        public char[]? _Name;                   // Channel name (3 characters)

        [ObservableProperty]
        public int _ChannelNumber;             // 1-based channel number for UI display

        [ObservableProperty]
        public int _AnalogRaw;                  // Raw analog value. Used for calibration

        [ObservableProperty]
        public float _CurrentValue;             // Active current value

        [ObservableProperty]
        public bool _Override;                  // Override flag

        [ObservableProperty]
        public float _CurrentThresholdHigh;     // Turn off threshold high

        [ObservableProperty]
        public float _CurrentThresholdLow;      // Turn off threshold low (open circuit detection)

        [ObservableProperty]
        public byte _RetryCount;                // Number of retries

        [ObservableProperty]
        public float _InrushDelay;              // Inrush delay in seconds

        [ObservableProperty]
        public float _InrushCurrentLimit;       // Inrush current limit in amps

        [ObservableProperty]
        public byte _MultiChannel;              // Grouped with other channels. Allows higher current loads

        [ObservableProperty]
        public byte _GroupNumber;               // Group membership number

        [ObservableProperty]
        public byte _ControlPin;                // Digital uC control pin

        [ObservableProperty]
        public byte _CurrentSensePin;           // Current sense input pin

        [ObservableProperty]
        public byte _InputControlPin;           // Digital input control pin (digital channels only)

        [ObservableProperty]
        public float _OnThreshold;              // On threshold (voltage)

        [ObservableProperty]
        public float _OffThreshold;             // Off threshold (voltage)

        [ObservableProperty]
        public float _ScaleMin;                 // Minimum input scale value (voltage)

        [ObservableProperty]
        public float _ScaleMax;                 // Maximum input scale value (voltage)

        [ObservableProperty]
        public byte _PWMMin;                    // Minimum PWM value (0-100)

        [ObservableProperty]
        public byte _PWMMax;                    // Maximum PWM value (0-100)

        [ObservableProperty]
        public byte _ErrorFlags;                // Bitmask for channel error flags

        [ObservableProperty]
        private double _holdProgress;           // Hold progress percentage for override button

        [ObservableProperty]
        private byte _SoftStartEnabled;          // Soft start enabled flag

        [ObservableProperty]
        private float _SoftStartTime;             // Soft start time in milliseconds

        [ObservableProperty]
        private byte _SoftStopEnabled;           // Soft stop enabled flag

        [ObservableProperty]
        private float _SoftStopTime;             // Soft stop time in milliseconds

        [ObservableProperty]
        private float _IntermittentOnTime;       // Intermittent on time in seconds

        [ObservableProperty]
        private float _IntermittentOffTime;      // Intermittent off time in seconds

        [ObservableProperty]
        private byte _DelayedOn;                 // Delayed on enabled flag

        [ObservableProperty]
        private int _DelayedOnTime;              // Delayed on time in milliseconds

        [ObservableProperty]
        private byte _DelayedOff;                // Delayed off enabled flag

        [ObservableProperty]
        private int _DelayedOffTime;             // Delayed off time in milliseconds

        [ObservableProperty]
        private byte _DelayedOffTrigger;         // Delayed off trigger source

        [property: JsonIgnore]
        [ObservableProperty]
        private int _DelayedOnTimeUnitIndex;

        [property: JsonIgnore]
        [ObservableProperty]
        private int _DelayedOffTimeUnitIndex;

        [ObservableProperty]
        private string _AnalogueInputVoltageDisplay = "-";

        private byte _mathRuleCount = 1;

        private byte _mathRootRule;

        private byte _mathOutputMode;

        private float _mathCalculatedRootValue;

        private byte _mathError;

        private ObservableCollection<MathRule> _mathRules = [];

        public byte MathRuleCount
        {
            get => _mathRuleCount;
            set => SetProperty(ref _mathRuleCount, value);
        }

        public byte MathRootRule
        {
            get => _mathRootRule;
            set => SetProperty(ref _mathRootRule, value);
        }

        public byte MathOutputMode
        {
            get => _mathOutputMode;
            set => SetProperty(ref _mathOutputMode, value);
        }

        public float MathCalculatedRootValue
        {
            get => _mathCalculatedRootValue;
            set => SetProperty(ref _mathCalculatedRootValue, value);
        }

        public byte MathError
        {
            get => _mathError;
            set
            {
                if (SetProperty(ref _mathError, value))
                {
                    OnPropertyChanged(nameof(HasMathRuntimeError));
                }
            }
        }

        public ObservableCollection<MathRule> MathRules
        {
            get => _mathRules;
            set
            {
                if (SetProperty(ref _mathRules, value))
                {
                    EnsureMathRuleSlots();
                }
            }
        }

        public IAsyncRelayCommand HoldToggleCommand { get; }
        public IRelayCommand CancelHoldCommand { get; }

        // Computed property for PWM channel detection
        public bool IsPWMChannel => ChanType == ChannelType.PWM ||
                        ChanType == ChannelType.CAN_PWM;

        public bool IsAnalogueThresholdChannel => ChanType == ChannelType.Analogue;

        public bool IsAnalogueScaledChannel => ChanType == ChannelType.AnalogueScaled;

        public bool IsIntermittentChannel => ChanType == ChannelType.Intermittent;

        public bool IsMathChannel => ChanType == ChannelType.MATH;

        public bool HasMathRuntimeError => MathError != 0;

        public bool IsAnalogueChannel => IsAnalogueThresholdChannel || IsAnalogueScaledChannel;

        public ChannelPriority Priority => GetPriority(Category);

        [JsonIgnore]
        public bool IsDelayedOnEnabled
        {
            get => DelayedOn != 0;
            set => DelayedOn = value ? (byte)1 : (byte)0;
        }

        [JsonIgnore]
        public bool IsDelayedOffEnabled
        {
            get => DelayedOff != 0;
            set => DelayedOff = value ? (byte)1 : (byte)0;
        }

        [JsonIgnore]
        public bool DelayedOffUsesIgnitionTrigger
        {
            get => DelayedOffTrigger == (byte)DelayedOffTriggerMode.IgnitionOff;
            set => DelayedOffTrigger = value ? (byte)DelayedOffTriggerMode.IgnitionOff : (byte)DelayedOffTriggerMode.AssignedInput;
        }

        [JsonIgnore]
        public bool DelayedOffUsesAssignedInputTrigger
        {
            get => DelayedOffTrigger == (byte)DelayedOffTriggerMode.AssignedInput;
            set => DelayedOffTrigger = value ? (byte)DelayedOffTriggerMode.AssignedInput : (byte)DelayedOffTriggerMode.IgnitionOff;
        }

        [JsonIgnore]
        public double DelayedOnSliderMinimum => GetDelaySliderMinimum(DelayedOnTimeUnitIndex);

        [JsonIgnore]
        public double DelayedOnSliderMaximum => GetDelaySliderMaximum(DelayedOnTimeUnitIndex);

        [JsonIgnore]
        public double DelayedOnSliderTickFrequency => GetDelaySliderTickFrequency(DelayedOnTimeUnitIndex);

        [JsonIgnore]
        public double DelayedOnSliderValue
        {
            get => GetDelaySliderValue(DelayedOnTime, DelayedOnTimeUnitIndex);
            set => DelayedOnTime = ConvertDelayValueToMilliseconds(value, DelayedOnTimeUnitIndex);
        }

        [JsonIgnore]
        public string DelayedOnSliderText => FormatDelaySliderValue(DelayedOnSliderValue, DelayedOnTimeUnitIndex);

        [JsonIgnore]
        public double DelayedOffSliderMinimum => GetDelaySliderMinimum(DelayedOffTimeUnitIndex);

        [JsonIgnore]
        public double DelayedOffSliderMaximum => GetDelaySliderMaximum(DelayedOffTimeUnitIndex);

        [JsonIgnore]
        public double DelayedOffSliderTickFrequency => GetDelaySliderTickFrequency(DelayedOffTimeUnitIndex);

        [JsonIgnore]
        public double DelayedOffSliderValue
        {
            get => GetDelaySliderValue(DelayedOffTime, DelayedOffTimeUnitIndex);
            set => DelayedOffTime = ConvertDelayValueToMilliseconds(value, DelayedOffTimeUnitIndex);
        }

        [JsonIgnore]
        public string DelayedOffSliderText => FormatDelaySliderValue(DelayedOffSliderValue, DelayedOffTimeUnitIndex);

        public OutputChannel()
        {
            HoldToggleCommand = new AsyncRelayCommand(OnHoldAsync);
            CancelHoldCommand = new RelayCommand(CancelHold);
            EnsureMathRuleSlots();
        }

        private const int DelayUnitMilliseconds = 0;
        private const int DelayUnitSeconds = 1;
        private const int DelayUnitMinutes = 2;
        private bool _suppressDelayUnitNormalization;

        private CancellationTokenSource? _holdCts;

        private async Task OnHoldAsync()
        {
            Debug.WriteLine($"OnHoldAsync called, Override={Override}");
            if (Override)
            {
                // Already active → single click deactivates
                Debug.WriteLine("Override turned OFF");
                Override = false;
                HoldProgress = 0;
                return;
            }

            _holdCts = new CancellationTokenSource();
            var start = DateTime.Now;
            var holdTime = TimeSpan.FromSeconds(2);

            try
            {
                Debug.WriteLine("Starting hold timer...");
                while ((DateTime.Now - start) < holdTime)
                {
                    await Task.Delay(50, _holdCts.Token);
                    var progress = (DateTime.Now - start).TotalMilliseconds / holdTime.TotalMilliseconds;
                    HoldProgress = Math.Clamp(progress, 0, 1);
                    Debug.WriteLine($"Hold progress: {HoldProgress:P0}");
                }

                Debug.WriteLine("Override turned ON");
                Override = true;      // toggle ON
                HoldProgress = 0;
            }
            catch (TaskCanceledException)
            {
                HoldProgress = 0;     // reset if released early
                Debug.WriteLine("Hold cancelled");
            }
            finally
            {
                _holdCts?.Dispose();
                _holdCts = null;
            }
        }

        private void CancelHold() => _holdCts?.Cancel();

        // Error flag constants
        private const byte CHN_OVERCURRENT_RANGE = 0x01;
        private const byte CHN_UNDERCURRENT_RANGE = 0x02;
        private const byte IS_FAULT = 0x04;
        private const byte RETRY_LOCKOUT = 0x08;

        // Individual error bit properties
        public bool HasOvercurrentRange => (ErrorFlags & CHN_OVERCURRENT_RANGE) != 0;
        public bool HasUndercurrentRange => (ErrorFlags & CHN_UNDERCURRENT_RANGE) != 0;
        public bool HasISFault => (ErrorFlags & IS_FAULT) != 0;
        public bool HasRetryLockout => (ErrorFlags & RETRY_LOCKOUT) != 0;

        // Overall status check
        public bool HasAnyError => ErrorFlags != 0;

        // Notify property changes when ErrorFlags changes
        partial void OnErrorFlagsChanged(byte value)
        {
            OnPropertyChanged(nameof(HasOvercurrentRange));
            OnPropertyChanged(nameof(HasUndercurrentRange));
            OnPropertyChanged(nameof(HasISFault));
            OnPropertyChanged(nameof(HasRetryLockout));
            OnPropertyChanged(nameof(HasAnyError));
        }

        // Notify IsPWMChannel when ChanType changes
        partial void OnChanTypeChanged(ChannelType value)
        {
            OnPropertyChanged(nameof(IsPWMChannel));
            OnPropertyChanged(nameof(IsAnalogueThresholdChannel));
            OnPropertyChanged(nameof(IsAnalogueScaledChannel));
            OnPropertyChanged(nameof(IsIntermittentChannel));
            OnPropertyChanged(nameof(IsMathChannel));
            OnPropertyChanged(nameof(IsAnalogueChannel));
        }

        public void EnsureMathRuleSlots()
        {
            MathRules ??= [];
            while (MathRules.Count < Constants.NUM_MATH_RULE_SLOTS)
            {
                MathRules.Add(new MathRule());
            }

            while (MathRules.Count > Constants.NUM_MATH_RULE_SLOTS)
            {
                MathRules.RemoveAt(MathRules.Count - 1);
            }
        }

        partial void OnCategoryChanged(ChannelCategory value)
        {
            OnPropertyChanged(nameof(Priority));
        }

        partial void OnDelayedOnChanged(byte value)
        {
            OnPropertyChanged(nameof(IsDelayedOnEnabled));
            if (value != 0 && DelayedOnTime <= 0)
            {
                DelayedOnTime = ConvertDelayValueToMilliseconds(GetDelaySliderMinimum(DelayedOnTimeUnitIndex), DelayedOnTimeUnitIndex);
            }
            RaiseDelayedOnUiPropertiesChanged();
        }

        partial void OnDelayedOffChanged(byte value)
        {
            OnPropertyChanged(nameof(IsDelayedOffEnabled));
            if (value != 0 && DelayedOffTime <= 0)
            {
                DelayedOffTime = ConvertDelayValueToMilliseconds(GetDelaySliderMinimum(DelayedOffTimeUnitIndex), DelayedOffTimeUnitIndex);
            }
            RaiseDelayedOffUiPropertiesChanged();
        }

        partial void OnDelayedOnTimeChanged(int value)
        {
            RaiseDelayedOnUiPropertiesChanged();
        }

        partial void OnDelayedOffTimeChanged(int value)
        {
            RaiseDelayedOffUiPropertiesChanged();
        }

        partial void OnDelayedOffTriggerChanged(byte value)
        {
            OnPropertyChanged(nameof(DelayedOffUsesIgnitionTrigger));
            OnPropertyChanged(nameof(DelayedOffUsesAssignedInputTrigger));
        }

        partial void OnDelayedOnTimeUnitIndexChanged(int value)
        {
            DelayedOnTimeUnitIndex = NormalizeDelayUnitIndex(value);
            if (_suppressDelayUnitNormalization)
            {
                RaiseDelayedOnUiPropertiesChanged();
                return;
            }

            if (DelayedOnTime > 0 || DelayedOn != 0)
            {
                DelayedOnTime = ConvertDelayValueToMilliseconds(GetDelaySliderValue(DelayedOnTime, DelayedOnTimeUnitIndex), DelayedOnTimeUnitIndex);
            }

            RaiseDelayedOnUiPropertiesChanged();
        }

        partial void OnDelayedOffTimeUnitIndexChanged(int value)
        {
            DelayedOffTimeUnitIndex = NormalizeDelayUnitIndex(value);
            if (_suppressDelayUnitNormalization)
            {
                RaiseDelayedOffUiPropertiesChanged();
                return;
            }

            if (DelayedOffTime > 0 || DelayedOff != 0)
            {
                DelayedOffTime = ConvertDelayValueToMilliseconds(GetDelaySliderValue(DelayedOffTime, DelayedOffTimeUnitIndex), DelayedOffTimeUnitIndex);
            }

            RaiseDelayedOffUiPropertiesChanged();
        }

        partial void OnOnThresholdChanged(float value)
        {
            if (UsesNegativeGoingThresholds())
            {
                if (OffThreshold < value)
                {
                    OffThreshold = value;
                }
            }
            else if (OffThreshold > value)
            {
                OffThreshold = value;
            }
        }

        partial void OnOffThresholdChanged(float value)
        {
            if (UsesNegativeGoingThresholds())
            {
                if (value < OnThreshold)
                {
                    OnThreshold = value;
                }
            }
            else if (value > OnThreshold)
            {
                OnThreshold = value;
            }
        }

        private bool UsesNegativeGoingThresholds() => OnThreshold < OffThreshold;

        public void RefreshDelayUiUnitsFromStoredValues()
        {
            _suppressDelayUnitNormalization = true;
            DelayedOnTimeUnitIndex = SelectDelayUnitIndex(DelayedOnTime);
            DelayedOffTimeUnitIndex = SelectDelayUnitIndex(DelayedOffTime);
            _suppressDelayUnitNormalization = false;
            RaiseDelayedOnUiPropertiesChanged();
            RaiseDelayedOffUiPropertiesChanged();
        }

        private void RaiseDelayedOnUiPropertiesChanged()
        {
            OnPropertyChanged(nameof(DelayedOnSliderMinimum));
            OnPropertyChanged(nameof(DelayedOnSliderMaximum));
            OnPropertyChanged(nameof(DelayedOnSliderTickFrequency));
            OnPropertyChanged(nameof(DelayedOnSliderValue));
            OnPropertyChanged(nameof(DelayedOnSliderText));
        }

        private void RaiseDelayedOffUiPropertiesChanged()
        {
            OnPropertyChanged(nameof(DelayedOffSliderMinimum));
            OnPropertyChanged(nameof(DelayedOffSliderMaximum));
            OnPropertyChanged(nameof(DelayedOffSliderTickFrequency));
            OnPropertyChanged(nameof(DelayedOffSliderValue));
            OnPropertyChanged(nameof(DelayedOffSliderText));
        }

        private static int NormalizeDelayUnitIndex(int value) => Math.Clamp(value, DelayUnitMilliseconds, DelayUnitMinutes);

        private static int SelectDelayUnitIndex(int delayTimeMs)
        {
            if (delayTimeMs >= 60000)
            {
                return DelayUnitMinutes;
            }

            if (delayTimeMs > 1000)
            {
                return DelayUnitSeconds;
            }

            return DelayUnitMilliseconds;
        }

        private static double GetDelaySliderMinimum(int unitIndex)
        {
            return NormalizeDelayUnitIndex(unitIndex) switch
            {
                DelayUnitMilliseconds => 100.0,
                DelayUnitSeconds => 1.0,
                DelayUnitMinutes => 1.0,
                _ => 100.0,
            };
        }

        private static double GetDelaySliderMaximum(int unitIndex)
        {
            return NormalizeDelayUnitIndex(unitIndex) switch
            {
                DelayUnitMilliseconds => 1000.0,
                DelayUnitSeconds => 59.0,
                DelayUnitMinutes => 60.0,
                _ => 1000.0,
            };
        }

        private static double GetDelaySliderTickFrequency(int unitIndex)
        {
            return NormalizeDelayUnitIndex(unitIndex) switch
            {
                DelayUnitMilliseconds => 100.0,
                DelayUnitSeconds => 1.0,
                DelayUnitMinutes => 1.0,
                _ => 100.0,
            };
        }

        private static double GetDelaySliderValue(int delayTimeMs, int unitIndex)
        {
            unitIndex = NormalizeDelayUnitIndex(unitIndex);
            if (delayTimeMs <= 0)
            {
                return GetDelaySliderMinimum(unitIndex);
            }

            return unitIndex switch
            {
                DelayUnitMilliseconds => Math.Clamp(Math.Round(delayTimeMs / 100.0) * 100.0, 100.0, 1000.0),
                DelayUnitSeconds => Math.Clamp(Math.Round(delayTimeMs / 1000.0), 1.0, 59.0),
                DelayUnitMinutes => Math.Clamp(Math.Round(delayTimeMs / 60000.0), 1.0, 60.0),
                _ => 100.0,
            };
        }

        private static int ConvertDelayValueToMilliseconds(double sliderValue, int unitIndex)
        {
            unitIndex = NormalizeDelayUnitIndex(unitIndex);

            return unitIndex switch
            {
                DelayUnitMilliseconds => (int)(Math.Clamp(Math.Round(sliderValue / 100.0) * 100.0, 100.0, 1000.0)),
                DelayUnitSeconds => (int)(Math.Clamp(Math.Round(sliderValue), 1.0, 59.0) * 1000.0),
                DelayUnitMinutes => (int)(Math.Clamp(Math.Round(sliderValue), 1.0, 60.0) * 60000.0),
                _ => 100,
            };
        }

        private static string FormatDelaySliderValue(double sliderValue, int unitIndex)
        {
            return NormalizeDelayUnitIndex(unitIndex) == DelayUnitMilliseconds
                ? sliderValue.ToString("F0")
                : sliderValue.ToString("F0");
        }

        public static ChannelPriority GetPriority(ChannelCategory category)
        {
            return category switch
            {
                ChannelCategory.HeatedSeats => ChannelPriority.Low,
                ChannelCategory.HeatedSteeringWheel => ChannelPriority.Low,
                ChannelCategory.Infotainment => ChannelPriority.Low,
                ChannelCategory.USBAccessoryPower => ChannelPriority.Low,
                ChannelCategory.DataLogger => ChannelPriority.Low,
                ChannelCategory.Telemetry => ChannelPriority.Low,
                ChannelCategory.CameraSystem => ChannelPriority.Low,
                ChannelCategory.LapTimer => ChannelPriority.Low,
                ChannelCategory.CoolSuitPump => ChannelPriority.Low,
                ChannelCategory.InteriorLights => ChannelPriority.Low,
                ChannelCategory.Auxiliary => ChannelPriority.Low,
                ChannelCategory.Spare => ChannelPriority.Low,
                ChannelCategory.Custom => ChannelPriority.Low,
                ChannelCategory.HVACBlower => ChannelPriority.Medium,
                ChannelCategory.ACClutch => ChannelPriority.Medium,
                ChannelCategory.PitLimiter => ChannelPriority.Medium,
                _ => ChannelPriority.Critical,
            };
        }

        public enum ChannelType
        {
            Digital,                    // Digital input
            PWM,                        // Digital input, PWM output
            Analogue,                   // Analog input (threshold detection)
            AnalogueScaled,             // Analog input, PWM output
            CAN,                        // CAN bus controlled digital output
            CAN_PWM,                    // CAN bus controlled PWM output
            Intermittent,               // Digital intermittent output
            MATH                        // Structured math output
        }

        public enum MathOutputModeType : byte
        {
            Digital = 0,
            PWM = 1,
        }

        public enum MathOperation : byte
        {
            ADD = 0,
            SUBTRACT = 1,
            MULTIPLY = 2,
            DIVIDE = 3,
            GREATER_THAN = 4,
            GREATER_EQUAL = 5,
            LESS_THAN = 6,
            LESS_EQUAL = 7,
            EQUAL = 8,
            NOT_EQUAL = 9,
            AND = 10,
            OR = 11,
            XOR = 12,
            NOT = 13,
            MIN = 14,
            MAX = 15,
            ABS = 16,
        }

        public enum MathOperandType : byte
        {
            Constant = 0,
            Channel = 1,
            AnalogueInput = 2,
            SystemValue = 3,
            Rule = 4,
            DigitalInput = 5,
        }

        public enum MathSystemValue : byte
        {
            SystemTemperature = 0,
            SIMModuleTemperature = 1,
            IMUTemperature = 2,
            BatteryVoltage = 3,
            SystemCurrent = 4,
            AccelerationX = 5,
            AccelerationY = 6,
            AccelerationZ = 7,
            GyroscopeX = 8,
            GyroscopeY = 9,
            GyroscopeZ = 10,
            MagneticFieldX = 11,
            MagneticFieldY = 12,
            MagneticFieldZ = 13,
        }

        public sealed class MathRule : ObservableObject
        {
            private byte _operation;
            private byte _hysteresisEnabled;
            private float _onThreshold;
            private float _offThreshold;
            private byte _operandAType;
            private byte _operandAIndex;
            private float _operandAConstant;
            private byte _operandBType;
            private byte _operandBIndex;
            private float _operandBConstant;
            private float _runtimeValue;
            private byte _runtimeError;
            private byte _evaluated;

            public byte Operation
            {
                get => _operation;
                set => SetProperty(ref _operation, value);
            }

            public byte HysteresisEnabled
            {
                get => _hysteresisEnabled;
                set
                {
                    if (SetProperty(ref _hysteresisEnabled, value))
                    {
                        OnPropertyChanged(nameof(IsHysteresisEnabled));
                    }
                }
            }

            public float OnThreshold
            {
                get => _onThreshold;
                set => SetProperty(ref _onThreshold, value);
            }

            public float OffThreshold
            {
                get => _offThreshold;
                set => SetProperty(ref _offThreshold, value);
            }

            public byte OperandAType
            {
                get => _operandAType;
                set => SetProperty(ref _operandAType, value);
            }

            public byte OperandAIndex
            {
                get => _operandAIndex;
                set => SetProperty(ref _operandAIndex, value);
            }

            public float OperandAConstant
            {
                get => _operandAConstant;
                set => SetProperty(ref _operandAConstant, value);
            }

            public byte OperandBType
            {
                get => _operandBType;
                set => SetProperty(ref _operandBType, value);
            }

            public byte OperandBIndex
            {
                get => _operandBIndex;
                set => SetProperty(ref _operandBIndex, value);
            }

            public float OperandBConstant
            {
                get => _operandBConstant;
                set => SetProperty(ref _operandBConstant, value);
            }

            public float RuntimeValue
            {
                get => _runtimeValue;
                set => SetProperty(ref _runtimeValue, value);
            }

            public byte RuntimeError
            {
                get => _runtimeError;
                set
                {
                    if (SetProperty(ref _runtimeError, value))
                    {
                        OnPropertyChanged(nameof(HasRuntimeError));
                    }
                }
            }

            public byte Evaluated
            {
                get => _evaluated;
                set
                {
                    if (SetProperty(ref _evaluated, value))
                    {
                        OnPropertyChanged(nameof(IsEvaluated));
                    }
                }
            }

            [JsonIgnore]
            public bool IsHysteresisEnabled
            {
                get => HysteresisEnabled != 0;
                set => HysteresisEnabled = value ? (byte)1 : (byte)0;
            }

            [JsonIgnore]
            public bool IsEvaluated => Evaluated != 0;

            [JsonIgnore]
            public bool HasRuntimeError => RuntimeError != 0;
        }

        public enum DelayedOffTriggerMode
        {
            AssignedInput = 0,
            IgnitionOff = 1,
        }

        public enum ChannelCategory
        {
            ECUPower,
            IgnitionCoils,
            FuelPump,
            FuelInjectors,
            EngineSensorsSupply,
            DriveByWire,
            Headlights,
            BrakeLights,
            Indicators,
            HazardLights,
            Horn,
            Wipers,
            WasherPump,
            ABSBrakeSystem,
            PowerSteering,
            CoolingFan,
            OilCoolerFan,
            WaterPump,
            IntercoolerPump,
            TransmissionPump,
            TailLights,
            DRL,
            ReverseLights,
            InteriorLights,
            DashCluster,
            GearSelector,
            HeatedSeats,
            HeatedSteeringWheel,
            HVACBlower,
            ACClutch,
            Infotainment,
            USBAccessoryPower,
            DataLogger,
            Telemetry,
            CameraSystem,
            LapTimer,
            CoolSuitPump,
            FireSuppression,
            RainLight,
            PitLimiter,
            Auxiliary,
            Spare,
            Custom
        }

        public enum ChannelPriority
        {
            Critical,
            Medium,
            Low,
        }
    }
}

