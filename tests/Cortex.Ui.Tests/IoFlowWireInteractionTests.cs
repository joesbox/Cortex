using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cortex.Services;
using Cortex.ViewModels;
using Cortex.ViewModels.IoFlow;
using Cortex.Views;
using Nodify.Avalonia;
using Nodify.Avalonia.Connections;
using System.Reflection;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class IoFlowWireInteractionTests
{
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(0.6)]
    public async Task PointerHoverSelectionAndDeleteWorkOnWire(double zoom)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-wire-interaction-{Guid.NewGuid():N}");
        var vm = new MainWindowViewModel(new ChannelCanTxSettingsTests.TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "graph.json"))));
        foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
            ((System.Timers.Timer)typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).Stop();
        var graph = vm.IoFlowGraph;
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        var source = graph.DigitalNodes[0];
        var target = graph.OutputNodes[0];
        source.Location = new Point(50, 50);
        target.Location = new Point(700, 50);
        await graph.ConnectNodes(source, target);
        var model = Assert.Single(graph.Connections);
        var view = new IoFlowTabView { DataContext = vm };
        var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            editor.ViewportZoom = zoom;
            Layout();
            Connection wire = Assert.Single(view.GetVisualDescendants().OfType<Connection>(),
                item => ReferenceEquals(item.DataContext, model) && !item.IsHitTestVisible && item.StrokeThickness == 4);
            Point midpoint = new((wire.Source.X + wire.Target.X) / 2, (wire.Source.Y + wire.Target.Y) / 2);
            Point cursor = wire.TranslatePoint(midpoint, window)!.Value;
            window.MouseMove(cursor);
            Layout();
            Assert.True(model.IsHovered);
            Assert.Equal(Color.Parse("#FF9580AD"), Assert.IsAssignableFrom<ISolidColorBrush>(wire.Stroke).Color);
            Layout();
            Assert.True(model.IsHovered);
            graph.RebuildConnections(vm.SettingsDataView);
            Layout();
            Assert.Same(model, Assert.Single(graph.Connections));
            Assert.True(model.IsHovered);

            ItemContainer sourceContainer = Assert.Single(editor.GetVisualDescendants().OfType<ItemContainer>(),
                item => ReferenceEquals(item.DataContext, source));
            sourceContainer.IsSelected = true;
            Layout();
            Assert.NotEmpty(graph.SelectedNodes);
            Point emptyCanvas = editor.TranslatePoint(new Point(20, editor.Bounds.Height - 40), window)!.Value;
            window.MouseMove(emptyCanvas);
            Assert.False(window.InputHitTest(emptyCanvas) is Connection,
                $"Empty canvas hit wire at {emptyCanvas}, zoom {zoom}");
            window.MouseDown(emptyCanvas, MouseButton.Left);
            window.MouseUp(emptyCanvas, MouseButton.Left);
            Layout();
            Assert.Empty(graph.SelectedNodes);
            Assert.False(sourceContainer.IsSelected);
            sourceContainer.IsSelected = true;
            Layout();
            window.MouseDown(cursor, MouseButton.Left);
            window.MouseUp(cursor, MouseButton.Left);
            Layout();
            Assert.Same(model, graph.SelectedConnection);
            Assert.True(model.IsSelected);
            Assert.Empty(graph.SelectedNodes);
            window.MouseMove(new Point(10, 10));
            Layout();
            Assert.False(model.IsHovered);
            Assert.True(model.IsSelected);
            Assert.Equal(Color.Parse("#FFB29ACB"), Assert.IsAssignableFrom<ISolidColorBrush>(wire.Stroke).Color);
            Assert.Equal(7, wire.StrokeThickness);
            graph.RebuildConnections(vm.SettingsDataView);
            Layout();
            Assert.Same(model, graph.SelectedConnection);
            Assert.Same(model, Assert.Single(graph.Connections));
            Assert.True(model.IsSelected);

            window.MouseMove(emptyCanvas);
            Assert.False(window.InputHitTest(emptyCanvas) is Connection,
                $"Empty canvas hit wire at {emptyCanvas}, zoom {zoom}");
            window.MouseDown(emptyCanvas, MouseButton.Left, RawInputModifiers.Control);
            window.MouseUp(emptyCanvas, MouseButton.Left, RawInputModifiers.Control);
            Layout();
            Assert.Same(model, graph.SelectedConnection);

            window.MouseDown(emptyCanvas, MouseButton.Left);
            window.MouseUp(emptyCanvas, MouseButton.Left);
            Layout();
            Assert.Null(graph.SelectedConnection);
            Assert.False(model.IsSelected);
            Assert.Empty(graph.SelectedNodes);

            window.MouseMove(cursor);
            window.MouseDown(cursor, MouseButton.Left);
            window.MouseUp(cursor, MouseButton.Left);
            Layout();
            Assert.Same(model, graph.SelectedConnection);
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, string.Empty);
            window.KeyRelease(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, string.Empty);
            if (graph.RemoveSelectedConnectionCommand.ExecutionTask is Task task)
                await task;
            Layout();
            Assert.Empty(graph.Connections);
            Assert.Null(graph.SelectedConnection);
            Assert.Equal(0, target.Channel.InputControlPin);
        }
        finally
        {
            window.Close();
        }

        void Layout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task RightClickSelectsWireAndOpensItsDeleteMenu()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-wire-menu-{Guid.NewGuid():N}");
        var vm = new MainWindowViewModel(new ChannelCanTxSettingsTests.TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "graph.json"))));
        foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
            ((System.Timers.Timer)typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).Stop();
        IoFlowGraphViewModel graph = vm.IoFlowGraph;
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.DigitalNodes[0].Location = new Point(50, 50);
        graph.OutputNodes[0].Location = new Point(700, 50);
        await graph.ConnectNodes(graph.DigitalNodes[0], graph.OutputNodes[0]);
        IoFlowConnectionViewModel model = Assert.Single(graph.Connections);
        var view = new IoFlowTabView { DataContext = vm };
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            Layout();
            Connection hitTarget = Assert.Single(view.GetVisualDescendants().OfType<Connection>(),
                item => ReferenceEquals(item.DataContext, model) && item.IsHitTestVisible);
            Point midpoint = new((hitTarget.Source.X + hitTarget.Target.X) / 2, (hitTarget.Source.Y + hitTarget.Target.Y) / 2);
            Point cursor = hitTarget.TranslatePoint(midpoint, window)!.Value;
            window.MouseMove(cursor);
            window.MouseDown(cursor, MouseButton.Right);
            window.MouseUp(cursor, MouseButton.Right);
            Layout();
            Assert.Same(model, graph.SelectedConnection);
            Assert.True(hitTarget.ContextMenu!.IsOpen);
            MenuItem delete = Assert.IsType<MenuItem>(hitTarget.ContextMenu.Items[0]);
            Assert.Same(graph.RemoveSelectedConnectionCommand, delete.Command);
            Assert.True(delete.Command!.CanExecute(null));
            delete.Command.Execute(null);
            if (graph.RemoveSelectedConnectionCommand.ExecutionTask is Task task)
                await task;
            Layout();
            Assert.Empty(graph.Connections);
        }
        finally
        {
            window.Close();
        }

        void Layout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
