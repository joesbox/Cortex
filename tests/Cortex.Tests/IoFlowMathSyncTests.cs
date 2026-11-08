using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Xunit;

namespace Cortex.Tests;

public sealed class IoFlowMathSyncTests
{
    [Fact]
    public void RebuildingWithoutInputNodesDoesNotEraseCompatibleSettings()
    {
        DataStructures settings = new();
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        OutputChannel channel = graph.OutputNodes[0].Channel;
        channel.ChanType = OutputChannel.ChannelType.Digital;
        channel.InputControlPin = InputPinCatalog.DIChannelInputPins[0];

        graph.RebuildConnections(settings);

        Assert.Equal(InputPinCatalog.DIChannelInputPins[0], channel.InputControlPin);
        graph.PopulateFromCurrentSettings();
        Assert.Contains(graph.Connections, connection =>
            connection.Source.Node is DigitalInputNodeViewModel &&
            connection.Target.Node is OutputNodeViewModel { ChannelNumber: 1 });
    }

    [Fact]
    public async Task DeletingPhysicalInputExplicitlyClearsItsOutputAssignments()
    {
        DataStructures settings = new();
        settings.ChannelsStaticData[0].InputControlPin = InputPinCatalog.DIChannelInputPins[0];
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();
        graph.SelectedNodes.Add(graph.DigitalNodes.Single());

        await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);

