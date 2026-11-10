using Cortex.Models;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;

namespace Cortex.ViewModels.IoFlow;

public sealed class MathRuleNodeViewModel : IoFlowNodeViewModel
{
    private readonly Func<MathRuleNodeViewModel, byte, IReadOnlyList<MathOperandIndexOption>> _operandIndexOptionsProvider;
    private MathRuleState _lastValidRuleState;
    private bool _isRestoringRuleState;
    private readonly Dictionary<int, string> _liveResultsByChannel = [];
    private readonly Dictionary<int, LiveEvaluationDisplay> _liveEvaluationsByChannel = [];
    private bool _isLinkedToOutput;
    private bool _pendingControllerConfiguration;

    private readonly record struct LiveEvaluationDisplay(string Evaluation, string OperandA, string OperandB, string Result);

    private readonly record struct MathRuleState(
        byte Operation,
        byte HysteresisEnabled,
        float OnThreshold,
        float OffThreshold,
        byte OperandAType,
        byte OperandAIndex,
        float OperandAConstant,
        byte OperandBType,
        byte OperandBIndex,
        float OperandBConstant);

    public MathRuleNodeViewModel(int logicNodeId, Avalonia.Point location, Func<MathRuleNodeViewModel, byte, IReadOnlyList<MathOperandIndexOption>>? operandIndexOptionsProvider = null)
        : base($"Logic {logicNodeId}", location, IoFlowNodePalette.Logic)
    {
        LogicNodeId = logicNodeId;
        RuleIndex = -1;
        Rule = CreateDefaultRule();
        OperandAConnector = new IoFlowConnectorEndpoint(this, false, true);
        OperandBConnector = new IoFlowConnectorEndpoint(this, false, false);
        _operandIndexOptionsProvider = operandIndexOptionsProvider ?? DefaultOperandIndexOptionsProvider;
        OperandAIndexOptions = [];
        OperandBIndexOptions = [];
        Rule.PropertyChanged += Rule_PropertyChanged;
        RefreshOperandAIndexOptions();
        RefreshOperandBIndexOptions();
        AcceptCurrentRuleState();
    }

    private static string FormatLiveValue(float value)
    {
        return MathRuntimeDisplayFormatter.FormatLiveValue(value);
    }

    public MathRuleNodeViewModel(OutputChannel ownerChannel, int ruleIndex, OutputChannel.MathRule rule, Avalonia.Point location, Func<MathRuleNodeViewModel, byte, IReadOnlyList<MathOperandIndexOption>>? operandIndexOptionsProvider = null)
        : base($"CH{ownerChannel.ChannelNumber} Rule {ruleIndex + 1}", location, IoFlowNodePalette.Logic)
    {
        LogicNodeId = (ownerChannel.ChannelNumber * 100) + ruleIndex + 1;
        OwnerChannel = ownerChannel;
        RuleIndex = ruleIndex;
        Rule = rule;
        OperandAConnector = new IoFlowConnectorEndpoint(this, false, true);
        OperandBConnector = new IoFlowConnectorEndpoint(this, false, false);
        _operandIndexOptionsProvider = operandIndexOptionsProvider ?? DefaultOperandIndexOptionsProvider;
        OperandAIndexOptions = [];
        OperandBIndexOptions = [];
        Rule.PropertyChanged += Rule_PropertyChanged;
        RefreshOperandAIndexOptions();
        RefreshOperandBIndexOptions();
        AcceptCurrentRuleState();
    }

    public int LogicNodeId { get; }

    public OutputChannel? OwnerChannel { get; }

    public int? OwnerChannelNumber => OwnerChannel?.ChannelNumber;

    public int RuleIndex { get; }

    public bool IsStandalone => OwnerChannel == null;

    public bool IsLinkedToOutput => _isLinkedToOutput;

    public OutputChannel.MathRule Rule { get; }

    public string OperationSummary => ((OutputChannel.MathOperation)Rule.Operation).ToString();

    public double? LiveChartValue { get; private set; }

    private bool _hasChartResult;

    public bool SupportsHysteresis => Rule.Operation >= (byte)OutputChannel.MathOperation.GREATER_THAN
        && Rule.Operation <= (byte)OutputChannel.MathOperation.LESS_EQUAL;

    public bool IsHysteresisConfigurationEnabled => SupportsHysteresis && Rule.IsHysteresisEnabled;

