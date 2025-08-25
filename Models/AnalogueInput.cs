using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cortex.Models
{
    [Serializable]
    public partial class AnalogueInput : ObservableObject
    {
        [ObservableProperty]
        private int _inputNumber;

        [ObservableProperty]
        private bool _pullUpEnable;         // Pull-up resistor enable flag

        [ObservableProperty]
        private bool _pullDownEnable;       // Pull-down resistor enable flag

        [ObservableProperty]
        private bool _isDigital;            // Flag to indicate if the input is digital

        [ObservableProperty]
        private bool _isThreshold;          // Flag to indicate if the input is threshold based or PWM scaled (if not digital)

        [ObservableProperty]
        private float _OnThreshold;         // On threshold value

        [ObservableProperty]
        private float _OffThreshold;        // Off threshold value

        [ObservableProperty]
        private float _inputScaleLow;       // Low end of input scaling range

        [ObservableProperty]
        private float _inputScaleHigh;      // High end of input scaling range

        [ObservableProperty]
        private byte _pwmLowValue;          // PWM low value for scaled input

        [ObservableProperty]
        private byte _pwmHighValue;         // PWM high value for scaled input

        // Constructor
        public AnalogueInput(int inputNumber, bool pullUpEnable, bool pullDownEnable, bool isDigital, bool isThreshold, float onThreshold, float offThreshold, float inputScaleLow, float inputScaleHigh, byte pwmLowValue, byte pwmHighValue)
        {
            _inputNumber = inputNumber;
            _pullUpEnable = pullUpEnable;
            _pullDownEnable = pullDownEnable;
            _isDigital = isDigital;
            _isThreshold = isThreshold;
            _OnThreshold = onThreshold;
            _OffThreshold = offThreshold;
            _inputScaleLow = inputScaleLow;
            _inputScaleHigh = inputScaleHigh;
            _pwmLowValue = pwmLowValue;
            _pwmHighValue = pwmHighValue;
        }
    }
}
