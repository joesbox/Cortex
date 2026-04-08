using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
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
        public byte _RunOn;                     // Run channel after ignition off

        [ObservableProperty]
        public int _RunOnTime;                  // Run channel time after ignition off in milliseconds

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
        private string _AnalogueInputVoltageDisplay = "-";


        public IAsyncRelayCommand HoldToggleCommand { get; }
        public IRelayCommand CancelHoldCommand { get; }

        // Computed property for PWM channel detection
        public bool IsPWMChannel => ChanType == ChannelType.PWM ||
                        ChanType == ChannelType.CAN_PWM;

        public bool IsAnalogueThresholdChannel => ChanType == ChannelType.Analogue;

        public bool IsAnalogueScaledChannel => ChanType == ChannelType.AnalogueScaled;

        public bool IsIntermittentChannel => ChanType == ChannelType.Intermittent;

        public bool IsAnalogueChannel => IsAnalogueThresholdChannel || IsAnalogueScaledChannel;

        public ChannelPriority Priority => GetPriority(Category);

        public OutputChannel()
        {
            HoldToggleCommand = new AsyncRelayCommand(OnHoldAsync);
            CancelHoldCommand = new RelayCommand(CancelHold);
        }

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
            OnPropertyChanged(nameof(IsAnalogueChannel));
        }

        partial void OnCategoryChanged(ChannelCategory value)
        {
            OnPropertyChanged(nameof(Priority));
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
            Intermittent                // Digital intermittent output
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

