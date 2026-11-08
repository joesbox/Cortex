using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Xunit;

namespace Cortex.Tests;

public sealed class IoFlowConnectionRefreshTests
{
    [Fact]
    public async Task RebuildRetainsUnchangedWiresAndClearsSelectionWhenWireDisappears()
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        await graph.ConnectNodes(graph.DigitalNodes[0], graph.OutputNodes[0]);
        await graph.ConnectNodes(graph.DigitalNodes[1], graph.OutputNodes[1]);
        IoFlowConnectionViewModel first = Assert.Single(graph.Connections, wire => wire.Target.Node == graph.OutputNodes[0]);
        IoFlowConnectionViewModel second = Assert.Single(graph.Connections, wire => wire.Target.Node == graph.OutputNodes[1]);
        graph.SelectConnectionCommand.Execute(first);
        first.IsHovered = true;

        graph.RebuildConnections(settings);
        Assert.Contains(first, graph.Connections);
        Assert.Contains(second, graph.Connections);
        Assert.Same(first, graph.SelectedConnection);
        Assert.True(first.IsSelected);
        Assert.True(first.IsHovered);

        graph.OutputNodes[0].Channel.InputControlPin = 0;
        graph.RebuildConnections(settings);
        Assert.DoesNotContain(first, graph.Connections);
        Assert.Contains(second, graph.Connections);
        Assert.Null(graph.SelectedConnection);
        Assert.False(first.IsSelected);
        Assert.False(first.Source.IsConnected);
        Assert.False(first.Target.IsConnected);
        Assert.True(second.Source.IsConnected);
        Assert.True(second.Target.IsConnected);
    }
}
