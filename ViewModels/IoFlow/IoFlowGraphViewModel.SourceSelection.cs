using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowGraphViewModel
{
    private static (OutputChannel.MathOperandType Type, int Index)? GetSelectableSource(IoFlowNodeViewModel node) =>
        node switch
        {
            AnalogueInputNodeViewModel analogue => (OutputChannel.MathOperandType.AnalogueInput, analogue.InputNumber - 1),
            DigitalInputNodeViewModel digital => (OutputChannel.MathOperandType.DigitalInput, digital.InputNumber - 1),
            MathSourceNodeViewModel source => (source.OperandType, source.SourceIndex),
            _ => null,
        };

    private IReadOnlyList<NodeSourceOption> GetNodeSourceOptions(IoFlowNodeViewModel node)
    {
        if (_settings == null || GetSelectableSource(node) is not { } identity) return [];
        IEnumerable<NodeSourceOption> options = identity.Type switch
        {
            OutputChannel.MathOperandType.AnalogueInput => _settings.AnalogueInputsStaticData
                .Select(input => new NodeSourceOption(input.InputNumber - 1, $"Analogue Input {input.InputNumber}")),
            OutputChannel.MathOperandType.DigitalInput => _settings.DigitalInputsStaticData
                .Select(input => new NodeSourceOption(input.InputNumber - 1, $"Digital Input {input.InputNumber}")),
            OutputChannel.MathOperandType.CanSignal => _settings.CustomCanSignalsStaticData
                .Where(signal => IsCanRxSignal(signal) || signal.Index == identity.Index)
                .Select(signal => new NodeSourceOption(signal.Index, GetMathSourceTitle(identity.Type, (byte)signal.Index))),
            OutputChannel.MathOperandType.SystemValue => Enum.GetValues<OutputChannel.MathSystemValue>()
                .Select(value => new NodeSourceOption((byte)value, GetMathSourceTitle(identity.Type, (byte)value))),
            _ => [],
        };
        return options.Where(option => !Nodes.Any(other => !ReferenceEquals(other, node)
            && GetSelectableSource(other) == (identity.Type, option.Index))).ToArray();
    }

    private void RefreshNodeSourceOptions()
    {
        foreach (OutputNodeViewModel outputNode in OutputNodes)
        {
            HashSet<int> usedElsewhere = OutputNodes
                .Where(other => !ReferenceEquals(other, outputNode))
                .Select(other => other.ChannelNumber)
                .ToHashSet();
            outputNode.RefreshOutputChannelNumbers(usedElsewhere);
        }

        foreach (IoFlowNodeViewModel node in Nodes)
        {
            if (GetSelectableSource(node) is not { } identity) continue;
            node.SourceSelectionChangedAction = SelectNodeSource;
            node.RefreshSourceOptions(GetNodeSourceOptions(node), identity.Index);
        }
    }

    private void SelectNodeSource(IoFlowNodeViewModel node, int index)
    {
        if (_settings == null || !Nodes.Contains(node) || GetSelectableSource(node) is not { } previous)
        {
            ShowAddNodeWarning("This source node is no longer part of the active graph.");
            return;
        }
        if (!GetNodeSourceOptions(node).Any(option => option.Index == index))
        {
            ShowAddNodeWarning("Select an available source not already represented by another node.");
            RefreshNodeSourceOptions();
            return;
        }
        if (index == previous.Index) return;

        TryGetInputPin(node, out byte oldPin);
        var changedRules = new Dictionary<OutputChannel.MathRule, MathRuleValues>();
        var changedOutputs = new Dictionary<OutputChannel, byte>();
        bool wasApplying = _isApplyingMathRulesFromGraph;
        _isApplyingMathRulesFromGraph = true;
        bool valid = false;
        string error = string.Empty;
        try
        {
            foreach (OutputChannel.MathRule rule in _settings.ChannelsStaticData
                .Where(channel => channel.IsMathChannel)
                .SelectMany(channel => channel.MathRules.Take(channel.MathRuleCount))
                .Concat(MathRuleNodes.Where(rule => rule.IsStandalone).Select(rule => rule.Rule)).Distinct())
            {
                bool a = rule.OperandAType == (byte)previous.Type && rule.OperandAIndex == previous.Index;
                bool b = rule.OperandBType == (byte)previous.Type && rule.OperandBIndex == previous.Index;
                if (!a && !b) continue;
                changedRules.Add(rule, CaptureMathRuleValues(rule));
                if (a) rule.OperandAIndex = (byte)index;
                if (b) rule.OperandBIndex = (byte)index;
            }
            if (oldPin != 0)
            {
                byte newPin = previous.Type == OutputChannel.MathOperandType.AnalogueInput
                    ? InputPinCatalog.ANAChannelInputPins[index] : InputPinCatalog.DIChannelInputPins[index];
                foreach (OutputChannel channel in _settings.ChannelsStaticData.Where(channel => channel.InputControlPin == oldPin))
                {
                    if (!OutputNodeViewModel.IsInputTypeCompatible(newPin, channel.ChanType, _settings.AnalogueInputsStaticData))
                    {
                        error = "The selected input mode is incompatible with a connected output. Change its mode or disconnect that wire first.";
                        break;
                    }
                    changedOutputs.Add(channel, oldPin);
                    channel.InputControlPin = newPin;
                }
            }
            valid = string.IsNullOrEmpty(error) && TrySynchronizeMappedMathOutputs(out error);
            if (!valid)
            {
                foreach (var pair in changedRules) RestoreMathRuleValues(pair.Key, pair.Value);
                foreach (var pair in changedOutputs) pair.Key.InputControlPin = pair.Value;
            }
            else
            {
                switch (node)
                {
                    case AnalogueInputNodeViewModel analogue:
                        analogue.AssignInput(_settings.AnalogueInputsStaticData.Single(input => input.InputNumber == index + 1));
                        break;
                    case DigitalInputNodeViewModel digital:
                        digital.AssignInput(_settings.DigitalInputsStaticData.Single(input => input.InputNumber == index + 1));
                        break;
                    case MathSourceNodeViewModel source:
                        source.AssignSource((byte)index);
                        break;
                }
                foreach (MathRuleNodeViewModel rule in MathRuleNodes) rule.AcceptCurrentRuleState();
                ClearUndoHistory();
            }
        }
        finally
        {
            _isApplyingMathRulesFromGraph = wasApplying;
        }
        RebuildConnections(_settings);
        UpdateLiveChannelSamples(_live?.ChannelsLiveData ?? []);
        if (!valid) ShowAddNodeWarning(error);
        else ClearAddNodeWarning();
        NotifyGraphChanged(valid && (changedRules.Count > 0 || changedOutputs.Count > 0));
    }
}
