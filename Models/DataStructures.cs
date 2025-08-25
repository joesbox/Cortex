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
        public List<OutputChannel> channelsLiveData;

        [ObservableProperty]
        public List<OutputChannel> channelsStaticData;

        [ObservableProperty]
        public List<DigitalInput> digitalInputs;

        [ObservableProperty]
        public List<AnalogueInput> analogueInputsStaticData;

        [ObservableProperty]
        public SystemParameters parameters;

        public DataStructures()
        {
            channelsLiveData = new List<OutputChannel>();
            channelsStaticData = new List<OutputChannel>();

            for (int i = 0; i < Constants.NUM_OUTPUT_CHANNELS; i++)
            {
                channelsLiveData.Add(new OutputChannel());
                channelsStaticData.Add(new OutputChannel());
            }

            digitalInputs = new List<DigitalInput>();

            for (int i = 0; i < Constants.NUM_DIGITAL_INPUTS; i++)
            {
                digitalInputs.Add(new DigitalInput(i + 1, true));
            }

            analogueInputsStaticData = new List<AnalogueInput>();

            for (int i = 0; i < Constants.NUM_ANALOGUE_INPUTS; i++)
            {
                analogueInputsStaticData.Add(new AnalogueInput(i + 1, false, false, true, true, 0.0f, 0.0f, 0.0f, 0.0f, 0, 0));
            }

            parameters = new SystemParameters();

        }
    }
}