        Assert.Equal((byte)0, settings.ChannelsStaticData[0].InputControlPin);
        Assert.Empty(graph.DigitalNodes);
        Assert.DoesNotContain(graph.Connections, connection =>
            connection.Source.Node is DigitalInputNodeViewModel);
    }

    [Fact]
    public async Task TraditionalEditRetainsStandaloneRulesSharedWithAnotherOutput()
    {
        DataStructures settings = new();
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        MathRuleNodeViewModel[] rules = graph.MathRuleNodes.Where(node => node.IsStandalone).ToArray();
        await graph.ConnectNodes(rules[0], rules[1]);
        await graph.ConnectNodes(rules[1], graph.OutputNodes[0]);
        await graph.ConnectNodes(rules[1], graph.OutputNodes[1]);
        OutputChannel firstChannel = graph.OutputNodes[0].Channel;
        OutputChannel secondChannel = graph.OutputNodes[1].Channel;

        firstChannel.MathRules[0].OperandAConstant = 27;
        graph.SynchronizeMathChannelFromSettings(firstChannel);
        rules[0].Rule.OperandAConstant = 42;

        Assert.Contains(rules[0], graph.MathRuleNodes);
        Assert.Contains(rules[1], graph.MathRuleNodes);
        Assert.Equal(27, firstChannel.MathRules[0].OperandAConstant);
        Assert.Equal(42, secondChannel.MathRules[0].OperandAConstant);
        Assert.Single(graph.CreateSnapshot().OutputRootLogicNodeByChannel);
        Assert.Same(firstChannel.MathRules[0], graph.MathRuleNodes.Single(node =>
            node.OwnerChannelNumber == firstChannel.ChannelNumber && node.RuleIndex == 0).Rule);
    }

    [Fact]
    public async Task EditingConnectedStandaloneMathRulesUpdatesOutputConfiguration()
    {
        DataStructures settings = new();
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);

        MathRuleNodeViewModel[] rules = graph.MathRuleNodes.Where(node => node.IsStandalone).ToArray();
        OutputChannel channel = graph.OutputNodes[0].Channel;
        await graph.ConnectNodes(rules[0], rules[1]);
        await graph.ConnectNodes(rules[1], graph.OutputNodes[0]);

        rules[0].Rule.OperandAConstant = 17;

        Assert.Equal((byte)2, channel.MathRuleCount);
        Assert.Equal((byte)1, channel.MathRootRule);
        Assert.Equal(17, channel.MathRules[0].OperandAConstant);
        Assert.Equal((byte)OutputChannel.MathOperandType.Rule, channel.MathRules[1].OperandAType);
        Assert.Equal((byte)0, channel.MathRules[1].OperandAIndex);
    }

    [Fact]
    public async Task CyclicStandaloneMathConnectionIsRejectedWithoutChangingOutputRules()
    {
        DataStructures settings = new();
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);

        MathRuleNodeViewModel[] rules = graph.MathRuleNodes.Where(node => node.IsStandalone).ToArray();
        OutputChannel channel = graph.OutputNodes[0].Channel;
        await graph.ConnectNodes(rules[0], rules[1]);
        await graph.ConnectNodes(rules[1], graph.OutputNodes[0]);
        float originalOutputConstant = channel.MathRules[0].OperandAConstant;

        await graph.ConnectNodes(rules[1], rules[0]);

        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, rules[0].Rule.OperandAType);
        Assert.Equal((byte)2, channel.MathRuleCount);
        Assert.Equal(originalOutputConstant, channel.MathRules[0].OperandAConstant);
        Assert.Contains("cycle", graph.AddNodeWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemovingConnectedLogicDependencyUpdatesCompiledRulesWithoutRestoringDeletedNode()
    {
        DataStructures settings = new();
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);

        MathRuleNodeViewModel[] rules = graph.MathRuleNodes.Where(node => node.IsStandalone).ToArray();
        OutputChannel channel = graph.OutputNodes[0].Channel;
        await graph.ConnectNodes(rules[0], rules[1]);
        await graph.ConnectNodes(rules[1], graph.OutputNodes[0]);
        graph.SelectedNodes.Add(rules[0]);

        await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);

        Assert.Equal((byte)1, channel.MathRuleCount);
        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, channel.MathRules[0].OperandAType);
        Assert.Equal(0, channel.MathRules[0].OperandAConstant);
        Assert.DoesNotContain(rules[0], graph.MathRuleNodes);
        Assert.Single(graph.MathRuleNodes);
        Assert.Single(graph.CreateSnapshot().OutputRootLogicNodeByChannel);
        Assert.Contains(graph.Connections, connection =>
            connection.Target.Node is OutputNodeViewModel output
            && output.ChannelNumber == channel.ChannelNumber
            && connection.Source.Node is MathRuleNodeViewModel rule
            && ReferenceEquals(rule, rules[1]));
        rules[1].Rule.OperandBConstant = 23;
        Assert.Equal((byte)1, channel.MathRuleCount);
        Assert.Equal(23, channel.MathRules[0].OperandBConstant);
        Assert.DoesNotContain(rules[0], graph.MathRuleNodes);
    }

    [Fact]
    public void SettingsMathRuleCountAndRootSynchronizeGraphNodesAndConnections()
    {
        DataStructures settings = new();
        OutputChannel channel = settings.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.MathRuleCount = 2;
        channel.MathRootRule = 1;
        channel.EnsureMathRuleSlots();

        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();

        Assert.Equal(2, graph.MathRuleNodes.Count(node => node.OwnerChannelNumber == channel.ChannelNumber));
        Assert.Contains(graph.Connections, connection =>
            connection.Target.Node is OutputNodeViewModel output
            && output.ChannelNumber == channel.ChannelNumber
            && connection.Source.Node is MathRuleNodeViewModel rule
            && rule.RuleIndex == 1
            && connection.Label == "Logic result");

        channel.MathRootRule = 0;
        graph.SynchronizeMathChannelFromSettings(channel);

        Assert.Contains(graph.Connections, connection =>
            connection.Target.Node is OutputNodeViewModel output
            && output.ChannelNumber == channel.ChannelNumber
            && connection.Source.Node is MathRuleNodeViewModel rule
            && rule.RuleIndex == 0
            && connection.Label == "Logic result");

        channel.MathRuleCount = 1;
        graph.SynchronizeMathChannelFromSettings(channel);
        Assert.Single(graph.MathRuleNodes.Where(node => node.OwnerChannelNumber == channel.ChannelNumber));
    }

    [Fact]
    public void MathChannelDropsUnsupportedInputConnectionAndPlacesRulesToItsLeft()
    {
        DataStructures settings = new();
        OutputChannel channel = settings.ChannelsStaticData[0];
        channel.InputControlPin = InputPinCatalog.DIChannelInputPins[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.MathRuleCount = 1;
        channel.MathRootRule = 0;

        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();

        OutputNodeViewModel output = graph.OutputNodes.Single(node => node.ChannelNumber == channel.ChannelNumber);
        MathRuleNodeViewModel rule = graph.MathRuleNodes.Single(node => node.OwnerChannelNumber == channel.ChannelNumber);

        Assert.Equal((byte)0, channel.InputControlPin);
        Assert.True(rule.Location.X < output.Location.X);
        Assert.DoesNotContain(graph.Connections, connection =>
            connection.Target.Node == output && connection.Source.Node is DigitalInputNodeViewModel);
    }

    [Fact]
    public async Task MathOutputRejectsPhysicalInputConnectionsButAcceptsRuleConnections()
    {
        DataStructures settings = new();
        OutputChannel channel = settings.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.MathRuleCount = 1;
        channel.MathRootRule = 0;

        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();
        graph.AddLogicNodeCommand.Execute(null);

        OutputNodeViewModel output = graph.OutputNodes.Single(node => node.ChannelNumber == channel.ChannelNumber);
        DigitalInputNodeViewModel input = new(settings.DigitalInputsStaticData[0], default);
        MathRuleNodeViewModel rule = graph.MathRuleNodes.Single(node => node.IsStandalone);

        await graph.ConnectNodes(input, output);
        Assert.Equal(OutputChannel.ChannelType.MATH, channel.ChanType);
        Assert.Equal((byte)0, channel.InputControlPin);
        Assert.Contains("Logic outputs can only connect", graph.AddNodeWarning);

        await graph.ConnectNodes(rule, output);
        Assert.Contains(graph.Connections, connection =>
            connection.Target.Node == output && connection.Source.Node == rule);
    }

    [Fact]
    public async Task MathRulesRejectCrossChannelAndSelfReferencingConnections()
    {
        DataStructures settings = new();
        OutputChannel firstChannel = settings.ChannelsStaticData[0];
        OutputChannel secondChannel = settings.ChannelsStaticData[1];
        firstChannel.ChanType = OutputChannel.ChannelType.MATH;
        firstChannel.MathRuleCount = 1;
        secondChannel.ChanType = OutputChannel.ChannelType.MATH;
        secondChannel.MathRuleCount = 1;

        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();

        MathRuleNodeViewModel firstRule = graph.MathRuleNodes.Single(node => node.OwnerChannelNumber == firstChannel.ChannelNumber);
        MathRuleNodeViewModel secondRule = graph.MathRuleNodes.Single(node => node.OwnerChannelNumber == secondChannel.ChannelNumber);
        OutputNodeViewModel firstOutput = graph.OutputNodes.Single(node => node.ChannelNumber == firstChannel.ChannelNumber);

        await graph.ConnectNodes(firstRule, secondRule);
        await graph.ConnectNodes(firstOutput, firstRule);

        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, secondRule.Rule.OperandAType);
        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, firstRule.Rule.OperandAType);
        Assert.DoesNotContain(graph.Connections, connection =>
            connection.Target.Node == secondRule && connection.Source.Node == firstRule);
    }

    [Fact]
    public void RebuildClearsInputConnectionWhenOutputTypeNoLongerSupportsIt()
    {
        DataStructures settings = new();
        OutputChannel channel = settings.ChannelsStaticData[0];
        channel.InputControlPin = InputPinCatalog.DIChannelInputPins[0];
        channel.ChanType = OutputChannel.ChannelType.Digital;

        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();

        channel.ChanType = OutputChannel.ChannelType.CAN;
        graph.RebuildConnections(settings);

        Assert.Equal((byte)0, channel.InputControlPin);
        Assert.DoesNotContain(graph.Connections, connection =>
            connection.Target.Node is OutputNodeViewModel output
            && output.ChannelNumber == channel.ChannelNumber
            && connection.Source.Node is DigitalInputNodeViewModel);
    }

    [Fact]
    public async Task StandaloneRuleCannotBecomeRootOfOutputItUsesAsOperand()
    {
        DataStructures settings = new();
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);

        OutputNodeViewModel output = graph.OutputNodes[0];
        MathRuleNodeViewModel rule = graph.MathRuleNodes.Single(node => node.IsStandalone);
        rule.Rule.OperandAType = (byte)OutputChannel.MathOperandType.Channel;
        rule.Rule.OperandAIndex = (byte)(output.ChannelNumber - 1);

        await graph.ConnectNodes(rule, output);

        Assert.NotEqual(OutputChannel.ChannelType.MATH, output.Channel.ChanType);
        Assert.DoesNotContain(graph.Connections, connection =>
            connection.Target.Node == output && connection.Source.Node == rule);
    }

    [Fact]
    public async Task OwnedMathRulesRejectCyclesWithoutKeepingTheInvalidConnection()
    {
        DataStructures settings = new();
        OutputChannel channel = settings.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.MathRuleCount = 2;

        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();

        MathRuleNodeViewModel[] rules = graph.MathRuleNodes
            .Where(node => node.OwnerChannelNumber == channel.ChannelNumber)
            .OrderBy(node => node.RuleIndex)
            .ToArray();

        await graph.ConnectNodes(rules[1], rules[0]);
        await graph.ConnectNodes(rules[0], rules[1]);

        Assert.Equal((byte)OutputChannel.MathOperandType.Rule, rules[0].Rule.OperandAType);
        Assert.Equal((byte)1, rules[0].Rule.OperandAIndex);
        Assert.Equal((byte)OutputChannel.MathOperandType.Constant, rules[1].Rule.OperandAType);
        Assert.Contains("cycle", graph.AddNodeWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LiveMathRuleAndRootResultsAppearOnFlowNodes()
    {
        DataStructures settings = new();
        DataStructures live = new();
        OutputChannel configuredChannel = settings.ChannelsStaticData[0];
        configuredChannel.ChanType = OutputChannel.ChannelType.MATH;
        configuredChannel.MathRuleCount = 2;
        configuredChannel.MathRootRule = 1;
        configuredChannel.EnsureMathRuleSlots();
        configuredChannel.MathRules[1].OperandAType = (byte)OutputChannel.MathOperandType.Rule;
        configuredChannel.MathRules[1].OperandAIndex = 0;

        OutputChannel liveChannel = live.ChannelsLiveData[0];
        liveChannel.ChanType = OutputChannel.ChannelType.MATH;
        liveChannel.MathRuleCount = 2;
        liveChannel.MathRootRule = 1;
        liveChannel.MathOutputMode = (byte)OutputChannel.MathOutputModeType.PWM;
        configuredChannel.MathOutputMode = liveChannel.MathOutputMode;
        liveChannel.MathRules[1].OperandAType = configuredChannel.MathRules[1].OperandAType;
        liveChannel.MathRules[1].OperandAIndex = configuredChannel.MathRules[1].OperandAIndex;
        liveChannel.MathCalculatedRootValue = 42.5f;
        liveChannel.MathRules[0].RuntimeValue = 15.5f;
        liveChannel.MathRules[0].Evaluated = 1;
        liveChannel.MathRules[1].RuntimeValue = 42.5f;
        liveChannel.MathRules[1].Evaluated = 1;

        IoFlowGraphViewModel graph = new();
        graph.Build(settings, live);
        graph.PopulateFromCurrentSettings();
        graph.UpdateLiveChannelSamples(live.ChannelsLiveData);

        MathRuleNodeViewModel[] rules = graph.MathRuleNodes
            .Where(node => node.OwnerChannelNumber == configuredChannel.ChannelNumber)
            .OrderBy(node => node.RuleIndex)
            .ToArray();
        OutputNodeViewModel output = graph.OutputNodes.Single(node => node.ChannelNumber == configuredChannel.ChannelNumber);
        Assert.Contains("feeds Rule 2", rules[0].LiveResultText);
        Assert.Contains("not directly driving output", rules[0].LiveResultText);
        Assert.Contains("PWM output: 43%", rules[1].LiveResultText);
        Assert.Contains("result 42.5", rules[1].LiveResultText);
        Assert.Contains("Rule 1: 15.5", rules[1].LiveEvaluationText);
        Assert.Contains("= 42.5", rules[1].LiveEvaluationText);
        Assert.Equal("Rule 1: 15.5", rules[1].LiveOperandAText);
        Assert.Equal("42.5", rules[1].LiveRuleResultText);
        Assert.Contains("PWM output: 43%", rules[1].LiveResultText);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("operand")]
    [InlineData("count")]
    [InlineData("root")]
    [InlineData("mode")]
    public void ChangedControllerMathDefinitionNeverAppearsAsTheCanvasResult(string difference)
    {
        var settings = new DataStructures();
        var live = new DataStructures();
        var configured = settings.ChannelsStaticData[0];
        var controller = live.ChannelsLiveData[0];
        configured.ChanType = controller.ChanType = OutputChannel.ChannelType.MATH;
        configured.MathRuleCount = controller.MathRuleCount = 1;
        controller.MathRules[0].RuntimeValue = 64.23f;
        controller.MathRules[0].Evaluated = 1;
        switch (difference)
        {
            case "operation": controller.MathRules[0].Operation++; break;
            case "operand": controller.MathRules[0].OperandAConstant = 37; break;
            case "count": controller.MathRuleCount = 0; break;
            case "root": controller.MathRootRule = 1; break;
            case "mode": controller.MathOutputMode = 1; break;
        }
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, live);
        graph.PopulateFromCurrentSettings();
        graph.UpdateLiveChannelSamples(live.ChannelsLiveData);
        var rule = Assert.Single(graph.MathRuleNodes);
        Assert.Contains("Pending controller configuration", rule.LiveResultText);
        Assert.DoesNotContain("64.2", rule.LiveResultText);
        Assert.Equal("Pending / not applied", rule.LiveRuleResultText);
        controller.MathRules[0].Operation = configured.MathRules[0].Operation;
        controller.MathRules[0].OperandAConstant = configured.MathRules[0].OperandAConstant;
        controller.MathRuleCount = configured.MathRuleCount;
        controller.MathRootRule = configured.MathRootRule;
        controller.MathOutputMode = configured.MathOutputMode;
        graph.UpdateLiveChannelSamples(live.ChannelsLiveData);
        Assert.DoesNotContain("Pending", rule.LiveResultText);
        Assert.Equal("64.2", rule.LiveRuleResultText);
    }

    [Fact]
    public void AnalogueOperandChoicesUseTheTraditionalOneBasedNamesAndZeroBasedIndices()
    {
        DataStructures settings = new();
        IoFlowGraphViewModel graph = new();
        graph.Build(settings, new DataStructures());
        graph.AddLogicNodeCommand.Execute(null);

        MathRuleNodeViewModel rule = Assert.Single(graph.MathRuleNodes);
        rule.Rule.OperandAType = (byte)OutputChannel.MathOperandType.AnalogueInput;

        Assert.Contains(rule.OperandAIndexOptions, option => option.Value == 0 && option.Label == "Analogue input 1");
        Assert.Contains(rule.OperandAIndexOptions, option => option.Value == 2 && option.Label == "Analogue input 3");
    }

    [Fact]
    public async Task RemovingMathRootWireResetsChannelAndDetachesOrRemovesRulesCleanly()
    {
        DataStructures settings = new();
        OutputChannel ownedChannel = settings.ChannelsStaticData[0];
        ownedChannel.ChanType = OutputChannel.ChannelType.MATH;

        IoFlowGraphViewModel ownedGraph = new();
        ownedGraph.Build(settings, new DataStructures());
        ownedGraph.PopulateFromCurrentSettings();
        OutputNodeViewModel ownedOutput = Assert.Single(ownedGraph.OutputNodes, node => node.ChannelNumber == ownedChannel.ChannelNumber);
        MathRuleNodeViewModel ownedRoot = Assert.Single(ownedGraph.MathRuleNodes, node => node.OwnerChannelNumber == ownedChannel.ChannelNumber);
        IoFlowConnectionViewModel ownedWire = Assert.Single(ownedGraph.Connections, connection =>
            ReferenceEquals(connection.Source.Node, ownedRoot) && ReferenceEquals(connection.Target.Node, ownedOutput));

        await ownedGraph.RemoveConnectionCommand.ExecuteAsync(ownedWire);

        Assert.Equal(OutputChannel.ChannelType.Digital, ownedChannel.ChanType);
        Assert.DoesNotContain(ownedGraph.MathRuleNodes, node => node.OwnerChannelNumber == ownedChannel.ChannelNumber);

        DataStructures standaloneSettings = new();
        IoFlowGraphViewModel standaloneGraph = new();
        standaloneGraph.Build(standaloneSettings, new DataStructures());
        standaloneGraph.AddNextOutputNodeCommand.Execute(null);
        standaloneGraph.AddLogicNodeCommand.Execute(null);
        MathRuleNodeViewModel standaloneRule = Assert.Single(standaloneGraph.MathRuleNodes);
        OutputNodeViewModel standaloneOutput = Assert.Single(standaloneGraph.OutputNodes);
        await standaloneGraph.ConnectNodes(standaloneRule, standaloneOutput);
        Assert.True(standaloneRule.IsLinkedToOutput);
        IoFlowConnectionViewModel standaloneWire = Assert.Single(standaloneGraph.Connections, connection =>
            ReferenceEquals(connection.Source.Node, standaloneRule) && ReferenceEquals(connection.Target.Node, standaloneOutput));

        await standaloneGraph.RemoveConnectionCommand.ExecuteAsync(standaloneWire);

        Assert.Equal(OutputChannel.ChannelType.Digital, standaloneOutput.Channel.ChanType);
        Assert.Contains(standaloneRule, standaloneGraph.MathRuleNodes);
        Assert.False(standaloneRule.IsLinkedToOutput);
        Assert.Contains("Not linked to an output", standaloneRule.LiveResultText);
    }
}
