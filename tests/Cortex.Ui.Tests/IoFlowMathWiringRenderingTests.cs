using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Cortex.Views;
using Nodify.Avalonia.Connections;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class IoFlowMathWiringRenderingTests
{
    [Fact]
    public void CanSignalRuleWaitsForFirstFrameInsteadOfShowingAnError()
    {
        DataStructures live = new();
        CustomCanSignal signal = live.CustomCanSignalsLiveData[0];
        signal.Name = "Key1_Status";
        signal.Status = (byte)CustomCanStatus.NotReceived;
        var source = new MathSourceNodeViewModel(
            OutputChannel.MathOperandType.CanSignal,
            0,
            "Key1_Status",
            new Point(0, 0));
        source.UpdateLiveState(live);

        var rule = new MathRuleNodeViewModel(1, new Point(100, 0));
        rule.SetLinkedToOutput(true);
        rule.Rule.Operation = (byte)OutputChannel.MathOperation.EQUAL;
        rule.Rule.OperandAType = (byte)OutputChannel.MathOperandType.CanSignal;
        rule.Rule.OperandAIndex = 0;
        rule.Rule.OperandBType = (byte)OutputChannel.MathOperandType.Constant;
        rule.Rule.OperandBConstant = 1;
        var liveRule = new OutputChannel.MathRule { RuntimeError = 2, Evaluated = 1 };
        rule.UpdateLiveResult(
            1,
            liveRule,
            isOutputRoot: true,
            outputMode: (byte)OutputChannel.MathOutputModeType.Digital,
            channelError: 2,
            outputRootValue: 0,
            consumerLabels: Array.Empty<string>(),
            evaluationText: string.Empty,
            operandAText: string.Empty,
            operandBText: string.Empty,
            liveData: live);

        Assert.Equal("Key1_Status: Waiting for first CAN frame", source.LiveValueText);
        Assert.Equal("Waiting for first CAN frame", rule.LiveRuleResultText);
        Assert.Contains("output is held off until a frame arrives", rule.LiveResultText);

        signal.Status = (byte)CustomCanStatus.Valid;
        signal.RuntimeValue = 1;
        source.UpdateLiveState(live);
        liveRule.RuntimeError = 0;
        liveRule.RuntimeValue = 1;
        rule.UpdateLiveResult(
            1,
            liveRule,
            isOutputRoot: true,
            outputMode: (byte)OutputChannel.MathOutputModeType.Digital,
            channelError: 0,
            outputRootValue: 1,
            consumerLabels: Array.Empty<string>(),
            evaluationText: string.Empty,
            operandAText: string.Empty,
            operandBText: string.Empty,
            liveData: live);

        Assert.Equal("Key1_Status: 1.0", source.LiveValueText);
        Assert.Equal("True", rule.LiveRuleResultText);
    }

    [AvaloniaFact]
    public void CanRxAndSystemSourceNodesRenderLiveValuesAndDisableUnavailableSignals()
    {
        DataStructures settings = new();
        DataStructures live = new();
        settings.CustomCanMessagesStaticData[0].Id = 0x123;
        settings.CustomCanSignalsStaticData[0].IsEnabled = true;
        settings.CustomCanSignalsStaticData[0].Name = "Engine speed";
        OutputChannel channel = settings.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.MathRules[0].OperandAType = (byte)OutputChannel.MathOperandType.CanSignal;
        channel.MathRules[0].OperandBType = (byte)OutputChannel.MathOperandType.SystemValue;
        channel.MathRules[0].OperandBIndex = (byte)OutputChannel.MathSystemValue.BatteryVoltage;
        live.CustomCanSignalsLiveData[0].RuntimeValue = 1500;
        live.SystemParams.VBatt = 12.6f;
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, live);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.UpdateLiveChannelSamples(live.ChannelsLiveData);
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 1600, Height = 1300, Content = view };
        try
        {
            window.Show();
            UpdateLayout();
            MathSourceNodeViewModel rx = graph.MathSourceNodes.Single(source => source.OperandType == OutputChannel.MathOperandType.CanSignal);
            MathSourceNodeViewModel system = graph.MathSourceNodes.Single(source => source.OperandType == OutputChannel.MathOperandType.SystemValue);
            Assert.Contains("Engine speed", rx.Title);
            Assert.Contains("0x123", rx.Title);
            Assert.Contains("1500", rx.LiveValueText);
            Assert.Contains("12.6", system.LiveValueText);
            OutputNodeViewModel output = Assert.Single(graph.OutputNodes);
            Assert.False(output.IsOutputActive);
            Connector connector = Assert.Single(view.GetVisualDescendants().OfType<Connector>(),
                control => ReferenceEquals(control.DataContext, rx.OutputConnector));
            Assert.True(connector.IsEffectivelyEnabled);
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Connector>(),
                control => ReferenceEquals(control.DataContext, rx.InputConnector));

            live.CustomCanSignalsLiveData[0].RuntimeValue = 0;
            live.ChannelsLiveData[0].Enabled = 1;
            graph.UpdateLiveChannelSamples(live.ChannelsLiveData);
            Assert.Contains(": 0", rx.LiveValueText);
            Assert.DoesNotContain("1500", rx.LiveValueText);
            Assert.True(output.IsOutputActive);

            live.ChannelsLiveData[0].Enabled = 0;
            graph.UpdateLiveChannelSamples(live.ChannelsLiveData);
            Assert.False(output.IsOutputActive);

            settings.CustomCanSignalsStaticData[0].IsEnabled = false;
            graph.RebuildConnections(settings);
            UpdateLayout();

            Assert.False(connector.IsEffectivelyEnabled);
            Assert.DoesNotContain(graph.Connections, wire => ReferenceEquals(wire.Source.Node, rx));
        }
        finally
        {
            window.Close();
        }

        void UpdateLayout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task OperandConnectorsAndSelectorsRenderIndependentWiresAndHideUnusedB()
    {
        DataStructures settings = new();
        OutputChannel channel = settings.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.MathRules[0].OperandAType = (byte)OutputChannel.MathOperandType.AnalogueInput;
        channel.MathRules[0].OperandAIndex = 1;
        channel.MathRules[0].OperandBType = (byte)OutputChannel.MathOperandType.AnalogueInput;
        channel.MathRules[0].OperandBIndex = 2;
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        var rule = Assert.Single(graph.MathRuleNodes);
        rule.Location = new Point(550, 50);
        graph.OutputNodes[0].Location = new Point(1050, 50);
        graph.AnalogueNodes[0].Location = new Point(50, 50);
        graph.AnalogueNodes[1].Location = new Point(50, 650);
        var view = new IoFlowTabView
        {
            DataContext = new
            {
                IoFlowGraph = graph,
                MathOperations = Enum.GetNames<OutputChannel.MathOperation>(),
                MathOutputModes = new[] { "Digital", "PWM" },
            },
        };
        var window = new Window { Width = 1600, Height = 1300, Content = view };
        try
        {
            window.Show();
            UpdateLayout();
            var a = Assert.Single(view.GetVisualDescendants().OfType<Connector>(), connector => connector.Classes.Contains("mathOperandA"));
            var b = Assert.Single(view.GetVisualDescendants().OfType<Connector>(), connector => connector.Classes.Contains("mathOperandB"));
            Assert.Same(rule.OperandAConnector, a.DataContext);
            Assert.Same(rule.OperandBConnector, b.DataContext);
            Assert.NotEqual(default, rule.OperandAConnector.Anchor);
            Assert.NotEqual(rule.OperandAConnector.Anchor, rule.OperandBConnector.Anchor);
            Assert.True(a.IsEffectivelyVisible);
            Assert.True(b.IsEffectivelyVisible);
            var aSource = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(), box => box.Classes.Contains("mathOperandASource"));
            var bSource = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(), box => box.Classes.Contains("mathOperandBSource"));
            Assert.Equal(3, aSource.ItemCount);
            Assert.Equal(3, bSource.ItemCount);
            Assert.Equal((byte)1, Assert.IsType<MathConnectedSourceOption>(aSource.SelectedItem).Index);
            Assert.Equal((byte)2, Assert.IsType<MathConnectedSourceOption>(bSource.SelectedItem).Index);
            AssertWire(rule.OperandAConnector);
            AssertWire(rule.OperandBConnector);

            bSource.SelectedItem = aSource.SelectedItem;
            UpdateLayout();
            Assert.Equal((byte)1, rule.Rule.OperandAIndex);
            Assert.Equal((byte)1, rule.Rule.OperandBIndex);
            IoFlowNodeViewModel source = graph.AnalogueNodes.Single(node => node.InputNumber == 3);
            await graph.ConnectionCompletedCommand.ExecuteAsync((rule.OperandBConnector, source.OutputConnector));
            UpdateLayout();
            Assert.Equal((byte)2, rule.Rule.OperandBIndex);
            AssertWire(rule.OperandBConnector);

            rule.Rule.Operation = (byte)OutputChannel.MathOperation.ABS;
            graph.SynchronizeMathChannelFromSettings(channel);
            UpdateLayout();
            Assert.True(a.IsEffectivelyVisible);
            Assert.False(b.IsEffectivelyVisible);
            Assert.False(bSource.IsEffectivelyVisible);
            Assert.DoesNotContain(graph.Connections, wire => ReferenceEquals(wire.Target, rule.OperandBConnector));
            graph.AutoArrangeNodesCommand.Execute(null);
            UpdateLayout();
            IoFlowNodeViewModel[] nodes = graph.Nodes.ToArray();
            for (int index = 0; index < nodes.Length; index++)
            {
                Rect bounds = new(nodes[index].Location, nodes[index].MeasuredSize);
                foreach (IoFlowNodeViewModel other in nodes.Skip(index + 1))
                {
                    Assert.False(bounds.Intersects(new Rect(other.Location, other.MeasuredSize)),
                        $"{nodes[index].Title} overlaps {other.Title}");
                }
            }
        }
        finally
        {
            window.Close();
        }

        void UpdateLayout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        void AssertWire(IoFlowConnectorEndpoint endpoint)
        {
            IoFlowConnectionViewModel model = Assert.Single(graph.Connections, connection => ReferenceEquals(connection.Target, endpoint));
            Connection wire = Assert.Single(view.GetVisualDescendants().OfType<Connection>(),
                connection => ReferenceEquals(connection.DataContext, model) && connection.StrokeThickness < 8);
            Assert.Equal(endpoint.Anchor, wire.Target);
            Assert.Equal(model.Source.Anchor, wire.Source);
            Assert.True(wire.IsEffectivelyVisible);
            Assert.NotNull(wire.RenderedGeometry);
        }
    }
}
