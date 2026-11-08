using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Xunit;

namespace Cortex.Tests;

public sealed class IoFlowImplicitOutputTests
{
    [Fact]
    public async Task DirectlyConnectedMathsRuleDeterminesFinalResultAndSurvivesReload()
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        var first = graph.MathRuleNodes[0];
        var second = graph.MathRuleNodes[1];
        first.Rule.OperandAConstant = 3;
        second.Rule.OperandBConstant = 7;
        await graph.ConnectNodes(first, second, true);
        var output = graph.OutputNodes[0];
        await graph.ConnectNodes(second, output);
        Assert.Equal(2, output.Channel.MathRuleCount);
        Assert.Equal(1, output.Channel.MathRootRule);
        Assert.Equal(7, output.Channel.MathRules[output.Channel.MathRootRule].OperandBConstant);
        Assert.Same(second, Assert.Single(graph.Connections.Where(wire => ReferenceEquals(wire.Target.Node, output))).Source.Node);

        await graph.ConnectNodes(first, output);
        Assert.Equal(1, output.Channel.MathRuleCount);
        Assert.Equal(0, output.Channel.MathRootRule);
        Assert.Equal(3, output.Channel.MathRules[0].OperandAConstant);
        Assert.Same(first, Assert.Single(graph.Connections.Where(wire => ReferenceEquals(wire.Target.Node, output))).Source.Node);
        var reloaded = new IoFlowGraphViewModel();
        reloaded.Build(settings, new DataStructures());
        reloaded.ApplySnapshot(graph.CreateSnapshot());
        Assert.Equal(0, reloaded.OutputNodes.Single().Channel.MathRootRule);
        Assert.DoesNotContain(reloaded.Connections, wire => wire.Label.Contains("root", StringComparison.OrdinalIgnoreCase));
    }
}