    public IoFlowConnectorEndpoint OperandAConnector { get; }

    public IoFlowConnectorEndpoint OperandBConnector { get; }

    private IReadOnlyList<MathConnectedSourceOption> _connectedSourceOptions = [];

    public IReadOnlyList<MathConnectedSourceOption> ConnectedSourceOptions
    {
        get => _connectedSourceOptions;
        private set => SetProperty(ref _connectedSourceOptions, value);
    }

    public Action<MathRuleNodeViewModel, bool, MathConnectedSourceOption>? OperandSourceChangedAction { get; set; }

    private bool _refreshingConnectedSources;
    private bool _changingSelectedSource;
    private int _sourceOptionsRefreshVersion;
    private MathConnectedSourceOption? _selectedOperandASource;
    private MathConnectedSourceOption? _selectedOperandBSource;

    public MathConnectedSourceOption? SelectedOperandASource
    {
        get => _selectedOperandASource;
        set
        {
            if (SetProperty(ref _selectedOperandASource, value) && !_refreshingConnectedSources && value != null)
            {
                ChangeOperandSource(true, value);
            }
        }
    }

    public MathConnectedSourceOption? SelectedOperandBSource
    {
        get => _selectedOperandBSource;
        set
        {
            if (SetProperty(ref _selectedOperandBSource, value) && !_refreshingConnectedSources && value != null)
            {
                ChangeOperandSource(false, value);
            }
        }
    }

    public void RefreshConnectedSources(IReadOnlyList<MathConnectedSourceOption> sources)
    {
        int version = ++_sourceOptionsRefreshVersion;
        if (_changingSelectedSource)
        {
            // Avalonia's selection model must finish its event before its items are replaced.
            Dispatcher.UIThread.Post(() =>
            {
                if (version == _sourceOptionsRefreshVersion)
                {
                    ApplyConnectedSources(sources);
                }
            });
            return;
        }
        ApplyConnectedSources(sources);
    }

    private void ChangeOperandSource(bool operandA, MathConnectedSourceOption source)
    {
        _changingSelectedSource = true;
        try
        {
            OperandSourceChangedAction?.Invoke(this, operandA, source);
        }
        finally
        {
            _changingSelectedSource = false;
        }
    }

    private void ApplyConnectedSources(IReadOnlyList<MathConnectedSourceOption> sources)
    {
        _refreshingConnectedSources = true;
        try
        {
            MathConnectedSourceOption[] options = new[]
            {
                new MathConnectedSourceOption((byte)OutputChannel.MathOperandType.Constant, 0, "Constant"),
            }.Concat(sources).ToArray();
            if (!ConnectedSourceOptions.SequenceEqual(options))
            {
                ConnectedSourceOptions = options;
            }
            SelectedOperandASource = ConnectedSourceOptions.FirstOrDefault(source =>
                source.Type == Rule.OperandAType && (source.Type == 0 || source.Index == Rule.OperandAIndex));
            SelectedOperandBSource = ConnectedSourceOptions.FirstOrDefault(source =>
                source.Type == Rule.OperandBType && (source.Type == 0 || source.Index == Rule.OperandBIndex));
        }
        finally
        {
            _refreshingConnectedSources = false;
        }
    }

    public Action<MathRuleNodeViewModel>? RuleChangedAction { get; set; }

    public void ClearLiveResults()
    {
        LiveChartValue = null;
        _hasChartResult = false;
        _pendingControllerConfiguration = false;
        _liveResultsByChannel.Clear();
        _liveEvaluationsByChannel.Clear();
        OnPropertyChanged(nameof(HasLiveResult));
        OnPropertyChanged(nameof(LiveResultText));
        OnPropertyChanged(nameof(LiveEvaluationText));
        RaiseLiveOperandPropertiesChanged();
    }

    public void MarkPendingControllerConfiguration()
    {
        LiveChartValue = null;
        _pendingControllerConfiguration = true;
        OnPropertyChanged(nameof(LiveResultText));
        RaiseLiveOperandPropertiesChanged();
    }

