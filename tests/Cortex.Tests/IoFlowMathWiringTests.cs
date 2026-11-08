using Avalonia;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Xunit;

namespace Cortex.Tests;

public sealed class IoFlowMathWiringTests
{
    public static IEnumerable<object[]> Operations => Enum.GetValues<OutputChannel.MathOperation>()
        .Select(operation => new object[] { operation });

    [Theory]
    [MemberData(nameof(Operations))]
    public void ConfiguredOperandsCreateSourceNodesAndSeparateWiresForEveryOperation(OutputChannel.MathOperation operation)
    {
        var (settings, graph, rule) = CreateAnalogueRule(operation);

        IoFlowConnectionViewModel a = Assert.Single(graph.Connections, wire => ReferenceEquals(wire.Target, rule.OperandAConnector));
        Assert.Equal(2, Assert.IsType<AnalogueInputNodeViewModel>(a.Source.Node).InputNumber);
        if (rule.IsUnaryOperation)
        {
            Assert.DoesNotContain(graph.Connections, wire => ReferenceEquals(wire.Target, rule.OperandBConnector));
            Assert.Single(graph.AnalogueNodes);
        }
        else
        {
            IoFlowConnectionViewModel b = Assert.Single(graph.Connections, wire => ReferenceEquals(wire.Target, rule.OperandBConnector));
            Assert.Equal(3, Assert.IsType<AnalogueInputNodeViewModel>(b.Source.Node).InputNumber);
            Assert.Equal(2, graph.AnalogueNodes.Count);
        }
        Assert.Same(settings.ChannelsStaticData[0].MathRules[0], rule.Rule);
        Assert.DoesNotContain(graph.Connections, wire => ReferenceEquals(wire.Target, rule.InputConnector));
        Assert.Equal(rule.IsUnaryOperation ? 2 : 3, rule.ConnectedSourceOptions.Count);
        OutputNodeViewModel output = graph.OutputNodes[0];
        Assert.True(rule.Location.X < output.Location.X);
        Assert.All(graph.AnalogueNodes, source => Assert.True(source.Location.X < rule.Location.X));
    }

