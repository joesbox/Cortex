using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Cortex.Models
{
    [Serializable]
    public partial class AnalogueInput : ObservableObject
    {
        public enum AnalogueChannelType : byte
        {
            RawVoltage = 0,
            Active = 1,
            Passive = 2,
            NTC = 3,
            Digital = 4,
        }

        public enum AnalogueUnits : byte
        {
            Volts = 0,
            Amps = 1,
            Celsius = 2,
            Fahrenheit = 3,
            Percent = 4,
            RPM = 5,
            KPH = 6,
            MPH = 7,
            Bar = 8,
            PSI = 9,
        }

        [ObservableProperty]
        private int _inputNumber;

        [ObservableProperty]
        private AnalogueChannelType _chanType;

        [ObservableProperty]
        private AnalogueUnits _units;

        [ObservableProperty]
        private byte _calibrationPoints;

        [ObservableProperty]
        private bool _pullUpEnable;         // Pull-up resistor enable flag

        [ObservableProperty]
        private bool _pullDownEnable;       // Pull-down resistor enable flag

        [ObservableProperty]
        private float? _inputVoltage;       // Live input voltage

        [ObservableProperty]
        private float? _inputValue;         // Live converted value in selected units

        [ObservableProperty]
        private float _calibrationVolt1;

        [ObservableProperty]
        private float _calibrationValue1;

        [ObservableProperty]
        private float _calibrationVolt2;

        [ObservableProperty]
        private float _calibrationValue2;

        [ObservableProperty]
        private float _calibrationVolt3;

        [ObservableProperty]
        private float _calibrationValue3;

        [ObservableProperty]
        private float _configRangeMin;

        [ObservableProperty]
        private float _configRangeMax;

        [ObservableProperty]
        private float _ntcBeta;

        [ObservableProperty]
        private float _ntcNominalResistance;

        public bool IsRawVoltageMode => ChanType == AnalogueChannelType.RawVoltage;

        public bool IsUnitsSelectable => ChanType != AnalogueChannelType.RawVoltage && ChanType != AnalogueChannelType.Digital;

        public bool IsNtcMode => ChanType == AnalogueChannelType.NTC;

        public bool IsCalibrationMode => ChanType == AnalogueChannelType.Active || ChanType == AnalogueChannelType.Passive;

        public bool HasDetailSettings => IsCalibrationMode || IsNtcMode;

        public bool IsThreePointCalibration => CalibrationPoints >= 3;

        public bool AllowPullResistors => ChanType == AnalogueChannelType.Passive || ChanType == AnalogueChannelType.NTC || ChanType == AnalogueChannelType.Digital;

        // Constructor
        public AnalogueInput(int inputNumber, bool pullUpEnable, bool pullDownEnable, float? inputVoltage = null)
        {
            _inputNumber = inputNumber;
            _chanType = AnalogueChannelType.RawVoltage;
            _units = AnalogueUnits.Volts;
            _calibrationPoints = 2;
            _pullUpEnable = pullUpEnable;
            _pullDownEnable = pullDownEnable;
            _inputVoltage = inputVoltage;
            _inputValue = null;
            _calibrationVolt1 = 0.0f;
            _calibrationValue1 = 0.0f;
            _calibrationVolt2 = 5.0f;
            _calibrationValue2 = 5.0f;
            _calibrationVolt3 = 5.0f;
            _calibrationValue3 = 5.0f;
            _configRangeMin = 0.0f;
            _configRangeMax = 5.0f;
            _ntcBeta = 3950.0f;
            _ntcNominalResistance = 10000.0f;
        }

        partial void OnChanTypeChanged(AnalogueChannelType value)
        {
            NormalizeForType();
            OnPropertyChanged(nameof(IsRawVoltageMode));
            OnPropertyChanged(nameof(IsUnitsSelectable));
            OnPropertyChanged(nameof(IsNtcMode));
            OnPropertyChanged(nameof(IsCalibrationMode));
            OnPropertyChanged(nameof(HasDetailSettings));
            OnPropertyChanged(nameof(AllowPullResistors));
        }

        partial void OnUnitsChanged(AnalogueUnits value)
        {
            if (ChanType == AnalogueChannelType.RawVoltage)
            {
                if (value != AnalogueUnits.Volts)
                {
                    Units = AnalogueUnits.Volts;
                }
                return;
            }

            if (ChanType == AnalogueChannelType.Digital)
            {
                if (value != AnalogueUnits.Volts)
                {
                    Units = AnalogueUnits.Volts;
                }
                return;
            }

            if (ChanType == AnalogueChannelType.NTC && value != AnalogueUnits.Celsius && value != AnalogueUnits.Fahrenheit)
            {
                Units = AnalogueUnits.Celsius;
            }
        }

        partial void OnCalibrationPointsChanged(byte value)
        {
            if (value < 2)
            {
                CalibrationPoints = 2;
                return;
            }

            if (value > 3)
            {
                CalibrationPoints = 3;
                return;
            }

            OnPropertyChanged(nameof(IsThreePointCalibration));
        }

        partial void OnConfigRangeMinChanged(float value)
        {
            if (value >= ConfigRangeMax)
            {
                ConfigRangeMax = value + 1.0f;
            }
        }

        partial void OnConfigRangeMaxChanged(float value)
        {
            if (value <= ConfigRangeMin)
            {
                ConfigRangeMin = value - 1.0f;
            }
        }

        partial void OnPullUpEnableChanged(bool value)
        {
            if (!AllowPullResistors)
            {
                if (value)
                {
                    PullUpEnable = false;
                }
                return;
            }

            if (value && PullDownEnable)
            {
                PullDownEnable = false;
            }

            if (!value && !PullDownEnable)
            {
                PullDownEnable = true;
            }
        }

        partial void OnPullDownEnableChanged(bool value)
        {
            if (!AllowPullResistors)
            {
                if (value)
                {
                    PullDownEnable = false;
                }
                return;
            }

            if (value && PullUpEnable)
            {
                PullUpEnable = false;
            }

            if (!value && !PullUpEnable)
            {
                PullUpEnable = true;
            }
        }

        private void NormalizeForType()
        {
            if (CalibrationPoints < 2)
            {
                CalibrationPoints = 2;
            }
            if (CalibrationPoints > 3)
            {
                CalibrationPoints = 3;
            }

            if (NtcBeta < 1.0f)
            {
                NtcBeta = 3950.0f;
            }

            if (NtcNominalResistance < 1.0f)
            {
                NtcNominalResistance = 10000.0f;
            }

            switch (ChanType)
            {
                case AnalogueChannelType.RawVoltage:
                    Units = AnalogueUnits.Volts;
                    PullUpEnable = false;
                    PullDownEnable = false;
                    break;
                case AnalogueChannelType.Active:
                    EnsureConfigRangeFromCalibration();
                    PullUpEnable = false;
                    PullDownEnable = false;
                    break;
                case AnalogueChannelType.Passive:
                    EnsureConfigRangeFromCalibration();
                    if (!PullUpEnable && !PullDownEnable)
                    {
                        PullUpEnable = true;
                    }
                    if (PullUpEnable && PullDownEnable)
                    {
                        PullDownEnable = false;
                    }
                    break;
                case AnalogueChannelType.NTC:
                    PullUpEnable = true;
                    PullDownEnable = false;
                    NtcNominalResistance = 10000.0f;
                    if (Units != AnalogueUnits.Celsius && Units != AnalogueUnits.Fahrenheit)
                    {
                        Units = AnalogueUnits.Celsius;
                    }
                    break;
                case AnalogueChannelType.Digital:
                    Units = AnalogueUnits.Volts;
                    if (!PullUpEnable && !PullDownEnable)
                    {
                        PullDownEnable = true;
                    }
                    if (PullUpEnable && PullDownEnable)
                    {
                        PullDownEnable = false;
                    }
                    break;
            }
        }

        private (float Min, float Max) GetCalibrationValueBounds()
        {
            float min = Math.Min(CalibrationValue1, CalibrationValue2);
            float max = Math.Max(CalibrationValue1, CalibrationValue2);

            if (CalibrationPoints >= 3)
            {
                min = Math.Min(min, CalibrationValue3);
                max = Math.Max(max, CalibrationValue3);
            }

            return (min, max);
        }

        private void EnsureConfigRangeFromCalibration()
        {
            bool invalidRange = ConfigRangeMax <= ConfigRangeMin;
            if (!invalidRange)
            {
                return;
            }

            var bounds = GetCalibrationValueBounds();
            ConfigRangeMin = bounds.Min;
            ConfigRangeMax = bounds.Max;

            if (ConfigRangeMax <= ConfigRangeMin)
            {
                ConfigRangeMax = ConfigRangeMin + 1.0f;
            }
        }
    }
}
