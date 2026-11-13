using Avalonia;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels.IoFlow;
using Xunit;

namespace Cortex.Tests;

public sealed class IoFlowAnaloguePersistenceTests
{
    [Fact]
    public async Task DisconnectedAnalogueSurvivesSaveReloadAndRemainsConfigurable()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cortex-flow-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new DataStructures();
            var graph = new IoFlowGraphViewModel();
            graph.Build(settings, new DataStructures());
            graph.AddNextAnalogueNodeCommand.Execute(null);
            graph.AddNextOutputNodeCommand.Execute(null);
            var input = Assert.Single(graph.AnalogueNodes);
            var output = Assert.Single(graph.OutputNodes);
            input.Input.ChanType = AnalogueInput.AnalogueChannelType.Active;
            await graph.ConnectNodes(input, output);
            output.Channel.CurrentThresholdHigh = 16;
            output.Channel.DelayedOff = 1;
            output.Channel.DelayedOffTime = 1500;
            await graph.RemoveConnectionCommand.ExecuteAsync(Assert.Single(graph.Connections));
            Assert.False(output.Channel.HasConfiguredInputSource);
            Assert.Equal(0, output.Channel.InputControlPin);
            Assert.Equal(16, output.Channel.CurrentThresholdHigh);
            Assert.Equal(1500, output.Channel.DelayedOffTime);
            input.Input.ChanType = AnalogueInput.AnalogueChannelType.RawVoltage;
            input.Location = new Point(600, 300);

            var store = new IoFlowGraphStore(path);
            store.Save(graph.CreateLayoutSnapshot());
            Assert.True(store.TryLoad(out var layout));
            var readback = new DataStructures();
            Assert.True(ConfigFileSerializer.TryDeserialize(ConfigFileSerializer.SerializeSettings(settings), out var config, out _));
            ConfigFileSerializer.ApplySnapshot(readback, config!);
            var reloaded = new IoFlowGraphViewModel();
            reloaded.Build(readback, new DataStructures());
            reloaded.PopulateFromCurrentSettings();
            reloaded.ApplyNodePositions(layout);
            var restored = Assert.Single(reloaded.AnalogueNodes);
            Assert.Equal(input.Location, restored.Location);
            Assert.Equal(AnalogueInput.AnalogueChannelType.RawVoltage, restored.Input.ChanType);
            Assert.Empty(reloaded.Connections);
            restored.Input.ChanType = AnalogueInput.AnalogueChannelType.Active;
            restored.Input.CalibrationValue2 = 42;
            Assert.Equal(42, readback.AnalogueInputsStaticData[0].CalibrationValue2);
            reloaded.RestoreAllNodesCommand.Execute(null);
            Assert.Single(reloaded.AnalogueNodes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task ReconnectingAndUndoRestoreInputWithoutTouchingEnabledOrProtectionSettings()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        var output = Assert.Single(graph.OutputNodes);
        byte enabledBefore = output.Channel.Enabled;
        await graph.ConnectNodes(graph.DigitalNodes[0], output);
        Assert.True(output.Channel.HasConfiguredInputSource);
        output.Channel.CurrentThresholdHigh = 20;
        await graph.RemoveConnectionCommand.ExecuteAsync(Assert.Single(graph.Connections));
        Assert.False(output.Channel.HasConfiguredInputSource);
        await graph.UndoCommand.ExecuteAsync(null);
        Assert.True(output.Channel.HasConfiguredInputSource);
        Assert.Equal(20, output.Channel.CurrentThresholdHigh);
        await graph.RemoveConnectionCommand.ExecuteAsync(Assert.Single(graph.Connections));
        await graph.ConnectNodes(graph.DigitalNodes[0], output);
        Assert.True(output.Channel.HasConfiguredInputSource);
        Assert.Equal(enabledBefore, output.Channel.Enabled);
    }