    public void UpdateLiveResult(
        int channelNumber,
        OutputChannel.MathRule liveRule,
        bool isOutputRoot,
        byte outputMode,
        byte channelError,
        float outputRootValue,
        IReadOnlyList<string> consumerLabels,
        string evaluationText,
        string operandAText,
        string operandBText,
        DataStructures? liveData = null)
    {
        if (!_hasChartResult && !_pendingControllerConfiguration)
        {
            LiveChartValue = liveRule.RuntimeError == 0 && liveRule.IsEvaluated ? liveRule.RuntimeValue : null;
            _hasChartResult = true;
        }
        string resultText = liveRule.RuntimeError != 0
            ? MathRuntimeDisplayFormatter.FormatRuleError(Rule, liveRule.RuntimeError, liveData)
            : liveRule.Evaluated == 0
                ? "--"
                : FormatLiveValue(liveRule.RuntimeValue);
        string description = isOutputRoot && channelError != 0
            ? liveRule.RuntimeError != 0 && resultText.StartsWith("Waiting for", StringComparison.Ordinal)
                ? $"{resultText}; output is held off until a frame arrives"
                : $"Output error {channelError}"
            : isOutputRoot && liveRule.RuntimeError == 0 && liveRule.Evaluated != 0
                ? MathRuntimeDisplayFormatter.FormatOutputEffect(outputRootValue, outputMode)
            : isOutputRoot
                ? $"Output result: {resultText}"
                : consumerLabels.Count > 0
                    ? $"Rule result: {resultText}; feeds {string.Join(", ", consumerLabels)} (intermediate, not directly driving output)"
                    : $"Rule result: {resultText}; not connected to the output";
        string valueText = $"CH{channelNumber}: {description}";

        bool hadLiveResult = _liveResultsByChannel.Count > 0;
        bool resultChanged = !_liveResultsByChannel.TryGetValue(channelNumber, out string? currentText)
            || currentText != valueText;
        LiveEvaluationDisplay evaluation = new(evaluationText, operandAText, operandBText,
            MathRuntimeDisplayFormatter.FormatRuleResult(Rule, liveRule, liveData));
        bool evaluationChanged = !_liveEvaluationsByChannel.TryGetValue(channelNumber, out LiveEvaluationDisplay currentEvaluation)
            || currentEvaluation != evaluation;
        if (!resultChanged && !evaluationChanged)
        {
            return;
        }

        _liveResultsByChannel[channelNumber] = valueText;
        _liveEvaluationsByChannel[channelNumber] = evaluation;
        if (resultChanged && hadLiveResult != HasLiveResult)
        {
            OnPropertyChanged(nameof(HasLiveResult));
        }

        if (resultChanged)
        {
            OnPropertyChanged(nameof(LiveResultText));
        }

        if (evaluationChanged)
        {
            OnPropertyChanged(nameof(LiveEvaluationText));
            RaiseLiveOperandPropertiesChanged();
        }
    }

    public void SetLinkedToOutput(bool value)
    {
        if (_isLinkedToOutput == value)
        {
            return;
        }

        _isLinkedToOutput = value;
        OnPropertyChanged(nameof(IsLinkedToOutput));
        OnPropertyChanged(nameof(LiveResultText));
        OnPropertyChanged(nameof(LiveEvaluationText));
        RaiseLiveOperandPropertiesChanged();
    }

    private void RaiseLiveOperandPropertiesChanged()
    {
        OnPropertyChanged(nameof(LiveOperandAText));
        OnPropertyChanged(nameof(LiveOperandBText));
        OnPropertyChanged(nameof(LiveRuleResultText));
    }

    public void AcceptCurrentRuleState()
    {
        _lastValidRuleState = CaptureRuleState();
    }

    public void RestoreLastValidRuleState()
    {
        _isRestoringRuleState = true;
        try
        {
            Rule.Operation = _lastValidRuleState.Operation;
            Rule.HysteresisEnabled = _lastValidRuleState.HysteresisEnabled;
            Rule.OnThreshold = _lastValidRuleState.OnThreshold;
            Rule.OffThreshold = _lastValidRuleState.OffThreshold;
            Rule.OperandAType = _lastValidRuleState.OperandAType;
            Rule.OperandAIndex = _lastValidRuleState.OperandAIndex;
            Rule.OperandAConstant = _lastValidRuleState.OperandAConstant;
            Rule.OperandBType = _lastValidRuleState.OperandBType;
            Rule.OperandBIndex = _lastValidRuleState.OperandBIndex;
            Rule.OperandBConstant = _lastValidRuleState.OperandBConstant;
        }
        finally
        {
            _isRestoringRuleState = false;
        }
    }

