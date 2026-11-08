using Avalonia;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Xunit;

namespace Cortex.Tests;

public sealed class IoFlowUndoTests
{
    [Fact]
    public async Task MultiNodeDragIsOneUndoAndDoesNotChangeControllerSettings()
    {
        var (graph, settings) = CreateGraph();
        IoFlowNodeViewModel[] nodes = [graph.DigitalNodes[0], graph.OutputNodes[0]];
        Point[] original = nodes.Select(node => node.Location).ToArray();
        List<bool> notifications = [];
        graph.GraphChangedAction = notifications.Add;

        graph.ItemsDragStartedCommand.Execute(null);
        foreach (IoFlowNodeViewModel node in nodes)
        {
            node.Location += new Vector(50, -75);
        }
        graph.ItemsDragCompletedCommand.Execute(null);
        Assert.True(graph.CanUndo);

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(original, nodes.Select(node => node.Location));
        Assert.False(graph.CanUndo);
        Assert.Equal(new[] { false, false }, notifications);
        Assert.Equal(0, settings.ChannelsStaticData[0].InputControlPin);
    }

    [Fact]
    public async Task UndoRetainsExactlyTenCompletedActions()
    {
        var (graph, _) = CreateGraph();
        IoFlowNodeViewModel node = graph.DigitalNodes[0];
        Point initial = node.Location;
        for (int action = 0; action < 12; action++)
        {
            Move(graph, node, new Vector(10, 0));
        }

        for (int action = 0; action < 10; action++)
        {
            Assert.True(graph.CanUndo);
            await graph.UndoCommand.ExecuteAsync(null);
        }

        Assert.False(graph.CanUndo);
        Assert.Equal(initial + new Vector(20, 0), node.Location);
    }

    [Fact]
    public async Task NoOpOrCancelledDragDoesNotConsumeHistory()
    {
        var (graph, _) = CreateGraph();
        IoFlowNodeViewModel node = graph.DigitalNodes[0];
        Point initial = node.Location;
        Move(graph, node, new Vector(10, 0));
        for (int index = 0; index < 12; index++)
        {
            graph.ItemsDragStartedCommand.Execute(null);
            graph.ItemsDragCompletedCommand.Execute(null);
        }

        graph.ItemsDragStartedCommand.Execute(null);
        node.Location += new Vector(50, 0);
        node.Location -= new Vector(50, 0);
        graph.ItemsDragCompletedCommand.Execute(null);
        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(initial, node.Location);
        Assert.False(graph.CanUndo);
    }

    [Fact]
    public async Task AutoArrangeCanBeUndoneInOneAction()
    {
        var (graph, _) = CreateGraph();
        Point[] original = graph.Nodes.Select(node => node.Location).ToArray();
        graph.AutoArrangeNodesCommand.Execute(null);
        Assert.NotEqual(original, graph.Nodes.Select(node => node.Location));

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(original, graph.Nodes.Select(node => node.Location));
        Assert.False(graph.CanUndo);
    }

    [Fact]
    public async Task ConnectAndDisconnectUndoRestoresSettingsAndKeepsLaterUnrelatedEdits()
    {
        var (graph, _) = CreateGraph();
        DigitalInputNodeViewModel input = graph.DigitalNodes[0];
        OutputNodeViewModel output = graph.OutputNodes[0];
        output.Channel.CurrentThresholdHigh = 8;
        output.Channel.SoftStartEnabled = 1;
        output.Channel.SoftStartTime = 500;
        await graph.ConnectNodes(input, output);
        Point outputLocation = output.Location;

        await graph.RemoveConnectionCommand.ExecuteAsync(Assert.Single(graph.Connections));
        Assert.Equal(0, output.Channel.InputControlPin);
        Assert.Equal(0, output.Channel.CurrentThresholdHigh);
        output.Channel.Name = "NEW".ToCharArray();

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(InputPinCatalog.DIChannelInputPins[0], output.Channel.InputControlPin);
        Assert.Equal(8, output.Channel.CurrentThresholdHigh);
        Assert.Equal(1, output.Channel.SoftStartEnabled);
        Assert.Equal(500, output.Channel.SoftStartTime);
        Assert.Equal("NEW", new string(output.Channel.Name));
        Assert.Equal(outputLocation, output.Location);
        Assert.Single(graph.Connections);

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(0, output.Channel.InputControlPin);
        Assert.Empty(graph.Connections);
        Assert.Equal(8, output.Channel.CurrentThresholdHigh);
        Assert.False(graph.CanUndo);
    }