    [Theory]
    [InlineData(0, 0, "Disabled")]
    [InlineData(1, 0, "Active")]
    [InlineData(0, 1, "Over current")]
    [InlineData(1, 2, "Under current")]
    [InlineData(0, 4, "Driver fault")]
    [InlineData(0, 8, "Retry lockout")]
    [InlineData(0, 16, "Over temperature")]
    [InlineData(0, 32, "Math error")]
    public void StatusIncludesDisabledAndFaults(byte active, byte errors, string expected)
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        var output = Assert.Single(graph.OutputNodes);
        output.Channel.InputControlPin = 0;
        output.UpdateLiveState(new OutputChannel { Enabled = active, ErrorFlags = errors });
        Assert.Equal(expected, output.OutputStatusText);
        Assert.Equal(active != 0, output.IsOutputActive);
        output.UpdateLiveState(null);
        Assert.Equal("Unknown", output.OutputStatusText);
    }

    [Fact]
    public void StatusDisabledOnlyWhenControllerHasNoInputSource()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        var output = Assert.Single(graph.OutputNodes);
        output.UpdateLiveState(new OutputChannel { Enabled = 0 }, new OutputChannel { Enabled = 0, InputControlPin = 0xD0 });
        Assert.Equal("Inactive", output.OutputStatusText);
        output.UpdateLiveState(new OutputChannel { Enabled = 0 }, new OutputChannel { Enabled = 0, ChanType = OutputChannel.ChannelType.MATH });
        Assert.Equal("Inactive", output.OutputStatusText);
        output.Channel.InputControlPin = 0xD0;
        output.UpdateLiveState(new OutputChannel { Enabled = 0 }, new OutputChannel { Enabled = 1, InputControlPin = 0 });
        Assert.Equal("Disabled", output.OutputStatusText);
    }

    [Fact]
    public void StatusFallsBackToEditorInputWhenControllerUnknown()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        var output = Assert.Single(graph.OutputNodes);
        output.Channel.InputControlPin = 0;
        output.UpdateLiveState(new OutputChannel { Enabled = 0 });
        Assert.Equal("Disabled", output.OutputStatusText);
        output.Channel.InputControlPin = 0xD0;
        output.UpdateLiveState(new OutputChannel { Enabled = 0 });
        Assert.Equal("Inactive", output.OutputStatusText);
    }

    [Fact]
    public void GraphStatusUsesControllerChannelsNotStaleLiveSettingsCopy()
    {
        var live = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), live);
        graph.AddNextOutputNodeCommand.Execute(null);
        var output = Assert.Single(graph.OutputNodes);
        int index = output.ChannelNumber - 1;
        live.ChannelsLiveData[index].Enabled = 0;
        live.ChannelsStaticData[index].InputControlPin = 0;

        // Analogue input in active-low digital mode at 5 V: configured but off.
        var controller = new DataStructures();
        controller.ChannelsStaticData[index].InputControlPin = 0xD0;
        graph.ControllerChannels = controller.ChannelsStaticData;
        graph.UpdateLiveChannelSamples(live.ChannelsLiveData);
        Assert.Equal("Inactive", output.OutputStatusText);

        controller.ChannelsStaticData[index].InputControlPin = 0;
        graph.UpdateLiveChannelSamples(live.ChannelsLiveData);
        Assert.Equal("Disabled", output.OutputStatusText);
    }

    [Fact]
    public async Task DeletedStandaloneAnalogueIsNotRestoredFromRetainedPositions()
    {
        string path = Path.Combine(Path.GetTempPath(), $"cortex-flow-{Guid.NewGuid():N}.json");
        try
        {
            var settings = new DataStructures();
            var graph = new IoFlowGraphViewModel();
            graph.Build(settings, new DataStructures());
            graph.AddNextAnalogueNodeCommand.Execute(null);
            var store = new IoFlowGraphStore(path);
            store.Save(graph.CreateLayoutSnapshot());
            graph.SelectedNodes.Add(graph.AnalogueNodes[0]);
            await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);
            store.Save(graph.CreateLayoutSnapshot(), preserveUnmatchedNodePositions: true);
            Assert.True(store.TryLoad(out var layout));
            Assert.Contains(layout.NodePositions, node => node.Kind == "Analogue");
            var reloaded = new IoFlowGraphViewModel();
            reloaded.Build(settings, new DataStructures());
            reloaded.PopulateFromCurrentSettings();
            reloaded.ApplyNodePositions(layout);
            Assert.Empty(reloaded.AnalogueNodes);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Theory]
    [InlineData(OutputChannel.ChannelType.Digital, (byte)0xD0, true)]
    [InlineData(OutputChannel.ChannelType.Digital, (byte)0, false)]
    [InlineData(OutputChannel.ChannelType.MATH, (byte)0, true)]
    [InlineData(OutputChannel.ChannelType.CAN, (byte)0, true)]
    public void ConfiguredInputSourceMatchesControllerRule(OutputChannel.ChannelType type, byte pin, bool expected)
    {
        var channel = new OutputChannel { ChanType = type, InputControlPin = pin };
        Assert.Equal(expected, channel.HasConfiguredInputSource);
    }

    [Fact]
    public void OutputChannelSelectorExcludesChannelsAlreadyOnCanvas()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        var first = graph.OutputNodes[0];
        var second = graph.OutputNodes[1];

        Assert.Contains(first.ChannelNumber, first.OutputChannelNumbers);
        Assert.DoesNotContain(second.ChannelNumber, first.OutputChannelNumbers);
        Assert.DoesNotContain(first.ChannelNumber, second.OutputChannelNumbers);

        int secondChannel = second.ChannelNumber;
        first.SelectedOutputChannelNumber = secondChannel;
        Assert.NotEqual(secondChannel, first.ChannelNumber);
        Assert.Equal(secondChannel, second.ChannelNumber);

        int freeChannel = first.OutputChannelNumbers.First(number => number != first.ChannelNumber);
        int previousChannel = first.ChannelNumber;
        first.SelectedOutputChannelNumber = freeChannel;
        Assert.Equal(freeChannel, first.ChannelNumber);
        Assert.Contains(previousChannel, second.OutputChannelNumbers);
        Assert.DoesNotContain(freeChannel, second.OutputChannelNumbers);
    }

    [Fact]
    public async Task OneInputNodeCanDriveMultipleOutputs()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextAnalogueNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        var input = Assert.Single(graph.AnalogueNodes);
        input.Input.ChanType = AnalogueInput.AnalogueChannelType.Digital;
        await graph.ConnectNodes(input, graph.OutputNodes[0]);
        await graph.ConnectNodes(input, graph.OutputNodes[1]);
        Assert.Equal(2, graph.Connections.Count);
        Assert.All(graph.OutputNodes, output => Assert.Equal(0xD0, output.Channel.InputControlPin));
    }

    [Fact]
    public async Task OneLogicRuleCanDriveMultipleOutputsAsFollowers()
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        var rule = Assert.Single(graph.MathRuleNodes);
        rule.Rule.OperandAConstant = 5;
        var (owner, follower, second) = (graph.OutputNodes[0], graph.OutputNodes[1], graph.OutputNodes[2]);

        await graph.ConnectNodes(rule, owner);
        await graph.ConnectNodes(rule, follower);
        await graph.ConnectNodes(rule, second);

        Assert.False(owner.Channel.IsMathFollower);
        Assert.True(follower.Channel.IsMathFollower);
        Assert.Equal(owner.ChannelNumber - 1, follower.Channel.MathFollowChannel);
        Assert.Equal(owner.ChannelNumber - 1, second.Channel.MathFollowChannel);
        Assert.Equal(3, graph.Connections.Count(wire => ReferenceEquals(wire.Source.Node, rule)));

        // Followers survive a settings round-trip and are drawn from the owner's root rule.
        Assert.True(ConfigFileSerializer.TryDeserialize(ConfigFileSerializer.SerializeSettings(settings), out var config, out _));
        var readback = new DataStructures();
        ConfigFileSerializer.ApplySnapshot(readback, config!);
        Assert.Equal(owner.ChannelNumber - 1, readback.ChannelsStaticData[follower.ChannelNumber - 1].MathFollowChannel);
        var reloaded = new IoFlowGraphViewModel();
        reloaded.Build(readback, new DataStructures());
        reloaded.PopulateFromCurrentSettings();
        var reloadedRoot = Assert.Single(reloaded.MathRuleNodes);
        Assert.Equal(3, reloaded.Connections.Count(wire => ReferenceEquals(wire.Source.Node, reloadedRoot)));

        // Removing the owner's wire promotes the first follower and re-points the others.
        await graph.RemoveConnectionCommand.ExecuteAsync(graph.Connections.Single(wire =>
            ReferenceEquals(wire.Source.Node, rule) && ReferenceEquals(wire.Target.Node, owner)));
        Assert.False(owner.Channel.HasConfiguredInputSource);
        Assert.False(follower.Channel.IsMathFollower);
        Assert.Equal(OutputChannel.ChannelType.MATH, follower.Channel.ChanType);
        Assert.Equal(5, follower.Channel.MathRules[follower.Channel.MathRootRule].OperandAConstant);
        Assert.Equal(follower.ChannelNumber - 1, second.Channel.MathFollowChannel);

        // Removing a follower's wire just orphans that output.
        var followerWire = graph.Connections.Single(wire => ReferenceEquals(wire.Target.Node, second));
        await graph.RemoveConnectionCommand.ExecuteAsync(followerWire);
        Assert.False(second.Channel.HasConfiguredInputSource);
        Assert.Equal(OutputChannel.MathFollowNone, second.Channel.MathFollowChannel);
        Assert.True(follower.Channel.HasConfiguredInputSource);
    }

    [Fact]
    public void ControllerFollowerHeaderIsDecoded()
    {
        var channel = new OutputChannel { ChanType = OutputChannel.ChannelType.MATH };
        channel.ApplyControllerMathHeader(0, 0x80 | 3);
        Assert.True(channel.IsMathFollower);
        Assert.Equal(3, channel.MathFollowChannel);
        Assert.Equal(1, channel.MathRuleCount);
        Assert.Equal(0, channel.MathRootRule);

        channel.ApplyControllerMathHeader(2, 1);
        Assert.False(channel.IsMathFollower);
        Assert.Equal(2, channel.MathRuleCount);
        Assert.Equal(1, channel.MathRootRule);
    }
}
