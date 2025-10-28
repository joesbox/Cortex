using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.ObjectModel;

namespace Cortex.Models
{

    [Serializable]
    public partial class DataStructures : ObservableObject
    {
        [ObservableProperty]
        public ObservableCollection<OutputChannel> channelsLiveData;

        [ObservableProperty]
        public ObservableCollection<OutputChannel> channelsStaticData;

        [ObservableProperty]
        public ObservableCollection<DigitalInput> digitalInputs;

        [ObservableProperty]
        public ObservableCollection<AnalogueInput> analogueInputsStaticData;

        [ObservableProperty]
        public SystemParameters systemParams;

        [ObservableProperty]
        public SystemParameters systemParamsStaticData;

        public DataStructures()
        {
            channelsLiveData = new ObservableCollection<OutputChannel>();
            channelsStaticData = new ObservableCollection<OutputChannel>();
            systemParamsStaticData = new SystemParameters();

            for (int i = 0; i < Constants.NUM_OUTPUT_CHANNELS; i++)
            {
                channelsLiveData.Add(new OutputChannel());
                channelsStaticData.Add(new OutputChannel());
            }

            digitalInputs = new ObservableCollection<DigitalInput>();

            for (int i = 0; i < Constants.NUM_DIGITAL_INPUTS; i++)
            {
                digitalInputs.Add(new DigitalInput(i + 1, true));
            }

            analogueInputsStaticData = new ObservableCollection<AnalogueInput>();

            for (int i = 0; i < Constants.NUM_ANALOGUE_INPUTS; i++)
            {
                analogueInputsStaticData.Add(new AnalogueInput(i + 1, false, false, true, true, 0.0f, 0.0f, 0.0f, 0.0f, 0, 0));
            }

            systemParams = new SystemParameters();



        }
    }
}
