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

public sealed class IoFlowNodeColorTests
{
    [AvaloniaFact]
    public void NodesSharePropertyPanelSurfaceAndHaveDistinctBordersEvenWhenSelected()
    {
        var settings = new DataStructures();
        var channel = new OutputChannel { ChannelNumber = 1 };
        var message = new CustomCanMessage { Index = 0, Id = 0x123, Flags = 0x04 };
        IoFlowNodeViewModel[] nodes =
        [
            new DigitalInputNodeViewModel(new DigitalInput(1, true), default),
            new AnalogueInputNodeViewModel(settings.AnalogueInputsStaticData[0], default),
            new CanInputNodeViewModel(default),
            new CanTxNodeViewModel(message, default, [message], null),
            new IgnitionOnNodeViewModel(default),
            new OutputNodeViewModel(channel, default, [], [channel]),
            new MathSourceNodeViewModel(OutputChannel.MathOperandType.CanSignal, 0, "CAN RX", default),
            new MathSourceNodeViewModel(OutputChannel.MathOperandType.SystemValue, 0, "System value", default),
            new MathRuleNodeViewModel(1, default),
        ];
        var graph = new IoFlowGraphViewModel();
        foreach (var node in nodes)
            graph.Nodes.Add(node);

        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
        var window = new Window { Width = 1600, Height = 1000, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            var containers = nodes.Select(node => Assert.Single(editor.GetVisualDescendants()
                .OfType<ItemContainer>(), item => ReferenceEquals(item.DataContext, node))).ToArray();
            var borders = containers.Select(container => Assert.Single(container.GetVisualDescendants()
                .OfType<Border>(), border => border.Classes.Contains("ioFlowNode"))).ToArray();
            var backgrounds = borders.Select(border => border.Background).ToArray();
            Color[] borderColors = borders.Select(border => Assert.IsAssignableFrom<ISolidColorBrush>(border.BorderBrush).Color).ToArray();
            Assert.All(borders, border => Assert.Equal(Color.Parse("#FF221C34"),
                Assert.IsAssignableFrom<ISolidColorBrush>(border.Background).Color));
            for (int i = 0; i < borderColors.Length; i++)
                for (int j = i + 1; j < borderColors.Length; j++)
                {
                    Color a = borderColors[i];
                    Color b = borderColors[j];
                    double distance = ColorDistance(a, b);
                    Assert.True(distance >= 55, $"{nodes[i].Title} and {nodes[j].Title} borders are too similar: {distance:F1}");
                }

            AssertPinColor(0, ConnectorPinRole.DigitalInput);
            AssertPinColor(1, ConnectorPinRole.AnalogueInput);
            AssertPinColor(2, ConnectorPinRole.CanHigh);
            AssertPinColor(4, ConnectorPinRole.Ignition);
            using (var outputPin = new ConnectorPinDisplayItem(ConnectorPinCatalog.Pins.First(pin => pin.Role == ConnectorPinRole.Output)))
            {
                outputPin.AttachSources(channel, null, null, null, null, null);
                foreach (OutputChannel.ChannelType type in Enum.GetValues<OutputChannel.ChannelType>())
                {
                    channel.ChanType = type;
                    window.UpdateLayout();
                    Dispatcher.UIThread.RunJobs();
                    Assert.Equal(Assert.IsType<SolidColorBrush>(outputPin.PinBrush).Color,
                        Assert.IsAssignableFrom<ISolidColorBrush>(borders[5].BorderBrush).Color);
                    Color outputColor = Assert.IsAssignableFrom<ISolidColorBrush>(borders[5].BorderBrush).Color;
                    Assert.Equal(Color.Parse("#68C579"), outputColor);
                    for (int i = 0; i < borderColors.Length; i++)
                    {
                        if (i == 5) continue;
                        Assert.True(ColorDistance(outputColor, borderColors[i]) >= 50,
                            $"{type} output and {nodes[i].Title} borders are too similar");
                    }
                }
            }
            channel.ChanType = OutputChannel.ChannelType.Digital;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            foreach (var container in containers)
                container.IsSelected = true;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();

            for (int i = 0; i < borders.Length; i++)
            {
                Assert.Equal(Color.Parse("#FF221C34"),
                    Assert.IsAssignableFrom<ISolidColorBrush>(borders[i].Background).Color);
                Assert.Equal(borderColors[i], Assert.IsAssignableFrom<ISolidColorBrush>(borders[i].BorderBrush).Color);
                Assert.Equal(2, borders[i].BoxShadow.Count);
                Assert.Equal(Color.FromArgb(255, borderColors[i].R, borderColors[i].G, borderColors[i].B), borders[i].BoxShadow[0].Color);
                Assert.Equal(Color.FromArgb(170, borderColors[i].R, borderColors[i].G, borderColors[i].B), borders[i].BoxShadow[1].Color);
            }

            foreach (OutputChannel.ChannelType type in Enum.GetValues<OutputChannel.ChannelType>())
            {
                channel.ChanType = type;
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                Color color = Assert.IsAssignableFrom<ISolidColorBrush>(borders[5].BorderBrush).Color;
                Assert.Equal(Color.FromArgb(255, color.R, color.G, color.B), borders[5].BoxShadow[0].Color);
                Assert.Equal(Color.FromArgb(170, color.R, color.G, color.B), borders[5].BoxShadow[1].Color);
            }

            foreach (var container in containers)
                container.IsSelected = false;
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            for (int i = 0; i < borders.Length; i++)
                Assert.Same(backgrounds[i], borders[i].Background);

            void AssertPinColor(int index, ConnectorPinRole role)
            {
                using var pin = new ConnectorPinDisplayItem(ConnectorPinCatalog.Pins.First(item => item.Role == role));
                Assert.Equal(Assert.IsType<SolidColorBrush>(pin.PinBrush).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(borders[index].BorderBrush).Color);
            }

            static double ColorDistance(Color a, Color b) =>
                Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));
        }
        finally
        {
            window.Close();
        }
    }
}