    [Fact]
    public async Task ExplicitOperandConnectorsReplaceOnlyThatOperandAndWireDeletionPreservesTheOther()
    {
        var (settings, graph, rule) = CreateAnalogueRule();
        AnalogueInputNodeViewModel second = graph.AnalogueNodes.Single(node => node.InputNumber == 2);
        byte originalB = rule.Rule.OperandBIndex;

        await graph.ConnectionCompletedCommand.ExecuteAsync((second.OutputConnector, rule.OperandBConnector));

        Assert.Equal((byte)1, rule.Rule.OperandAIndex);
        Assert.Equal((byte)1, rule.Rule.OperandBIndex);
        Assert.Equal(2, graph.Connections.Count(wire => ReferenceEquals(wire.Source.Node, second) && ReferenceEquals(wire.Target.Node, rule)));
        IoFlowConnectionViewModel a = graph.Connections.Single(wire => ReferenceEquals(wire.Target, rule.OperandAConnector));
        await graph.RemoveConnectionCommand.ExecuteAsync(a);

        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, rule.Rule.OperandAType);
        Assert.Equal((byte)OutputChannel.MathOperandType.AnalogueInput, rule.Rule.OperandBType);
        Assert.Equal((byte)1, rule.Rule.OperandBIndex);
        Assert.Single(graph.Connections, wire => ReferenceEquals(wire.Target, rule.OperandBConnector));
        Assert.NotEqual(originalB, settings.ChannelsStaticData[0].MathRules[0].OperandBIndex);
        await graph.UndoCommand.ExecuteAsync(null);
        Assert.Equal((byte)OutputChannel.MathOperandType.AnalogueInput, rule.Rule.OperandAType);
        Assert.Equal((byte)1, rule.Rule.OperandBIndex);
    }

    [Fact]
    public void SelectorsContainOnlyConnectedSourcesAndConstantAndReassignIndependently()
    {
        var (_, graph, rule) = CreateAnalogueRule();
        graph.AddNextDigitalNodeCommand.Execute(null);
        Assert.DoesNotContain(rule.ConnectedSourceOptions, option => option.Type == (byte)OutputChannel.MathOperandType.DigitalInput);
        rule.SelectedOperandBSource = rule.ConnectedSourceOptions.Single(option => option.Index == 1 &&
            option.Type == (byte)OutputChannel.MathOperandType.AnalogueInput);
        Assert.Equal((byte)1, rule.Rule.OperandAIndex);
        Assert.Equal((byte)1, rule.Rule.OperandBIndex);
        Assert.Equal(2, graph.Connections.Count(wire => ReferenceEquals(wire.Target.Node, rule)));
        Assert.Equal(2, graph.AnalogueNodes.Count);
    }

    [Theory]
    [InlineData(OutputChannel.MathOperandType.Channel, 1)]
    [InlineData(OutputChannel.MathOperandType.DigitalInput, 1)]
    [InlineData(OutputChannel.MathOperandType.SystemValue, 3)]
    [InlineData(OutputChannel.MathOperandType.CanSignal, 0)]
    public void EveryNonconstantInputKindMaterializesAndRoundTrips(OutputChannel.MathOperandType type, byte index)
    {
        var (settings, graph, rule) = CreateAnalogueRule();
        ConfigureRx(settings);
        rule.Rule.OperandAType = (byte)type;
        rule.Rule.OperandAIndex = index;
        graph.RebuildConnections(settings);
        IoFlowConnectionViewModel wire = Assert.Single(graph.Connections, connection => ReferenceEquals(connection.Target, rule.OperandAConnector));
        Point position = wire.Source.Node.Location;
        IoFlowGraphSnapshot snapshot = graph.CreateSnapshot();

        graph.ApplySnapshot(snapshot);

        MathRuleNodeViewModel restored = graph.MathRuleNodes.Single();
        IoFlowConnectionViewModel restoredWire = Assert.Single(graph.Connections, connection => ReferenceEquals(connection.Target, restored.OperandAConnector));
        Assert.Equal(wire.Source.Node.GetType(), restoredWire.Source.Node.GetType());
        Assert.Equal(position, restoredWire.Source.Node.Location);
        Assert.Equal((byte)type, restored.Rule.OperandAType);
        Assert.Equal(index, restored.Rule.OperandAIndex);
        Assert.Equal(3, restored.ConnectedSourceOptions.Count);
    }

    [Fact]
    public async Task CanRxSourcesOnlyConnectToMathAndSystemCanDoesNotChooseAnArbitrarySignal()
    {
        DataStructures settings = new();
        ConfigureRx(settings);
        settings.CustomCanMessagesStaticData[1].IsTx = true;
        settings.CustomCanMessagesStaticData[1].Id = 0x200;
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        graph.AddCanRxNodeCommand.Execute(null);
        graph.AddCanInputNodeCommand.Execute(null);
        graph.AddCanTxNodeCommand.Execute(null);
        MathSourceNodeViewModel rx = Assert.Single(graph.MathSourceNodes);
        MathRuleNodeViewModel rule = graph.MathRuleNodes.Single();

        await graph.ConnectNodes(rx, graph.OutputNodes[0]);
        await graph.ConnectNodes(rx, graph.CanTxNodes[0]);
        await graph.ConnectNodes(graph.CanNodes[0], rule, true);
        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, rule.Rule.OperandAType);
        Assert.DoesNotContain(graph.Connections, wire => ReferenceEquals(wire.Source.Node, rx));

        await graph.ConnectNodes(rx, rule, false);
        Assert.Equal((byte)OutputChannel.MathOperandType.CanSignal, rule.Rule.OperandBType);
        Assert.Equal((byte)0, rule.Rule.OperandBIndex);
        Assert.Single(graph.Connections, wire => ReferenceEquals(wire.Source.Node, rx));
    }

    [Fact]
    public async Task RemovingSourceClearsAllItsMathUsesWithoutRecreatingIt()
    {
        var (settings, graph, rule) = CreateAnalogueRule();
        graph.SelectedNodes.Add(graph.AnalogueNodes.Single(node => node.InputNumber == 2));
        await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);
        graph.RebuildConnections(settings);

        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, rule.Rule.OperandAType);
        Assert.Equal(0, rule.Rule.OperandAConstant);
        Assert.Equal((byte)OutputChannel.MathOperandType.AnalogueInput, rule.Rule.OperandBType);
        Assert.DoesNotContain(graph.AnalogueNodes, node => node.InputNumber == 2);
        Assert.Single(graph.Connections, wire => ReferenceEquals(wire.Target.Node, rule));
    }

    [Fact]
    public void UnconnectedAnalogueNodesRetainSharedConfigurationAndPersistForLogging()
    {
        DataStructures settings = new();
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddNextAnalogueNodeCommand.Execute(null);
        AnalogueInputNodeViewModel analogue = Assert.Single(graph.AnalogueNodes);
        analogue.Input.ChanType = AnalogueInput.AnalogueChannelType.RawVoltage;
        analogue.Location = new Point(200, 150);
        graph.ApplySnapshot(graph.CreateSnapshot());
        graph.RebuildConnections(settings);

        analogue = Assert.Single(graph.AnalogueNodes);
        Assert.Same(settings.AnalogueInputsStaticData[0], analogue.Input);
        Assert.Equal(AnalogueInput.AnalogueChannelType.RawVoltage, analogue.Input.ChanType);
        Assert.Equal(new Point(200, 150), analogue.Location);
        Assert.Empty(graph.Connections);
        Assert.Empty(graph.OutputNodes);
    }

    [Fact]
    public void UnaryOperationHidesBWithoutLosingItsConfigurationAndRestoresTheWire()
    {
        var (settings, graph, rule) = CreateAnalogueRule();
        rule.Rule.Operation = (byte)OutputChannel.MathOperation.ABS;
        graph.RebuildConnections(settings);
        Assert.DoesNotContain(graph.Connections, wire => ReferenceEquals(wire.Target, rule.OperandBConnector));
        Assert.Equal((byte)2, rule.Rule.OperandBIndex);
        rule.Rule.Operation = (byte)OutputChannel.MathOperation.ADD;
        graph.RebuildConnections(settings);
        Assert.Single(graph.Connections, wire => ReferenceEquals(wire.Target, rule.OperandBConnector));
        Assert.Equal(2, graph.AnalogueNodes.Count);
    }

    [Fact]
    public async Task RestoredStandaloneChainRetainsDependenciesAndItsPhysicalSourceWires()
    {
        DataStructures settings = new();
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        MathRuleNodeViewModel[] rules = graph.MathRuleNodes.ToArray();
        rules[0].Rule.OperandAIndex = 1;
        rules[0].Rule.OperandAType = (byte)OutputChannel.MathOperandType.AnalogueInput;
        await graph.ConnectNodes(rules[0], rules[1], true);
        await graph.ConnectNodes(rules[1], graph.OutputNodes[0]);

        graph.ApplySnapshot(graph.CreateSnapshot());

        MathRuleNodeViewModel root = graph.MathRuleNodes.Single(node => node.LogicNodeId == rules[1].LogicNodeId);
        MathRuleNodeViewModel source = graph.MathRuleNodes.Single(node => node.LogicNodeId == rules[0].LogicNodeId);
        Assert.Equal((byte)OutputChannel.MathOperandType.Rule, root.Rule.OperandAType);
        Assert.Equal((byte)source.LogicNodeId, root.Rule.OperandAIndex);
        Assert.Contains(graph.Connections, wire => ReferenceEquals(wire.Source.Node, source) && ReferenceEquals(wire.Target, root.OperandAConnector));
        Assert.Contains(graph.Connections, wire => wire.Source.Node is AnalogueInputNodeViewModel { InputNumber: 2 }
            && ReferenceEquals(wire.Target, source.OperandAConnector));
        source.Rule.OperandBConstant = 24;
        Assert.Equal(24, settings.ChannelsStaticData[0].MathRules[0].OperandBConstant);
    }

    [Fact]
    public async Task SourceDeletionAlsoClearsConfiguredMathUsesInChannelsNotOnTheCanvas()
    {
        var (settings, graph, _) = CreateAnalogueRule();
        graph.Nodes.Remove(graph.OutputNodes.Single(node => node.ChannelNumber == 2));
        OutputChannel hidden = settings.ChannelsStaticData[1];
        hidden.ChanType = OutputChannel.ChannelType.MATH;
        hidden.MathRules[0].OperandAType = (byte)OutputChannel.MathOperandType.AnalogueInput;
        hidden.MathRules[0].OperandAIndex = 1;
        graph.SelectedNodes.Add(graph.AnalogueNodes.Single(node => node.InputNumber == 2));
        await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);

        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, hidden.MathRules[0].OperandAType);
        graph.PopulateFromCurrentSettings();
        Assert.DoesNotContain(graph.AnalogueNodes, node => node.InputNumber == 2);
    }

    [Fact]
    public async Task CanRxNodeBecomesUnavailableIfItsSignalChangesToTx()
    {
        DataStructures settings = new();
        ConfigureRx(settings);
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddCanRxNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        MathSourceNodeViewModel source = graph.MathSourceNodes.Single();
        MathRuleNodeViewModel rule = graph.MathRuleNodes.Single();
        await graph.ConnectNodes(source, rule, true);

        settings.CustomCanMessagesStaticData[0].IsTx = true;
        graph.RebuildConnections(settings);

        Assert.False(source.IsAvailable);
        Assert.Contains("Unavailable", source.SourceDescription);
        Assert.DoesNotContain(graph.Connections, wire => ReferenceEquals(wire.Source.Node, source));
        await graph.ConnectNodes(source, rule, false);
        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, rule.Rule.OperandBType);
    }

    private static (DataStructures Settings, IoFlowGraphViewModel Graph, MathRuleNodeViewModel Rule) CreateAnalogueRule(
        OutputChannel.MathOperation operation = OutputChannel.MathOperation.ADD)
    {
        DataStructures settings = new();
        OutputChannel channel = settings.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.MathRuleCount = 1;
        channel.MathRules[0].Operation = (byte)operation;
        channel.MathRules[0].OperandAType = (byte)OutputChannel.MathOperandType.AnalogueInput;
        channel.MathRules[0].OperandAIndex = 1;
        channel.MathRules[0].OperandBType = (byte)OutputChannel.MathOperandType.AnalogueInput;
        channel.MathRules[0].OperandBIndex = 2;
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();
        return (settings, graph, Assert.Single(graph.MathRuleNodes));
    }

    private static void ConfigureRx(DataStructures settings)
    {
        settings.CustomCanMessagesStaticData[0].Id = 0x100;
        settings.CustomCanMessagesStaticData[0].IsTx = false;
        settings.CustomCanMessagesStaticData[0].IsEnabled = true;
        settings.CustomCanSignalsStaticData[0].IsEnabled = true;
        settings.CustomCanSignalsStaticData[0].MessageIndex = 0;
    }
}
