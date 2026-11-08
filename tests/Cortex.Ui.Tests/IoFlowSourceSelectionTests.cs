using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Cortex.Views;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class IoFlowSourceSelectionTests
{
    [AvaloniaFact]
    public async Task EverySelectableSourceCanBeReassignedThroughItsRenderedSelector()
    {
        var settings = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        for (int i = 0; i < 3; i++)
            settings.CustomCanSignalsStaticData[i].IsEnabled = true;
        graph.Build(settings, new DataStructures());
        graph.AddNextAnalogueNodeCommand.Execute(null);
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddCanRxNodeCommand.Execute(null);
        graph.AddSystemValueNodeCommand.Execute(null);
        graph.AddLogicNodeCommand.Execute(null);
        MathRuleNodeViewModel rule = graph.MathRuleNodes.Single();
        await graph.ConnectNodes(graph.AnalogueNodes[0], rule, true);
        var view = new IoFlowTabView { DataContext = new { IoFlowGraph = graph } };
        var window = new Window { Width = 2000, Height = 1600, Content = view };
        try
        {
            window.Show();
            Layout();
            IoFlowNodeViewModel[] sources = [graph.AnalogueNodes[0], graph.DigitalNodes[0], .. graph.MathSourceNodes];
            foreach (IoFlowNodeViewModel source in sources)
            {
                ComboBox selector = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(),
                    box => box.Classes.Contains("nodeSourceSelector") && ReferenceEquals(box.DataContext, source));
                Assert.True(selector.IsEffectivelyVisible);
                Assert.True(selector.IsEffectivelyEnabled);
                for (int cycle = 0; cycle < 4; cycle++)
                {
                    int index = cycle % 2 == 0 ? 2 : 0;
                    selector.SelectedItem = source.AvailableSources.Single(option => option.Index == index);
                    Layout();
                    Assert.Equal(index, source.SelectedSource!.Index);
                    Assert.Equal(index, ((NodeSourceOption)selector.SelectedItem!).Index);
                    if (source is AnalogueInputNodeViewModel analogue)
                    {
                        Assert.Equal(index + 1, analogue.InputNumber);
                        Assert.Same(settings.AnalogueInputsStaticData[index], analogue.Input);
                        Assert.Equal(index, rule.Rule.OperandAIndex);
                        Assert.Single(graph.Connections.Where(wire => ReferenceEquals(wire.Source.Node, source)));
                    }
                    if (source is DigitalInputNodeViewModel digital) Assert.Equal(index + 1, digital.InputNumber);
                    if (source is MathSourceNodeViewModel math) Assert.Equal(index, math.SourceIndex);
                }
            }
            graph.AddNextAnalogueNodeCommand.Execute(null);
            Layout();
            Assert.DoesNotContain(graph.AnalogueNodes[0].AvailableSources, option => option.Index == 1);
            graph.AnalogueNodes[0].SelectedSource = new NodeSourceOption(1, "Already assigned");
            Layout();
            Assert.Equal(1, graph.AnalogueNodes[0].InputNumber);
            Assert.Equal(0, graph.AnalogueNodes[0].SelectedSource!.Index);
            Assert.Equal(0, rule.Rule.OperandAIndex);
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
