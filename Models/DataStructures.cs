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

        public ObservableCollection<CustomCanMessage> CustomCanMessagesLiveData { get; set; }

        public ObservableCollection<CustomCanMessage> CustomCanMessagesStaticData { get; set; }

        public ObservableCollection<CustomCanSignal> CustomCanSignalsLiveData { get; set; }

        public ObservableCollection<CustomCanSignal> CustomCanSignalsStaticData { get; set; }

        public ObservableCollection<CustomCanSignalMetadata> CustomCanSignalsMetadata { get; set; }

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

            CustomCanMessagesLiveData = new ObservableCollection<CustomCanMessage>();
            CustomCanMessagesStaticData = new ObservableCollection<CustomCanMessage>();
            for (int i = 0; i < Constants.MAX_CUSTOM_CAN_MESSAGES; i++)
            {
                CustomCanMessagesLiveData.Add(new CustomCanMessage { Index = i, Dlc = 8, TimeoutMs = 1000 });
                CustomCanMessagesStaticData.Add(new CustomCanMessage { Index = i, Dlc = 8, TimeoutMs = 1000 });
            }

            CustomCanSignalsLiveData = new ObservableCollection<CustomCanSignal>();
            CustomCanSignalsStaticData = new ObservableCollection<CustomCanSignal>();
            CustomCanSignalsMetadata = new ObservableCollection<CustomCanSignalMetadata>();
            for (int i = 0; i < Constants.MAX_CUSTOM_CAN_SIGNALS; i++)
            {
                CustomCanSignalsLiveData.Add(new CustomCanSignal { Index = i, Factor = 1.0f, Name = $"Signal {i + 1}" });
                CustomCanSignalsStaticData.Add(new CustomCanSignal { Index = i, Factor = 1.0f, Name = $"Signal {i + 1}" });
            }

            systemParams = new SystemParameters();
            cellularParams = new CellularParameters();
        }
    }
}
