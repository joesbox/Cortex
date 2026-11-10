using CommunityToolkit.Mvvm.ComponentModel;
using Cortex.Models;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class AnalogueInputNodeViewModel : IoFlowNodeViewModel
{
    private static readonly AnalogueUnitDisplay[] AllAnalogueUnits =
    [
        new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Volts, Label = "Volts (V)" },
        new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Amps, Label = "Amps (A)" },
        new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Celsius, Label = "Celsius (°C)" },
        new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Fahrenheit, Label = "Fahrenheit (°F)" },
        new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Percent, Label = "Percent (%)" },
        new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.RPM, Label = "RPM" },
        new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.KPH, Label = "KPH" },
        new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.MPH, Label = "MPH" },
        new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.Bar, Label = "Bar" },
        new AnalogueUnitDisplay { Units = AnalogueInput.AnalogueUnits.PSI, Label = "PSI" },
    ];

    private static readonly byte[] CalibrationPointOptions = [2, 3];

    public AnalogueInputNodeViewModel(AnalogueInput input, Avalonia.Point location)
        : base($"Analogue Input {input.InputNumber}", location, ConnectorPinPalette.AnalogueInput)
    {
        Input = input;
        Input.PropertyChanged += OnInputPropertyChanged;
        _liveVoltageText = "-";
        _liveValueText = "-";
    }

    public AnalogueInput Input { get; private set; }

    public void AssignInput(AnalogueInput input)
    {
        LiveHistory.Clear();
        Input.PropertyChanged -= OnInputPropertyChanged;
        Input = input;
        Input.PropertyChanged += OnInputPropertyChanged;
        Title = $"Analogue Input {input.InputNumber}";
        OnPropertyChanged(nameof(Input));
        OnPropertyChanged(nameof(InputNumber));
        OnPropertyChanged(nameof(FilteredAnalogueUnits));
        OnPropertyChanged(nameof(ChartLabel));
        UpdateLiveState(null);
    }

    public int InputNumber => Input.InputNumber;

    public IReadOnlyList<byte> AnalogueCalibrationPointOptions => CalibrationPointOptions;

    public IEnumerable<AnalogueUnitDisplay> FilteredAnalogueUnits => Input.ChanType switch
    {
        AnalogueInput.AnalogueChannelType.RawVoltage => AllAnalogueUnits.Where(unit => unit.Units == AnalogueInput.AnalogueUnits.Volts),
        AnalogueInput.AnalogueChannelType.Digital => AllAnalogueUnits.Where(unit => unit.Units == AnalogueInput.AnalogueUnits.Volts),
        AnalogueInput.AnalogueChannelType.NTC => AllAnalogueUnits.Where(unit => unit.Units == AnalogueInput.AnalogueUnits.Celsius || unit.Units == AnalogueInput.AnalogueUnits.Fahrenheit),
        _ => AllAnalogueUnits,
    };

    [ObservableProperty]
    private string _liveVoltageText;

    [ObservableProperty]
    private string _liveValueText;

    public void UpdateLiveState(AnalogueInput? liveInput)
    {
        LiveVoltageText = liveInput?.InputVoltage is float voltage
            ? $"{voltage.ToString("F1", CultureInfo.InvariantCulture)} V"
            : "-";

        LiveValueText = liveInput?.InputValue is float value
            ? value.ToString("F1", CultureInfo.InvariantCulture)
            : "-";
    }

    private void OnInputPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalogueInput.ChanType))
        {
            LiveHistory.Clear();
            OnPropertyChanged(nameof(FilteredAnalogueUnits));
            OnPropertyChanged(nameof(ChartLabel));
        }
        else if (e.PropertyName == nameof(AnalogueInput.Units))
            LiveHistory.Clear();
    }
}
