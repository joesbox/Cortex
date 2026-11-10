using Avalonia;
using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Xunit;

namespace Cortex.Tests;

public sealed class IoFlowLiveHistoryTests
{
    [Fact]
    public void HistoryKeepsExactlyTenSecondsAndBoundsHighFrequencySamples()
    {
        var history = new IoFlowLiveHistory();
        for (int milliseconds = 0; milliseconds <= 20000; milliseconds++)
            history.Record(TimeSpan.FromMilliseconds(milliseconds), milliseconds);

        Assert.Equal(101, history.Samples.Count);
        Assert.Equal(TimeSpan.FromSeconds(10), history.Samples[0].Timestamp);
        Assert.Equal(TimeSpan.FromSeconds(20), history.Samples[^1].Timestamp);
        Assert.Equal(20000, history.Samples[^1].Value);
        history.Prune(TimeSpan.FromMilliseconds(20001));
        Assert.Equal(TimeSpan.FromMilliseconds(10100), history.Samples[0].Timestamp);
        history.Prune(TimeSpan.FromSeconds(31));
        Assert.Empty(history.Samples);
    }

    [Fact]
    public void InvalidAndMissingReadingsAreGapsNotZeroes()
    {
        var history = new IoFlowLiveHistory();
        double?[] readings = [1, null, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0];
        for (int i = 0; i < readings.Length; i++)
            history.Record(TimeSpan.FromSeconds(i), readings[i]);
        Assert.Equal(new double?[] { 1, null, null, null, null, 0 }, history.Samples.Select(sample => sample.Value));
    }

    [Fact]
    public void GraphRecordsNumericTelemetryWithoutChangingSettings()
    {
        var settings = new DataStructures();
        var live = new DataStructures();
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, live);
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextAnalogueNodeCommand.Execute(null);
        graph.AddNextOutputNodeCommand.Execute(null);
        graph.AddSystemValueNodeCommand.Execute(null);
        live.DigitalInputsLiveData[0].LiveElectricalState = true;
        live.AnalogueInputsLiveData[0].InputValue = 12.5f;
        live.ChannelsLiveData[0].CurrentValue = 3.25f;
        live.SystemParams.SystemTemperature = 42;
        int changes = 0;
        graph.GraphChangedAction = _ => changes++;

        graph.UpdateLiveChannelSamples(live.ChannelsLiveData);

        Assert.Equal(1, graph.DigitalNodes[0].LiveHistory.Samples[^1].Value);
        Assert.Equal(12.5, graph.AnalogueNodes[0].LiveHistory.Samples[^1].Value);
        Assert.Equal(3.25, graph.OutputNodes[0].LiveHistory.Samples[^1].Value);
        Assert.Equal(42, graph.MathSourceNodes[0].LiveHistory.Samples[^1].Value);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void SourceReassignmentDoesNotMixHistories()
    {
        var node = new DigitalInputNodeViewModel(new DigitalInput(1, true), default);
        node.LiveHistory.Record(TimeSpan.Zero, 1);
        node.AssignInput(new DigitalInput(2, true));
        Assert.Empty(node.LiveHistory.Samples);

        var analogue = new AnalogueInputNodeViewModel(new AnalogueInput(1, false, false, null), default);
        analogue.LiveHistory.Record(TimeSpan.Zero, 42);
        analogue.Input.ChanType = AnalogueInput.AnalogueChannelType.NTC;
        Assert.Empty(analogue.LiveHistory.Samples);
    }

    [Fact]
    public void CanSignalChartsRequireValidStatus()
    {
        var live = new DataStructures();
        var node = new MathSourceNodeViewModel(OutputChannel.MathOperandType.CanSignal, 0, "CAN RX", default);
        live.CustomCanSignalsLiveData[0].RuntimeValue = 99;
        live.CustomCanSignalsLiveData[0].Status = (byte)CustomCanStatus.Timeout;
        Assert.Null(node.GetLiveChartValue(live));
        live.CustomCanSignalsLiveData[0].Status = (byte)CustomCanStatus.Valid;
        Assert.Equal(99, node.GetLiveChartValue(live));
    }

    [Fact]
    public void PropertiesTargetOnlySingleSelectionAndClearOnRemoval()
    {
        var graph = new IoFlowGraphViewModel();
        graph.Build(new DataStructures(), new DataStructures());
        graph.AddNextDigitalNodeCommand.Execute(null);
        graph.AddNextAnalogueNodeCommand.Execute(null);
        var notifications = new List<string?>();
        graph.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        graph.SelectedNodes.Add(graph.DigitalNodes[0]);
        Assert.Same(graph.DigitalNodes[0], graph.SelectedNode);
        graph.SelectedNodes.Add(graph.AnalogueNodes[0]);
        Assert.Null(graph.SelectedNode);
        Assert.False(graph.HasSelectedNode);
        graph.SelectedNodes.Remove(graph.DigitalNodes[0]);
        Assert.Same(graph.AnalogueNodes[0], graph.SelectedNode);
        graph.ClearSelection();
        Assert.Null(graph.SelectedNode);
        Assert.Contains(nameof(graph.SelectedNode), notifications);
        Assert.Contains(nameof(graph.HasSelectedNode), notifications);
    }
}
