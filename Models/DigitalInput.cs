using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Cortex.Models
{
    [Serializable]
    public partial class DigitalInput : ObservableObject
    {
        [ObservableProperty]
        private int _inputNumber;

        [ObservableProperty]
        private bool _isActiveHigh;

        private bool? _liveElectricalState;

        public bool? LiveElectricalState
        {
            get => _liveElectricalState;
            set => SetProperty(ref _liveElectricalState, value);
        }

        public DigitalInput(int inputNumber, bool isActiveHigh)
        {
            _inputNumber = inputNumber;
            _isActiveHigh = isActiveHigh;
            _liveElectricalState = null;
        }
    }
}
