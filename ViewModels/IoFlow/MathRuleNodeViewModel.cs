using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Cortex.ViewModels.IoFlow;

public sealed class MathRuleNodeViewModel : IoFlowNodeViewModel
{
    private readonly Func<MathRuleNodeViewModel, byte, IReadOnlyList<MathOperandIndexOption>> _operandIndexOptionsProvider;

    public MathRuleNodeViewModel(int logicNodeId, Avalonia.Point location, Func<MathRuleNodeViewModel, byte, IReadOnlyList<MathOperandIndexOption>>? operandIndexOptionsProvider = null)
        : base($"Logic {logicNodeId}", location, ConnectorPinPalette.OutputMath)
    {
        LogicNodeId = logicNodeId;
        RuleIndex = -1;
        Rule = CreateDefaultRule();
        _operandIndexOptionsProvider = operandIndexOptionsProvider ?? DefaultOperandIndexOptionsProvider;
        OperandAIndexOptions = [];
        OperandBIndexOptions = [];
        Rule.PropertyChanged += Rule_PropertyChanged;
        RefreshOperandAIndexOptions();
        RefreshOperandBIndexOptions();
    }

    public MathRuleNodeViewModel(OutputChannel ownerChannel, int ruleIndex, OutputChannel.MathRule rule, Avalonia.Point location, Func<MathRuleNodeViewModel, byte, IReadOnlyList<MathOperandIndexOption>>? operandIndexOptionsProvider = null)
        : base($"CH{ownerChannel.ChannelNumber} Rule {ruleIndex + 1}", location, ConnectorPinPalette.OutputMath)
    {
        LogicNodeId = (ownerChannel.ChannelNumber * 100) + ruleIndex + 1;
        OwnerChannel = ownerChannel;
        RuleIndex = ruleIndex;
        Rule = rule;
        _operandIndexOptionsProvider = operandIndexOptionsProvider ?? DefaultOperandIndexOptionsProvider;
        OperandAIndexOptions = [];
        OperandBIndexOptions = [];
        Rule.PropertyChanged += Rule_PropertyChanged;
        RefreshOperandAIndexOptions();
        RefreshOperandBIndexOptions();
    }

    public int LogicNodeId { get; }

    public OutputChannel? OwnerChannel { get; }

    public int? OwnerChannelNumber => OwnerChannel?.ChannelNumber;

    public int RuleIndex { get; }

    public bool IsStandalone => OwnerChannel == null;

    public OutputChannel.MathRule Rule { get; }

    public bool IsUnaryOperation => Rule.Operation is (byte)OutputChannel.MathOperation.NOT
        or (byte)OutputChannel.MathOperation.ABS
        or (byte)OutputChannel.MathOperation.TOGGLE;

    public bool ShowOperandB => !IsUnaryOperation;

    public bool ShowOperandAConstant => Rule.OperandAType == (byte)OutputChannel.MathOperandType.Constant;

    public bool ShowOperandAIndex => Rule.OperandAType != (byte)OutputChannel.MathOperandType.Constant;

    public bool ShowOperandBConstant => ShowOperandB && Rule.OperandBType == (byte)OutputChannel.MathOperandType.Constant;

    public bool ShowOperandBIndex => ShowOperandB && Rule.OperandBType != (byte)OutputChannel.MathOperandType.Constant;

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
            OnPropertyChanged(nameof(IsUnaryOperation));
            OnPropertyChanged(nameof(ShowOperandB));
            OnPropertyChanged(nameof(ShowOperandBConstant));
            OnPropertyChanged(nameof(ShowOperandBIndex));
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
    }

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
