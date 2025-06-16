using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.VisualBasic;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Cortex.Models
{

    [Serializable]
    public partial class DataStructures : ObservableObject
    {
        [ObservableProperty]
        public List<OutputChannel> channels;

        [ObservableProperty]
        public List<DigitalInput> digitalInputs;

        [ObservableProperty]
        public List<AnalogueInput> analogueInputs;

        [ObservableProperty]
        public SystemParameters parameters;

        public DataStructures()
        {
            channels = new List<OutputChannel>();

            for (int i = 0; i < Constants.NUM_OUTPUT_CHANNELS; i++)
            {
                channels.Add(new OutputChannel());
            }

            digitalInputs = new List<DigitalInput>();

            for (int i = 0; i < Constants.NUM_DIGITAL_INPUTS; i++)
            {
                digitalInputs.Add(new DigitalInput());
            }

            analogueInputs = new List<AnalogueInput>();

            for (int i = 0; i < Constants.NUM_ANALOGUE_INPUTS; i++)
            {
                analogueInputs.Add(new AnalogueInput());
            }

            parameters = new SystemParameters();

        }
    }
}
