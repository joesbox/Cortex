using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cortex.Controls;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Cortex.Views;
using Nodify.Avalonia;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class IoFlowPropertiesPanelTests
{
    [AvaloniaFact]
    public void OutputNameAlignsWithChannelAndCategoryEditors()
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.SelectedNodes.Add(Assert.Single(graph.OutputNodes));
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        try
        {
            window.Show();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var name = Assert.Single(view.GetVisualDescendants().OfType<TextBox>(),
                control => control.MaxLength == 3 && control.Width == 100);
            var grid = Assert.IsType<Grid>(name.Parent);
            var selectors = grid.Children.OfType<ComboBox>().ToArray();
            Assert.Equal(2, selectors.Length);
            Assert.All(selectors, selector => Assert.Equal(selector.Bounds.Left, name.Bounds.Left, 2));
            Assert.Equal(100, name.Bounds.Width);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(AnalogueInput.AnalogueChannelType.RawVoltage, 0, 5, 5, 116)]
    [InlineData(AnalogueInput.AnalogueChannelType.Digital, 0, 5, 5, 116)]
    [InlineData(AnalogueInput.AnalogueChannelType.RawVoltage, 1, 2, 71.6, 93.8)]
    [InlineData(AnalogueInput.AnalogueChannelType.Active, 20, 80, 14.25, 106.75)]
    [InlineData(AnalogueInput.AnalogueChannelType.NTC, 20, 80, 14.25, 106.75)]
    public void AnalogueChartUsesConnectorRangeWithoutRestrictingEngineeringValues(
        AnalogueInput.AnalogueChannelType mode, double low, double high, double top, double bottom)
    {
        var input = new AnalogueInput(1, false, false) { ChanType = mode };
        var node = new AnalogueInputNodeViewModel(input, default);
        TimeSpan now = IoFlowLiveHistory.Now;
        node.LiveHistory.Record(now - TimeSpan.FromMilliseconds(400), low);
        node.LiveHistory.Record(now - TimeSpan.FromMilliseconds(200), high);
        var chart = new IoFlowMiniChart { DataContext = node };
        chart.Measure(new Size(260, 140));
        chart.Arrange(new Rect(0, 0, 260, 140));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            chart.Render(context);
        var trace = Assert.Single(Flatten(drawing).OfType<GeometryDrawing>(), item =>
            item.Pen?.Brush is ISolidColorBrush brush && brush.Color == Color.Parse("#52E08A"));
        Assert.Equal(top, trace.Geometry!.Bounds.Top, 3);
        Assert.Equal(bottom, trace.Geometry.Bounds.Bottom, 3);

        static IEnumerable<Drawing> Flatten(DrawingGroup group) =>
            group.Children.SelectMany(item => item is DrawingGroup nested ? Flatten(nested) : [item]);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void GreenPathKeepsMissingReadingsAsGapsWithoutMarkingFlatSamples(bool digital)
    {
        IoFlowNodeViewModel node = digital
            ? new DigitalInputNodeViewModel(new DigitalInput(1, true), default)
            : new AnalogueInputNodeViewModel(new AnalogueInput(1, false, false, null), default);
        TimeSpan now = IoFlowLiveHistory.Now;
        node.LiveHistory.Record(now - TimeSpan.FromMilliseconds(900), 0);
        node.LiveHistory.Record(now - TimeSpan.FromMilliseconds(700), 0);
        node.LiveHistory.Record(now - TimeSpan.FromMilliseconds(500), null);
        node.LiveHistory.Record(now - TimeSpan.FromMilliseconds(300), 0);
        node.LiveHistory.Record(now - TimeSpan.FromMilliseconds(100), 0);
        var chart = new IoFlowMiniChart { DataContext = node };
        chart.Measure(new Size(260, 96));
        chart.Arrange(new Rect(0, 0, 260, 96));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            chart.Render(context);
        var series = Assert.Single(Flatten(drawing).OfType<GeometryDrawing>(), item =>
            item.Pen?.Brush is ISolidColorBrush brush && brush.Color == Color.Parse("#52E08A"));
        Assert.Null(series.Brush);
        Assert.Equal(PenLineCap.Flat, series.Pen!.LineCap);
        // Avalonia reports the first figure's length, not the sum of separate figures.
        Assert.Equal(5, series.Geometry!.ContourLength, 3);
        Assert.Equal(20, series.Geometry.Bounds.Width, 3);
        Assert.Equal(0, series.Geometry.Bounds.Height);

        static IEnumerable<Drawing> Flatten(DrawingGroup group) =>
            group.Children.SelectMany(item => item is DrawingGroup nested ? Flatten(nested) : [item]);
    }

    [AvaloniaFact]
    public void MiniChartDrawsOnlyLinesWithReadableLabels()
    {
        var node = new AnalogueInputNodeViewModel(new AnalogueInput(1, false, false, null), default);
        TimeSpan now = IoFlowLiveHistory.Now;
        node.LiveHistory.Record(now - TimeSpan.FromMilliseconds(600), 1);
        node.LiveHistory.Record(now - TimeSpan.FromMilliseconds(400), 2);
        node.LiveHistory.Record(now - TimeSpan.FromMilliseconds(200), 3);
        var chart = new IoFlowMiniChart { DataContext = node };
        chart.Measure(new Size(260, 96));
        chart.Arrange(new Rect(0, 0, 260, 96));
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
            chart.Render(context);

        Drawing[] drawings = Flatten(drawing).ToArray();
        Assert.DoesNotContain(drawings.OfType<GeometryDrawing>(), item => item.Geometry is EllipseGeometry);
        var trace = drawings.OfType<GeometryDrawing>().Where(item =>
            item.Pen?.Brush is ISolidColorBrush brush && brush.Color == Color.Parse("#52E08A")).ToArray();
        var series = Assert.Single(trace);
        Assert.Null(series.Brush);
        Assert.Equal(PenLineCap.Flat, series.Pen!.LineCap);
        Assert.Equal(PenLineJoin.Bevel, series.Pen.LineJoin);
        Rect bounds = series.Geometry!.Bounds;
        Assert.Equal(Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height),
            series.Geometry.ContourLength, 3);
        var labels = drawings.OfType<GlyphRunDrawing>().ToArray();
        Assert.Equal(2, labels.Length);
        Assert.All(labels, label => Assert.Equal(14, label.GlyphRun!.FontRenderingEmSize));
        var majorGrid = drawings.OfType<GeometryDrawing>().Where(item =>
            item.Pen?.Brush is ISolidColorBrush brush && brush.Color == Color.FromRgb(120, 120, 120)).ToArray();
        Assert.Equal(10, majorGrid.Length);
        Assert.Equal(5, majorGrid.Count(line => line.Geometry!.Bounds.Width == 0));
        Assert.Equal(5, majorGrid.Count(line => line.Geometry!.Bounds.Height == 0));
        Assert.Equal(36, drawings.OfType<GeometryDrawing>().Count(item =>
            item.Pen?.Brush is ISolidColorBrush brush && brush.Color == Color.FromRgb(50, 50, 50)));
        Assert.Equal(36, drawings.OfType<GeometryDrawing>().Count(item =>
            item.Pen?.Brush is ISolidColorBrush brush && brush.Color == Color.FromRgb(34, 34, 34)));

        static IEnumerable<Drawing> Flatten(DrawingGroup group) =>
            group.Children.SelectMany(item => item is DrawingGroup nested ? Flatten(nested) : [item]);
    }

    [AvaloniaFact]
    public void OnlyRequestedNodeTypesShowChartsAndSummaryFontsAreLarger()
    {
        var channel = new OutputChannel { ChannelNumber = 1 };
        (IoFlowNodeViewModel Node, bool Chart)[] cases =
        [
            (new DigitalInputNodeViewModel(new DigitalInput(1, true), default), true),
            (new AnalogueInputNodeViewModel(new AnalogueInput(1, false, false, null), default), true),
            (new OutputNodeViewModel(channel, default, [], [channel]), true),
            (new MathSourceNodeViewModel(OutputChannel.MathOperandType.CanSignal, 0, "CAN RX", default), true),
            (new MathSourceNodeViewModel(OutputChannel.MathOperandType.SystemValue, 0, "System value", default), true),
            (new MathRuleNodeViewModel(1, default), false),
            (new CanTxNodeViewModel(new CustomCanMessage(), default, [], null), false),
            (new IgnitionOnNodeViewModel(default), false),
            (new CanInputNodeViewModel(default), false),
        ];
        var view = new IoFlowTabView();
        var window = new Window { Width = 1200, Height = 1000, Content = view };
        try
        {
            window.Show();
            var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
            var graph = new IoFlowGraphViewModel();
            view.DataContext = new { IoFlowGraph = graph };
            foreach (var (node, expectedChart) in cases)
            {
                graph.ClearSelection();
                graph.Nodes.Clear();
                graph.Nodes.Add(node);
                graph.SelectedNodes.Add(node);
                window.UpdateLayout();
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var card = Assert.Single(editor.GetVisualDescendants().OfType<IoFlowNodeCard>());
                var shell = Assert.Single(card.GetVisualDescendants().OfType<Border>(),
                    border => border.Classes.Contains("ioFlowNode"));
                Assert.Equal(Color.Parse("#FF221C34"), Assert.IsAssignableFrom<ISolidColorBrush>(shell.Background).Color);
                Assert.Equal(new CornerRadius(10), shell.CornerRadius);
                Assert.Equal(new Thickness(12), shell.Padding);
                Assert.Equal(expectedChart, node.HasLiveChart);
                var ports = card.GetVisualDescendants().OfType<IoFlowConnector>()
                    .Where(port => port.IsEffectivelyVisible).ToArray();
                Assert.NotEmpty(ports);
                Assert.All(ports, port =>
                {
                    Assert.Equal(10, port.Bounds.Width);
                    Assert.Equal(24, port.Bounds.Height);
                    var pill = Assert.Single(port.GetVisualDescendants().OfType<Border>(),
                        border => border.Name == "Connector");
                    Assert.Equal(6, pill.Bounds.Width);
                    Assert.Equal(18, pill.Bounds.Height);
                    Assert.Equal(new CornerRadius(3), pill.CornerRadius);
                    Assert.Equal(Color.Parse("#FF544060"),
                        Assert.IsAssignableFrom<ISolidColorBrush>(pill.BorderBrush).Color);
                    Assert.Equal(Color.Parse(port.IsConnected ? "#FF463452" : "#FF2C2238"),
                        Assert.IsAssignableFrom<ISolidColorBrush>(pill.Background).Color);
                });
                var miniChart = Assert.Single(card.GetVisualDescendants().OfType<IoFlowMiniChart>());
                Assert.Equal(expectedChart, miniChart.IsEffectivelyVisible);
                var nodeCards = card.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("settingGroup") && border.IsEffectivelyVisible).ToArray();
                Assert.True(nodeCards.Length >= 2);
                Point firstOrigin = nodeCards[0].TranslatePoint(default, card)!.Value;
                Assert.All(nodeCards, nodeCard =>
                {
                    Assert.Equal(nodeCards[0].Bounds.Width, nodeCard.Bounds.Width, 6);
                    Assert.Equal(firstOrigin.X, nodeCard.TranslatePoint(default, card)!.Value.X, 6);
                });
                if (expectedChart)
                {
                    var chartCard = miniChart.GetVisualAncestors().OfType<Border>().First();
                    Assert.IsType<Grid>(chartCard.Parent);
                    Assert.Equal(1, Grid.GetColumn(chartCard));
                    Assert.Equal(1, Grid.GetColumnSpan(chartCard));
                    Assert.Equal(140, miniChart.Bounds.Height);
                    Assert.Equal(chartCard.Bounds.Width - chartCard.Padding.Left - chartCard.Padding.Right
                        - chartCard.BorderThickness.Left - chartCard.BorderThickness.Right, miniChart.Bounds.Width, 6);
                }
                Assert.Equal(20, Assert.Single(card.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Classes.Contains("nodeTitle")).FontSize);
                Assert.Contains(card.GetVisualDescendants().OfType<TextBlock>(), text => text.FontSize == 16);
                var properties = view.FindControl<ContentControl>("IoFlowNodeProperties")!;
                Assert.Equal(20, Assert.Single(properties.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Classes.Contains("nodePropertiesTitle")).FontSize);
                var settingsCards = properties.GetVisualDescendants().OfType<Border>()
                    .Where(border => border.Classes.Contains("settingGroup")).ToArray();
                Assert.NotEmpty(settingsCards);
                Assert.All(settingsCards, setting =>
                {
                    Assert.Equal(new Thickness(10, 8), setting.Padding);
                    Assert.Equal(new CornerRadius(6), setting.CornerRadius);
                    Assert.Equal(Color.Parse("#0DFFFFFF"), Assert.IsAssignableFrom<ISolidColorBrush>(setting.Background).Color);
                    Assert.Equal(Color.Parse("#1FC8A7FF"), Assert.IsAssignableFrom<ISolidColorBrush>(setting.BorderBrush).Color);
                });
                Assert.All(properties.GetVisualDescendants().OfType<TextBlock>()
                    .Where(text => !text.Classes.Contains("nodePropertiesTitle")), text => Assert.Equal(14, text.FontSize));
                if (node is not OutputNodeViewModel)
                    Assert.All(properties.GetVisualDescendants().OfType<Control>()
                        .Where(control => control is ComboBox or TextBox or ToggleSwitch or Slider),
                        control => Assert.Contains(control.GetVisualAncestors().OfType<Border>(),
                            border => border.Classes.Contains("settingGroup")));
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CardsAreReadOnlyAndPropertiesFollowSingleSelection()
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextAnalogueNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        var digital = graph.DigitalNodes[0];
        var analogue = graph.AnalogueNodes[0];
        var output = graph.OutputNodes[0];
        var rule = graph.MathRuleNodes[0];
        digital.Location = new Point(50, 50);
        analogue.Location = new Point(50, 340);
        output.Location = new Point(450, 50);
        rule.Location = new Point(450, 340);
        string[] operations = Enum.GetNames<OutputChannel.MathOperation>();
        var view = new IoFlowTabView
        {
            DataContext = new
            {
                IoFlowGraph = graph,
                MathOperations = operations,
            },
        };
        var window = new Window { Width = 1600, Height = 1100, Content = view };
        try
        {
            window.Show();
            Layout();
            var editor = view.FindControl<NodifyEditor>("IoFlowEditor")!;
            var properties = view.FindControl<ContentControl>("IoFlowNodeProperties")!;
            var panel = view.FindControl<Border>("IoFlowPropertiesPanel")!;
            var cards = editor.GetVisualDescendants().OfType<IoFlowNodeCard>().ToArray();
            Assert.Equal(graph.Nodes.Count, cards.Length);
            Assert.All(cards, card =>
            {
                var chart = Assert.Single(card.GetVisualDescendants().OfType<IoFlowMiniChart>());
                Assert.Equal(((IoFlowNodeViewModel)card.DataContext!).HasLiveChart, chart.IsEffectivelyVisible);
                var title = Assert.Single(card.GetVisualDescendants().OfType<TextBlock>(),
                    text => text.Classes.Contains("nodeTitle"));
                Assert.Equal(20, title.FontSize);
                Assert.All(card.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsEffectivelyVisible),
                    text => Assert.True(text.FontSize >= 14));
                Assert.DoesNotContain(card.GetVisualDescendants(), control =>
                    control is TextBox or ComboBox or Slider or ToggleSwitch or Button);
            });
            Assert.Null(properties.Content);
            Assert.True(panel.TranslatePoint(default, view)!.Value.X >= editor.TranslatePoint(new Point(editor.Bounds.Width, 0), view)!.Value.X);
            var digitalCard = cards.Single(card => ReferenceEquals(card.DataContext, digital));
            Point click = digitalCard.TranslatePoint(new Point(80, 20), window)!.Value;
            window.MouseMove(click);
            window.MouseDown(click, MouseButton.Left);
            window.MouseUp(click, MouseButton.Left);
            Layout();
            Assert.Same(digital, properties.Content);
            graph.ClearSelection();
            int changes = 0;
            graph.GraphChangedAction = _ => changes++;
            Point anchor = analogue.OutputConnector.Anchor;
            graph.SelectedNodes.Add(analogue);
            Layout();
            Assert.Same(analogue, properties.Content);
            Assert.True(properties.IsEffectivelyVisible);
            var selector = Assert.Single(properties.GetVisualDescendants().OfType<ComboBox>(),
                control => control.Classes.Contains("nodeSourceSelector"));
            Assert.Same(analogue, selector.DataContext);
            Assert.Empty(properties.GetVisualDescendants().OfType<IoFlowConnector>());
            Assert.Equal(0, changes);

            analogue.Input.ChanType = AnalogueInput.AnalogueChannelType.NTC;
            Layout();
            var analogueCard = cards.Single(card => ReferenceEquals(card.DataContext, analogue));
            Assert.Contains(analogueCard.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Mode: NTC");
            Assert.Equal(anchor, analogue.OutputConnector.Anchor);
            graph.SelectedNodes.Clear();
            graph.SelectedNodes.Add(rule);
            Layout();
            Assert.Same(rule, properties.Content);
            Assert.DoesNotContain(properties.GetVisualDescendants().OfType<ComboBox>(),
                control => control.Classes.Contains("nodeSourceSelector"));
            var operation = Assert.Single(properties.GetVisualDescendants().OfType<ComboBox>(),
                control => ReferenceEquals(control.ItemsSource, operations));
            operation.SelectedIndex = (int)OutputChannel.MathOperation.ABS;
            Layout();
            Assert.Equal((byte)OutputChannel.MathOperation.ABS, rule.Rule.Operation);
            var ruleCard = cards.Single(card => ReferenceEquals(card.DataContext, rule));
            Assert.Contains(ruleCard.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "Operation: ABS");
            Assert.False(Assert.Single(ruleCard.GetVisualDescendants().OfType<IoFlowConnector>(),
                connector => connector.Classes.Contains("mathOperandB")).IsEffectivelyVisible);
            graph.SelectedNodes.Add(output);
            Layout();
            Assert.Null(properties.Content);
            Assert.False(properties.IsEffectivelyVisible);
            graph.ClearSelection();
            graph.SelectedNodes.Add(analogue);
            Layout();
            Assert.Same(analogue, properties.Content);
            Assert.Equal(AnalogueInput.AnalogueChannelType.NTC, analogue.Input.ChanType);

            var chart = Assert.Single(analogueCard.GetVisualDescendants().OfType<IoFlowMiniChart>());
            TimeSpan now = IoFlowLiveHistory.Now;
            analogue.LiveHistory.Record(now - TimeSpan.FromSeconds(15), 1000);
            analogue.LiveHistory.Record(now - TimeSpan.FromSeconds(9), 1);
            analogue.LiveHistory.Record(now - TimeSpan.FromSeconds(8), null);
            analogue.LiveHistory.Record(now - TimeSpan.FromSeconds(7), 3);
            chart.InvalidateVisual();
            using (var context = new DrawingGroup().Open())
                chart.Render(context);
            Assert.DoesNotContain(analogue.LiveHistory.Samples, sample => sample.Value == 1000);

            graph.Nodes.Remove(analogue);
            graph.SelectedNodes.Remove(analogue);
            Layout();
            Assert.Null(properties.Content);
        }
        finally
        {
            window.Close();
        }

        void Layout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }
}
