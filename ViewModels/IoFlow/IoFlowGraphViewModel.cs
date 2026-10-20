using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using Nodify.Avalonia.Connections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowGraphViewModel : ObservableObject
{
    private DataStructures? _settings;
    private DataStructures? _live;

    public ObservableCollection<IoFlowNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<IoFlowConnectionViewModel> Connections { get; } = [];

    public ObservableCollection<object> SelectedNodes { get; } = [];

    public IReadOnlyList<DigitalInputNodeViewModel> DigitalNodes => Nodes.OfType<DigitalInputNodeViewModel>().ToList();

    public IReadOnlyList<AnalogueInputNodeViewModel> AnalogueNodes => Nodes.OfType<AnalogueInputNodeViewModel>().ToList();

    public IReadOnlyList<OutputNodeViewModel> OutputNodes => Nodes.OfType<OutputNodeViewModel>().ToList();

    public IReadOnlyList<MathRuleNodeViewModel> MathRuleNodes => Nodes.OfType<MathRuleNodeViewModel>().ToList();

    public void Build(DataStructures settings, DataStructures live)
    {
        _settings = settings;
        _live = live;
        Nodes.Clear();
        Connections.Clear();
        SelectedNodes.Clear();
    }

    [RelayCommand]
    private void AddNextDigitalNode()
    {
        if (_settings == null)
        {
            return;
        }

        DigitalInput? next = _settings.DigitalInputsStaticData
            .FirstOrDefault(input => Nodes.OfType<DigitalInputNodeViewModel>().All(node => node.InputNumber != input.InputNumber));

        if (next == null)
        {
            return;
        }

        next.IsActiveHigh = true;
        Nodes.Add(new DigitalInputNodeViewModel(next, new Point(50, 50)));
        AutoArrangeNodes();
        RebuildConnections(_settings);
    }

    [RelayCommand]
    private void AddNextAnalogueNode()
    {
        if (_settings == null)
        {
            return;
        }

        AnalogueInput? next = _settings.AnalogueInputsStaticData
            .FirstOrDefault(input => Nodes.OfType<AnalogueInputNodeViewModel>().All(node => node.InputNumber != input.InputNumber));

        if (next == null)
        {
            return;
        }

        Nodes.Add(new AnalogueInputNodeViewModel(next, new Point(430, 50)));
        AutoArrangeNodes();
        RebuildConnections(_settings);
    }

    [RelayCommand]
    private void AddNextOutputNode()
    {
        if (_settings == null)
        {
            return;
        }

        OutputChannel? next = _settings.ChannelsStaticData
            .FirstOrDefault(channel => Nodes.OfType<OutputNodeViewModel>().All(node => node.ChannelNumber != channel.ChannelNumber));

        if (next == null)
        {
            return;
        }

        AddOutputNodeWithOptionalMath(next);
        AutoArrangeNodes();
        RebuildConnections(_settings);
    }

    [RelayCommand]
    private void ImportCurrentChannel(OutputChannel? channel)
    {
        if (_settings == null || channel == null)
        {
            return;
        }

        AddOutputNodeWithOptionalMath(channel);
        AutoArrangeNodes();
        RebuildConnections(_settings);
    }

    [RelayCommand]
    private void AddLogicNodeForSelectedOutput()
    {
        if (_settings == null)
        {
            return;
        }

        OutputNodeViewModel? targetOutput = SelectedNodes.OfType<OutputNodeViewModel>().FirstOrDefault()
            ?? OutputNodes.FirstOrDefault();

        if (targetOutput == null)
        {
            return;
        }

        targetOutput.Channel.ChanType = OutputChannel.ChannelType.MATH;
        targetOutput.Channel.EnsureMathRuleSlots();

        int nextRuleIndex = MathRuleNodes
            .Where(node => node.OwnerChannelNumber == targetOutput.ChannelNumber)
            .Select(node => node.RuleIndex)
            .DefaultIfEmpty(-1)
            .Max() + 1;

        if (nextRuleIndex >= Constants.NUM_MATH_RULE_SLOTS)
        {
            return;
        }

        if (targetOutput.Channel.MathRuleCount <= nextRuleIndex)
        {
            targetOutput.Channel.MathRuleCount = (byte)(nextRuleIndex + 1);
        }

        OutputChannel.MathRule rule = targetOutput.Channel.MathRules[nextRuleIndex];
        rule.Operation = (byte)OutputChannel.MathOperation.ADD;
        Nodes.Add(new MathRuleNodeViewModel(targetOutput.Channel, nextRuleIndex, rule, new Point(1240, 50)));

        AutoArrangeNodes();
        RebuildConnections(_settings);
    }

    [RelayCommand]
    private void ConnectSelectedNodes()
    {
        var selected = SelectedNodes.OfType<IoFlowNodeViewModel>().Take(2).ToList();
        if (selected.Count < 2)
        {
            return;
        }

        ConnectNodes(selected[0], selected[1]);
    }

    [RelayCommand]
    private void ConnectionCompleted(object? parameter)
    {
        IoFlowConnectorEndpoint? source = null;
        IoFlowConnectorEndpoint? target = null;

        if (parameter is not null)
        {
            TryExtractEndpointsFromParameter(parameter, out source, out target);
        }

        if (source == null || target == null || ReferenceEquals(source.Node, target.Node))
        {
            return;
        }

        IoFlowNodeViewModel sourceNode = source.Node;
        IoFlowNodeViewModel targetNode = target.Node;

        if (CanConnect(sourceNode, targetNode))
        {
            ConnectNodes(sourceNode, targetNode);
            return;
        }

        if (CanConnect(targetNode, sourceNode))
        {
            ConnectNodes(targetNode, sourceNode);
        }
    }

    private static void TryExtractEndpointsFromParameter(object parameter, out IoFlowConnectorEndpoint? source, out IoFlowConnectorEndpoint? target)
    {
        source = null;
        target = null;

        if (parameter is ITuple tuple && tuple.Length >= 2)
        {
            source = ExtractEndpoint(tuple[0]);
            target = ExtractEndpoint(tuple[1]);
        }

        if ((source != null || target != null) || parameter is not PendingConnection pendingConnection)
        {
            return;
        }

        source = ExtractEndpoint(pendingConnection.Source);
        target = ExtractEndpoint(pendingConnection.Target)
            ?? ExtractEndpoint(pendingConnection.PreviewTarget);
    }

    private static bool CanConnect(IoFlowNodeViewModel source, IoFlowNodeViewModel target)
    {
        if (target is OutputNodeViewModel)
        {
            return source is DigitalInputNodeViewModel
                || source is AnalogueInputNodeViewModel
                || source is MathRuleNodeViewModel;
        }

        if (target is MathRuleNodeViewModel targetRule)
        {
            return source is DigitalInputNodeViewModel
                || source is AnalogueInputNodeViewModel
                || source is OutputNodeViewModel
                || (source is MathRuleNodeViewModel sourceRule && sourceRule.OwnerChannelNumber == targetRule.OwnerChannelNumber);
        }

        return false;
    }

    private static IoFlowConnectorEndpoint? ExtractEndpoint(object? value)
    {
        if (value is IoFlowConnectorEndpoint endpoint)
        {
            return endpoint;
        }

        if (value is Connector connector)
        {
            return connector.DataContext as IoFlowConnectorEndpoint;
        }

        if (value == null)
        {
            return null;
        }

        var dataContextProperty = value.GetType().GetProperty("DataContext");
        return dataContextProperty?.GetValue(value) as IoFlowConnectorEndpoint;
    }

    public void ConnectNodes(IoFlowNodeViewModel source, IoFlowNodeViewModel target)
    {
        if (_settings == null || source == target)
        {
            return;
        }

        if (target is OutputNodeViewModel outputTarget)
        {
            if (source is MathRuleNodeViewModel sourceRule)
            {
                outputTarget.Channel.ChanType = OutputChannel.ChannelType.MATH;
                outputTarget.Channel.MathRootRule = (byte)sourceRule.RuleIndex;
                if (outputTarget.Channel.MathRuleCount <= sourceRule.RuleIndex)
                {
                    outputTarget.Channel.MathRuleCount = (byte)(sourceRule.RuleIndex + 1);
                }
            }
            else if (source is DigitalInputNodeViewModel digitalSource
                && digitalSource.InputNumber >= 1
                && digitalSource.InputNumber <= InputPinCatalog.DIChannelInputPins.Length)
            {
                byte pin = InputPinCatalog.DIChannelInputPins[digitalSource.InputNumber - 1];
                ClearInputPinFromOtherOutputs(pin, outputTarget.ChannelNumber);
                outputTarget.Channel.InputControlPin = pin;
            }
            else if (source is AnalogueInputNodeViewModel analogueSource
                && analogueSource.InputNumber >= 1
                && analogueSource.InputNumber <= InputPinCatalog.ANAChannelInputPins.Length)
            {
                byte pin = InputPinCatalog.ANAChannelInputPins[analogueSource.InputNumber - 1];
                ClearInputPinFromOtherOutputs(pin, outputTarget.ChannelNumber);
                outputTarget.Channel.InputControlPin = pin;
            }
            else
            {
                return;
            }
        }
        else if (source is OutputNodeViewModel sourceOutput)
        {
            if (target is DigitalInputNodeViewModel digitalTarget
                && digitalTarget.InputNumber >= 1
                && digitalTarget.InputNumber <= InputPinCatalog.DIChannelInputPins.Length)
            {
                byte pin = InputPinCatalog.DIChannelInputPins[digitalTarget.InputNumber - 1];
                ClearInputPinFromOtherOutputs(pin, sourceOutput.ChannelNumber);
                sourceOutput.Channel.InputControlPin = pin;
            }
            else if (target is AnalogueInputNodeViewModel analogueTarget
                && analogueTarget.InputNumber >= 1
                && analogueTarget.InputNumber <= InputPinCatalog.ANAChannelInputPins.Length)
            {
                byte pin = InputPinCatalog.ANAChannelInputPins[analogueTarget.InputNumber - 1];
                ClearInputPinFromOtherOutputs(pin, sourceOutput.ChannelNumber);
                sourceOutput.Channel.InputControlPin = pin;
            }
            else
            {
                return;
            }
        }
        else if (target is MathRuleNodeViewModel targetRule)
        {
            ApplySourceToRuleOperand(targetRule, source);
        }
        else
        {
            return;
        }

        RebuildConnections(_settings);
    }

    private void ClearInputPinFromOtherOutputs(byte inputPin, int keepChannelNumber)
    {
        foreach (OutputNodeViewModel outputNode in OutputNodes)
        {
            if (outputNode.ChannelNumber == keepChannelNumber)
            {
                continue;
            }

            if (outputNode.Channel.InputControlPin == inputPin)
            {
                outputNode.Channel.InputControlPin = 0;
            }
        }
    }

    [RelayCommand]
    private void RestoreAllNodes()
    {
        if (_settings == null)
        {
            return;
        }

        Nodes.Clear();

        foreach (DigitalInput input in _settings.DigitalInputsStaticData)
        {
            input.IsActiveHigh = true;
            Nodes.Add(new DigitalInputNodeViewModel(input, new Point(50, 50)));
        }

        foreach (AnalogueInput input in _settings.AnalogueInputsStaticData)
        {
            Nodes.Add(new AnalogueInputNodeViewModel(input, new Point(430, 50)));
        }

        foreach (OutputChannel channel in _settings.ChannelsStaticData)
        {
            AddOutputNodeWithOptionalMath(channel);
        }

        AutoArrangeNodes();
        RebuildConnections(_settings);
    }

    [RelayCommand]
    private void RemoveSelectedNodes()
    {
        if (SelectedNodes.Count == 0)
        {
            return;
        }

        var toRemove = SelectedNodes.OfType<IoFlowNodeViewModel>().ToList();
        foreach (var node in toRemove)
        {
            if (node is OutputNodeViewModel outputNode)
            {
                var relatedRules = MathRuleNodes.Where(rule => rule.OwnerChannelNumber == outputNode.ChannelNumber).ToList();
                foreach (var ruleNode in relatedRules)
                {
                    Nodes.Remove(ruleNode);
                }
            }

            Nodes.Remove(node);
        }

        SelectedNodes.Clear();

        if (_settings != null)
        {
            RebuildConnections(_settings);
        }
    }

    [RelayCommand]
    private void AutoArrangeNodes()
    {
        int digitalRow = 0;
        int analogueRow = 0;
        int outputRow = 0;
        int ruleRow = 0;

        foreach (IoFlowNodeViewModel node in Nodes.OrderBy(node => node.Title))
        {
            switch (node)
            {
                case DigitalInputNodeViewModel:
                    node.Location = new Point(50, 50 + (digitalRow++ * 110));
                    break;
                case AnalogueInputNodeViewModel:
                    node.Location = new Point(430, 50 + (analogueRow++ * 140));
                    break;
                case OutputNodeViewModel:
                    node.Location = new Point(830, 50 + (outputRow++ * 180));
                    break;
                case MathRuleNodeViewModel:
                    node.Location = new Point(1240, 50 + (ruleRow++ * 130));
                    break;
            }
        }
    }

    public void UpdateLiveChannelSamples(IReadOnlyList<OutputChannel> liveChannels)
    {
        foreach (OutputNodeViewModel node in OutputNodes)
        {
            int index = node.ChannelNumber - 1;
            if (index < 0 || index >= liveChannels.Count)
            {
                continue;
            }

            node.UpdateLiveCurrent(liveChannels[index].CurrentValue);
        }

        foreach (DigitalInputNodeViewModel digitalNode in DigitalNodes)
        {
            DigitalInput? liveInput = _live?.DigitalInputsLiveData.ElementAtOrDefault(digitalNode.InputNumber - 1);
            digitalNode.UpdateLiveState(liveInput);
        }

        foreach (AnalogueInputNodeViewModel analogueNode in AnalogueNodes)
        {
            AnalogueInput? liveInput = _live?.AnalogueInputsLiveData.ElementAtOrDefault(analogueNode.InputNumber - 1);
            analogueNode.UpdateLiveState(liveInput);
        }
    }

    public void RebuildConnections(DataStructures settings)
    {
        Dictionary<byte, IoFlowNodeViewModel> inputsByPin = [];

        foreach (IoFlowNodeViewModel node in Nodes)
        {
            switch (node)
            {
                case DigitalInputNodeViewModel digitalNode when digitalNode.InputNumber >= 1 && digitalNode.InputNumber <= InputPinCatalog.DIChannelInputPins.Length:
                    inputsByPin[InputPinCatalog.DIChannelInputPins[digitalNode.InputNumber - 1]] = digitalNode;
                    break;
                case AnalogueInputNodeViewModel analogueNode when analogueNode.InputNumber >= 1 && analogueNode.InputNumber <= InputPinCatalog.ANAChannelInputPins.Length:
                    inputsByPin[InputPinCatalog.ANAChannelInputPins[analogueNode.InputNumber - 1]] = analogueNode;
                    break;
            }
        }

        foreach (IoFlowNodeViewModel node in Nodes)
        {
            node.InputConnector.IsConnected = false;
            node.OutputConnector.IsConnected = false;
        }

        Connections.Clear();

        foreach (OutputNodeViewModel outputNode in OutputNodes)
        {
            byte pin = outputNode.Channel.InputControlPin;
            if (pin != 0 && inputsByPin.TryGetValue(pin, out IoFlowNodeViewModel? sourceNode))
            {
                AddConnection(sourceNode, outputNode, $"{InputPinCatalog.GetLabelForPin(pin)} → OUT {outputNode.Channel.ChannelNumber}");
            }

            if (outputNode.Channel.ChanType == OutputChannel.ChannelType.MATH)
            {
                AddMathConnections(outputNode);
            }
        }
    }

    private void ApplySourceToRuleOperand(MathRuleNodeViewModel targetRule, IoFlowNodeViewModel source)
    {
        bool useA = targetRule.Rule.OperandAType == (byte)OutputChannel.MathOperandType.Constant;

        void Assign(byte operandType, byte operandIndex)
        {
            if (useA)
            {
                targetRule.Rule.OperandAType = operandType;
                targetRule.Rule.OperandAIndex = operandIndex;
            }
            else
            {
                targetRule.Rule.OperandBType = operandType;
                targetRule.Rule.OperandBIndex = operandIndex;
            }
        }

        switch (source)
        {
            case DigitalInputNodeViewModel digitalNode:
                Assign((byte)OutputChannel.MathOperandType.DigitalInput, (byte)(digitalNode.InputNumber - 1));
                break;
            case AnalogueInputNodeViewModel analogueNode:
                Assign((byte)OutputChannel.MathOperandType.AnalogueInput, (byte)(analogueNode.InputNumber - 1));
                break;
            case OutputNodeViewModel outputNode:
                Assign((byte)OutputChannel.MathOperandType.Channel, (byte)(outputNode.ChannelNumber - 1));
                break;
            case MathRuleNodeViewModel sourceRule when sourceRule.OwnerChannelNumber == targetRule.OwnerChannelNumber:
                Assign((byte)OutputChannel.MathOperandType.Rule, (byte)sourceRule.RuleIndex);
                break;
        }
    }

    private void AddMathConnections(OutputNodeViewModel outputNode)
    {
        var rules = MathRuleNodes
            .Where(rule => rule.OwnerChannelNumber == outputNode.ChannelNumber)
            .OrderBy(rule => rule.RuleIndex)
            .ToList();

        foreach (MathRuleNodeViewModel ruleNode in rules)
        {
            if (ruleNode.RuleIndex == outputNode.Channel.MathRootRule)
            {
                AddConnection(ruleNode, outputNode, "Root rule");
            }

            AddOperandConnectionIfRule(ruleNode, ruleNode.Rule.OperandAType, ruleNode.Rule.OperandAIndex, "A", rules);
            AddOperandConnectionIfRule(ruleNode, ruleNode.Rule.OperandBType, ruleNode.Rule.OperandBIndex, "B", rules);
        }
    }

    private void AddOperandConnectionIfRule(MathRuleNodeViewModel targetRule, byte operandType, byte operandIndex, string operandLabel, List<MathRuleNodeViewModel> rules)
    {
        if (operandType != (byte)OutputChannel.MathOperandType.Rule)
        {
            return;
        }

        MathRuleNodeViewModel? sourceRule = rules.FirstOrDefault(rule => rule.RuleIndex == operandIndex);
        if (sourceRule == null)
        {
            return;
        }

        AddConnection(sourceRule, targetRule, $"Rule {operandLabel}");
    }

    private static IoFlowConnectionViewModel CreateConnection(IoFlowNodeViewModel source, IoFlowNodeViewModel target, string label)
    {
        return new IoFlowConnectionViewModel(source.OutputConnector, target.InputConnector, label);
    }

    private void AddConnection(IoFlowNodeViewModel source, IoFlowNodeViewModel target, string label)
    {
        source.OutputConnector.IsConnected = true;
        target.InputConnector.IsConnected = true;
        Connections.Add(CreateConnection(source, target, label));
    }

    private void AddOutputNodeWithOptionalMath(OutputChannel channel)
    {
        if (Nodes.OfType<OutputNodeViewModel>().All(node => node.ChannelNumber != channel.ChannelNumber))
        {
            Nodes.Add(new OutputNodeViewModel(channel, new Point(830, 50)));
        }

        if (channel.ChanType != OutputChannel.ChannelType.MATH)
        {
            return;
        }

        channel.EnsureMathRuleSlots();
        int activeRuleCount = channel.MathRuleCount;
        for (int ruleIndex = 0; ruleIndex < activeRuleCount && ruleIndex < channel.MathRules.Count; ruleIndex++)
        {
            if (Nodes.OfType<MathRuleNodeViewModel>().Any(node => node.OwnerChannelNumber == channel.ChannelNumber && node.RuleIndex == ruleIndex))
            {
                continue;
            }

            Nodes.Add(new MathRuleNodeViewModel(channel, ruleIndex, channel.MathRules[ruleIndex], new Point(1240, 50)));
        }
    }
}
