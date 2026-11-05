using CommunityToolkit.Mvvm.ComponentModel;
using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class OutputNodeViewModel : IoFlowNodeViewModel
{
    private static readonly OutputChannelTypeOption[] DigitalTypeOptions =
    [
        new OutputChannelTypeOption(OutputChannel.ChannelType.Digital, "Digital"),
        new OutputChannelTypeOption(OutputChannel.ChannelType.PWM, "Digital PWM"),
        new OutputChannelTypeOption(OutputChannel.ChannelType.Intermittent, "Intermittent"),
    ];

    private static readonly OutputChannelTypeOption[] AnalogueTypeOptions =
    [
        new OutputChannelTypeOption(OutputChannel.ChannelType.Analogue, "Analogue threshold"),
        new OutputChannelTypeOption(OutputChannel.ChannelType.AnalogueScaled, "Analogue scaled PWM"),
    ];

    private static readonly OutputChannelTypeOption[] CanTypeOptions =
    [
        new OutputChannelTypeOption(OutputChannel.ChannelType.CAN, "CAN digital"),
        new OutputChannelTypeOption(OutputChannel.ChannelType.CAN_PWM, "CAN PWM"),
    ];

    private readonly IReadOnlyList<AnalogueInput> _analogueInputs;
    private readonly IReadOnlyList<OutputChannel> _availableChannels;
    private readonly Func<OutputNodeViewModel, int, bool>? _channelSelectionHandler;
    private OutputChannel _channel;

    public OutputNodeViewModel(
        OutputChannel channel,
        Avalonia.Point location,
        IReadOnlyList<AnalogueInput> analogueInputs,
        IReadOnlyList<OutputChannel> availableChannels,
        Func<OutputNodeViewModel, int, bool>? channelSelectionHandler = null)
        : base(BuildNodeTitle(channel), location, ConnectorPinDisplayItem.GetOutputColorHex(channel.ChanType))
    {
        _channel = channel;
        _analogueInputs = analogueInputs;
        _availableChannels = availableChannels;
        _channelSelectionHandler = channelSelectionHandler;

        foreach (AnalogueInput input in _analogueInputs)
        {
            input.PropertyChanged += AnalogueInput_PropertyChanged;
        }

        Channel.PropertyChanged += Channel_PropertyChanged;
        AvailableInputChannelTypes = [];
        RefreshInputContext();
    }

    public OutputChannel Channel => _channel;

    public int ChannelNumber => Channel.ChannelNumber;

    public IReadOnlyList<int> OutputChannelNumbers => _availableChannels.Select(channel => channel.ChannelNumber).OrderBy(channelNumber => channelNumber).ToList();

    public int SelectedOutputChannelNumber
    {
        get => Channel.ChannelNumber;
        set
        {
            if (value == Channel.ChannelNumber)
            {
                return;
            }

            if (_channelSelectionHandler?.Invoke(this, value) != true)
            {
                OnPropertyChanged();
            }
        }
    }

    public string ChannelName
    {
        get => GetChannelName(Channel);
        set
        {
            string channelNameValue = value ?? string.Empty;
            if (channelNameValue.Length > Constants.CHANNEL_NAME_LENGTH)
            {
                channelNameValue = channelNameValue[..Constants.CHANNEL_NAME_LENGTH];
            }

            char[] channelName = new char[Constants.CHANNEL_NAME_LENGTH];
            channelNameValue.CopyTo(0, channelName, 0, Math.Min(channelNameValue.Length, channelName.Length));

            if (Channel.Name != null && Channel.Name.SequenceEqual(channelName))
            {
                return;
            }

            Channel.Name = channelName;
        }
    }

    public ObservableCollection<OutputChannelTypeOption> AvailableInputChannelTypes { get; }

    [ObservableProperty]
    private OutputChannelTypeOption? _selectedInputChannelTypeOption;

    [ObservableProperty]
    private bool _hasConnectedInput;

    [ObservableProperty]
    private bool _isInputChannelTypeSelectionRequired;

    [ObservableProperty]
    private string _inputChannelTypeSelectionHint = string.Empty;

    private string _liveCurrentText = "0.00 A";

    public string LiveCurrentText
    {
        get => _liveCurrentText;
        private set => SetProperty(ref _liveCurrentText, value);
    }

    public bool ShowConnectInputHint => !HasConnectedInput;

    public bool ShowInputChannelTypeOptions => HasConnectedInput && AvailableInputChannelTypes.Count > 0;

    public bool ShowInputDependentConfiguration => ShowInputChannelTypeOptions && !IsInputChannelTypeSelectionRequired;

    public bool ShowDigitalConfiguration => ShowInputDependentConfiguration && IsDigitalType(Channel.ChanType);

    public bool SupportsAnalogueChannelTypes => AvailableInputChannelTypes.Any(option => option.ChannelType is OutputChannel.ChannelType.Analogue or OutputChannel.ChannelType.AnalogueScaled);

    public bool ShowAnalogueThresholdConfiguration => ShowInputDependentConfiguration && SupportsAnalogueChannelTypes && Channel.ChanType == OutputChannel.ChannelType.Analogue;

    public bool ShowAnalogueScaledConfiguration => ShowInputDependentConfiguration && SupportsAnalogueChannelTypes && Channel.ChanType == OutputChannel.ChannelType.AnalogueScaled;

    public bool SupportsIntermittentChannelType => AvailableInputChannelTypes.Any(option => option.ChannelType == OutputChannel.ChannelType.Intermittent);

    public bool IsIntermittentChannelSelected => Channel.ChanType == OutputChannel.ChannelType.Intermittent;

    public bool ShowIntermittentSelectionHint => ShowIntermittentConfiguration && !IsIntermittentChannelSelected;

    public bool ShowIntermittentConfiguration => ShowInputDependentConfiguration && SupportsIntermittentChannelType;

    public bool ShowDutyConfiguration => ShowInputDependentConfiguration && Channel.ChanType == OutputChannel.ChannelType.PWM;

    public bool ShowCurrentInrushConfiguration => ShowInputDependentConfiguration;

    public bool ShowAnalogueConfiguration => ShowInputDependentConfiguration && SupportsAnalogueChannelTypes;

    public bool ShowTimingConfiguration => ShowInputDependentConfiguration;

    public bool IsDelayedOnTimingEnabled => Channel.IsDelayedOnEnabled;

    public bool IsDelayedOffTimingEnabled => Channel.IsDelayedOffEnabled;

    public bool IsSoftStartTimingEnabled => Channel.SoftStartEnabled != 0;

    public bool IsSoftStopTimingEnabled => Channel.SoftStopEnabled != 0;

    public bool UsesNegativeGoingThresholds => Channel.OnThreshold < Channel.OffThreshold;

    public string OnThresholdTooltip => UsesNegativeGoingThresholds
        ? "Channel turns ON below this value"
        : "Channel turns ON above this value";

    public string OffThresholdTooltip => UsesNegativeGoingThresholds
        ? "Channel turns OFF above this value"
        : "Channel turns OFF below this value";

    public double InputMinimum => GetInputRangeForChannel().Min;

    public double InputMaximum => GetInputRangeForChannel().Max;

    public double OnThresholdMinimum => UsesNegativeGoingThresholds ? InputMinimum : Channel.OffThreshold;

    public double OnThresholdMaximum => UsesNegativeGoingThresholds ? Channel.OffThreshold : InputMaximum;

    public double OffThresholdMinimum => UsesNegativeGoingThresholds ? Channel.OnThreshold : InputMinimum;

    public double OffThresholdMaximum => UsesNegativeGoingThresholds ? InputMaximum : Channel.OnThreshold;

    public double DutyValue
    {
        get => Channel.PWMSetDuty;
        set => Channel.PWMSetDuty = (byte)Math.Clamp(Math.Round(value), 0, 100);
    }

    public double RetryCountValue
    {
        get => Channel.RetryCount;
        set => Channel.RetryCount = (byte)Math.Clamp(Math.Round(value), 0, 10);
    }

    public double CurrentThresholdLowValue
    {
        get => Channel.CurrentThresholdLow;
        set => Channel.CurrentThresholdLow = (float)Math.Clamp(value, 0.0, 17.0);
    }

    public double CurrentThresholdHighValue
    {
        get => Channel.CurrentThresholdHigh;
        set => Channel.CurrentThresholdHigh = (float)Math.Clamp(value, 0.0, 17.0);
    }

    public double InrushDelayValue
    {
        get => Channel.InrushDelay;
        set => Channel.InrushDelay = (float)Math.Clamp(value, 0.0, 2.0);
    }

    public double InrushCurrentLimitValue
    {
        get => Channel.InrushCurrentLimit;
        set => Channel.InrushCurrentLimit = (float)Math.Clamp(value, 0.0, 50.0);
    }

    public double OnThresholdValue
    {
        get => Channel.OnThreshold;
        set => Channel.OnThreshold = (float)Math.Clamp(value, OnThresholdMinimum, OnThresholdMaximum);
    }

    public double OffThresholdValue
    {
        get => Channel.OffThreshold;
        set => Channel.OffThreshold = (float)Math.Clamp(value, OffThresholdMinimum, OffThresholdMaximum);
    }

    public double ScaleMinValue
    {
        get => Channel.ScaleMin;
        set => Channel.ScaleMin = (float)Math.Clamp(value, InputMinimum, InputMaximum);
    }

    public double ScaleMaxValue
    {
        get => Channel.ScaleMax;
        set => Channel.ScaleMax = (float)Math.Clamp(value, InputMinimum, InputMaximum);
    }

    public double PwmMinValue
    {
        get => Channel.PWMMin;
        set => Channel.PWMMin = (byte)Math.Clamp(Math.Round(value), 0, 100);
    }

    public double PwmMaxValue
    {
        get => Channel.PWMMax;
        set => Channel.PWMMax = (byte)Math.Clamp(Math.Round(value), 0, 100);
    }

    public double IntermittentOnTimeValue
    {
        get => Channel.IntermittentOnTime;
        set => Channel.IntermittentOnTime = (float)Math.Clamp(value, 0.0, 10.0);
    }

    public double IntermittentOffTimeValue
    {
        get => Channel.IntermittentOffTime;
        set => Channel.IntermittentOffTime = (float)Math.Clamp(value, 0.0, 10.0);
    }

    public double SoftStartTimeValue
    {
        get => Channel.SoftStartTime;
        set => Channel.SoftStartTime = (float)Math.Clamp(value, 0.0, 5.0);
    }

    public double SoftStopTimeValue
    {
        get => Channel.SoftStopTime;
        set => Channel.SoftStopTime = (float)Math.Clamp(value, 0.0, 5.0);
    }

    public void UpdateLiveCurrent(double value)
    {
        LiveCurrentText = $"{value:0.00} A";
    }

    public void AssignChannel(OutputChannel channel)
    {
        if (ReferenceEquals(Channel, channel))
        {
            return;
        }

        Channel.PropertyChanged -= Channel_PropertyChanged;
        _channel = channel;
        Channel.PropertyChanged += Channel_PropertyChanged;

        Title = BuildNodeTitle(channel);
        SetDefaultBorderHex(ConnectorPinDisplayItem.GetOutputColorHex(Channel.ChanType));

        OnPropertyChanged(nameof(Channel));
        OnPropertyChanged(nameof(ChannelNumber));
        OnPropertyChanged(nameof(SelectedOutputChannelNumber));
        OnPropertyChanged(nameof(ChannelName));

        RefreshInputContext();
        RaiseInputUiStateChanged();
        RaiseAnalogueRangeUiStateChanged();
        OnPropertyChanged(nameof(DutyValue));
        OnPropertyChanged(nameof(RetryCountValue));
        OnPropertyChanged(nameof(CurrentThresholdLowValue));
        OnPropertyChanged(nameof(CurrentThresholdHighValue));
        OnPropertyChanged(nameof(InrushDelayValue));
        OnPropertyChanged(nameof(InrushCurrentLimitValue));
        OnPropertyChanged(nameof(OnThresholdValue));
        OnPropertyChanged(nameof(OffThresholdValue));
        OnPropertyChanged(nameof(ScaleMinValue));
        OnPropertyChanged(nameof(ScaleMaxValue));
        OnPropertyChanged(nameof(PwmMinValue));
        OnPropertyChanged(nameof(PwmMaxValue));
        OnPropertyChanged(nameof(IntermittentOnTimeValue));
        OnPropertyChanged(nameof(IntermittentOffTimeValue));
        OnPropertyChanged(nameof(SoftStartTimeValue));
        OnPropertyChanged(nameof(SoftStopTimeValue));
        RaiseTimingEnableStateChanged();
    }

    partial void OnSelectedInputChannelTypeOptionChanged(OutputChannelTypeOption? value)
    {
        if (value == null || value.ChannelType == Channel.ChanType)
        {
            return;
        }

        if (AvailableInputChannelTypes.Any(option => option.ChannelType == value.ChannelType))
        {
            Channel.ChanType = value.ChannelType;
        }
    }

    private void RefreshInputContext()
    {
        OutputChannelTypeOption[] options = ResolveInputCompatibleChannelTypes();
        HasConnectedInput = Channel.InputControlPin != 0
            || Channel.ChanType is OutputChannel.ChannelType.CAN or OutputChannel.ChannelType.CAN_PWM;

        AvailableInputChannelTypes.Clear();
        foreach (OutputChannelTypeOption option in options)
        {
            AvailableInputChannelTypes.Add(option);
        }

        bool currentTypeIsValid = options.Any(option => option.ChannelType == Channel.ChanType);
        IsInputChannelTypeSelectionRequired = HasConnectedInput && !currentTypeIsValid;
        SelectedInputChannelTypeOption = currentTypeIsValid
            ? AvailableInputChannelTypes.FirstOrDefault(option => option.ChannelType == Channel.ChanType)
            : null;
        InputChannelTypeSelectionHint = IsInputChannelTypeSelectionRequired
            ? "Select a channel type compatible with the connected input."
            : string.Empty;

        RaiseInputUiStateChanged();
    }

    public void RefreshCanContext()
    {
        RefreshInputContext();
    }

    private OutputChannelTypeOption[] ResolveInputCompatibleChannelTypes()
    {
        if (Channel.InputControlPin == 0)
        {
            if (Channel.ChanType is not (OutputChannel.ChannelType.CAN or OutputChannel.ChannelType.CAN_PWM))
            {
                return [];
            }

            return CanTypeOptions;
        }

        if (InputPinCatalog.DIChannelInputPins.Contains(Channel.InputControlPin) || Channel.InputControlPin == InputPinCatalog.IgnitionInputPin)
        {
            return DigitalTypeOptions;
        }

        int analogueInputIndex = Array.IndexOf(InputPinCatalog.ANAChannelInputPins, Channel.InputControlPin);
        if (analogueInputIndex >= 0 && analogueInputIndex < _analogueInputs.Count)
        {
            return _analogueInputs[analogueInputIndex].ChanType == AnalogueInput.AnalogueChannelType.Digital
                ? DigitalTypeOptions
                : AnalogueTypeOptions;
        }

        return [];
    }

    private static bool IsDigitalType(OutputChannel.ChannelType channelType)
    {
        return channelType == OutputChannel.ChannelType.Digital
            || channelType == OutputChannel.ChannelType.PWM
            || channelType == OutputChannel.ChannelType.Intermittent;
    }

    private static string BuildNodeTitle(OutputChannel channel)
    {
        string channelName = GetChannelName(channel);
        return string.IsNullOrWhiteSpace(channelName)
            ? $"Output {channel.ChannelNumber}"
            : $"Output {channel.ChannelNumber} - {channelName}";
    }

    private static string GetChannelName(OutputChannel channel)
    {
        return channel.Name == null
            ? string.Empty
            : new string(channel.Name).TrimEnd('\0', ' ');
    }

    private (double Min, double Max) GetInputRangeForChannel()
    {
        int analogueInputIndex = Array.IndexOf(InputPinCatalog.ANAChannelInputPins, Channel.InputControlPin);
        if (analogueInputIndex < 0 || analogueInputIndex >= _analogueInputs.Count)
        {
            return (0.0, 5.0);
        }

        AnalogueInput input = _analogueInputs[analogueInputIndex];
        if (input.ChanType == AnalogueInput.AnalogueChannelType.NTC)
        {
            return input.Units == AnalogueInput.AnalogueUnits.Fahrenheit
                ? (-22.0, 302.0)
                : (-30.0, 150.0);
        }

        if (input.ChanType == AnalogueInput.AnalogueChannelType.RawVoltage)
        {
            return (0.0, 5.0);
        }

        double min = Math.Min(input.ConfigRangeMin, input.ConfigRangeMax);
        double max = Math.Max(input.ConfigRangeMin, input.ConfigRangeMax);
        if (Math.Abs(max - min) < 0.001)
        {
            max = min + 1.0;
        }

        return (min, max);
    }

    private void RaiseAnalogueRangeUiStateChanged()
    {
        OnPropertyChanged(nameof(UsesNegativeGoingThresholds));
        OnPropertyChanged(nameof(InputMinimum));
        OnPropertyChanged(nameof(InputMaximum));
        OnPropertyChanged(nameof(OnThresholdMinimum));
        OnPropertyChanged(nameof(OnThresholdMaximum));
        OnPropertyChanged(nameof(OffThresholdMinimum));
        OnPropertyChanged(nameof(OffThresholdMaximum));
        OnPropertyChanged(nameof(OnThresholdTooltip));
        OnPropertyChanged(nameof(OffThresholdTooltip));
    }

    private void RaiseInputUiStateChanged()
    {
        OnPropertyChanged(nameof(ShowConnectInputHint));
        OnPropertyChanged(nameof(ShowInputChannelTypeOptions));
        OnPropertyChanged(nameof(ShowInputDependentConfiguration));
        OnPropertyChanged(nameof(ShowDigitalConfiguration));
        OnPropertyChanged(nameof(SupportsAnalogueChannelTypes));
        OnPropertyChanged(nameof(ShowAnalogueThresholdConfiguration));
        OnPropertyChanged(nameof(ShowAnalogueScaledConfiguration));
        OnPropertyChanged(nameof(SupportsIntermittentChannelType));
        OnPropertyChanged(nameof(IsIntermittentChannelSelected));
        OnPropertyChanged(nameof(ShowIntermittentSelectionHint));
        OnPropertyChanged(nameof(ShowIntermittentConfiguration));
        OnPropertyChanged(nameof(ShowDutyConfiguration));
        OnPropertyChanged(nameof(ShowCurrentInrushConfiguration));
        OnPropertyChanged(nameof(ShowAnalogueConfiguration));
        OnPropertyChanged(nameof(ShowTimingConfiguration));
    }

    private void RaiseTimingEnableStateChanged()
    {
        OnPropertyChanged(nameof(IsDelayedOnTimingEnabled));
        OnPropertyChanged(nameof(IsDelayedOffTimingEnabled));
        OnPropertyChanged(nameof(IsSoftStartTimingEnabled));
        OnPropertyChanged(nameof(IsSoftStopTimingEnabled));
    }

    private void Channel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputChannel.ChanType))
        {
            SetDefaultBorderHex(ConnectorPinDisplayItem.GetOutputColorHex(Channel.ChanType));
            RefreshInputContext();
            RaiseInputUiStateChanged();
            RaiseAnalogueRangeUiStateChanged();
        }

        if (e.PropertyName == nameof(OutputChannel.InputControlPin))
        {
            RefreshInputContext();
            RaiseAnalogueRangeUiStateChanged();
        }

        if (e.PropertyName == nameof(OutputChannel.PWMSetDuty))
        {
            OnPropertyChanged(nameof(DutyValue));
        }

        if (e.PropertyName == nameof(OutputChannel.RetryCount))
        {
            OnPropertyChanged(nameof(RetryCountValue));
        }

        if (e.PropertyName == nameof(OutputChannel.CurrentThresholdLow))
        {
            OnPropertyChanged(nameof(CurrentThresholdLowValue));
        }

        if (e.PropertyName == nameof(OutputChannel.CurrentThresholdHigh))
        {
            OnPropertyChanged(nameof(CurrentThresholdHighValue));
        }

        if (e.PropertyName == nameof(OutputChannel.InrushDelay))
        {
            OnPropertyChanged(nameof(InrushDelayValue));
        }

        if (e.PropertyName == nameof(OutputChannel.InrushCurrentLimit))
        {
            OnPropertyChanged(nameof(InrushCurrentLimitValue));
        }

        if (e.PropertyName == nameof(OutputChannel.OnThreshold))
        {
            OnPropertyChanged(nameof(OnThresholdValue));
            RaiseAnalogueRangeUiStateChanged();
        }

        if (e.PropertyName == nameof(OutputChannel.OffThreshold))
        {
            OnPropertyChanged(nameof(OffThresholdValue));
            RaiseAnalogueRangeUiStateChanged();
        }

        if (e.PropertyName == nameof(OutputChannel.ScaleMin))
        {
            OnPropertyChanged(nameof(ScaleMinValue));
        }

        if (e.PropertyName == nameof(OutputChannel.ScaleMax))
        {
            OnPropertyChanged(nameof(ScaleMaxValue));
        }

        if (e.PropertyName == nameof(OutputChannel.PWMMin))
        {
            OnPropertyChanged(nameof(PwmMinValue));
        }

        if (e.PropertyName == nameof(OutputChannel.PWMMax))
        {
            OnPropertyChanged(nameof(PwmMaxValue));
        }

        if (e.PropertyName == nameof(OutputChannel.IntermittentOnTime))
        {
            OnPropertyChanged(nameof(IntermittentOnTimeValue));
        }

        if (e.PropertyName == nameof(OutputChannel.IntermittentOffTime))
        {
            OnPropertyChanged(nameof(IntermittentOffTimeValue));
        }

        if (e.PropertyName == nameof(OutputChannel.DelayedOn)
            || e.PropertyName == nameof(OutputChannel.DelayedOff)
            || e.PropertyName == nameof(OutputChannel.SoftStartEnabled)
            || e.PropertyName == nameof(OutputChannel.SoftStopEnabled))
        {
            RaiseTimingEnableStateChanged();
        }

        if (e.PropertyName == nameof(OutputChannel.ChannelNumber)
            || e.PropertyName == nameof(OutputChannel.Name))
        {
            Title = BuildNodeTitle(Channel);
            OnPropertyChanged(nameof(ChannelName));
        }

        if (e.PropertyName == nameof(OutputChannel.SoftStartTime))
        {
            OnPropertyChanged(nameof(SoftStartTimeValue));
        }

        if (e.PropertyName == nameof(OutputChannel.SoftStopTime))
        {
            OnPropertyChanged(nameof(SoftStopTimeValue));
        }
    }

    private void AnalogueInput_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AnalogueInput.ChanType))
        {
            RefreshInputContext();
            RaiseAnalogueRangeUiStateChanged();
        }

        if (e.PropertyName == nameof(AnalogueInput.Units)
            || e.PropertyName == nameof(AnalogueInput.ConfigRangeMin)
            || e.PropertyName == nameof(AnalogueInput.ConfigRangeMax))
        {
            RaiseAnalogueRangeUiStateChanged();
        }
    }
}

public sealed class OutputChannelTypeOption
{
    public OutputChannelTypeOption(OutputChannel.ChannelType channelType, string label)
    {
        ChannelType = channelType;
        Label = label;
    }

    public OutputChannel.ChannelType ChannelType { get; }

    public string Label { get; }
}
