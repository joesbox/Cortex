using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Xunit;

namespace Cortex.Tests;

public sealed class IoFlowSettingsParityTests
{
    [Fact]
    public async Task LogicAndUnconnectedOutputsExposeProtectionAndTimingWithWiringDeterminingMode()
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        OutputNodeViewModel output = Assert.Single(graph.OutputNodes);
        Assert.True(output.ShowCurrentInrushConfiguration);
        Assert.True(output.ShowTimingConfiguration);
        graph.AddLogicNodeCommand.Execute(null);
        await graph.ConnectNodes(graph.MathRuleNodes.Single(), output);
        Assert.Equal(OutputChannel.ChannelType.MATH, output.Channel.ChanType);
        Assert.True(output.ShowCurrentInrushConfiguration);
        Assert.True(output.ShowTimingConfiguration);
        output.CurrentThresholdHighValue = 12.3;
        output.InrushDelayValue = 1.2;
        output.RetryCountValue = 4;
        output.SoftStartTimeValue = 2.5;
        Assert.Equal(12.3f, output.Channel.CurrentThresholdHigh);
        Assert.Equal(1.2f, output.Channel.InrushDelay);
        Assert.Equal(4, output.Channel.RetryCount);
        Assert.Equal(2.5f, output.Channel.SoftStartTime);
        OutputChannel liveChannel = new() { Enabled = 1 };
        output.UpdateLiveState(liveChannel);
        Assert.True(output.IsOutputActive);
        liveChannel.Enabled = 0;
        output.UpdateLiveState(liveChannel);
        Assert.False(output.IsOutputActive);
        output.Channel.ChanType = OutputChannel.ChannelType.CAN_PWM;
        Assert.True(output.ShowDutyConfiguration);
    }

    [Fact]
    public void DisabledCanTxAssignmentsRetainNodesAndWiresAfterControllerReadback()
    {
        var settings = new DataStructures();
        CustomCanMessage message = settings.CustomCanMessagesStaticData[0];
        message.Id = 0x123;
        message.Dlc = 8;
        message.IsTx = true;
        message.IsEnabled = false;
        message.TxTrigger = 0;
        settings.CustomCanSignalsStaticData[0].IsEnabled = true;
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();
        CanTxNodeViewModel tx = Assert.Single(graph.CanTxNodes);
        Assert.Single(graph.Connections.Where(wire => ReferenceEquals(wire.Target.Node, tx)));
        message.IsEnabled = true;
        graph.RebuildConnections(settings);
        message.IsEnabled = false;
        graph.SynchronizeCanTxNodesFromSettings(settings);
        graph.RebuildConnections(settings);
        Assert.Same(tx, Assert.Single(graph.CanTxNodes));
        Assert.Single(graph.Connections.Where(wire => ReferenceEquals(wire.Target.Node, tx)));
        Assert.Equal(0, message.TxTrigger);
    }
}
