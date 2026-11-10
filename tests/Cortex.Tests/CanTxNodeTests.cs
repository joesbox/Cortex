using Cortex.Models;
using Cortex.ViewModels.IoFlow;
using Xunit;

namespace Cortex.Tests;

public sealed class CanTxNodeTests
{
    [Fact]
    public void FailedAutomaticStagingRestoresFieldsAndReportsFailure()
    {
        var (graph, node) = CreateGraph();
        node.Message.TimeoutMs = 1000;
        graph.SetCustomCanTxModeAction = (_, _, _) => false;

        node.EditIsPeriodic = true;

        Assert.False(node.Message.IsPeriodic);
        Assert.False(node.EditIsPeriodic);
        Assert.Equal(1000, node.EditTimeoutMs);
        Assert.Contains("Unable to apply", node.ValidationMessage);
    }

    [Fact]
    public async Task FailedNodeDeletionRetainsNodeAndReportsFailure()
    {
        var (graph, node) = CreateGraph();
        node.Message.IsEnabled = true;
        node.Message.TxTrigger = 0;
        graph.SelectedNodes.Add(node);
        graph.SetCustomCanTxStateAction = (_, _, _) => Task.FromResult(false);

        await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);

        Assert.Contains(node, graph.Nodes);
        Assert.True(node.Message.IsEnabled);
        Assert.Equal(0, node.Message.TxTrigger);
        Assert.Contains("retained", graph.AddNodeWarning);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(101)]
    [InlineData(30001)]
    [InlineData(60000)]
    [InlineData(-1)]
    public void PeriodicEditRejectsInvalidPeriodWithoutCallingSettingsCallback(int period)
    {
        var (graph, node) = CreateGraph();
        bool called = false;
        graph.SetCustomCanTxModeAction = (_, _, _) =>
        {
            called = true;
            return true;
        };
        node.EditIsPeriodic = true;
        called = false;
        node.EditTimeoutMs = period;

        Assert.False(called);
        Assert.True(node.HasValidationMessage);
        Assert.Contains("100 and 30,000", node.ValidationMessage);
    }

    [Theory]
    [InlineData(100)]
    [InlineData(200)]
    [InlineData(30000)]
    public void PeriodicEditAcceptsBoundariesAndNotifiesDirtyTracking(int period)
    {
        var (graph, node) = CreateGraph();
        (bool Periodic, ushort Period)? applied = null;
        bool? settingsChanged = null;
        graph.GraphChangedAction = changed => settingsChanged = changed;
        graph.SetCustomCanTxModeAction = (_, periodic, value) =>
        {
            applied = (periodic, value);
            return true;
        };
        node.EditIsPeriodic = true;
        node.EditTimeoutMs = period;

        Assert.Equal((true, (ushort)period), applied);
        Assert.True(settingsChanged);
        Assert.False(node.HasValidationMessage);
    }

    [Fact]
    public void OneShotIgnoresHiddenPeriod()
    {
        var (graph, node) = CreateGraph();
        node.Message.TimeoutMs = 750;
        ushort? appliedPeriod = null;
        graph.SetCustomCanTxModeAction = (_, _, period) =>
        {
            appliedPeriod = period;
            return true;
        };

        node.EditTimeoutMs = -1;

        Assert.Equal((ushort)750, appliedPeriod);
        Assert.False(node.HasValidationMessage);
    }

    private static (IoFlowGraphViewModel Graph, CanTxNodeViewModel Node) CreateGraph()
    {
        DataStructures settings = new();
        CustomCanMessage message = settings.CustomCanMessagesStaticData[0];
        message.Id = 0x123;
        message.Flags = 0x04;
        var graph = new IoFlowGraphViewModel();
        graph.Build(settings, new DataStructures());
        graph.AddCanTxNodeCommand.Execute(null);
        return (graph, Assert.Single(graph.CanTxNodes));
    }
}
