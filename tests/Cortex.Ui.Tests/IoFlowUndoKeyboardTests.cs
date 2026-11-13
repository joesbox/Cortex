using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using Cortex.ViewModels.IoFlow;
using Cortex.Views;
using Nodify.Avalonia;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class IoFlowUndoKeyboardTests
{
    [AvaloniaTheory]
    [InlineData("undo")]
    [InlineData("delete-connection")]
    [InlineData("delete-node")]
    public async Task KeyboardCommandFailureIsReportedWithoutEscaping(string action)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-ioflow-key-failure-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "graph.json");
        using var vm = new MainWindowViewModel(new ChannelCanTxSettingsTests.TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: new IoFlowGraphStore(path)));
        var graph = vm.IoFlowGraph;
        var message = vm.SettingsDataView.CustomCanMessagesStaticData[0];
        message.Id = 0x97;
        message.Flags = 0x04;
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddCanTxNodeCommand.Execute(null);
        graph.SetCustomCanTxStateAction = (_, trigger, enabled) =>
        {
            message.TxTrigger = trigger;
            message.IsEnabled = enabled;
            return Task.FromResult(true);
        };
        await graph.ConnectNodes(graph.OutputNodes[0], graph.CanTxNodes[0]);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        graph.SetCustomCanTxStateAction = (_, _, _) => completion.Task;
        var view = new IoFlowTabView { DataContext = vm };
        var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
        var window = new Window { Width = 1400, Height = 1000, Content = view };
        try
        {
            window.Show();
            UpdateLayout(window);
            if (action == "delete-node") graph.SelectedNodes.Add(graph.CanTxNodes[0]);
            if (action == "delete-connection") graph.SelectConnectionCommand.Execute(Assert.Single(graph.Connections));
            Assert.True(editor.Focus());

            if (action == "undo")
                window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            else
                window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Task? commandTask = action == "undo" ? graph.UndoCommand.ExecutionTask
                : action == "delete-node" ? graph.RemoveSelectedNodesCommand.ExecutionTask
                : graph.RemoveSelectedConnectionCommand.ExecutionTask;
            Assert.NotNull(commandTask);
            Assert.False(commandTask.IsCompleted);
            completion.SetException(new InvalidOperationException("Injected failure"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => commandTask);
            UpdateLayout(window);

            string expected = action == "undo"
                ? "Couldn't undo the I/O Flow change. Check the graph before continuing."
                : "Couldn't delete the I/O Flow selection. Check the graph before continuing.";
            Assert.Equal(expected, graph.AddNodeWarning);
            Assert.Contains(LoggingService.LogEntries, entry => entry.EndsWith(expected, StringComparison.Ordinal));
            Assert.True(message.IsEnabled);
        }
        finally
        {
            window.Close();
            if (File.Exists(path)) File.Delete(path);
            if (Directory.Exists(directory)) Directory.Delete(directory);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, 1.0, false)]
    [InlineData(true, 1.0, false)]
    [InlineData(false, 0.6, false)]
    [InlineData(true, 0.6, false)]
    [InlineData(false, 1.0, true)]
    [InlineData(true, 1.0, true)]
    [InlineData(false, 0.6, true)]
    [InlineData(true, 0.6, true)]
    public async Task RealNodeDragAndUndoRestoreVisiblePositionAndUpdateUndoButton(bool clickButton, double zoom, bool multipleNodes)
    {
        var (graph, view, editor, window) = CreateView();
        IoFlowNodeViewModel node = graph.DigitalNodes[0];
        Point before = node.Location;
        if (multipleNodes)
        {
            graph.AddNextDigitalNodeCommand.Execute(null);
            graph.DigitalNodes[1].Location = new Point(500, 50);
        }
        List<bool> notifications = [];
        graph.GraphChangedAction = notifications.Add;
        try
        {
            window.Show();
            editor.ViewportZoom = zoom;
            UpdateLayout(window);
            notifications.Clear();
            Button undo = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Undo"));
            Assert.Same(graph.UndoCommand, undo.Command);
            Assert.False(undo.IsEffectivelyEnabled);
            ItemContainer container = Assert.Single(editor.GetVisualDescendants().OfType<ItemContainer>(),
                item => ReferenceEquals(item.DataContext, node));
            var originals = graph.Nodes.Select(item =>
            {
                ItemContainer visual = Assert.Single(editor.GetVisualDescendants().OfType<ItemContainer>(),
                    candidate => ReferenceEquals(candidate.DataContext, item));
                visual.IsSelected = true;
                return (Node: item, Container: visual, Location: item.Location,
                    Visible: visual.TranslatePoint(default, editor), Anchor: item.OutputConnector.Anchor);
            }).ToArray();
            Point start = container.TranslatePoint(new Point(40, 20), window)!.Value;
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(5, 5));
            window.MouseMove(start + new Vector(90, 60));
            UpdateLayout(window);
            window.MouseUp(start + new Vector(90, 60), MouseButton.Left);
            UpdateLayout(window);
            Assert.NotEqual(before, node.Location);
            Assert.True(graph.CanUndo);
            Assert.True(undo.IsEffectivelyEnabled);
            foreach (var original in originals)
            {
                Assert.NotEqual(original.Location, original.Node.Location);
                Assert.NotEqual(original.Visible, original.Container.TranslatePoint(default, editor));
            }

            if (clickButton)
            {
                Point buttonCenter = undo.TranslatePoint(new Point(undo.Bounds.Width / 2, undo.Bounds.Height / 2), window)!.Value;
                window.MouseMove(buttonCenter);
                window.MouseDown(buttonCenter, MouseButton.Left);
                window.MouseUp(buttonCenter, MouseButton.Left);
                if (graph.UndoCommand.ExecutionTask is Task undoTask)
                {
                    await undoTask;
                }
                UpdateLayout(window);
            }
            else
            {
                Assert.True(editor.Focus());
                await PressUndo(window, graph);
            }

            Assert.Equal(before, node.Location);
            foreach (var original in originals)
            {
                Assert.Equal(original.Location, original.Node.Location);
                Assert.Equal(original.Location, original.Container.Location);
                Assert.Equal(original.Visible, original.Container.TranslatePoint(default, editor));
                Assert.Equal(original.Anchor, original.Node.OutputConnector.Anchor);
            }
            Assert.False(graph.CanUndo);
            Assert.False(undo.IsEffectivelyEnabled);
            Assert.Equal(new[] { false, false }, notifications);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PositionBindingSurvivesRepeatedDragsArrangeAndGraphReload()
    {
        var (graph, _, editor, window) = CreateView();
        IoFlowNodeViewModel node = graph.DigitalNodes[0];
        Point original = node.Location;
        try
        {
            window.Show();
            UpdateLayout(window);
            ItemContainer container = Assert.Single(editor.GetVisualDescendants().OfType<ItemContainer>(),
                item => ReferenceEquals(item.DataContext, node));
            List<(Point Location, Point? Visible)> positions = [];
            for (int action = 0; action < 3; action++)
            {
                positions.Add((node.Location, container.TranslatePoint(default, editor)));
                Point start = container.TranslatePoint(new Point(40, 20), window)!.Value;
                window.MouseMove(start);
                window.MouseDown(start, MouseButton.Left);
                window.MouseMove(start + new Vector(5, 5));
                window.MouseMove(start + new Vector(60, 30));
                window.MouseUp(start + new Vector(60, 30), MouseButton.Left);
                UpdateLayout(window);
            }

            Point dragged = node.Location;
            graph.AutoArrangeNodesCommand.Execute(null);
            UpdateLayout(window);
            Assert.NotEqual(dragged, node.Location);
            Assert.Equal(node.Location, container.Location);
            await graph.UndoCommand.ExecuteAsync(null);
            UpdateLayout(window);
            Assert.Equal(dragged, container.Location);

            for (int action = positions.Count - 1; action >= 0; action--)
            {
                await graph.UndoCommand.ExecuteAsync(null);
                UpdateLayout(window);
                Assert.Equal(positions[action].Location, node.Location);
                Assert.Equal(positions[action].Location, container.Location);
                Assert.Equal(positions[action].Visible, container.TranslatePoint(default, editor));
            }

            graph.ApplySnapshot(graph.CreateSnapshot());
            UpdateLayout(window);
            Assert.Equal(original, node.Location);
            IoFlowNodeViewModel restored = graph.DigitalNodes[0];
            ItemContainer restoredContainer = Assert.Single(editor.GetVisualDescendants().OfType<ItemContainer>(),
                item => ReferenceEquals(item.DataContext, restored));
            Assert.Equal(original, restoredContainer.Location);
            restored.Location += new Vector(25, 50);
            UpdateLayout(window);
            Assert.Equal(restored.Location, restoredContainer.Location);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CancellingRealDragPreservesPreviousUndoAction()
    {
        var (graph, _, editor, window) = CreateView();
        IoFlowNodeViewModel node = graph.DigitalNodes[0];
        Point original = node.Location;
        graph.AutoArrangeNodesCommand.Execute(null);
        Point arranged = node.Location;
        Assert.NotEqual(original, arranged);
        editor.ViewportLocation = arranged - new Vector(50, 50);
        try
        {
            window.Show();
            UpdateLayout(window);
            Assert.True(editor.Focus());
            ItemContainer container = Assert.Single(editor.GetVisualDescendants().OfType<ItemContainer>(),
                item => ReferenceEquals(item.DataContext, node));
            Point start = container.TranslatePoint(new Point(40, 20), window)!.Value;
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(5, 5));
            window.MouseMove(start + new Vector(90, 60));
            Assert.False(graph.CanUndo);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, string.Empty);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, string.Empty);
            window.MouseUp(start + new Vector(90, 60), MouseButton.Left);
            UpdateLayout(window);
            Assert.Equal(arranged, node.Location);
            Assert.True(graph.CanUndo);

            await PressUndo(window, graph);

            Assert.Equal(original, node.Location);
            Assert.False(graph.CanUndo);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlZUndoesConnectionWithoutMovingNodes()
    {
        var (graph, view, editor, window) = CreateView();
        graph.AddNextOutputNodeCommand.Execute(null);
        try
        {
            window.Show();
            UpdateLayout(window);
            await graph.ConnectNodes(graph.DigitalNodes[0], graph.OutputNodes[0]);
            Point[] locations = graph.Nodes.Select(node => node.Location).ToArray();
            Assert.Single(graph.Connections);
            Assert.True(editor.Focus());

            await PressUndo(window, graph);

            Assert.Empty(graph.Connections);
            Assert.Equal(0, graph.OutputNodes[0].Channel.InputControlPin);
            Assert.Equal(locations, graph.Nodes.Select(node => node.Location));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlZInTextBoxUsesTextUndoRatherThanGraphUndo()
    {
        var (graph, view, _, window) = CreateView();
        graph.AddLogicNodeCommand.Execute(null);
        graph.MathRuleNodes[0].Location = new Point(400, 50);
        graph.SelectedNodes.Add(graph.MathRuleNodes[0]);
        try
        {
            window.Show();
            UpdateLayout(window);
            TextBox textBox = view.GetVisualDescendants().OfType<TextBox>().First(control => control.IsEffectivelyVisible && control.IsEffectivelyEnabled);
            Assert.True(textBox.Focus());
            textBox.SelectAll();
            window.KeyTextInput("42");
            Assert.Equal("42", textBox.Text);
            graph.ItemsDragStartedCommand.Execute(null);
            graph.DigitalNodes[0].Location += new Vector(50, 0);
            graph.ItemsDragCompletedCommand.Execute(null);
            Point moved = graph.DigitalNodes[0].Location;

            await PressUndo(window, graph);

            Assert.NotEqual("42", textBox.Text);
            Assert.Equal(moved, graph.DigitalNodes[0].Location);
            Assert.True(graph.CanUndo);
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task PressUndo(Window window, IoFlowGraphViewModel graph)
    {
        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        window.KeyRelease(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        if (graph.UndoCommand.ExecutionTask is Task undoTask)
        {
            await undoTask;
        }
        UpdateLayout(window);
    }

    private static (IoFlowGraphViewModel, IoFlowTabView, NodifyEditor, Window) CreateView()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.DigitalNodes[0].Location = new Point(50, 50);
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
        var window = new Window { Width = 1400, Height = 1000, Content = view };
        return (graph, view, editor, window);
    }

    private static void UpdateLayout(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }
}
