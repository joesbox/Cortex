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
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class CanTxNodeRenderingTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void MultipleTxNodesDoNotOverlapRenderedNodesInCrowdedViewport(bool periodic)
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.PopulateFromCurrentSettings();
        graph.SetNextNodeSpawnLocation(new Point(50, 50), default, new Size(600, 400));
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        try
        {
            window.Show();
            for (int index = 0; index < 3; index++)
            {
                CustomCanMessage message = settings.CustomCanMessagesStaticData[index];
                message.Id = (uint)(0x123 + index);
                message.Flags = periodic ? (byte)0x0D : (byte)0x05;
                message.TimeoutMs = 1000;
                message.TxTrigger = 0;
                graph.SynchronizeCanTxNodesFromSettings(settings);
                graph.RebuildConnections(settings);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
            }

            Assert.Equal(3, graph.CanTxNodes.Count);
            Assert.Equal(3, graph.Connections.Count(wire => wire.Target.Node is CanTxNodeViewModel));
            AssertNoTxOverlap(view, graph);
            graph.AutoArrangeNodesCommand.Execute(null);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            AssertNoTxOverlap(view, graph);
        }
        finally
        {
            window.Close();
        }
    }

    internal static void AssertNoTxOverlap(IoFlowTabView view, IoFlowGraphViewModel graph)
    {
        ItemContainer[] containers = view.GetVisualDescendants().OfType<ItemContainer>().ToArray();
        foreach (CanTxNodeViewModel node in graph.CanTxNodes)
        {
            ItemContainer container = Assert.Single(containers, item => ReferenceEquals(item.DataContext, node));
            Rect bounds = new(node.Location, container.Bounds.Size);
            Assert.True(bounds.Width > 0 && bounds.Height > 0);
            foreach (ItemContainer other in containers.Where(item => !ReferenceEquals(item, container)))
            {
                var otherNode = Assert.IsAssignableFrom<IoFlowNodeViewModel>(other.DataContext);
                Rect otherBounds = new(otherNode.Location, other.Bounds.Size);
                Assert.False(bounds.Intersects(otherBounds),
                    $"{node.Title} {bounds} overlaps {otherNode.Title} {otherBounds}");
            }
        }
    }

    [AvaloniaFact]
    public void PeriodSliderAndLabelAreHiddenForOneShotAndUseHundredMillisecondSteps()
    {
        var settings = new DataStructures();
        var message = settings.CustomCanMessagesStaticData[0];
        message.Id = 0x123;
        message.Flags = 0x04;
        message.TimeoutMs = 1000;
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.SetCustomCanTxModeAction = (_, periodic, period) =>
        {
            message.IsPeriodic = periodic;
            if (periodic)
                message.TimeoutMs = period;
            return true;
        };
        graph.AddCanTxNodeCommand.Execute(null);
        var node = Assert.Single(graph.CanTxNodes);
        graph.SelectedNodes.Add(node);
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var slider = Assert.Single(view.GetVisualDescendants().OfType<Slider>()
                .Where(control => ReferenceEquals(control.DataContext, node)));
            var label = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>()
                .Where(control => ReferenceEquals(control.DataContext, node) && control.Text == "Period (ms)"));
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(),
                control => ReferenceEquals(control.DataContext, node) && Equals(control.Content, "Apply"));

            Assert.False(slider.IsEffectivelyVisible);
            Assert.False(label.IsEffectivelyVisible);
            Assert.Equal(100, slider.Minimum);
            Assert.Equal(30000, slider.Maximum);
            Assert.Equal(100, slider.TickFrequency);
            Assert.Equal(100, slider.SmallChange);
            Assert.Equal(100, slider.LargeChange);
            Assert.True(slider.IsSnapToTickEnabled);

            node.EditIsPeriodic = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.True(slider.IsEffectivelyVisible);
            Assert.True(label.IsEffectivelyVisible);
            Assert.True(slider.Focus());
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, string.Empty);
            window.KeyRelease(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, string.Empty);
            Assert.Equal(1100, node.EditTimeoutMs);
            window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, string.Empty);
            window.KeyRelease(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, string.Empty);
            Assert.Equal(1000, node.EditTimeoutMs);
            slider.Value = -50;
            Assert.Equal(100, node.EditTimeoutMs);
            slider.Value = 40000;
            Assert.Equal(30000, node.EditTimeoutMs);

            node.EditIsPeriodic = false;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Assert.False(slider.IsEffectivelyVisible);
            Assert.False(label.IsEffectivelyVisible);
            Assert.Equal(30000, message.TimeoutMs);
            Assert.False(message.IsPeriodic);
        }
        finally
        {
            window.Close();
        }
    }
}
