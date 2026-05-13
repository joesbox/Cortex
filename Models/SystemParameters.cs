using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Cortex.Models
{
    [Serializable]
    public partial class SystemParameters : ObservableObject
    {
        [ObservableProperty]
        public Int32 _SystemTemperature;        // Internal system (Teensy processor) temperature

        [ObservableProperty]
        public float _SIMModuleTemp;            // SIM7600 internal module temperature

        [ObservableProperty]
        public float _IMUTemp;                  // BMI270 temperature

        [ObservableProperty]
        public bool _CANResEnabled;             // CAN bus termination resistor enableda

        [ObservableProperty]
        public float _VBatt;                    // Battery supply voltage

        [ObservableProperty]
        public float _SystemCurrent;            // Total current draw for all enabled channels

        [ObservableProperty]
        public float _SystemCurrentLimit;       // System current limit

        [ObservableProperty]
        public UInt16 _ErrorFlags;              // Bitmask for system error flags

        [ObservableProperty]
        public UInt16 _ChannelDataCANID;        // Channel data CAN ID (transmit)

        [ObservableProperty]
        public UInt16 _DigitalInputDataCANID;   // Digital input data CAN ID (transmit)

        [ObservableProperty]
        public UInt16 _AnalogueInputDataCANID;  // Analogue input data CAN ID (transmit)

        [ObservableProperty]
        public UInt16 _SystemDataCANID;         // System data CAN ID (transmit)

        [ObservableProperty]
        public UInt16 _SystemConfigCANID;       // System configuration CAN ID (receive)    

        [ObservableProperty]
        public UInt16 _ConfigDataCANID;         // Configuration data CAN ID (receive)

        [ObservableProperty]
        public UInt32 _IMUWakeWindow;           // IMU wake window in ms. Time that the controller stays awake after motion detected

        [ObservableProperty]
        public bool _SpeedUnitPref;             // Speed unit preference (0 = mph, 1 = km/h)

        [ObservableProperty]
        public bool _DistanceUnitPref;          // Distance unit preference (0 = metres, 1 = feet)

        [ObservableProperty]
        public bool _AllowData;                 // Allow GSM data

        [ObservableProperty]
        public bool _AllowGPS;                  // Allow GPS

        [ObservableProperty]
        public bool _AllowMotionDetect;         // Allow GPS

        [ObservableProperty]
        public UInt16 _MobileSignalPercent;     // Mobile signal strength

        [ObservableProperty]
        public string? _TimeZoneId;             // Cortex-selected time zone identifier

        [ObservableProperty]
        public byte[] _TimeZoneRule = Array.Empty<byte>(); // Serialized controller time zone and DST rule
    }
}
