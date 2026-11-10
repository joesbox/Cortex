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
    public async Task AutoArrangeLeavesTwoHundredFortyPixelsBetweenColumns()
    {
        var graph = CreateGraph();
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddNextDigitalNodeCommand.Execute(null);
        var output = Assert.Single(graph.OutputNodes);
        var input = Assert.Single(graph.DigitalNodes);
        await graph.ConnectNodes(input, output);

        graph.AutoArrangeNodesCommand.Execute(null);

        Assert.Equal(240, output.Location.X - input.Location.X - 310);
    }

    [Fact]
    public async Task RestartRestoresStandaloneLogicAsControllerRulesOnEveryCanvas()
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        var expected = new Dictionary<(int Channel, int Rule), (int Canvas, Point Location)>();
        var outputs = new Dictionary<int, (int Canvas, Point Location)>();
        for (int canvas = 1; canvas <= 3; canvas++)
        {
            if (canvas > 1)
                graph.AddCanvasCommand.Execute(null);
            graph.AddNextOutputNodeCommand.Execute(null);
            var output = graph.OutputNodes.Last();
            graph.AddLogicNodeCommand.Execute(null);
            var dependency = graph.MathRuleNodes.Last();
            graph.AddLogicNodeCommand.Execute(null);
            var root = graph.MathRuleNodes.Last();
            await graph.ConnectNodes(dependency, root, operandA: true);
            await graph.ConnectNodes(root, output);
            dependency.Location = new Point(-350 + canvas * 10, 230 + canvas * 15);
            root.Location = new Point(135 + canvas * 20, -90 + canvas * 10);
            output.Location = new Point(650 + canvas * 30, 180);
            expected[(output.ChannelNumber, 0)] = (canvas, dependency.Location);
            expected[(output.ChannelNumber, 1)] = (canvas, root.Location);
            outputs[output.ChannelNumber] = (canvas, output.Location);
            graph.SelectedCanvas.ViewportLocation = new Point(-canvas * 45, canvas * 18);
            graph.SelectedCanvas.ViewportZoom = 0.4 + canvas * 0.1;
        }
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-logic-layout-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "graph.json");
        try
        {
            var store = new IoFlowGraphStore(path);
            store.Save(graph.CreateLayoutSnapshot());
            Assert.True(store.TryLoad(out var layout));
            var restored = new IoFlowGraphViewModel();
            restored.Build(settings, new DataStructures());
            restored.ApplyNodePositions(layout);
            store.Save(restored.CreateLayoutSnapshot(), preserveUnmatchedNodePositions: true);
            Assert.True(store.TryLoad(out layout));
            restored.PopulateFromCurrentSettings();
            restored.ApplyNodePositions(layout);
            Assert.Equal(graph.SelectedCanvas.Id, restored.SelectedCanvas.Id);
            foreach (var canvas in graph.Canvases)
            {
                var restoredCanvas = Assert.Single(restored.Canvases, item => item.Id == canvas.Id);
                Assert.Equal(canvas.ViewportLocation, restoredCanvas.ViewportLocation);
                Assert.Equal(canvas.ViewportZoom, restoredCanvas.ViewportZoom);
            }
            foreach (var (channel, position) in outputs)
            {
                var output = Assert.Single(restored.OutputNodes, node => node.ChannelNumber == channel);
                Assert.Equal(position.Canvas, output.CanvasId);
                Assert.Equal(position.Location, output.Location);
            }
            foreach (var (identity, position) in expected)
            {
                var rule = Assert.Single(restored.MathRuleNodes, node =>
                    node.OwnerChannelNumber == identity.Channel && node.RuleIndex == identity.Rule);
                Assert.Equal(position.Canvas, rule.CanvasId);
                Assert.Equal(position.Location, rule.Location);
            }
            Assert.Equal(graph.Connections.Count, restored.Connections.Count);
            // Once reloaded, rules use controller identities directly on subsequent saves.
            store.Save(restored.CreateLayoutSnapshot());
            Assert.True(store.TryLoad(out var secondLayout));
            restored.Build(settings, new DataStructures());
            restored.PopulateFromCurrentSettings();
            restored.ApplyNodePositions(secondLayout);
            foreach (var (identity, position) in expected)
            {
                var rule = Assert.Single(restored.MathRuleNodes, node =>
                    node.OwnerChannelNumber == identity.Channel && node.RuleIndex == identity.Rule);
                Assert.Equal(position.Canvas, rule.CanvasId);
                Assert.Equal(position.Location, rule.Location);
            }
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

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
        Vector firstOffset = first.Location - source.Location;
        Vector secondOffset = second.Location - source.Location;
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
        Assert.Equal(firstOffset, first.Location - source.Location);
        Assert.Equal(secondOffset, second.Location - source.Location);
        if (accept)
            Assert.NotEqual(new Point(31, 72), source.Location);
        else
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MovingConnectedNodesFindsFreeSpaceOnDestinationAndKeepsRelativeLayout(bool crowded)
    {
        var graph = CreateGraph();
        graph.AddIgnitionNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        var ignition = Assert.Single(graph.IgnitionNodes);
        var output = Assert.Single(graph.OutputNodes);
        ignition.Location = new Point(-1700, 900);
        output.Location = new Point(-1000, 960);
        ignition.MeasuredSize = new Size(380, 210);
        output.MeasuredSize = new Size(600, 900);
        await graph.ConnectNodes(ignition, output);
        var original = graph.SelectedCanvas;
        graph.AddCanvasCommand.Execute(null);
        var destination = graph.SelectedCanvas;
        destination.ViewportLocation = new Point(500, 300);
        graph.AddNextDigitalNodeCommand.Execute(null);
        var existing = Assert.Single(graph.DigitalNodes);
        existing.Location = new Point(500, 300);
        existing.MeasuredSize = crowded ? new Size(8000, 8000) : new Size(340, 250);
        graph.SelectedCanvas = original;
        graph.SelectedNodes.Add(ignition);
        graph.SelectedNodes.Add(output);
        Vector relative = output.Location - ignition.Location;
        var notifications = new List<bool>();
        graph.GraphChangedAction = notifications.Add;
        await graph.MoveSelectedNodesToCanvasCommand.ExecuteAsync(destination);
        Assert.Equal(relative, output.Location - ignition.Location);
        Assert.Equal(destination.Id, ignition.CanvasId);
        Assert.Equal(destination.Id, output.CanvasId);
        Assert.NotEqual(new Point(-1700, 900), ignition.Location);
        Assert.Equal(new Point(500, 300), existing.Location);
        var occupied = new Rect(existing.Location, existing.MeasuredSize).Inflate(20);
        Assert.False(occupied.Intersects(new Rect(ignition.Location, ignition.MeasuredSize)));
        Assert.False(occupied.Intersects(new Rect(output.Location, output.MeasuredSize)));
        if (crowded)
            Assert.True(ignition.Location.Y >= occupied.Bottom);
        Assert.Equal([false], notifications);
        Assert.Single(graph.Connections);
        Assert.Same(original, graph.SelectedCanvas);
        graph.SelectedCanvas = destination;
        Assert.Equal(3, graph.VisibleNodes.Count);
        Assert.Single(graph.VisibleConnections);
    }

    [Fact]
    public void PartialLayoutSavesRetainUnloadedPositionsButExplicitReplacementsDoNot()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-partial-layout-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "graph.json");
        try
        {
            var store = new IoFlowGraphStore(path);
            var snapshot = new IoFlowGraphSnapshot
            {
                Canvases =
                [
                    new() { Id = 1, Name = "Outputs" },
                    new() { Id = 2, Name = "Ignition" },
                    new() { Id = 3, Name = "Unused" },
                ],
                Nodes =
                [
                    new() { Kind = "Output", Id = 1, X = 100, Y = 200 },
                    new() { Kind = "Ignition", Id = 1, CanvasId = 2, X = -300, Y = 450 },
                    new() { Kind = "Digital", Id = 1, CanvasId = 3, X = 900, Y = 1000 },
                ],
            };
            store.Save(snapshot);
            snapshot.Nodes.RemoveAll(node => node.Kind != "Output");
            snapshot.Nodes[0].X = 999;
            snapshot.Canvases.RemoveAt(2);
            store.Save(snapshot, preserveUnmatchedNodePositions: true);
            Assert.True(store.TryLoad(out var partial));
            Assert.Equal(2, partial.NodePositions.Count);
            Assert.Equal(999, Assert.Single(partial.NodePositions, node => node.Kind == "Output").X);
            var ignition = Assert.Single(partial.NodePositions, node => node.Kind == "Ignition");
            Assert.Equal(2, ignition.CanvasId);
            Assert.Equal(-300, ignition.X);
            Assert.Equal(450, ignition.Y);
            store.Save(snapshot);
            Assert.True(store.TryLoad(out var replaced));
            Assert.Equal("Output", Assert.Single(replaced.NodePositions).Kind);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
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
    public async Task LocalLayoutPersistsCanvasesAndNodePositionsWithoutControllerConfiguration()
    {
        var graph = CreateGraph();
        graph.AddNextOutputNodeCommand.Execute(null);
        var output = graph.OutputNodes[0];
        output.Location = new Point(127, 356);
        var original = graph.SelectedCanvas;
        graph.AddCanvasCommand.Execute(null);
        var destination = graph.SelectedCanvas;
        destination.Name = "Lighting";
        destination.ViewportLocation = new Point(43, 82);
        destination.ViewportZoom = 0.6;
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
            Assert.True(store.TryLoad(out IoFlowGraphLayoutSnapshot snapshot));
            var restored = CreateGraph();
            restored.AddNextOutputNodeCommand.Execute(null);
            restored.ApplyNodePositions(snapshot);
            Assert.Equal(3, restored.Canvases.Count);
            Assert.Equal(empty.Id, restored.SelectedCanvas.Id);
            Assert.Empty(restored.VisibleNodes);
            Assert.Equal("Lighting", restored.Canvases[1].Name);
            Assert.Equal(destination.ViewportLocation, restored.Canvases[1].ViewportLocation);
            Assert.Equal(destination.ViewportZoom, restored.Canvases[1].ViewportZoom);
            Assert.Equal(destination.Id, Assert.Single(restored.OutputNodes).CanvasId);
            Assert.Equal(output.Location, restored.OutputNodes[0].Location);
            Assert.DoesNotContain("StandaloneLogicRules", File.ReadAllText(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [Fact]
    public void PositionsOnlyLayoutsRemainCompatible()
    {
        var snapshot = JsonSerializer.Deserialize<IoFlowGraphLayoutSnapshot>(
            """{"NodePositions":[{"Kind":"Output","Id":1,"X":42,"Y":91}]}""")!;
        var graph = CreateGraph();
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.ApplyNodePositions(snapshot);
        Assert.Single(graph.Canvases);
        Assert.Equal(1, graph.SelectedCanvas.Id);
        Assert.Equal(1, Assert.Single(graph.OutputNodes).CanvasId);
        Assert.Equal(new Point(42, 91), graph.OutputNodes[0].Location);
    }

    [Theory]
    [InlineData("""{"Canvases":null}""")]
    [InlineData("""{"Canvases":[null]}""")]
    [InlineData("""{"Canvases":[{"Id":1,"Name":"Lights"},{"Id":1,"Name":"Duplicate"}]}""")]
    [InlineData("""{"Canvases":[{"Id":1,"Name":" "}]}""")]
    [InlineData("""{"Canvases":[{"Id":1,"Name":"Lights","ViewportZoom":0}]}""")]
    [InlineData("""{"Canvases":[{"Id":1,"Name":"Lights"}],"SelectedCanvasId":2}""")]
    [InlineData("""{"Canvases":[{"Id":1,"Name":"Lights"}],"NodePositions":[{"Kind":"Output","Id":1,"CanvasId":2}]}""")]
    [InlineData("""{"NodePositions":null}""")]
    [InlineData("""{"NodePositions":[null]}""")]
    [InlineData("""{"NodePositions":[{"Kind":"Output","Id":1},{"Kind":"Output","Id":1}]}""")]
    public void InvalidLocalLayoutsAreRejectedWithoutChangingTheGraph(string json)
    {
        var snapshot = JsonSerializer.Deserialize<IoFlowGraphLayoutSnapshot>(json)!;
        Assert.False(IoFlowGraphViewModel.IsNodePositionsValid(snapshot));
        var graph = CreateGraph();
        graph.AddNextOutputNodeCommand.Execute(null);
        Assert.Throws<ArgumentException>(() => graph.ApplyNodePositions(snapshot));
        Assert.Single(graph.Canvases);
        Assert.Single(graph.OutputNodes);
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