    [Fact]
    public async Task ConnectingAnInputToAnotherOutputKeepsExistingLinksAndUndoRemovesOnlyTheNewOne()
    {
        var (graph, _) = CreateGraph();
        graph.AddNextOutputNodeCommand.Execute(null);
        DigitalInputNodeViewModel input = graph.DigitalNodes[0];
        OutputNodeViewModel first = graph.OutputNodes[0];
        OutputNodeViewModel second = graph.OutputNodes[1];
        OutputNodeViewModel third = graph.OutputNodes[2];
        byte pin = InputPinCatalog.DIChannelInputPins[0];
        first.Channel.CurrentThresholdHigh = 12;
        await graph.ConnectNodes(input, first);
        await graph.ConnectNodes(input, second);
        graph.ClearUndoHistory();

        await graph.ConnectNodes(input, third);

        Assert.All(new[] { first, second, third }, output => Assert.Equal(pin, output.Channel.InputControlPin));
        Assert.Equal(12, first.Channel.CurrentThresholdHigh);
        Assert.Equal(3, graph.Connections.Count);
        Assert.All(graph.Connections, wire => Assert.Same(input, wire.Source.Node));
        Assert.Equal(new[] { first, second, third }, graph.Connections.Select(wire => wire.Target.Node).OrderBy(node => ((OutputNodeViewModel)node).ChannelNumber));

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(pin, first.Channel.InputControlPin);
        Assert.Equal(pin, second.Channel.InputControlPin);
        Assert.Equal(0, third.Channel.InputControlPin);
        Assert.Equal(2, graph.Connections.Count);
        Assert.False(graph.CanUndo);
    }

    [Fact]
    public async Task UndoingConnectionDoesNotUndoANodeMoveAndConversely()
    {
        var (graph, _) = CreateGraph();
        IoFlowNodeViewModel input = graph.DigitalNodes[0];
        OutputNodeViewModel output = graph.OutputNodes[0];
        Point original = output.Location;
        Move(graph, output, new Vector(100, 50));
        await graph.ConnectNodes(input, output);
        await graph.UndoCommand.ExecuteAsync(null);
        Assert.Empty(graph.Connections);
        Assert.Equal(original + new Vector(100, 50), output.Location);
        await graph.UndoCommand.ExecuteAsync(null);
        Assert.Equal(original, output.Location);
    }

