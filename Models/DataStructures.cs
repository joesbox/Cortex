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

        [ObservableProperty]
        public CellularParameters cellularParams;

        [ObservableProperty]
        public CellularParameters cellularParamsStaticData;

        public DataStructures()
        {
            channelsLiveData = new ObservableCollection<OutputChannel>();
            channelsStaticData = new ObservableCollection<OutputChannel>();
            systemParamsStaticData = new SystemParameters();
            cellularParamsStaticData = new CellularParameters();

            for (int i = 0; i < Constants.NUM_OUTPUT_CHANNELS; i++)
            {
                channelsLiveData.Add(new OutputChannel { ChannelNumber = i + 1 });
                channelsStaticData.Add(new OutputChannel { ChannelNumber = i + 1 });
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
                analogueInputsStaticData.Add(new AnalogueInput(i + 1, false, false, null));
                analogueInputsLiveData.Add(new AnalogueInput(i + 1, false, false, null));
            }

            systemParams = new SystemParameters();
            cellularParams = new CellularParameters();
        }
    }
}
