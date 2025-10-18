using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cortex.Models
{
    [Serializable]
    public partial class OutputChannel : ObservableObject
    {
        [ObservableProperty]
        public ChannelType _ChanType;           // Channel type

        [ObservableProperty]
        public byte _PWMSetDuty;                // Current duty set percentage

        [ObservableProperty]
        public byte _Enabled;                   // Channel enabled flag        

        [ObservableProperty]
        public char[]? _Name;                   // Channel name (3 characters)

        [ObservableProperty]
        public int _AnalogRaw;                  // Raw analog value. Used for calibration

        [ObservableProperty]
        public float _CurrentValue;             // Active current value

        [ObservableProperty]
        public float _CurrentLimitHigh;         // Absolute current limit high

        [ObservableProperty]
        public float _CurrentThresholdHigh;     // Turn off threshold high

        [ObservableProperty]
        public float _CurrentThresholdLow;      // Turn off threshold low (open circuit detection)

        [ObservableProperty]
        public byte _RetryCount;                // Number of retries

        [ObservableProperty]
        public float _InrushDelay;              // Inrush delay in seconds

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
        public byte _RunOn;                     // Run channel after ignition off

        [ObservableProperty]
        public int _RunOnTime;                         // Run channel time after ignition off in milliseconds

        [ObservableProperty]
        public byte _ErrorFlags;                // Bitmask for channel error flags

        // Error flag constants
        private const byte CHN_OVERCURRENT_RANGE = 0x01;
        private const byte CHN_OVERCURRENT_LIMIT = 0x02;
        private const byte CHN_UNDERCURRENT_RANGE = 0x04;
        private const byte IS_FAULT = 0x08;
        private const byte RETRY_LOCKOUT = 0x10;

        // Individual error bit properties
        public bool HasOvercurrentRange => (ErrorFlags & CHN_OVERCURRENT_RANGE) != 0;
        public bool HasOvercurrentLimit => (ErrorFlags & CHN_OVERCURRENT_LIMIT) != 0;
        public bool HasUndercurrentRange => (ErrorFlags & CHN_UNDERCURRENT_RANGE) != 0;
        public bool HasISFault => (ErrorFlags & IS_FAULT) != 0;
        public bool HasRetryLockout => (ErrorFlags & RETRY_LOCKOUT) != 0;

        // Overall status check
        public bool HasAnyError => ErrorFlags != 0;

        // Notify property changes when ErrorFlags changes
        partial void OnErrorFlagsChanged(byte value)
        {
            OnPropertyChanged(nameof(HasOvercurrentRange));
            OnPropertyChanged(nameof(HasOvercurrentLimit));
            OnPropertyChanged(nameof(HasUndercurrentRange));
            OnPropertyChanged(nameof(HasISFault));
            OnPropertyChanged(nameof(HasRetryLockout));
            OnPropertyChanged(nameof(HasAnyError));
        }

        public enum ChannelType
        {
            DIG,                // Digital input
            DIG_PWM,            // Digital input, PWM output
            ANA,                // Analog input (threshold detection)
            ANA_PWM,            // Analog input, PWM output
            CAN_DIGITAL,        // CAN bus controlled digital output
            CAN_PWM             // CAN bus controlled PWM output
        }
    }
}