    [Fact]
    public async Task LogicOperandsAndRootConnectionsCanBeUndone()
    {
        var (graph, _) = CreateGraph();
        graph.AddLogicNodeCommand.Execute(null);
        MathRuleNodeViewModel logic = Assert.Single(graph.MathRuleNodes);
        logic.Rule.Operation = (byte)OutputChannel.MathOperation.GREATER_THAN;
        logic.Rule.OperandBConstant = 9;
        await graph.ConnectNodes(graph.DigitalNodes[0], logic);
        await graph.ConnectNodes(logic, graph.OutputNodes[0]);
        Assert.Equal(OutputChannel.ChannelType.MATH, graph.OutputNodes[0].Channel.ChanType);
        Assert.Single(graph.CreateSnapshot().OutputRootLogicNodeByChannel);

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Empty(graph.CreateSnapshot().OutputRootLogicNodeByChannel);
        Assert.Equal(OutputChannel.ChannelType.Digital, graph.OutputNodes[0].Channel.ChanType);
        Assert.Single(graph.Connections);
        Assert.Equal((byte)OutputChannel.MathOperandType.DigitalInput, logic.Rule.OperandAType);

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Empty(graph.Connections);
        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, logic.Rule.OperandAType);
        Assert.Equal(9, logic.Rule.OperandBConstant);
    }

    [Fact]
    public async Task RemovingOwnedMathOperandConnectionCanBeUndone()
    {
        var (graph, settings) = CreateGraph();
        OutputChannel channel = settings.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.MathRuleCount = 2;
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();
        MathRuleNodeViewModel rule = graph.MathRuleNodes[1];
        await graph.ConnectNodes(graph.MathRuleNodes[0], rule);
        graph.ClearUndoHistory();

        await graph.RemoveConnectionCommand.ExecuteAsync(Assert.Single(graph.Connections,
            connection => ReferenceEquals(connection.Target.Node, rule)));
        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, rule.Rule.OperandAType);
        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal((byte)OutputChannel.MathOperandType.Rule, rule.Rule.OperandAType);
        Assert.Contains(graph.Connections, connection => ReferenceEquals(connection.Target.Node, rule));
    }

    [Fact]
    public async Task FailedAndRepeatedConnectionsDoNotConsumeHistory()
    {
        var (graph, _) = CreateGraph();
        await graph.ConnectNodes(graph.DigitalNodes[0], graph.OutputNodes[0]);
        await graph.ConnectNodes(graph.DigitalNodes[0], graph.OutputNodes[0]);
        graph.AddLogicNodeCommand.Execute(null);
        graph.ConfirmAction = (_, _, _, _) => Task.FromResult(false);
        await graph.ConnectNodes(graph.MathRuleNodes[0], graph.OutputNodes[0]);
        await graph.ConnectNodes(graph.DigitalNodes[0], graph.DigitalNodes[0]);

        await graph.UndoCommand.ExecuteAsync(null);
        Assert.Empty(graph.Connections);
        Assert.False(graph.CanUndo);
    }

    [Fact]
    public async Task IndependentlyEditedConnectionSettingsAreNotOverwritten()
    {
        var (graph, _) = CreateGraph();
        await graph.ConnectNodes(graph.DigitalNodes[0], graph.OutputNodes[0]);
        graph.OutputNodes[0].Channel.InputControlPin = InputPinCatalog.DIChannelInputPins[1];

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(InputPinCatalog.DIChannelInputPins[1], graph.OutputNodes[0].Channel.InputControlPin);
        Assert.False(graph.CanUndo);
        Assert.Contains("settings have changed", graph.AddNodeWarning);
    }

    [Fact]
    public async Task CanTxConnectAndDisconnectUndoUsesTheControllerCallback()
    {
        var (graph, settings) = CreateGraph();
        CustomCanMessage message = settings.CustomCanMessagesStaticData[0];
        message.Id = 0x97;
        message.Flags = 0x04;
        message.TxTrigger = Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS;
        graph.AddCanTxNodeCommand.Execute(null);
        CanTxNodeViewModel tx = Assert.Single(graph.CanTxNodes);
        List<(byte Trigger, bool Enabled)> calls = [];
        graph.SetCustomCanTxStateAction = (_, trigger, enabled) =>
        {
            calls.Add((trigger, enabled));
            message.TxTrigger = trigger;
            message.IsEnabled = enabled;
            return Task.FromResult(true);
        };

        await graph.ConnectNodes(graph.OutputNodes[0], tx);
        await graph.RemoveConnectionCommand.ExecuteAsync(Assert.Single(graph.Connections));
        await graph.UndoCommand.ExecuteAsync(null);
        Assert.True(message.IsEnabled);
        Assert.Single(graph.Connections);
        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(new[] { ((byte)0, true), ((byte)0, false), ((byte)0, true),
            (Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, false) }, calls);
        Assert.False(message.IsEnabled);
        Assert.Equal(Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, message.TxTrigger);
        Assert.Empty(graph.Connections);
    }

    [Fact]
    public async Task FailedCanTxUndoRetainsHistoryAndDoesNotFakeSuccess()
    {
        var (graph, settings) = CreateGraph();
        CustomCanMessage message = settings.CustomCanMessagesStaticData[0];
        message.Id = 0x97;
        message.Flags = 0x04;
        graph.AddCanTxNodeCommand.Execute(null);
        await graph.ConnectNodes(graph.OutputNodes[0], graph.CanTxNodes[0]);
        graph.SetCustomCanTxStateAction = (_, _, _) => Task.FromResult(false);

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.True(message.IsEnabled);
        Assert.Single(graph.Connections);
        Assert.True(graph.CanUndo);
        Assert.Contains("controller update failed", graph.AddNodeWarning);
        graph.SetCustomCanTxStateAction = (_, trigger, enabled) =>
        {
            message.TxTrigger = trigger;
            message.IsEnabled = enabled;
            return Task.FromResult(true);
        };
        await graph.UndoCommand.ExecuteAsync(null);
        Assert.False(message.IsEnabled);
        Assert.False(graph.CanUndo);
    }

    [Fact]
    public async Task NodeMoveDuringCanTxUndoRetainsItsOwnHistory()
    {
        var (graph, settings) = CreateGraph();
        CustomCanMessage message = settings.CustomCanMessagesStaticData[0];
        message.Id = 0x97;
        message.Flags = 0x04;
        graph.AddCanTxNodeCommand.Execute(null);
        await graph.ConnectNodes(graph.OutputNodes[0], graph.CanTxNodes[0]);
        var completion = new TaskCompletionSource<bool>();
        graph.SetCustomCanTxStateAction = async (_, trigger, enabled) =>
        {
            if (!await completion.Task)
            {
                return false;
            }
            message.TxTrigger = trigger;
            message.IsEnabled = enabled;
            return true;
        };

        Task undo = graph.UndoCommand.ExecuteAsync(null);
        Assert.False(undo.IsCompleted);
        Point original = graph.DigitalNodes[0].Location;
        Move(graph, graph.DigitalNodes[0], new Vector(50, 0));
        completion.SetResult(true);
        await undo;
        Assert.False(message.IsEnabled);
        Assert.True(graph.CanUndo);

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(original, graph.DigitalNodes[0].Location);
        Assert.False(graph.CanUndo);
    }

    [Fact]
    public async Task CanTxRemovalWaitsForControllerAcceptanceBeforeRecordingHistory()
    {
        var (graph, settings) = CreateGraph();
        CustomCanMessage message = settings.CustomCanMessagesStaticData[0];
        message.Id = 0x97;
        message.Flags = 0x05;
        message.TxTrigger = 0;
        graph.AddCanTxNodeCommand.Execute(null);
        var completion = new TaskCompletionSource<bool>();
        graph.SetCustomCanTxStateAction = async (_, _, enabled) =>
        {
            if (!await completion.Task)
            {
                return false;
            }
            message.IsEnabled = enabled;
            return true;
        };

        Task remove = graph.RemoveConnectionCommand.ExecuteAsync(Assert.Single(graph.Connections));
        Assert.False(remove.IsCompleted);
        Assert.False(graph.CanUndo);
        Assert.Single(graph.Connections);
        completion.SetResult(false);
        await remove;
        Assert.True(message.IsEnabled);
        Assert.Single(graph.Connections);
        Assert.False(graph.CanUndo);
    }

    [Theory]
    [InlineData("build")]
    [InlineData("snapshot")]
    [InlineData("remove")]
    public void ReplacingOrRemovingNodesClearsStaleHistory(string operation)
    {
        var (graph, settings) = CreateGraph();
        Move(graph, graph.DigitalNodes[0], new Vector(50, 0));
        switch (operation)
        {
            case "build":
                graph.Build(settings, new DataStructures());
                break;
            case "snapshot":
                graph.ApplySnapshot(graph.CreateSnapshot());
                break;
            case "remove":
                graph.SelectedNodes.Add(graph.DigitalNodes[0]);
                graph.RemoveSelectedNodesCommand.Execute(null);
                break;
        }

        Assert.False(graph.CanUndo);
    }

    private static void Move(IoFlowGraphViewModel graph, IoFlowNodeViewModel node, Vector delta)
    {
        graph.ItemsDragStartedCommand.Execute(null);
        node.Location += delta;
        graph.ItemsDragCompletedCommand.Execute(null);
    }

    private static (IoFlowGraphViewModel Graph, DataStructures Settings) CreateGraph()
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        return (graph, settings);
    }
}
