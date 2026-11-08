using Avalonia;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels.IoFlow;
using System.Text.Json;
using Xunit;

namespace Cortex.Tests;

public sealed class IoFlowCanvasTests
{
    [Fact]
    public void CanvasLimitAndEmptyOnlyDeletionAreEnforced()
    {
        var graph = CreateGraph();
        Assert.False(graph.CanDeleteCanvas);
        graph.AddNextOutputNodeCommand.Execute(null);
        Assert.False(graph.DeleteCanvasCommand.CanExecute(null));
        for (int index = 1; index < 20; index++)
            graph.AddCanvasCommand.Execute(null);
        Assert.Equal(20, graph.Canvases.Count);
        Assert.False(graph.AddCanvasCommand.CanExecute(null));
        graph.AddCanvasCommand.Execute(null);
        Assert.Equal(20, graph.Canvases.Count);
        graph.DeleteCanvasCommand.Execute(null);
        Assert.Equal(19, graph.Canvases.Count);
        Assert.True(graph.CanAddCanvas);
        graph.SelectedCanvas = graph.Canvases[0];
        graph.DeleteCanvasCommand.Execute(null);
        Assert.Equal(19, graph.Canvases.Count);
        Assert.Single(graph.VisibleNodes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovingPartialSelectionConfirmsAndMovesWholeConnectedComponent(bool accept)
    {
        var graph = CreateGraph();
        graph.AddCanInputNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        var source = graph.CanNodes[0];
        var first = graph.OutputNodes[0];
        var second = graph.OutputNodes[1];
        source.Location = new Point(31, 72);
        await graph.ConnectNodes(source, first);
        await graph.ConnectNodes(source, second);
        graph.AddLogicNodeCommand.Execute(null);
        var unrelated = graph.MathRuleNodes.Single();
        var original = graph.SelectedCanvas;
        graph.AddCanvasCommand.Execute(null);
        var destination = graph.SelectedCanvas;
        graph.SelectedCanvas = original;
        graph.SelectedNodes.Add(first);
        int confirmations = 0;
        graph.ConfirmAction = (title, message, confirm, cancel) =>
        {
            confirmations++;
            Assert.Contains("2 connected node(s)", message);
            return Task.FromResult(accept);
        };
        await graph.MoveSelectedNodesToCanvasCommand.ExecuteAsync(destination);
        Assert.Equal(1, confirmations);
        Assert.Equal(accept ? destination.Id : original.Id, first.CanvasId);
        Assert.Equal(first.CanvasId, source.CanvasId);
        Assert.Equal(first.CanvasId, second.CanvasId);
        Assert.Equal(original.Id, unrelated.CanvasId);
        Assert.Equal(new Point(31, 72), source.Location);
        Assert.Equal(2, graph.Connections.Count);
        if (accept)
        {
            Assert.Single(graph.VisibleNodes);
            Assert.Empty(graph.VisibleConnections);
            graph.SelectedCanvas = destination;
            Assert.Equal(3, graph.VisibleNodes.Count);
            Assert.Equal(2, graph.VisibleConnections.Count);
            Assert.Empty(graph.SelectedNodes);
        }
    }

    [Fact]
    public async Task FullSelectionMovesWithoutConfirmationAndAutoArrangeOnlyTouchesCurrentCanvas()
    {
        var graph = CreateGraph();
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        await graph.ConnectNodes(graph.DigitalNodes[0], graph.OutputNodes[0]);
        var original = graph.SelectedCanvas;
        graph.AddCanvasCommand.Execute(null);
        var destination = graph.SelectedCanvas;
        graph.AddLogicNodeCommand.Execute(null);
        var unrelated = graph.MathRuleNodes.Single();
        unrelated.Location = new Point(83, 117);
        graph.SelectedCanvas = original;
        foreach (var node in graph.VisibleNodes)
            graph.SelectedNodes.Add(node);
        await graph.MoveSelectedNodesToCanvasCommand.ExecuteAsync(destination);
        graph.SelectedCanvas = destination;
        var positions = graph.VisibleNodes.ToDictionary(node => node, node => node.Location);
        graph.SelectedCanvas = original;
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AutoArrangeNodesCommand.Execute(null);
        Assert.All(positions, pair => Assert.Equal(pair.Value, pair.Key.Location));
        Assert.Equal(new Point(83, 117), unrelated.Location);
    }

    [Fact]
    public async Task LocalDraftRoundTripsCanvasNamesMembershipPositionsAndEmptyCanvases()
    {
        var graph = CreateGraph();
        graph.AddNextOutputNodeCommand.Execute(null);
        var output = graph.OutputNodes[0];
        output.Location = new Point(127, 356);
        var original = graph.SelectedCanvas;
        graph.AddCanvasCommand.Execute(null);
        var destination = graph.SelectedCanvas;
        destination.Name = "Lighting";
        graph.AddCanvasCommand.Execute(null);
        var empty = graph.SelectedCanvas;
        graph.SelectedCanvas = original;
        graph.SelectedNodes.Add(output);
        await graph.MoveSelectedNodesToCanvasCommand.ExecuteAsync(destination);
        graph.SelectedCanvas = empty;
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-canvases-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "graph.json");
        try
        {
            var store = new IoFlowGraphStore(path);
            store.Save(graph.CreateSnapshot());
            Assert.True(store.TryLoad(out var snapshot));
            var restored = CreateGraph();
            restored.ApplySnapshot(snapshot);
            Assert.Equal(3, restored.Canvases.Count);
            Assert.Equal("Lighting", restored.Canvases[1].Name);
            Assert.Equal(empty.Id, restored.SelectedCanvas.Id);
            Assert.Empty(restored.VisibleNodes);
            Assert.Equal(destination.Id, Assert.Single(restored.OutputNodes).CanvasId);
            Assert.Equal(output.Location, restored.OutputNodes[0].Location);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void LegacySnapshotsUseOneCanvasAndInvalidLayoutsAreRejected()
    {
        var graph = CreateGraph();
        graph.ApplySnapshot(JsonSerializer.Deserialize<IoFlowGraphSnapshot>(
            """{"Nodes":[{"Kind":"Output","Id":1,"X":42,"Y":91}]}""")!);
        Assert.Single(graph.Canvases);
        Assert.Equal(1, Assert.Single(graph.OutputNodes).CanvasId);
        Assert.Equal(new Point(42, 91), graph.OutputNodes[0].Location);
        var invalid = graph.CreateSnapshot();
        invalid.Nodes[0].CanvasId = 999;
        Assert.Throws<ArgumentException>(() => graph.ApplySnapshot(invalid));
        Assert.Single(graph.Nodes);
        invalid.Nodes[0].CanvasId = 1;
        invalid.Canvases.Add(new IoFlowCanvasSnapshot { Id = 1, Name = "Duplicate" });
        Assert.False(IoFlowGraphViewModel.IsCanvasLayoutValid(invalid));
        invalid.Canvases = Enumerable.Range(1, 21).Select(id => new IoFlowCanvasSnapshot { Id = id, Name = $"Canvas {id}" }).ToList();
        Assert.False(IoFlowGraphViewModel.IsCanvasLayoutValid(invalid));
        invalid.Canvases.RemoveAt(20);
        Assert.True(IoFlowGraphViewModel.IsCanvasLayoutValid(invalid));
    }

    [Fact]
    public async Task ImplicitOperandNodesStayWithTheirRuleWhenAnotherCanvasIsSelected()
    {
        var graph = CreateGraph();
        graph.AddNextOutputNodeCommand.Execute(null);
        var output = graph.OutputNodes[0];
        output.Channel.ChanType = OutputChannel.ChannelType.MATH;
        graph.SynchronizeMathChannelFromSettings(output.Channel);
        var rule = Assert.Single(graph.MathRuleNodes);
        var original = graph.SelectedCanvas;
        graph.AddCanvasCommand.Execute(null);
        var destination = graph.SelectedCanvas;
        graph.SelectedCanvas = original;
        foreach (var node in graph.VisibleNodes)
            graph.SelectedNodes.Add(node);
        await graph.MoveSelectedNodesToCanvasCommand.ExecuteAsync(destination);
        rule.Rule.OperandAType = (byte)OutputChannel.MathOperandType.Channel;
        rule.Rule.OperandAIndex = 1;
        graph.SynchronizeMathChannelFromSettings(output.Channel);
        Assert.All(graph.Nodes, node => Assert.Equal(destination.Id, node.CanvasId));
        Assert.Empty(graph.VisibleNodes);
        graph.SelectedCanvas = destination;
        Assert.Equal(2, graph.OutputNodes.Count);
        Assert.Equal(2, graph.VisibleConnections.Count);
        var snapshot = graph.CreateSnapshot();
        graph.ApplySnapshot(snapshot);
        Assert.All(graph.Nodes, node => Assert.Equal(destination.Id, node.CanvasId));
    }

    private static IoFlowGraphViewModel CreateGraph()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        return graph;
    }
}
