using Avalonia;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Xunit;

namespace Cortex.Tests;

public sealed class IoFlowSourceSelectionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InputReassignmentRetargetsBothOperandsPhysicalWiresAndHiddenRules(bool analogue)
    {
        var settings = new DataStructures();
        var live = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, live);
        if (analogue) graph.AddNextAnalogueNodeCommand.Execute(null);
        else graph.AddNextDigitalNodeCommand.Execute(null);
        IoFlowNodeViewModel source = analogue ? graph.AnalogueNodes[0] : graph.DigitalNodes[0];
        source.Location = new Point(100, 200);
        if (analogue)
        {
            settings.AnalogueInputsStaticData[0].ChanType = AnalogueInput.AnalogueChannelType.RawVoltage;
            settings.AnalogueInputsStaticData[2].ChanType = AnalogueInput.AnalogueChannelType.RawVoltage;
            settings.AnalogueInputsStaticData[0].NtcBeta = 3000;
            settings.AnalogueInputsStaticData[2].NtcBeta = 4000;
            live.AnalogueInputsLiveData[2].InputVoltage = 3.25f;
        }
        graph.AddNextOutputNodeCommand.Execute(null);
        await graph.ConnectNodes(source, graph.OutputNodes[0]);
        graph.AddLogicNodeCommand.Execute(null);
        MathRuleNodeViewModel rule = graph.MathRuleNodes.Single();
        await graph.ConnectNodes(source, rule, true);
        await graph.ConnectNodes(source, rule, false);
        graph.AddNextOutputNodeCommand.Execute(null);
        await graph.ConnectNodes(rule, graph.OutputNodes[1]);
        OutputChannel hidden = settings.ChannelsStaticData[5];
        hidden.ChanType = OutputChannel.ChannelType.MATH;
        hidden.MathRules[0].OperandAType = (byte)(analogue ? OutputChannel.MathOperandType.AnalogueInput : OutputChannel.MathOperandType.DigitalInput);
        graph.RebuildConnections(settings);
        Point[] positions = graph.Nodes.Select(node => node.Location).ToArray();
        int wireCount = graph.Connections.Count;

        source.SelectedSource = source.AvailableSources.Single(option => option.Index == 2);

        Assert.Equal(2, rule.Rule.OperandAIndex);
        Assert.Equal(2, rule.Rule.OperandBIndex);
        Assert.Equal(2, settings.ChannelsStaticData[1].MathRules[0].OperandAIndex);
        Assert.Equal(2, hidden.MathRules[0].OperandAIndex);
        byte expectedPin = analogue ? InputPinCatalog.ANAChannelInputPins[2] : InputPinCatalog.DIChannelInputPins[2];
        Assert.Equal(expectedPin, graph.OutputNodes[0].Channel.InputControlPin);
        Assert.Equal(wireCount, graph.Connections.Count);
        Assert.Equal(positions, graph.Nodes.Select(node => node.Location));
        Assert.False(graph.CanUndo);
        if (analogue)
        {
            var input = Assert.IsType<AnalogueInputNodeViewModel>(source);
            Assert.Same(settings.AnalogueInputsStaticData[2], input.Input);
            Assert.Equal(4000, input.Input.NtcBeta);
            Assert.Equal(3000, settings.AnalogueInputsStaticData[0].NtcBeta);
            Assert.Equal("3.2 V", input.LiveVoltageText);
        }
        else Assert.Same(settings.DigitalInputsStaticData[2], Assert.IsType<DigitalInputNodeViewModel>(source).Input);

        var restored = new IoFlowGraphViewModel();
        restored.Build(settings, live);
        restored.ApplySnapshot(graph.CreateSnapshot());
        Assert.True(restored.Connections.Count >= wireCount);
        Assert.DoesNotContain(restored.Connections, wire => wire.Source.Node is AnalogueInputNodeViewModel { InputNumber: 1 }
            or DigitalInputNodeViewModel { InputNumber: 1 });
        Assert.Equal(3, analogue ? restored.AnalogueNodes.Single().InputNumber : restored.DigitalNodes.Single().InputNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MathSourceReassignmentPreservesTypeWiresAndExcludesDuplicates(bool can)
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        OutputChannel channel = settings.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        var type = can ? OutputChannel.MathOperandType.CanSignal : OutputChannel.MathOperandType.SystemValue;
        channel.MathRules[0].OperandAType = channel.MathRules[0].OperandBType = (byte)type;
        for (int i = 0; i < 3; i++)
        {
            settings.CustomCanSignalsStaticData[i].IsEnabled = true;
            settings.CustomCanSignalsStaticData[i].MessageIndex = 0;
        }
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        MathSourceNodeViewModel source = graph.MathSourceNodes.Single();
        if (can) graph.AddCanRxNodeCommand.Execute(null);
        else graph.AddSystemValueNodeCommand.Execute(null);
        Assert.DoesNotContain(source.AvailableSources, option => option.Index == 1);
        source.SelectedSource = source.AvailableSources.Single(option => option.Index == 2);
        Assert.Equal(type, source.OperandType);
        Assert.Equal(2, source.SourceIndex);
        Assert.Equal(2, channel.MathRules[0].OperandAIndex);
        Assert.Equal(2, channel.MathRules[0].OperandBIndex);
        Assert.Equal(2, graph.Connections.Count(wire => ReferenceEquals(wire.Source.Node, source)));
        Assert.Contains(source.AvailableSources, option => option.Index == 0);
    }

    [Fact]
    public async Task IncompatibleInputReassignmentIsRejectedWithoutChangingConfiguration()
    {
        var settings = new DataStructures();
        settings.AnalogueInputsStaticData[0].ChanType = AnalogueInput.AnalogueChannelType.RawVoltage;
        settings.AnalogueInputsStaticData[1].ChanType = AnalogueInput.AnalogueChannelType.Digital;
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddNextAnalogueNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        await graph.ConnectNodes(graph.AnalogueNodes[0], graph.OutputNodes[0]);
        byte pin = graph.OutputNodes[0].Channel.InputControlPin;
        graph.AnalogueNodes[0].SelectedSource = graph.AnalogueNodes[0].AvailableSources.Single(option => option.Index == 1);
        Assert.Equal(1, graph.AnalogueNodes[0].InputNumber);
        Assert.Equal(pin, graph.OutputNodes[0].Channel.InputControlPin);
        Assert.Single(graph.Connections);
        Assert.Contains("incompatible", graph.AddNodeWarning);
    }
}
