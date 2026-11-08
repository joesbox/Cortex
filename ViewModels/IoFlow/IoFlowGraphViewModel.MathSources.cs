using Avalonia;
using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowGraphViewModel
{
    public IReadOnlyList<MathSourceNodeViewModel> MathSourceNodes => Nodes.OfType<MathSourceNodeViewModel>().ToList();

    public bool CanAddCanRxNode => _settings?.CustomCanSignalsStaticData.Any(signal =>
        IsCanRxSignal(signal) && !MathSourceNodes.Any(node =>
            node.OperandType == OutputChannel.MathOperandType.CanSignal && node.SourceIndex == signal.Index)) == true;

    public bool CanAddSystemValueNode => _settings != null && Enum.GetValues<OutputChannel.MathSystemValue>().Any(value =>
        !MathSourceNodes.Any(node => node.OperandType == OutputChannel.MathOperandType.SystemValue && node.SourceIndex == (byte)value));

    private bool IsCanRxSignal(CustomCanSignal signal) =>
        signal.IsEnabled && _settings?.CustomCanMessagesStaticData.ElementAtOrDefault(signal.MessageIndex) is { IsTx: false };

    [RelayCommand]
    private void AddCanRxNode()
    {
        CustomCanSignal? signal = _settings?.CustomCanSignalsStaticData.FirstOrDefault(signal =>
            IsCanRxSignal(signal) && !MathSourceNodes.Any(node =>
                node.OperandType == OutputChannel.MathOperandType.CanSignal && node.SourceIndex == signal.Index));
        if (signal == null)
        {
            ShowAddNodeWarning("No additional enabled CAN RX signals are available. Configure a receive message and its decoded signals first.");
            return;
        }
        AddMathSourceNode(OutputChannel.MathOperandType.CanSignal, (byte)signal.Index, ConsumeNextNodeSpawnLocation(new Point(50, 50), 340, 200));
        if (_settings != null)
        {
            RebuildConnections(_settings);
        }
        ClearAddNodeWarning();
        NotifyGraphChanged(false);
    }

    [RelayCommand]
    private void AddSystemValueNode()
    {
        if (_settings == null)
        {
            return;
        }
        OutputChannel.MathSystemValue? value = Enum.GetValues<OutputChannel.MathSystemValue>()
            .Where(value => !MathSourceNodes.Any(node =>
                node.OperandType == OutputChannel.MathOperandType.SystemValue && node.SourceIndex == (byte)value))
            .Select(value => (OutputChannel.MathSystemValue?)value).FirstOrDefault();
        if (value == null)
        {
            ShowAddNodeWarning("All system value sources are already on the canvas.");
            return;
        }
        AddMathSourceNode(OutputChannel.MathOperandType.SystemValue, (byte)value.Value, ConsumeNextNodeSpawnLocation(new Point(50, 50), 340, 200));
        RebuildConnections(_settings);
        ClearAddNodeWarning();
        NotifyGraphChanged(false);
    }

    private MathSourceNodeViewModel AddMathSourceNode(OutputChannel.MathOperandType type, byte index, Point location, int? canvasId = null)
    {
        var node = new MathSourceNodeViewModel(type, index, GetMathSourceTitle(type, index), location);
        node.CanvasId = canvasId ?? SelectedCanvas.Id;
        Nodes.Add(node);
        return node;
    }

    private string GetMathSourceTitle(OutputChannel.MathOperandType type, byte index)
    {
        if (type == OutputChannel.MathOperandType.SystemValue)
        {
            return $"System: {(OutputChannel.MathSystemValue)index}";
        }
        CustomCanSignal? signal = _settings?.CustomCanSignalsStaticData.FirstOrDefault(signal => signal.Index == index);
        CustomCanMessage? message = signal == null ? null : _settings?.CustomCanMessagesStaticData.ElementAtOrDefault(signal.MessageIndex);
        return $"CAN RX {index + 1}: {signal?.Name} (0x{message?.Id:X})";
    }

    private void RefreshMathSources()
    {
        foreach (MathSourceNodeViewModel source in MathSourceNodes)
        {
            bool available = source.OperandType == OutputChannel.MathOperandType.SystemValue
                || _settings?.CustomCanSignalsStaticData.Any(signal => signal.Index == source.SourceIndex && IsCanRxSignal(signal)) == true;
            source.UpdateSource(GetMathSourceTitle(source.OperandType, source.SourceIndex), available);
        }
    }

    private IoFlowNodeViewModel? ResolveOperandSource(MathRuleNodeViewModel target, byte type, byte index) =>
        (OutputChannel.MathOperandType)type switch
        {
            OutputChannel.MathOperandType.Rule => MathRuleNodes.FirstOrDefault(node =>
                target.IsStandalone ? node.IsStandalone && node.LogicNodeId == index
                    : node.OwnerChannelNumber == target.OwnerChannelNumber && node.RuleIndex == index),
            OutputChannel.MathOperandType.AnalogueInput => AnalogueNodes.FirstOrDefault(node => node.InputNumber == index + 1),
            OutputChannel.MathOperandType.DigitalInput => DigitalNodes.FirstOrDefault(node => node.InputNumber == index + 1),
            OutputChannel.MathOperandType.Channel => OutputNodes.FirstOrDefault(node => node.ChannelNumber == index + 1),
            OutputChannel.MathOperandType.CanSignal or OutputChannel.MathOperandType.SystemValue =>
                MathSourceNodes.FirstOrDefault(node => node.IsAvailable && (byte)node.OperandType == type && node.SourceIndex == index),
            _ => null,
        };

    private void EnsureOperandSourceNodes()
    {
        if (_settings == null)
        {
            return;
        }
        // Output operands may add channels with their own rules, so inspect newly added nodes too.
        for (int nodeIndex = 0; nodeIndex < Nodes.Count; nodeIndex++)
        {
            if (Nodes[nodeIndex] is not MathRuleNodeViewModel target)
            {
                continue;
            }
            EnsureSource(target, target.Rule.OperandAType, target.Rule.OperandAIndex);
            if (!target.IsUnaryOperation)
            {
                EnsureSource(target, target.Rule.OperandBType, target.Rule.OperandBIndex);
            }
        }

        void EnsureSource(MathRuleNodeViewModel target, byte type, byte index)
        {
            if (ResolveOperandSource(target, type, index) != null)
            {
                return;
            }
            Point preferred = new(target.Location.X - 460, target.Location.Y);
            switch ((OutputChannel.MathOperandType)type)
            {
                case OutputChannel.MathOperandType.DigitalInput:
                    DigitalInput? digital = _settings.DigitalInputsStaticData.ElementAtOrDefault(index);
                    if (digital != null)
                    {
                        AddAtAvailableLocation(new DigitalInputNodeViewModel(digital, preferred) { CanvasId = target.CanvasId });
                    }
                    break;
                case OutputChannel.MathOperandType.AnalogueInput:
                    AnalogueInput? analogue = _settings.AnalogueInputsStaticData.ElementAtOrDefault(index);
                    if (analogue != null)
                    {
                        AddAtAvailableLocation(new AnalogueInputNodeViewModel(analogue, preferred) { CanvasId = target.CanvasId });
                    }
                    break;
                case OutputChannel.MathOperandType.Channel:
                    OutputChannel? channel = _settings.ChannelsStaticData.ElementAtOrDefault(index);
                    if (channel != null)
                    {
                        AddOutputNodeWithOptionalMath(channel, canvasId: target.CanvasId);
                    }
                    break;
                case OutputChannel.MathOperandType.CanSignal:
                    if (_settings.CustomCanSignalsStaticData.Any(signal => signal.Index == index && IsCanRxSignal(signal)))
                    {
                        AddSource();
                    }
                    break;
                case OutputChannel.MathOperandType.SystemValue:
                    if (Enum.IsDefined((OutputChannel.MathSystemValue)index))
                    {
                        AddSource();
                    }
                    break;
            }

            void AddSource()
            {
                Point location = FindNearestAvailableSpawnLocation(preferred, 340, 200);
                AddMathSourceNode((OutputChannel.MathOperandType)type, index, location, target.CanvasId);
            }
        }
    }

    private void AddAtAvailableLocation(IoFlowNodeViewModel node)
    {
        NodeSize size = EstimateNodeSize(node);
        node.Location = FindNearestAvailableSpawnLocation(node.Location, size.Width, size.Height);
        Nodes.Add(node);
    }

    private static MathConnectedSourceOption? GetSourceOption(MathRuleNodeViewModel target, IoFlowNodeViewModel source) =>
        source switch
        {
            AnalogueInputNodeViewModel node => new((byte)OutputChannel.MathOperandType.AnalogueInput, (byte)(node.InputNumber - 1), node.Title),
            DigitalInputNodeViewModel node => new((byte)OutputChannel.MathOperandType.DigitalInput, (byte)(node.InputNumber - 1), node.Title),
            OutputNodeViewModel node => new((byte)OutputChannel.MathOperandType.Channel, (byte)(node.ChannelNumber - 1), node.Title),
            MathRuleNodeViewModel node => new((byte)OutputChannel.MathOperandType.Rule, (byte)(target.IsStandalone ? node.LogicNodeId : node.RuleIndex), node.Title),
            MathSourceNodeViewModel node => new((byte)node.OperandType, node.SourceIndex, node.Title),
            _ => null,
        };

    private void AddMathOperandConnection(List<IoFlowConnectionViewModel> desiredConnections, MathRuleNodeViewModel target, bool operandA)
    {
        byte type = operandA ? target.Rule.OperandAType : target.Rule.OperandBType;
        byte index = operandA ? target.Rule.OperandAIndex : target.Rule.OperandBIndex;
        IoFlowNodeViewModel? source = ResolveOperandSource(target, type, index);
        if (source == null)
        {
            return;
        }
        IoFlowConnectorEndpoint endpoint = operandA ? target.OperandAConnector : target.OperandBConnector;
        desiredConnections.Add(new IoFlowConnectionViewModel(source.OutputConnector, endpoint, $"Operand {(operandA ? "A" : "B")}"));
    }

    private void RefreshMathSourceSelectors()
    {
        foreach (MathRuleNodeViewModel target in MathRuleNodes)
        {
            MathConnectedSourceOption[] sources = Connections.Where(connection => ReferenceEquals(connection.Target.Node, target))
                .Select(connection => GetSourceOption(target, connection.Source.Node))
                .OfType<MathConnectedSourceOption>().Distinct().ToArray();
            target.RefreshConnectedSources(sources);
        }
    }

    private void SelectMathOperandSource(MathRuleNodeViewModel target, bool operandA, MathConnectedSourceOption source)
    {
        if (_settings == null || !Nodes.Contains(target))
        {
            ShowAddNodeWarning("This logic rule is no longer part of the active graph.");
            return;
        }
        if (!target.ConnectedSourceOptions.Contains(source))
        {
            ShowAddNodeWarning("Select a source already connected to this logic node.");
            return;
        }
        if (ApplyMathOperand(target, operandA, source))
        {
            RebuildConnections(_settings);
            NotifyGraphChanged(!target.IsStandalone || _outputRootLogicNodeByChannel.Count > 0);
        }
    }

    private bool ApplyMathOperand(MathRuleNodeViewModel target, bool operandA, MathConnectedSourceOption source)
    {
        MathRuleValues previous = CaptureMathRuleValues(target.Rule);
        bool wasApplying = _isApplyingMathRulesFromGraph;
        _isApplyingMathRulesFromGraph = true;
        string error = string.Empty;
        try
        {
            if (operandA)
            {
                target.Rule.OperandAIndex = source.Index;
                target.Rule.OperandAType = source.Type;
            }
            else
            {
                target.Rule.OperandBIndex = source.Index;
                target.Rule.OperandBType = source.Type;
            }
            if (source.Type == (byte)OutputChannel.MathOperandType.Constant)
            {
                if (operandA)
                {
                    target.Rule.OperandAConstant = 0;
                }
                else
                {
                    target.Rule.OperandBConstant = 0;
                }
            }
            bool valid = target.IsStandalone
                ? TryBuildOrderedLogicTree(target, out _, out error) && TrySynchronizeMappedMathOutputs(out error)
                : !HasOwnedMathRuleCycle(target.OwnerChannel!);
            if (!valid)
            {
                RestoreMathRuleValues(target.Rule, previous);
                error = string.IsNullOrEmpty(error) ? "Logic rule connections cannot contain a cycle." : error;
            }
            target.AcceptCurrentRuleState();
        }
        finally
        {
            _isApplyingMathRulesFromGraph = wasApplying;
        }
        if (!string.IsNullOrEmpty(error))
        {
            ShowAddNodeWarning(error);
            if (_settings != null)
            {
                RebuildConnections(_settings);
            }
            return false;
        }
        ClearAddNodeWarning();
        return true;
    }
}
