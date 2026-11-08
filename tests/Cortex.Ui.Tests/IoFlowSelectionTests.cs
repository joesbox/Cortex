using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Cortex.Views;
using Nodify.Avalonia;
using Xunit;

namespace Cortex.Ui.Tests;

public class IoFlowSelectionTests
{
    [AvaloniaFact]
    public void Selection_StaysProminentAcrossZoomLevelsWithoutChangingNodeLayout()
    {
        var graph = new IoFlowGraphViewModel();
        int graphChangedNotifications = 0;
        graph.GraphChangedAction = _ => graphChangedNotifications++;
        var channel = new OutputChannel { ChannelNumber = 1 };
        IoFlowNodeViewModel[] nodes =
        [
            new DigitalInputNodeViewModel(new DigitalInput(1, true), new Point(50, 50)),
            new OutputNodeViewModel(channel, new Point(550, 50), [], [channel]),
            new MathRuleNodeViewModel(1, new Point(1200, 50)),
        ];
        foreach (var node in nodes)
        {
            graph.Nodes.Add(node);
            node.OutputConnector.IsConnected = true;
        }

        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
        var window = new Window { Width = 1600, Height = 1000, Content = view };

        try
        {
            window.Show();
            UpdateLayout();

            var containers = nodes.Select(node => Assert.Single(editor.GetVisualDescendants()
                .OfType<ItemContainer>().Where(item => ReferenceEquals(item.DataContext, node)))).ToArray();
            var borders = containers.Select(container => Assert.Single(container.GetVisualDescendants()
                .OfType<Border>().Where(border => border.Classes.Contains("ioFlowNode")))).ToArray();
            var originalBounds = borders.Select(border => border.Bounds).ToArray();
            var originalLocations = nodes.Select(node => node.Location).ToArray();
            var originalBackgrounds = borders.Select(border => border.Background).ToArray();
            var originalShadows = borders.Select(border => border.BoxShadow).ToArray();
            var originalBorderBrushes = borders.Select(border => border.BorderBrush).ToArray();
            var resultPanel = Assert.Single(borders[2].GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("mathRuleResult"));
            var originalResultBackground = resultPanel.Background;
            var originalResultBorder = resultPanel.BorderBrush;
            var originalResultBounds = resultPanel.Bounds;

            foreach (double zoom in new[] { 1.0, 0.25, 0.1, 0.5, 2.0 })
            {
                editor.ViewportZoom = zoom;
                UpdateLayout();
                graphChangedNotifications = 0;

                var screenPositions = borders.Select(border => border.TranslatePoint(default, editor)).ToArray();
                var containerBounds = containers.Select(container => container.Bounds).ToArray();
                var connectorAnchors = nodes.Select(node => node.OutputConnector.Anchor).ToArray();

                foreach (var container in containers)
                {
                    container.IsSelected = true;
                }
                UpdateLayout();

                for (int index = 0; index < borders.Length; index++)
                {
                    var border = borders[index];
                    Assert.False(containers[index].ClipToBounds);
                    Assert.Equal(2, border.BoxShadow.Count);
                    var outline = border.BoxShadow[0];
                    Assert.Equal(255, outline.Color.A);
                    Assert.Equal(16, outline.Blur * zoom, 6);
                    Assert.Equal(0, outline.Spread);
                    Assert.Equal(36, border.BoxShadow[1].Blur * zoom, 6);
                    Assert.Equal(0, border.BoxShadow[1].Spread);
                    Assert.Equal(new CornerRadius(8), border.CornerRadius);
                    Color borderColor = Assert.IsAssignableFrom<ISolidColorBrush>(border.BorderBrush).Color;
                    Assert.Equal(Color.FromArgb(255, borderColor.R, borderColor.G, borderColor.B), outline.Color);
                    Assert.Equal(Color.FromArgb(170, borderColor.R, borderColor.G, borderColor.B), border.BoxShadow[1].Color);
                    Assert.Same(originalBackgrounds[index], border.Background);
                    Assert.Equal(originalBounds[index], border.Bounds);
                    Assert.Equal(originalLocations[index], nodes[index].Location);
                    Assert.Equal(originalBorderBrushes[index], border.BorderBrush);
                    Assert.Equal(screenPositions[index], border.TranslatePoint(default, editor));
                    Assert.Equal(containerBounds[index], containers[index].Bounds);
                    Assert.Equal(connectorAnchors[index], nodes[index].OutputConnector.Anchor);
                }
                Assert.Same(originalResultBackground, resultPanel.Background);
                Assert.Same(originalResultBorder, resultPanel.BorderBrush);
                Assert.Equal(originalResultBounds, resultPanel.Bounds);

                foreach (var container in containers)
                {
                    container.IsSelected = false;
                }
                Assert.Same(originalResultBackground, resultPanel.Background);
                Assert.Same(originalResultBorder, resultPanel.BorderBrush);
                UpdateLayout();

                for (int index = 0; index < borders.Length; index++)
                {
                    Assert.Same(originalBackgrounds[index], borders[index].Background);
                    Assert.Equal(originalShadows[index], borders[index].BoxShadow);
                    Assert.Equal(originalBounds[index], borders[index].Bounds);
                    Assert.Equal(screenPositions[index], borders[index].TranslatePoint(default, editor));
                    Assert.Equal(containerBounds[index], containers[index].Bounds);
                    Assert.Equal(connectorAnchors[index], nodes[index].OutputConnector.Anchor);
                }
                Assert.Equal(0, graphChangedNotifications);
            }
            Assert.Equal(0, graphChangedNotifications);
        }
        finally
        {
            window.Close();
        }

        void UpdateLayout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public async Task OutputGuidanceSupportsCanAndMathWithRuleDrivenPwmMode()
    {
        DataStructures settings = new();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        OutputNodeViewModel output = graph.OutputNodes[0];
        output.Channel.Enabled = 1;
        var view = new IoFlowTabView
        {
            DataContext = new { IoFlowGraph = graph, MathOutputModes = new[] { "Digital", "PWM" } },
        };
        var window = new Window { Width = 1200, Height = 1000, Content = view };
        try
        {
            window.Show();
            UpdateLayout();
            Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Classes.Contains("outputControlHint"));
            Border outputBorder = Assert.Single(view.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("ioFlowOutputNode"));
            Assert.DoesNotContain(outputBorder.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("effect", StringComparison.OrdinalIgnoreCase) == true
                    || text.Text?.Contains("root", StringComparison.OrdinalIgnoreCase) == true);
            Assert.DoesNotContain(outputBorder.GetVisualDescendants().OfType<Button>(),
                button => button.Classes.Contains("addOutputMathRule"));
            Assert.DoesNotContain(outputBorder.GetVisualDescendants().OfType<ComboBox>(),
                box => box.Classes.Contains("outputChannelType"));

            output.Channel.ChanType = OutputChannel.ChannelType.CAN;
            UpdateLayout();

            await graph.ConnectNodes(graph.MathRuleNodes.Single(), output);
            UpdateLayout();
            Assert.True(output.IsMathChannel);
            ComboBox mode = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(),
                box => box.Classes.Contains("mathOutputMode"));
            Assert.True(mode.IsEffectivelyVisible);
            Assert.Equal(0, mode.SelectedIndex);
            mode.SelectedIndex = 1;
            UpdateLayout();
            Assert.Equal((byte)OutputChannel.MathOutputModeType.PWM, output.Channel.MathOutputMode);
            output.Channel.MathOutputMode = (byte)OutputChannel.MathOutputModeType.Digital;
            UpdateLayout();
            Assert.Equal(0, mode.SelectedIndex);
            Assert.False(output.ShowDutyConfiguration);
            Assert.DoesNotContain(graph.Connections, wire => wire.Label.Contains("root", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            window.Close();
        }

        void UpdateLayout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
