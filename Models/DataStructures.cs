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
        public ObservableCollection<DigitalInput> digitalInputsLiveData;

        [ObservableProperty]
        public ObservableCollection<DigitalInput> digitalInputsStaticData;

        [ObservableProperty]
        public ObservableCollection<AnalogueInput> analogueInputsLiveData;

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

            digitalInputsLiveData = new ObservableCollection<DigitalInput>();
            digitalInputsStaticData = new ObservableCollection<DigitalInput>();

            for (int i = 0; i < Constants.NUM_DIGITAL_INPUTS; i++)
            {
                digitalInputsStaticData.Add(new DigitalInput(i + 1, true));
                digitalInputsLiveData.Add(new DigitalInput(i + 1, true));
            }

            analogueInputsLiveData = new ObservableCollection<AnalogueInput>();
            analogueInputsStaticData = new ObservableCollection<AnalogueInput>();

            for (int i = 0; i < Constants.NUM_ANALOGUE_INPUTS; i++)
            {
                analogueInputsStaticData.Add(new AnalogueInput(i + 1, false, false, true, true, 0.0f, 0.0f, 0.0f, 0.0f, 0, 0));
                analogueInputsLiveData.Add(new AnalogueInput(i + 1, false, false, true, true, 0.0f, 0.0f, 0.0f, 0.0f, 0, 0));
            }

            systemParams = new SystemParameters();
        }
    }
}
