using CommunityToolkit.Mvvm.ComponentModel;
using Cortex.Models;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class DigitalInputNodeViewModel : IoFlowNodeViewModel
{
    public DigitalInputNodeViewModel(DigitalInput input, Avalonia.Point location)
        : base($"Digital Input {input.InputNumber}", location, ConnectorPinPalette.DigitalInput)
    {
        Input = input;
        _liveStatusText = "Unknown";
    }

    public DigitalInput Input { get; }

    public int InputNumber => Input.InputNumber;

    [ObservableProperty]
    private string _liveStatusText;

    public void UpdateLiveState(DigitalInput? liveInput)
    {
        LiveStatusText = liveInput?.LiveElectricalState switch
        {
            true => "High",
            false => "Low",
            null => "Unknown",
        };
    }
}
