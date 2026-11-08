using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Ellipse = Avalonia.Controls.Shapes.Ellipse;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using Cortex.ViewModels.IoFlow;
using Cortex.Views;
using Nodify.Avalonia;
using System.Reflection;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class IoFlowCanvasRenderingTests
{
    [AvaloniaFact]
    public void CanvasPanAndZoomPersistAndRestoreInNewView()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-viewport-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "graph.json");
        var vm = CreateViewModel(path);
        var graph = vm.IoFlowGraph;
        var view = new IoFlowTabView { DataContext = vm };
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            Layout(window);
            var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
            var first = graph.SelectedCanvas;
            Point cursor = editor.TranslatePoint(new Point(300, 200), window)!.Value;
            window.MouseMove(cursor);
            window.MouseWheel(cursor, new Vector(0, -1));
            Layout(window);
            Assert.NotEqual(1, editor.ViewportZoom);
            Assert.Equal(editor.ViewportZoom, first.ViewportZoom);
            Assert.Equal(editor.ViewportLocation, first.ViewportLocation);
            editor.SetCurrentValue(NodifyEditor.ViewportZoomProperty, 0.4);
            editor.SetCurrentValue(NodifyEditor.ViewportLocationProperty, new Point(750, 220));
            Layout(window);
            Assert.Equal(0.4, first.ViewportZoom);
            Assert.Equal(new Point(750, 220), first.ViewportLocation);
            graph.AddCanvasCommand.Execute(null);
            Layout(window);
            Assert.Equal(1, editor.ViewportZoom);
            Assert.Equal(default, editor.ViewportLocation);
            editor.SetCurrentValue(NodifyEditor.ViewportZoomProperty, 0.7);
            editor.SetCurrentValue(NodifyEditor.ViewportLocationProperty, new Point(-120, 430));
            Layout(window);
            graph.SelectedCanvas = first;
            Layout(window);
            Assert.Equal(0.4, editor.ViewportZoom);
            Assert.Equal(new Point(750, 220), editor.ViewportLocation);
            Assert.True(new IoFlowGraphStore(path).TryLoad(out var snapshot));
            window.Close();
            var restored = new IoFlowGraphViewModel();
            restored.Build(new DataStructures(), new DataStructures());
            restored.ApplySnapshot(snapshot);
            var restoredView = new IoFlowTabView { DataContext = new { IoFlowGraph = restored } };
            var reopened = new Window { Width = 1600, Height = 1000, Content = restoredView };
            try
            {
                reopened.Show();
                Layout(reopened);
                var restoredEditor = restoredView.FindControl<NodifyEditor>("IoFlowEditor")!;
                Assert.Equal(0.4, restoredEditor.ViewportZoom);
                Assert.Equal(new Point(750, 220), restoredEditor.ViewportLocation);
                restored.SelectedCanvas = restored.Canvases[1];
                Layout(reopened);
                Assert.Equal(0.7, restoredEditor.ViewportZoom);
                Assert.Equal(new Point(-120, 430), restoredEditor.ViewportLocation);
            }
            finally
            {
                reopened.Close();
            }
        }
        finally
        {
            window.Close();
            CleanUp(path, directory);
        }
    }

    [AvaloniaFact]
    public void TabDeleteTargetsClickedCanvasAndWarnsWhenNonempty()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        var occupied = graph.SelectedCanvas;
        graph.AddCanvasCommand.Execute(null);
        var empty = graph.SelectedCanvas;
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            Layout(window);
            void DeleteTab(IoFlowCanvasViewModel canvas)
            {
                var header = Assert.Single(view.GetVisualDescendants().OfType<Grid>(),
                    item => item.Classes.Contains("canvasTabHeader") && item.DataContext == canvas);
                Point cursor = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
                window.MouseMove(cursor);
                window.MouseDown(cursor, MouseButton.Right);
                window.MouseUp(cursor, MouseButton.Right);
                Layout(window);
                Assert.True(header.ContextMenu!.IsOpen);
                var delete = Assert.IsType<MenuItem>(header.ContextMenu.Items[1]);
                Assert.Same(canvas, delete.CommandParameter);
                Assert.Same(graph.DeleteCanvasTabCommand, delete.Command);
                delete.Command!.Execute(delete.CommandParameter);
                header.ContextMenu.Close();
                Layout(window);
            }
            DeleteTab(occupied);
            Assert.Equal(2, graph.Canvases.Count);
            Assert.Contains("not empty", graph.AddNodeWarning);
            Assert.Single(graph.Nodes);
            graph.SelectedCanvas = occupied;
            DeleteTab(empty);
            Assert.Same(occupied, Assert.Single(graph.Canvases));
            Assert.Same(occupied, graph.SelectedCanvas);
            Assert.False(graph.HasAddNodeWarning);
            graph.Nodes.Clear();
            DeleteTab(occupied);
            Assert.Single(graph.Canvases);
            Assert.Contains("At least one", graph.AddNodeWarning);
            var tab = Assert.Single(view.GetVisualDescendants().OfType<TabStripItem>());
            Assert.Equal(14, tab.FontSize);
            Assert.Equal(new Thickness(12, 5), tab.Padding);
            Assert.Equal(new CornerRadius(6, 6, 0, 0), tab.CornerRadius);
            Assert.Equal(Color.Parse("#FF1B1E24"), Assert.IsAssignableFrom<ISolidColorBrush>(tab.Background).Color);
            var frame = view.FindControl<Border>("IoFlowCanvasFrame")!;
            Assert.True(frame.IsVisualAncestorOf(tab));
            var surface = view.FindControl<Border>("IoFlowSurfaceBorder")!;
            Assert.Equal(Color.Parse("#FF1B1E24"), Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color);
            Assert.Equal(Math.Round(surface.TranslatePoint(default, view)!.Value.Y),
                Math.Round(tab.TranslatePoint(new Point(0, tab.Bounds.Height - 1), view)!.Value.Y));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CanvasReconciliationDoesNotEmitRoutineConnectionWarnings()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-canvas-warning-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "graph.json");
        var vm = CreateViewModel(path);
        try
        {
            var graph = vm.IoFlowGraph;
            graph.AddNextDigitalNodeCommand.Execute(null);
            graph.AddNextOutputNodeCommand.Execute(null);
            graph.AddCanvasCommand.Execute(null);
            graph.SelectedCanvas = graph.Canvases[0];
            Dispatcher.UIThread.RunJobs();
            int before = LoggingService.LogEntries.Count(entry => entry.Contains("connected nodes were kept together"));
            await graph.ConnectNodes(graph.DigitalNodes[0], graph.OutputNodes[0]);
            var snapshot = graph.CreateSnapshot();
            snapshot.Nodes.Single(node => node.Kind == "Digital").CanvasId = graph.Canvases[1].Id;
            graph.ApplyNodeLocations(snapshot);
            graph.RebuildConnections(vm.SettingsDataView);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(graph.OutputNodes[0].CanvasId, graph.DigitalNodes[0].CanvasId);
            Assert.Equal(before, LoggingService.LogEntries.Count(entry => entry.Contains("connected nodes were kept together")));
        }
        finally
        {
            CleanUp(path, directory);
        }
    }

    [AvaloniaFact]
    public void TabContextMenuRenamesInPlaceAndEscapeCancels()
    {
        var graph = new IoFlowGraphViewModel();
        graph.AddCanvasCommand.Execute(null);
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            Layout(window);
            var tabs = view.FindControl<TabStrip>("IoFlowCanvasTabs")!;
            var header = Assert.Single(tabs.GetVisualDescendants().OfType<Grid>(),
                item => item.Classes.Contains("canvasTabHeader") && item.DataContext == graph.Canvases[0]);
            Assert.Equal(2000, ToolTip.GetShowDelay(header));
            Assert.Contains("Right-click", Assert.IsType<string>(ToolTip.GetTip(header)));
            var editor = Assert.Single(header.Children.OfType<TextBox>());
            void StartRename()
            {
                Point cursor = header.TranslatePoint(new Point(header.Bounds.Width / 2, header.Bounds.Height / 2), window)!.Value;
                window.MouseMove(cursor);
                window.MouseDown(cursor, MouseButton.Right);
                window.MouseUp(cursor, MouseButton.Right);
                Layout(window);
                var menu = header.ContextMenu!;
                Assert.True(menu.IsOpen);
                var rename = Assert.IsType<MenuItem>(menu.Items[0]);
                Assert.Same(editor, rename.Tag);
                rename.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
                menu.Close();
                Layout(window);
                Assert.True(editor.IsVisible);
            }
            StartRename();
            editor.Text = "Lights";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, string.Empty);
            window.KeyRelease(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, string.Empty);
            Layout(window);
            Assert.Equal("Lights", graph.Canvases[0].Name);
            Assert.False(editor.IsVisible);
            Assert.Equal("Canvas 2", graph.Canvases[1].Name);
            StartRename();
            editor.Text = "Cancelled";
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, string.Empty);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, string.Empty);
            Layout(window);
            Assert.Equal("Lights", graph.Canvases[0].Name);
            StartRename();
            editor.Text = "Accessories";
            view.FindControl<Button>("IoFlowAddCanvasTab")!.Focus();
            Layout(window);
            Assert.Equal("Accessories", graph.Canvases[0].Name);
            Assert.False(editor.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void HoveredTabMergesWithCanvasWithoutSelectionBar()
    {
        var graph = new IoFlowGraphViewModel();
        graph.AddCanvasCommand.Execute(null);
        graph.SelectedCanvas = graph.Canvases[0];
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 900, Height = 520, Content = view };
        try
        {
            window.Show();
            Layout(window);
            var tabs = view.GetVisualDescendants().OfType<TabStripItem>().ToArray();
            var hovered = tabs.Single(tab => ReferenceEquals(tab.DataContext, graph.Canvases[1]));
            Point centre = hovered.TranslatePoint(new Point(hovered.Bounds.Width / 2, hovered.Bounds.Height / 2), window)!.Value;
            window.MouseMove(centre);
            Layout(window);

            var root = hovered.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_LayoutRoot");
            var pipe = hovered.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_SelectedPipe");
            Assert.False(hovered.IsSelected);
            Assert.Equal(Color.Parse("#FF1B1E24"), Assert.IsAssignableFrom<ISolidColorBrush>(root.Background).Color);
            Assert.Equal(Color.Parse("#443553"), Assert.IsAssignableFrom<ISolidColorBrush>(root.BorderBrush).Color);
            Assert.Equal(Color.Parse("#FFF0E7FF"), Assert.IsAssignableFrom<ISolidColorBrush>(TextElement.GetForeground(root)).Color);
            Assert.False(pipe.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void PlusTabAddsAndSelectsCanvasAndDisablesAtLimit()
    {
        var graph = new IoFlowGraphViewModel();
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            Layout(window);
            var tabs = view.FindControl<TabStrip>("IoFlowCanvasTabs")!;
            var add = view.FindControl<Button>("IoFlowAddCanvasTab")!;
            Point click = add.TranslatePoint(new Point(add.Bounds.Width / 2, add.Bounds.Height / 2), window)!.Value;
            window.MouseMove(click);
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            Layout(window);
            Assert.Equal(2, tabs.ItemCount);
            Assert.Same(graph.Canvases[1], tabs.SelectedItem);
            graph.SelectedCanvas.Name = "Lighting";
            Layout(window);
            Assert.Contains(tabs.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Lighting");
            tabs.SelectedItem = graph.Canvases[0];
            Assert.Same(graph.Canvases[0], graph.SelectedCanvas);
            for (int count = graph.Canvases.Count; count < 20; count++)
                graph.AddCanvasCommand.Execute(null);
            Layout(window);
            Assert.Equal(20, tabs.ItemCount);
            Assert.False(add.IsEnabled);
            graph.DeleteCanvasCommand.Execute(null);
            Layout(window);
            Assert.Equal(19, tabs.ItemCount);
            Assert.True(add.IsEnabled);
            Assert.Same(graph.SelectedCanvas, tabs.SelectedItem);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void ActiveIndicatorLivesInOutputHeader()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            Layout(window);
            var led = Assert.Single(view.GetVisualDescendants().OfType<Ellipse>(), item => item.Classes.Contains("outputActiveLed"));
            var header = led.GetVisualAncestors().OfType<Grid>().First();
            Assert.Contains(header.GetVisualDescendants().OfType<TextBlock>(), item => item.Text == graph.OutputNodes[0].Title);
            Assert.Contains(header.GetVisualDescendants().OfType<TextBlock>(), item => item.Text == "Active:");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RightClickMenuMovesSelectionAndCanvasSelectorShowsOnlyDestinationNodes(bool selectWholeGroup)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-canvas-ui-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "graph.json");
        var vm = CreateViewModel(path);
        var graph = vm.IoFlowGraph;
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.DigitalNodes[0].Location = new Point(60, 60);
        graph.OutputNodes[0].Location = new Point(700, 60);
        await graph.ConnectNodes(graph.DigitalNodes[0], graph.OutputNodes[0]);
        var original = graph.SelectedCanvas;
        graph.AddCanvasCommand.Execute(null);
        var destination = graph.SelectedCanvas;
        graph.SelectedCanvas = original;
        var view = new IoFlowTabView { DataContext = vm };
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            Layout(window);
            var source = graph.DigitalNodes[0];
            if (selectWholeGroup)
                foreach (var node in graph.VisibleNodes)
                    graph.SelectedNodes.Add(node);
            Layout(window);
            var container = Assert.Single(view.GetVisualDescendants().OfType<ItemContainer>(), item => item.DataContext == source);
            Point click = container.TranslatePoint(new Point(50, 25), window)!.Value;
            window.MouseMove(click);
            window.MouseDown(click, MouseButton.Right);
            window.MouseUp(click, MouseButton.Right);
            Layout(window);
            Assert.Contains(source, graph.SelectedNodes);
            var menu = view.FindControl<MenuItem>("IoFlowMoveToCanvasMenu")!;
            Assert.NotNull(menu);
            var entry = Assert.Single(menu.Items.OfType<MenuItem>(), item => item.CommandParameter == destination);
            Assert.True(entry.IsEnabled);
            bool confirmed = false;
            graph.ConfirmAction = (_, _, _, _) =>
            {
                confirmed = true;
                return Task.FromResult(true);
            };
            await Assert.IsAssignableFrom<IAsyncRelayCommand>(entry.Command).ExecuteAsync(entry.CommandParameter);
            Layout(window);
            Assert.Equal(!selectWholeGroup, confirmed);
            Assert.Empty(view.GetVisualDescendants().OfType<ItemContainer>());
            view.FindControl<TabStrip>("IoFlowCanvasTabs")!.SelectedItem = destination;
            Layout(window);
            Assert.Same(destination, graph.SelectedCanvas);
            Assert.Equal(2, view.GetVisualDescendants().OfType<ItemContainer>().Count());
            Assert.Single(graph.VisibleConnections);
        }
        finally
        {
            window.Close();
            CleanUp(path, directory);
        }
    }

    [AvaloniaFact]
    public async Task ConfigurationSaveAndLoadPreservesCanvasLayout()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-canvas-config-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "graph.json");
        var vm = CreateViewModel(path);
        var graph = vm.IoFlowGraph;
        try
        {
            graph.AddNextOutputNodeCommand.Execute(null);
            var output = graph.OutputNodes[0];
            output.Location = new Point(135, 246);
            var original = graph.SelectedCanvas;
            graph.AddCanvasCommand.Execute(null);
            var destination = graph.SelectedCanvas;
            destination.Name = "Lights";
            destination.ViewportLocation = new Point(123, 456);
            destination.ViewportZoom = 0.5;
            graph.AddCanvasCommand.Execute(null);
            var empty = graph.SelectedCanvas;
            graph.SelectedCanvas = original;
            graph.SelectedNodes.Add(output);
            await graph.MoveSelectedNodesToCanvasCommand.ExecuteAsync(destination);
            graph.SelectedCanvas = empty;
            var configGraph = Assert.IsType<IoFlowGraphConfigSnapshot>(typeof(MainWindowViewModel)
                .GetMethod("CreateIoFlowConfigSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(vm, null));
            string json = ConfigFileSerializer.SerializeSettings(vm.SettingsDataView, configGraph);
            Assert.True(ConfigFileSerializer.TryDeserialize(json, out var config, out var error), error);
            graph.Build(vm.SettingsDataView, vm.LiveDataView);
            ConfigFileSerializer.ApplySnapshot(vm.SettingsDataView, config!);
            var snapshot = Assert.IsType<IoFlowGraphSnapshot>(typeof(MainWindowViewModel)
                .GetMethod("ConvertIoFlowConfigSnapshot", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [config!.IoFlowGraph]));
            typeof(MainWindowViewModel).GetMethod("ApplyIoFlowSnapshotFromConfig", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(vm, [snapshot]);
            Assert.Equal(3, graph.Canvases.Count);
            Assert.Equal("Lights", graph.Canvases[1].Name);
            Assert.Equal(new Point(123, 456), graph.Canvases[1].ViewportLocation);
            Assert.Equal(0.5, graph.Canvases[1].ViewportZoom);
            Assert.Equal(empty.Id, graph.SelectedCanvas.Id);
            Assert.Empty(graph.VisibleNodes);
            Assert.Equal(destination.Id, Assert.Single(graph.OutputNodes).CanvasId);
            Assert.Equal(output.Location, graph.OutputNodes[0].Location);
            Assert.True(new IoFlowGraphStore(path).TryLoad(out var persisted));
            Assert.Equal(3, persisted.Canvases.Count);
        }
        finally
        {
            CleanUp(path, directory);
        }
    }

    private static MainWindowViewModel CreateViewModel(string path)
    {
        var vm = new MainWindowViewModel(new ChannelCanTxSettingsTests.TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: new IoFlowGraphStore(path)));
        foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
            ((System.Timers.Timer)typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).Stop();
        vm.IoFlowGraph.Build(vm.SettingsDataView, vm.LiveDataView);
        return vm;
    }

    private static void Layout(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void CleanUp(string path, string directory)
    {
        if (File.Exists(path)) File.Delete(path);
        if (Directory.Exists(directory)) Directory.Delete(directory);
    }
}