    public bool IsUnaryOperation => IsUnary(Rule.Operation);

    public static bool IsUnary(byte operation) => operation is (byte)OutputChannel.MathOperation.NOT
        or (byte)OutputChannel.MathOperation.ABS
        or (byte)OutputChannel.MathOperation.TOGGLE;

    public bool ShowOperandB => !IsUnaryOperation;

    public bool ShowOperandAConstant => Rule.OperandAType == (byte)OutputChannel.MathOperandType.Constant;

    public bool ShowOperandAIndex => Rule.OperandAType != (byte)OutputChannel.MathOperandType.Constant;

    public bool ShowOperandBConstant => ShowOperandB && Rule.OperandBType == (byte)OutputChannel.MathOperandType.Constant;

    public bool ShowOperandBIndex => ShowOperandB && Rule.OperandBType != (byte)OutputChannel.MathOperandType.Constant;

    public bool HasLiveResult => _liveResultsByChannel.Count > 0;

    public string LiveResultText => _pendingControllerConfiguration
        ? "Pending controller configuration. Save changes to apply; live result unavailable."
        : IsStandalone && !IsLinkedToOutput
        ? "Not linked to an output; this rule stays on the canvas."
        : _liveResultsByChannel.Count == 0
            ? "Live: --"
            : $"Live: {string.Join("  |  ", _liveResultsByChannel.OrderBy(pair => pair.Key).Select(pair => pair.Value))}";

    public string LiveEvaluationText => IsStandalone && !IsLinkedToOutput
        ? "No output evaluation while this rule is unlinked."
        : _liveEvaluationsByChannel.Count == 0
            ? "Evaluation: --"
            : string.Join("  |  ", _liveEvaluationsByChannel.OrderBy(pair => pair.Key).Select(pair => $"CH{pair.Key}: {pair.Value.Evaluation}"));

    public string LiveOperandAText => FormatLiveSection(display => display.OperandA);

    public string LiveOperandBText => FormatLiveSection(display => display.OperandB);

    public string LiveRuleResultText => FormatLiveSection(display => display.Result);

    private string FormatLiveSection(Func<LiveEvaluationDisplay, string> select)
    {
        if (_pendingControllerConfiguration) return "Pending / not applied";
        if ((IsStandalone && !IsLinkedToOutput) || _liveEvaluationsByChannel.Count == 0)
        {
            return "--";
        }

        return string.Join(Environment.NewLine, _liveEvaluationsByChannel.OrderBy(pair => pair.Key)
            .Select(pair => _liveEvaluationsByChannel.Count == 1 ? select(pair.Value) : $"CH{pair.Key}: {select(pair.Value)}"));
    }

    public ObservableCollection<MathOperandIndexOption> OperandAIndexOptions { get; }

    public ObservableCollection<MathOperandIndexOption> OperandBIndexOptions { get; }

    private MathOperandIndexOption? _selectedOperandAIndexOption;

    public MathOperandIndexOption? SelectedOperandAIndexOption
    {
        get => _selectedOperandAIndexOption;
        set
        {
            if (!SetProperty(ref _selectedOperandAIndexOption, value) || value == null)
            {
                return;
            }

            if (Rule.OperandAIndex != value.Value)
            {
                Rule.OperandAIndex = value.Value;
            }
        }
    }

    private MathOperandIndexOption? _selectedOperandBIndexOption;

    public MathOperandIndexOption? SelectedOperandBIndexOption
    {
        get => _selectedOperandBIndexOption;
        set
        {
            if (!SetProperty(ref _selectedOperandBIndexOption, value) || value == null)
            {
                return;
            }

            if (Rule.OperandBIndex != value.Value)
            {
                Rule.OperandBIndex = value.Value;
            }
        }
    }

    private void RefreshOperandAIndexOptions()
    {
        ReplaceOptions(OperandAIndexOptions, _operandIndexOptionsProvider(this, Rule.OperandAType));
        SelectedOperandAIndexOption = OperandAIndexOptions.FirstOrDefault(option => option.Value == Rule.OperandAIndex);
    }

