using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Cortex.Views;
using Nodify.Avalonia;
using Nodify.Avalonia.Connections;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class IoFlowWireDragTests
{
    [AvaloniaFact]
    public async Task ConnectorsStillCreateWiresWithPointerGestures()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        var source = graph.DigitalNodes[0];
        var target = graph.OutputNodes[0];
        source.Location = new Point(50, 50);
        target.Location = new Point(550, 50);
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            Layout();
            Connector sourceConnector = Assert.Single(view.GetVisualDescendants().OfType<Connector>(),
                item => ReferenceEquals(item.DataContext, source.OutputConnector));
            Connector targetConnector = Assert.Single(view.GetVisualDescendants().OfType<Connector>(),
                item => ReferenceEquals(item.DataContext, target.InputConnector));
            Point from = sourceConnector.TranslatePoint(new Point(sourceConnector.Bounds.Width / 2, sourceConnector.Bounds.Height / 2), window)!.Value;
            Point to = targetConnector.TranslatePoint(new Point(targetConnector.Bounds.Width / 2, targetConnector.Bounds.Height / 2), window)!.Value;
            window.MouseMove(from);
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(from + new Vector(30, 0));
            window.MouseMove(to);
            window.MouseUp(to, MouseButton.Left);
            if (graph.ConnectionCompletedCommand.ExecutionTask is Task task)
                await task;
            Layout();
            IoFlowConnectionViewModel wire = Assert.Single(graph.Connections);
            Assert.Same(source.OutputConnector, wire.Source);
            Assert.Same(target.InputConnector, wire.Target);
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

    [AvaloniaTheory]
    [InlineData(1.0, false)]
    [InlineData(0.6, false)]
    [InlineData(1.0, true)]
    [InlineData(0.6, true)]
    public void WireTracksNodeThroughoutDragAndLiveUpdates(double zoom, bool dragTarget)
    {
        var graph = new IoFlowGraphViewModel();
        var source = new DigitalInputNodeViewModel(new DigitalInput(1, true), new Point(50, 50));
        var target = new OutputNodeViewModel(new OutputChannel { ChannelNumber = 1 }, new Point(550, 50), [], []);
        graph.Nodes.Add(source);
        graph.Nodes.Add(target);
        source.OutputConnector.IsConnected = true;
        target.InputConnector.IsConnected = true;
        var model = new IoFlowConnectionViewModel(source.OutputConnector, target.InputConnector, "Test");
        graph.Connections.Add(model);
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            editor.ViewportZoom = zoom;
            Layout();
            IoFlowNodeViewModel node = dragTarget ? target : source;
            IoFlowConnectorEndpoint endpoint = dragTarget ? target.InputConnector : source.OutputConnector;
            ItemContainer container = Assert.Single(editor.GetVisualDescendants().OfType<ItemContainer>(),
                item => ReferenceEquals(item.DataContext, node));
            Connector connector = Assert.Single(container.GetVisualDescendants().OfType<Connector>(),
                item => ReferenceEquals(item.DataContext, endpoint));
            Assert.Equal(10, connector.Width);
            Assert.Equal(24, connector.Height);
            Connection wire = Assert.Single(view.GetVisualDescendants().OfType<Connection>(),
                item => ReferenceEquals(item.DataContext, model) && !item.IsHitTestVisible && item.StrokeThickness == 4);
            List<Point> changes = [];
            endpoint.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(endpoint.Anchor))
                    changes.Add(endpoint.Anchor);
            };
            Point start = container.TranslatePoint(new Point(40, 20), window)!.Value;
            window.MouseMove(start);
            window.MouseDown(start, MouseButton.Left);
            for (int step = 1; step <= 8; step++)
            {
                window.MouseMove(start + new Vector(step * 15, step * 8));
                source.UpdateLiveState(step % 2 == 0 ? new DigitalInput(1, true) : null);
                container.Width = 600 + step * 10;
                Layout();
                Point center = connector.TranslatePoint(new Point(connector.Bounds.Width / 2, connector.Bounds.Height / 2), editor)!.Value;
                Point wirePoint = wire.TranslatePoint(dragTarget ? wire.Target : wire.Source, editor)!.Value;
                Assert.Equal(center.X, wirePoint.X, 2);
                Assert.Equal(center.Y, wirePoint.Y, 2);
            }
            window.MouseUp(start + new Vector(120, 64), MouseButton.Left);
            Layout();
            Assert.NotEmpty(changes);
            for (int i = 1; i < changes.Count; i++)
                Assert.True(changes[i].X >= changes[i - 1].X - 0.01,
                    $"Anchor moved backwards from {changes[i - 1]} to {changes[i]}");
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
