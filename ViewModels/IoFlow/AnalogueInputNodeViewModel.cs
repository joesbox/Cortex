using CommunityToolkit.Mvvm.ComponentModel;
using Cortex.Models;
using System.Globalization;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class AnalogueInputNodeViewModel : IoFlowNodeViewModel
{
    public AnalogueInputNodeViewModel(AnalogueInput input, Avalonia.Point location)
        : base($"Analogue Input {input.InputNumber}", location, ConnectorPinPalette.AnalogueInput)
    {
        Input = input;
        _liveVoltageText = "-";
        _liveValueText = "-";
    }

    public AnalogueInput Input { get; }

    public int InputNumber => Input.InputNumber;

    [ObservableProperty]
    private string _liveVoltageText;

    [ObservableProperty]
    private string _liveValueText;

    public void UpdateLiveState(AnalogueInput? liveInput)
    {
        LiveVoltageText = liveInput?.InputVoltage is float voltage
            ? $"{voltage.ToString("F2", CultureInfo.InvariantCulture)} V"
            : "-";

        LiveValueText = liveInput?.InputValue is float value
            ? value.ToString("F2", CultureInfo.InvariantCulture)
            : "-";
    }
}
