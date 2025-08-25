using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cortex.Models
{
    [Serializable]
    public partial class DigitalInput : ObservableObject
    {
        [ObservableProperty]
        private int _inputNumber;

        [ObservableProperty]
        private bool _isActiveHigh;

        public DigitalInput(int inputNumber, bool isActiveHigh)
        {
            _inputNumber = inputNumber;
            _isActiveHigh = isActiveHigh;
        }
    }
}