    private void RefreshOperandBIndexOptions()
    {
        ReplaceOptions(OperandBIndexOptions, _operandIndexOptionsProvider(this, Rule.OperandBType));
        SelectedOperandBIndexOption = OperandBIndexOptions.FirstOrDefault(option => option.Value == Rule.OperandBIndex);
    }

    private static IReadOnlyList<MathOperandIndexOption> DefaultOperandIndexOptionsProvider(MathRuleNodeViewModel ruleNode, byte operandType)
    {
        return [];
    }

    private static void ReplaceOptions(ObservableCollection<MathOperandIndexOption> target, IReadOnlyList<MathOperandIndexOption> source)
    {
        target.Clear();
        foreach (MathOperandIndexOption option in source)
        {
            target.Add(option);
        }
    }

    private void Rule_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(OutputChannel.MathRule.Operation))
        {
            OnPropertyChanged(nameof(OperationSummary));
            LiveHistory.Clear();
            OnPropertyChanged(nameof(SupportsHysteresis));
            OnPropertyChanged(nameof(IsHysteresisConfigurationEnabled));
            OnPropertyChanged(nameof(IsUnaryOperation));
            OnPropertyChanged(nameof(ShowOperandB));
            OnPropertyChanged(nameof(ShowOperandBConstant));
            OnPropertyChanged(nameof(ShowOperandBIndex));
        }

        if (e.PropertyName == nameof(OutputChannel.MathRule.HysteresisEnabled))
        {
            OnPropertyChanged(nameof(IsHysteresisConfigurationEnabled));
        }

        if (e.PropertyName == nameof(OutputChannel.MathRule.OperandAType))
        {
            OnPropertyChanged(nameof(ShowOperandAConstant));
            OnPropertyChanged(nameof(ShowOperandAIndex));
            RefreshOperandAIndexOptions();
        }

        if (e.PropertyName == nameof(OutputChannel.MathRule.OperandAIndex))
        {
            SelectedOperandAIndexOption = OperandAIndexOptions.FirstOrDefault(option => option.Value == Rule.OperandAIndex);
        }

        if (e.PropertyName == nameof(OutputChannel.MathRule.OperandBType))
        {
            OnPropertyChanged(nameof(ShowOperandBConstant));
            OnPropertyChanged(nameof(ShowOperandBIndex));
            RefreshOperandBIndexOptions();
        }

        if (e.PropertyName == nameof(OutputChannel.MathRule.OperandBIndex))
        {
            SelectedOperandBIndexOption = OperandBIndexOptions.FirstOrDefault(option => option.Value == Rule.OperandBIndex);
        }

        if (e.PropertyName is nameof(OutputChannel.MathRule.Operation)
            or nameof(OutputChannel.MathRule.HysteresisEnabled)
            or nameof(OutputChannel.MathRule.OnThreshold)
            or nameof(OutputChannel.MathRule.OffThreshold)
            or nameof(OutputChannel.MathRule.OperandAType)
            or nameof(OutputChannel.MathRule.OperandAIndex)
            or nameof(OutputChannel.MathRule.OperandAConstant)
            or nameof(OutputChannel.MathRule.OperandBType)
            or nameof(OutputChannel.MathRule.OperandBIndex)
            or nameof(OutputChannel.MathRule.OperandBConstant))
        {
            if (!_isRestoringRuleState)
            {
                RuleChangedAction?.Invoke(this);
            }
        }
    }

    private MathRuleState CaptureRuleState() =>
        new(Rule.Operation, Rule.HysteresisEnabled, Rule.OnThreshold, Rule.OffThreshold,
            Rule.OperandAType, Rule.OperandAIndex, Rule.OperandAConstant,
            Rule.OperandBType, Rule.OperandBIndex, Rule.OperandBConstant);

    private static OutputChannel.MathRule CreateDefaultRule()
    {
        return new OutputChannel.MathRule
        {
            Operation = (byte)OutputChannel.MathOperation.ADD,
            OperandAType = (byte)OutputChannel.MathOperandType.Constant,
            OperandBType = (byte)OutputChannel.MathOperandType.Constant,
        };
    }
}

public sealed record MathConnectedSourceOption(byte Type, byte Index, string Label);

public sealed class MathOperandIndexOption
{
    public MathOperandIndexOption(byte value, string label)
    {
        Value = value;
        Label = label;
    }

    public byte Value { get; }

    public string Label { get; }
}
