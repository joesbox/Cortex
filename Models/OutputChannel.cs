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
        public byte _Retry;                     // Retry after current threshold reached

        [ObservableProperty]
        public byte _RetryCount;                // Number of retries

        [ObservableProperty]
        public float _RetryDelay;               // Retry delay in seconds

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
        public bool _RunOn;                     // Run channel after ignition off

        [ObservableProperty]
        int _RunOnTime;                         // Run channel time after ignition off in milliseconds

        [ObservableProperty]
        public byte _ErrorFlags;                // Bitmask for channel error flags

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

