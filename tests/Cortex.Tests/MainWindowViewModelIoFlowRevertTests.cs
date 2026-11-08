using Avalonia;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using Cortex.ViewModels.IoFlow;
using System.Reflection;
using Xunit;

namespace Cortex.Tests;

public sealed class MainWindowViewModelIoFlowRevertTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cortex-ioflow-revert-{Guid.NewGuid():N}");
    private readonly IoFlowGraphStore _store;
    private readonly MainWindowViewModel _viewModel;

    public MainWindowViewModelIoFlowRevertTests()
    {
        _store = new IoFlowGraphStore(Path.Combine(_directory, "ioflow-graph.json"));
        _viewModel = new MainWindowViewModel(new TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: _store));
        foreach (string fieldName in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
        {
            Assert.IsType<System.Timers.Timer>(GetField(fieldName).GetValue(_viewModel)).Stop();
        }

        ConfigureBaseline();
    }

    [Fact]
    public void TraditionalCanTxCandidatesExcludeRxAndRejectStaleRxSelection()
    {
        CustomCanMessage rx = _viewModel.SettingsDataView.CustomCanMessagesStaticData[1];
        rx.Id = 0x456;
        rx.Dlc = 8;
        rx.IsEnabled = true;
        CustomCanSignal signal = _viewModel.SettingsDataView.CustomCanSignalsStaticData[0];
        signal.IsEnabled = true;
        signal.MessageIndex = 1;
        _viewModel.NotifySelectedChannelCanTxChanged();

        Assert.DoesNotContain(rx, _viewModel.SelectedChannelCanTxMessages);
        Assert.All(_viewModel.SelectedChannelCanTxMessages, message => Assert.True(message.IsTx));
        _viewModel.ChannelCanTxMessageToAdd = rx;
        _viewModel.AddSelectedChannelCanTxMessageCommand.Execute(null);

        Assert.False(rx.IsTx);
        Assert.Equal(Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, rx.TxTrigger);
        Assert.DoesNotContain(rx, _viewModel.AssignedChannelCanTxMessages);
        Assert.Contains("Select a configured TX message", _viewModel.CustomCanStatusMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoredDirectionChangesOnlyOfferTxForExplicitNodeOrTraditionalAssignment(bool useTraditionalSettings)
    {
        CustomCanSignal signal = _viewModel.SettingsDataView.CustomCanSignalsStaticData[0];
        signal.IsEnabled = true;
        signal.MessageIndex = 0;
        _viewModel.ClearAllStoredSignalsCommand.Execute(null);
        Assert.Empty(_viewModel.IoFlowGraph.CanTxNodes);

        CustomCanMessage message = _viewModel.SettingsDataView.CustomCanMessagesStaticData[0];
        message.Id = 0x456;
        message.Flags = 0x01;
        signal.IsEnabled = true;
        _viewModel.NotifySelectedChannelCanTxChanged();
        Assert.DoesNotContain(message, _viewModel.SelectedChannelCanTxMessages);

        message.IsSelected = true;
        _viewModel.SelectedStoredDirectionIndex = 1;
        _viewModel.ApplySelectedStoredDirectionCommand.Execute(null);

        Assert.True(message.IsTx);
        Assert.Contains(message, _viewModel.SelectedChannelCanTxMessages);
        Assert.Empty(_viewModel.IoFlowGraph.CanTxNodes);
        message.IsPeriodic = true;
        message.TimeoutMs = 500;
        _viewModel.NotifySelectedChannelCanTxChanged();
        Assert.Empty(_viewModel.IoFlowGraph.CanTxNodes);
        _viewModel.IoFlowGraph.PopulateFromCurrentSettings();
        Assert.Empty(_viewModel.IoFlowGraph.CanTxNodes);

        if (useTraditionalSettings)
        {
            _viewModel.ChannelCanTxMessageToAdd = message;
            _viewModel.AddSelectedChannelCanTxMessageCommand.Execute(null);
            Assert.Same(message, Assert.Single(_viewModel.AssignedChannelCanTxMessages));
            Assert.Single(_viewModel.IoFlowGraph.Connections, wire => wire.Target.Node is CanTxNodeViewModel);
        }
        else
        {
            _viewModel.IoFlowGraph.AddCanTxNodeCommand.Execute(null);
            Assert.Empty(_viewModel.AssignedChannelCanTxMessages);
        }
        Assert.Same(message, Assert.Single(_viewModel.IoFlowGraph.CanTxNodes).Message);
        _viewModel.NotifySelectedChannelCanTxChanged();
        Assert.Single(_viewModel.IoFlowGraph.CanTxNodes);

        _viewModel.SelectedStoredDirectionIndex = 0;
        _viewModel.ApplySelectedStoredDirectionCommand.Execute(null);
        Assert.Empty(_viewModel.IoFlowGraph.CanTxNodes);
        Assert.Empty(_viewModel.AssignedChannelCanTxMessages);
        Assert.DoesNotContain(message, _viewModel.SelectedChannelCanTxMessages);
    }

    [Fact]
    public void DeletingStoredMessagesRemovesAssignmentsNodesAndWiresAndDisablesEmptyChannel()
    {
        ConfigureTwoAssignedMessages();
        CaptureCurrentBaseline();
        CustomCanMessage first = _viewModel.SettingsDataView.CustomCanMessagesStaticData[0];
        CustomCanMessage second = _viewModel.SettingsDataView.CustomCanMessagesStaticData[1];
        first.IsSelected = true;

        _viewModel.DeleteCheckedStoredMessagesCommand.Execute(null);

        Assert.Same(second, Assert.Single(_viewModel.AssignedChannelCanTxMessages));
        Assert.Equal(1, Assert.Single(_viewModel.IoFlowGraph.CanTxNodes).MessageIndex);
        Assert.Single(_viewModel.IoFlowGraph.Connections, wire => wire.Target.Node is CanTxNodeViewModel);
        Assert.DoesNotContain(first, _viewModel.SelectedChannelCanTxMessages);
        Assert.True(_viewModel.SelectedChannelCanTxEnabled);
        second.IsSelected = true;

        _viewModel.DeleteCheckedStoredMessagesCommand.Execute(null);

        Assert.Empty(_viewModel.AssignedChannelCanTxMessages);
        Assert.Empty(_viewModel.SelectedChannelCanTxMessages);
        Assert.Empty(_viewModel.IoFlowGraph.CanTxNodes);
        Assert.DoesNotContain(_viewModel.IoFlowGraph.Connections, wire => wire.Target.Node is CanTxNodeViewModel);
        Assert.False(_viewModel.SelectedChannelCanTxEnabled);
        Assert.False(_viewModel.HasAssignedChannelCanTxMessages);
        Assert.False(_viewModel.SelectedChannelCanTxControlsEnabled);
        Assert.Null(_viewModel.SelectedChannelCanTxMessage);
        Assert.Null(_viewModel.ChannelCanTxMessageToAdd);
        Assert.True(_viewModel.CanSendConfig);
        _viewModel.NotifySelectedChannelCanTxChanged();
        Assert.Empty(_viewModel.IoFlowGraph.CanTxNodes);

        _viewModel.RevertChanges();

        Assert.Equal(2, _viewModel.AssignedChannelCanTxMessages.Count);
        Assert.Equal(2, _viewModel.IoFlowGraph.CanTxNodes.Count);
        Assert.True(_viewModel.SelectedChannelCanTxEnabled);
        Assert.False(_viewModel.HasPendingConfigChanges);
    }

    [Fact]
    public void DeletingLastStoredSignalRemovesItsMessageAssignmentButPartialDeletionDoesNot()
    {
        ConfigureTwoAssignedMessages();
        for (int index = 0; index < 2; index++)
        {
            CustomCanSignal signal = _viewModel.SettingsDataView.CustomCanSignalsStaticData[index];
            signal.MessageIndex = 0;
            signal.IsEnabled = true;
        }
        CustomCanSignal first = _viewModel.SettingsDataView.CustomCanSignalsStaticData[0];
        CustomCanSignal second = _viewModel.SettingsDataView.CustomCanSignalsStaticData[1];
        first.IsMarkedForDeletion = true;
        _viewModel.DeleteSelectedStoredSignalsCommand.Execute(null);
        Assert.Equal(2, _viewModel.AssignedChannelCanTxMessages.Count);
        Assert.Equal(2, _viewModel.IoFlowGraph.CanTxNodes.Count);

        second.IsMarkedForDeletion = true;
        _viewModel.DeleteSelectedStoredSignalsCommand.Execute(null);

        Assert.Equal(1, Assert.Single(_viewModel.AssignedChannelCanTxMessages).Index);
        Assert.Equal(1, Assert.Single(_viewModel.IoFlowGraph.CanTxNodes).MessageIndex);
        Assert.DoesNotContain(_viewModel.SelectedChannelCanTxMessages, message => message.Index == 0);
    }

    [Fact]
    public void ClearingStoredSignalsRemovesAllTxAssignmentsIncludingDisabledMessages()
    {
        ConfigureTwoAssignedMessages();
        _viewModel.SettingsDataView.CustomCanMessagesStaticData[1].IsEnabled = false;
        CustomCanSignal signal = _viewModel.SettingsDataView.CustomCanSignalsStaticData[0];
        signal.MessageIndex = 0;
        signal.IsEnabled = true;

        _viewModel.ClearAllStoredSignalsCommand.Execute(null);

        Assert.Empty(_viewModel.AssignedChannelCanTxMessages);
        Assert.Empty(_viewModel.SelectedChannelCanTxMessages);
        Assert.Empty(_viewModel.IoFlowGraph.CanTxNodes);
        Assert.False(_viewModel.SelectedChannelCanTxEnabled);
        Assert.True(_viewModel.HasPendingConfigChanges);
    }

    [Fact]
    public async Task RemovingAllNodesThenAddingTwoTraditionalAssignmentsRestoresBothNodesAndWires()
    {
        ConfigureTwoAssignedMessages();
        CaptureCurrentBaseline();
        var graph = _viewModel.IoFlowGraph;
        graph.SetNextNodeSpawnLocation(new Point(20, 20), default, new Size(800, 500));

        for (int cycle = 0; cycle < 3; cycle++)
        {
            foreach (CanTxNodeViewModel node in graph.CanTxNodes)
            {
                graph.SelectedNodes.Add(node);
            }
            await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);
            Assert.Empty(graph.CanTxNodes);

            for (int index = 0; index < 2; index++)
            {
                _viewModel.ChannelCanTxMessageToAdd = _viewModel.SettingsDataView.CustomCanMessagesStaticData[index];
                _viewModel.AddSelectedChannelCanTxMessageCommand.Execute(null);
                Assert.Equal(index + 1, graph.CanTxNodes.Count);
                Assert.Equal(index + 1, graph.Connections.Count(wire => wire.Target.Node is CanTxNodeViewModel));
            }

            var nodes = graph.CanTxNodes;
            Assert.False(new Rect(nodes[0].Location, new Size(420, 480))
                .Intersects(new Rect(nodes[1].Location, new Size(420, 480))));
        }
    }

    [Fact]
    public void RefreshRestoresEveryMissingActiveAssignmentEvenWhenConfigurationHasNotChanged()
    {
        ConfigureTwoAssignedMessages();
        var graph = _viewModel.IoFlowGraph;
        graph.Nodes.Remove(graph.CanTxNodes.Single(node => node.MessageIndex == 1));

        _viewModel.NotifySelectedChannelCanTxChanged();

        Assert.Equal(2, graph.CanTxNodes.Count);
        Assert.Equal(2, graph.Connections.Count(wire => wire.Target.Node is CanTxNodeViewModel));
    }

    [Fact]
    public async Task RefreshingUnchangedSettingsDoesNotResurrectDeletedCanTxNodes()
    {
        var graph = _viewModel.IoFlowGraph;
        graph.SelectedNodes.Add(Assert.Single(graph.CanTxNodes));
        await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);

        _viewModel.NotifySelectedChannelCanTxChanged();
        _viewModel.SettingsDataView.ChannelsStaticData[0].InputControlPin = InputPinCatalog.DIChannelInputPins[1];

        Assert.Empty(graph.CanTxNodes);

        _viewModel.SettingsDataView.CustomCanMessagesStaticData[0].TimeoutMs = 2000;
        _viewModel.NotifySelectedChannelCanTxChanged();
        Assert.Empty(graph.CanTxNodes);
    }

    [Fact]
    public async Task DeletingTwoCanTxNodesDetachesBothAssignmentsAndEnablesSave()
    {
        ConfigureTwoAssignedMessages();
        CaptureCurrentBaseline();
        var graph = _viewModel.IoFlowGraph;
        foreach (CanTxNodeViewModel node in graph.CanTxNodes)
        {
            graph.SelectedNodes.Add(node);
        }

        await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);

        Assert.Empty(graph.CanTxNodes);
        Assert.Empty(_viewModel.AssignedChannelCanTxMessages);
        Assert.False(_viewModel.SelectedChannelCanTxEnabled);
        Assert.True(_viewModel.HasPendingConfigChanges);
        Assert.True(_viewModel.CanSendConfig);
        Assert.True(_viewModel.HasIoFlowControllerMismatchWarning);
        Assert.All(_viewModel.SettingsDataView.CustomCanMessagesStaticData.Take(2), message =>
        {
            Assert.False(message.IsEnabled);
            Assert.Equal(Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, message.TxTrigger);
        });
        Assert.True(_store.TryLoad(out IoFlowGraphSnapshot stored));
        Assert.DoesNotContain(stored.Nodes, node => node.Kind == "CanTx");

        _viewModel.RevertChanges();

        Assert.Equal(2, _viewModel.AssignedChannelCanTxMessages.Count);
        Assert.Equal(2, _viewModel.IoFlowGraph.CanTxNodes.Count);
        Assert.False(_viewModel.HasPendingConfigChanges);
    }

    [Fact]
    public async Task DeletingOneCanTxNodeDoesNotDisableOtherMessagesOnTheChannel()
    {
        ConfigureTwoAssignedMessages();
        CaptureCurrentBaseline();
        var graph = _viewModel.IoFlowGraph;
        graph.SelectedNodes.Add(graph.CanTxNodes.Single(node => node.MessageIndex == 1));

        await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);

        Assert.Equal(0, Assert.Single(_viewModel.AssignedChannelCanTxMessages).Index);
        Assert.Equal(0, Assert.Single(graph.CanTxNodes).MessageIndex);
        Assert.True(_viewModel.SettingsDataView.CustomCanMessagesStaticData[0].IsEnabled);
        Assert.True(_viewModel.HasPendingConfigChanges);
    }

    [Fact]
    public void TraditionalSettingsAddMultipleMessagesAndEditEachIndependently()
    {
        CustomCanMessage first = _viewModel.SettingsDataView.CustomCanMessagesStaticData[0];
        CustomCanMessage second = _viewModel.SettingsDataView.CustomCanMessagesStaticData[1];
        second.Id = 0x456;
        second.Dlc = 8;
        second.IsTx = true;
        _viewModel.ChannelCanTxMessageToAdd = first;
        _viewModel.AddSelectedChannelCanTxMessageCommand.Execute(null);
        _viewModel.SelectedChannelCanTxModeIndex = 1;
        _viewModel.SelectedChannelCanTxTimeoutMs = 500;
        _viewModel.ChannelCanTxMessageToAdd = second;
        _viewModel.AddSelectedChannelCanTxMessageCommand.Execute(null);

        Assert.Equal(2, _viewModel.AssignedChannelCanTxMessages.Count);
        Assert.Equal(2, _viewModel.IoFlowGraph.Connections.Count(wire => wire.Target.Node is CanTxNodeViewModel));
        Assert.True(first.IsEnabled);
        Assert.True(first.IsPeriodic);
        Assert.Equal(500, first.TimeoutMs);
        Assert.False(second.IsPeriodic);
        _viewModel.SelectedChannelCanTxMessage = first;
        Assert.Equal(500, _viewModel.SelectedChannelCanTxTimeoutMs);
        _viewModel.SelectedChannelCanTxMessage = second;
        _viewModel.SelectedChannelCanTxModeIndex = 1;
        _viewModel.SelectedChannelCanTxTimeoutMs = 1500;
        Assert.Equal(1500, second.TimeoutMs);
        Assert.Equal(500, first.TimeoutMs);
        _viewModel.AddSelectedChannelCanTxMessageCommand.Execute(null);
        Assert.Equal(2, _viewModel.AssignedChannelCanTxMessages.Count);
    }

    [Fact]
    public void ChannelSwitchAndGroupTogglePreserveMultipleAssignmentsAndTheirModes()
    {
        ConfigureTwoAssignedMessages();
        CustomCanMessage first = _viewModel.SettingsDataView.CustomCanMessagesStaticData[0];
        CustomCanMessage second = _viewModel.SettingsDataView.CustomCanMessagesStaticData[1];
        first.IsPeriodic = true;
        first.TimeoutMs = 700;
        _viewModel.NotifySelectedChannelCanTxChanged();
        CaptureCurrentBaseline();

        _viewModel.SelectedChannel = _viewModel.SettingsDataView.ChannelsStaticData[1];
        Assert.Empty(_viewModel.AssignedChannelCanTxMessages);
        Assert.Null(_viewModel.SelectedChannelCanTxMessage);
        Assert.False((bool)typeof(MainWindowViewModel).GetMethod("HasPendingCustomCanChanges", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null)!);
        _viewModel.SelectedChannel = _viewModel.SettingsDataView.ChannelsStaticData[0];
        Assert.Equal(2, _viewModel.AssignedChannelCanTxMessages.Count);
        _viewModel.ToggleSelectedChannelCanTxCommand.Execute(null);
        Assert.False(first.IsEnabled);
        Assert.False(second.IsEnabled);
        Assert.Equal(2, _viewModel.AssignedChannelCanTxMessages.Count);
        Assert.True(_viewModel.SelectedChannelCanTxControlsEnabled);
        _viewModel.ToggleSelectedChannelCanTxCommand.Execute(null);
        Assert.True(first.IsEnabled);
        Assert.True(second.IsEnabled);
        Assert.True(first.IsPeriodic);
        Assert.Equal(700, first.TimeoutMs);
        Assert.False(second.IsPeriodic);
        Assert.Equal(0, first.TxTrigger);
        Assert.Equal(0, second.TxTrigger);
    }

    [Fact]
    public async Task TraditionalRemoveDetachesOnlySelectedAssignmentAndSelectionNeverReassigns()
    {
        ConfigureTwoAssignedMessages();
        CaptureCurrentBaseline();
        CustomCanMessage first = _viewModel.SettingsDataView.CustomCanMessagesStaticData[0];
        CustomCanMessage second = _viewModel.SettingsDataView.CustomCanMessagesStaticData[1];
        _viewModel.SelectedChannelCanTxMessage = first;
        _viewModel.SelectedChannelCanTxMessage = second;
        Assert.False(_viewModel.HasPendingConfigChanges);

        await _viewModel.RemoveSelectedChannelCanTxMessageCommand.ExecuteAsync(null);

        Assert.Same(first, Assert.Single(_viewModel.AssignedChannelCanTxMessages));
        Assert.True(first.IsEnabled);
        Assert.False(second.IsEnabled);
        Assert.Equal(Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, second.TxTrigger);
        Assert.DoesNotContain(_viewModel.IoFlowGraph.Connections, wire =>
            wire.Target.Node is CanTxNodeViewModel { MessageIndex: 1 });
        Assert.True(_viewModel.HasPendingConfigChanges);
    }

    private void ConfigureTwoAssignedMessages()
    {
        for (int index = 0; index < 2; index++)
        {
            CustomCanMessage message = _viewModel.SettingsDataView.CustomCanMessagesStaticData[index];
            message.Id = (uint)(0x123 + index);
            message.Dlc = 8;
            message.Flags = 0x05;
            message.TxTrigger = 0;
        }

        _viewModel.NotifySelectedChannelCanTxChanged();
    }

    private void CaptureCurrentBaseline()
    {
        GetField("_controllerConfigBaseline").SetValue(_viewModel,
            typeof(MainWindowViewModel).GetMethod("DeepCopyDataStructures", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_viewModel, [_viewModel.SettingsDataView]));
        typeof(MainWindowViewModel).GetMethod("CaptureIoFlowBaselineSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null);
        _viewModel.HasPendingConfigChanges = false;
    }

    [Fact]
    public void ControllerCanSnapshotPopulatesMessagesAndNodesWithoutCanTabAndRevertRetainsThem()
    {
        DataStructures source = (DataStructures)typeof(MainWindowViewModel)
            .GetMethod("DeepCopyDataStructures", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, [_viewModel.SettingsDataView])!;
        CustomCanMessage message = source.CustomCanMessagesStaticData[1];
        message.Id = 0x456;
        message.Dlc = 8;
        message.Flags = 0x0D;
        message.TimeoutMs = 1000;
        message.TxTrigger = 0;
        source.CustomCanSignalsStaticData[0].IsEnabled = true;
        source.CustomCanSignalsStaticData[0].MessageIndex = 1;
        source.CustomCanSignalsStaticData[0].BitLength = 8;
        typeof(MainWindowViewModel).GetMethod("UpdateLiveDataInPlace", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, [source]);

        Assert.Contains(_viewModel.ControllerCustomCanMessages, item => item.Id == 0x456);
        CanTxNodeViewModel tx = Assert.Single(_viewModel.IoFlowGraph.CanTxNodes, node => node.MessageIndex == 1);
        Assert.True(tx.EditIsPeriodic);
        Assert.Equal(1000, tx.EditTimeoutMs);
        Assert.Contains(_viewModel.IoFlowGraph.Connections, wire => ReferenceEquals(wire.Target.Node, tx));
        Assert.False(_viewModel.HasPendingConfigChanges);

        _viewModel.RevertChanges();

        Assert.Contains(_viewModel.IoFlowGraph.CanTxNodes, node => node.MessageIndex == 1);
        Assert.False(_viewModel.HasPendingConfigChanges);
    }

    [Fact]
    public async Task SavingCustomCanBaselineDoesNotClearUnrelatedPendingChannelChanges()
    {
        await _viewModel.SetCustomCanMessageTxStateAsync(0, 0, true);
        _viewModel.SettingsDataView.ChannelsStaticData[0].Enabled ^= 1;

        typeof(MainWindowViewModel).GetMethod("SyncCustomCanBaselineSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null);
        typeof(MainWindowViewModel).GetMethod("RecalculatePendingConfigChanges", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null);

        Assert.True(_viewModel.HasPendingConfigChanges);
        Assert.False((bool)typeof(MainWindowViewModel).GetMethod("HasPendingCustomCanChanges", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null)!);
    }

    [Fact]
    public async Task CanTxConnectionsStageChangesAndUndoClearsDirtyStateWithoutAController()
    {
        var graph = _viewModel.IoFlowGraph;
        CanTxNodeViewModel tx = Assert.Single(graph.CanTxNodes);
        OutputNodeViewModel output = graph.OutputNodes.Single(node => node.ChannelNumber == 1);

        await graph.ConnectNodes(output, tx);

        Assert.Equal(0, tx.Message.TxTrigger);
        Assert.True(tx.Message.IsEnabled);
        Assert.True(_viewModel.HasPendingConfigChanges);
        Assert.True(_viewModel.HasIoFlowControllerMismatchWarning);
        Assert.Contains(tx.Message, _viewModel.SelectedChannelCanTxMessages);
        Assert.True(_viewModel.SelectedChannelCanTxEnabled);
        Assert.True((bool)typeof(MainWindowViewModel).GetMethod("HasPendingCustomCanChanges", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null)!);

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, tx.Message.TxTrigger);
        Assert.False(_viewModel.HasPendingConfigChanges);
        Assert.False(_viewModel.HasIoFlowControllerMismatchWarning);
    }

    [Fact]
    public async Task CanTxModeStagesChangesAndRefreshesTraditionalSettings()
    {
        var graph = _viewModel.IoFlowGraph;
        CanTxNodeViewModel tx = Assert.Single(graph.CanTxNodes);
        await graph.ConnectNodes(graph.OutputNodes.Single(node => node.ChannelNumber == 1), tx);
        tx.EditIsPeriodic = true;
        tx.EditTimeoutMs = 30000;

        await graph.ApplyCanTxNodeSettingsCommand.ExecuteAsync(tx);

        Assert.True(tx.Message.IsPeriodic);
        Assert.Equal(30000, tx.Message.TimeoutMs);
        Assert.Equal(1, _viewModel.SelectedChannelCanTxModeIndex);
        Assert.Equal(30000, _viewModel.SelectedChannelCanTxTimeoutMs);
        Assert.True(_viewModel.HasPendingConfigChanges);
        Assert.True(_viewModel.HasIoFlowControllerMismatchWarning);
        Assert.False(tx.HasValidationMessage);
    }

    [Fact]
    public async Task CanTxPendingChangesSurviveLiveSnapshotsAndRevertRestoresBaseline()
    {
        DataStructures staleSnapshot = (DataStructures)typeof(MainWindowViewModel)
            .GetMethod("DeepCopyDataStructures", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, [_viewModel.SettingsDataView])!;
        var graph = _viewModel.IoFlowGraph;
        await graph.ConnectNodes(graph.OutputNodes.Single(node => node.ChannelNumber == 1), graph.CanTxNodes[0]);

        typeof(MainWindowViewModel).GetMethod("UpdateLiveDataInPlace", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, [staleSnapshot]);

        Assert.Equal(0, graph.CanTxNodes[0].Message.TxTrigger);
        Assert.True(_viewModel.HasPendingConfigChanges);

        _viewModel.RevertChanges();

        Assert.Equal(Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, Assert.Single(_viewModel.IoFlowGraph.CanTxNodes).Message.TxTrigger);
        Assert.False(_viewModel.HasPendingConfigChanges);
    }

    [Fact]
    public void TraditionalCanTxSettingsCreateNodeAndSynchronizeWithoutMovingExistingNodes()
    {
        var graph = _viewModel.IoFlowGraph;
        var before = graph.Nodes.ToDictionary(node => node, node => node.Location);
        CustomCanMessage message = _viewModel.SettingsDataView.CustomCanMessagesStaticData[1];
        message.Id = 0x456;
        message.Dlc = 8;
        message.IsTx = true;
        _viewModel.ChannelCanTxMessageToAdd = message;

        _viewModel.AddSelectedChannelCanTxMessageCommand.Execute(null);
        _viewModel.SelectedChannelCanTxModeIndex = 1;
        _viewModel.SelectedChannelCanTxTimeoutMs = 500;

        CanTxNodeViewModel tx = Assert.Single(graph.CanTxNodes, node => node.MessageIndex == 1);
        Assert.True(tx.Message.IsEnabled);
        Assert.True(tx.EditIsPeriodic);
        Assert.Equal(500, tx.EditTimeoutMs);
        Assert.Contains(graph.Connections, wire => ReferenceEquals(wire.Target.Node, tx) &&
            wire.Source.Node is OutputNodeViewModel { ChannelNumber: 1 });
        Assert.True(_viewModel.HasPendingConfigChanges);
        Assert.All(before, pair => Assert.Equal(pair.Value, pair.Key.Location));

        _viewModel.ToggleSelectedChannelCanTxCommand.Execute(null);
        Assert.False(tx.Message.IsEnabled);
        Assert.Contains(graph.Connections, wire => ReferenceEquals(wire.Target.Node, tx));

        message.IsPeriodic = false;
        message.IsTx = false;
        _viewModel.NotifySelectedChannelCanTxChanged();
        Assert.DoesNotContain(graph.CanTxNodes, node => node.MessageIndex == 1);
        Assert.All(before, pair => Assert.Equal(pair.Value, pair.Key.Location));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(99)]
    [InlineData(101)]
    [InlineData(30100)]
    [InlineData(65535)]
    public async Task CanTxModeApiRejectsOutOfRangeAndNonStepPeriods(ushort period)
    {
        Assert.False(await _viewModel.SetCustomCanMessageTxModeAsync(0, true, period));
        Assert.False(_viewModel.SettingsDataView.CustomCanMessagesStaticData[0].IsPeriodic);
        Assert.False(_viewModel.HasPendingConfigChanges);
    }

    [Fact]
    public async Task UndoNodeMovePersistsRestoredLocationsWithoutMarkingSettingsDirty()
    {
        IoFlowGraphSnapshot before = _viewModel.IoFlowGraph.CreateSnapshot();
        _viewModel.IoFlowGraph.ItemsDragStartedCommand.Execute(null);
        MoveAllNodes();
        Assert.True(_viewModel.IoFlowGraph.CanUndo);

        await _viewModel.IoFlowGraph.UndoCommand.ExecuteAsync(null);

        AssertLocations(before, _viewModel.IoFlowGraph.CreateSnapshot());
        Assert.True(_store.TryLoad(out IoFlowGraphSnapshot persisted));
        AssertLocations(before, persisted);
        Assert.False(_viewModel.HasPendingConfigChanges);
        Assert.False(_viewModel.HasIoFlowControllerMismatchWarning);
    }

    [Fact]
    public async Task UndoConnectionRestoresBaselineSettingsAndClearsPendingChanges()
    {
        IoFlowConnectionViewModel connection = Assert.Single(_viewModel.IoFlowGraph.Connections, wire =>
            wire.Source.Node is DigitalInputNodeViewModel &&
            wire.Target.Node is OutputNodeViewModel { ChannelNumber: 1 });
        await _viewModel.IoFlowGraph.RemoveConnectionCommand.ExecuteAsync(connection);
        Assert.True(_viewModel.HasPendingConfigChanges);
        Assert.True(_viewModel.HasIoFlowControllerMismatchWarning);

        await _viewModel.IoFlowGraph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(InputPinCatalog.DIChannelInputPins[0], _viewModel.SettingsDataView.ChannelsStaticData[0].InputControlPin);
        Assert.False(_viewModel.HasPendingConfigChanges);
        Assert.False(_viewModel.HasIoFlowControllerMismatchWarning);
        Assert.True(_store.TryLoad(out IoFlowGraphSnapshot persisted));
        AssertLocations(_viewModel.IoFlowGraph.CreateSnapshot(), persisted);
    }

    [Fact]
    public async Task UndoAnalogueConnectionRestoresChannelTypeWithSettingsTrackingEnabled()
    {
        _viewModel.SettingsDataView.AnalogueInputsStaticData[0].ChanType = AnalogueInput.AnalogueChannelType.RawVoltage;
        var graph = _viewModel.IoFlowGraph;
        OutputNodeViewModel output = graph.OutputNodes.Single(node => node.ChannelNumber == 1);
        await graph.ConnectNodes(graph.AnalogueNodes[0], output);
        Assert.Equal(OutputChannel.ChannelType.Analogue, output.Channel.ChanType);

        await graph.UndoCommand.ExecuteAsync(null);

        Assert.Equal(OutputChannel.ChannelType.Digital, output.Channel.ChanType);
        Assert.Equal(InputPinCatalog.DIChannelInputPins[0], output.Channel.InputControlPin);
    }

    [Fact]
    public void RevertChanges_LayoutOnlyKeepsEveryNodePositionAndPersistsIt()
    {
        MoveAllNodes();
        IoFlowGraphSnapshot expected = _viewModel.IoFlowGraph.CreateSnapshot();
        Assert.False(_viewModel.HasPendingConfigChanges);
        Assert.False(_viewModel.HasIoFlowControllerMismatchWarning);

        _viewModel.RevertChanges();

        AssertLocations(expected, _viewModel.IoFlowGraph.CreateSnapshot());
        Assert.False(_viewModel.HasPendingConfigChanges);
        Assert.False(_viewModel.HasIoFlowControllerMismatchWarning);
        Assert.True(_store.TryLoad(out IoFlowGraphSnapshot persisted));
        AssertLocations(expected, persisted);

        var reopenedGraph = new IoFlowGraphViewModel();
        reopenedGraph.Build(_viewModel.SettingsDataView, _viewModel.LiveDataView);
        reopenedGraph.ApplySnapshot(persisted);
        AssertLocations(expected, reopenedGraph.CreateSnapshot());
    }

    [Fact]
    public void RevertChanges_RestoresSettingsAndConnectionsWithoutMovingNodes()
    {
        MoveAllNodes();
        IoFlowGraphSnapshot expected = _viewModel.IoFlowGraph.CreateSnapshot();
        _viewModel.SettingsDataView.ChannelsStaticData[0].InputControlPin = InputPinCatalog.DIChannelInputPins[1];
        _viewModel.IoFlowGraph.MathRuleNodes.Single(node => node.IsStandalone).Rule.OperandAConstant = 42;
        _viewModel.IoFlowGraph.RebuildConnections(_viewModel.SettingsDataView);

        _viewModel.RevertChanges();

        AssertLocations(expected, _viewModel.IoFlowGraph.CreateSnapshot());
        Assert.Equal(InputPinCatalog.DIChannelInputPins[0], _viewModel.SettingsDataView.ChannelsStaticData[0].InputControlPin);
        Assert.Equal(0, _viewModel.IoFlowGraph.MathRuleNodes.Single(node => node.IsStandalone).Rule.OperandAConstant);
        Assert.Contains(_viewModel.IoFlowGraph.Connections, connection =>
            connection.Source.Node is DigitalInputNodeViewModel { InputNumber: 1 } &&
            connection.Target.Node is OutputNodeViewModel { ChannelNumber: 1 });
        Assert.False(_viewModel.HasPendingConfigChanges);
        Assert.False(_viewModel.HasIoFlowControllerMismatchWarning);
    }

    [Fact]
    public void RevertChanges_RemovesAdditionsAndRestoresDeletedNodesWithoutResettingSurvivors()
    {
        IoFlowGraphSnapshot baseline = _viewModel.IoFlowGraph.CreateSnapshot();
        MoveAllNodes();
        _viewModel.IoFlowGraph.Nodes.Remove(_viewModel.IoFlowGraph.AnalogueNodes.Single());
        _viewModel.IoFlowGraph.AddNextDigitalNodeCommand.Execute(null);
        _viewModel.IoFlowGraph.AddLogicNodeCommand.Execute(null);
        IoFlowGraphSnapshot beforeRevert = _viewModel.IoFlowGraph.CreateSnapshot();

        _viewModel.RevertChanges();

        IoFlowGraphSnapshot reverted = _viewModel.IoFlowGraph.CreateSnapshot();
        Assert.Equal(baseline.Nodes.Count, reverted.Nodes.Count);
        foreach (IoFlowNodeSnapshot node in baseline.Nodes)
        {
            IoFlowNodeSnapshot expected = beforeRevert.Nodes.FirstOrDefault(item => Key(item) == Key(node)) ?? node;
            IoFlowNodeSnapshot actual = Assert.Single(reverted.Nodes, item => Key(item) == Key(node));
            Assert.Equal(new Point(expected.X, expected.Y), new Point(actual.X, actual.Y));
        }

        MoveAllNodes();
        IoFlowGraphSnapshot latestLayout = _viewModel.IoFlowGraph.CreateSnapshot();
        _viewModel.RevertChanges();
        AssertLocations(latestLayout, _viewModel.IoFlowGraph.CreateSnapshot());
        Assert.True(_store.TryLoad(out IoFlowGraphSnapshot persisted));
        AssertLocations(latestLayout, persisted);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task RestartRecoversCompleteIoDraftAndAsksOnlyForIndependentControllerChanges(bool controllerChanged, bool restore)
    {
        DataStructures baseline = (DataStructures)GetField("_controllerConfigBaseline").GetValue(_viewModel)!;
        _viewModel.SettingsDataView.ChannelsStaticData[0].Enabled ^= 1;
        _viewModel.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1 = 73;
        _viewModel.SettingsDataView.CustomCanMessagesStaticData[0].TimeoutMs = 1450;
        _viewModel.NotifySelectedChannelCanTxChanged();
        MoveAllNodes();
        Assert.True(_viewModel.CanSendConfig);
        Assert.True(_store.TryLoad(out var retained));
        Assert.NotNull(retained.DraftSettings);
        Assert.Null(retained.DraftSettings.InputsAndOutputs.CellularParameters);
        Assert.Null(retained.DraftSettings.InputsAndOutputs.SystemParameters);
        if (controllerChanged) baseline.ChannelsStaticData[0].InrushDelay++;
        baseline.ChannelsStaticData[0].MathCalculatedRootValue = 99;
        var closer = new TestAppCloser { ConfirmationResult = restore };
        var restarted = new MainWindowViewModel(closer, new MainWindowViewModelDependencies(ioFlowGraphStore: _store));
        foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
            Assert.IsType<System.Timers.Timer>(GetField(name).GetValue(restarted)).Stop();
        try
        {
            typeof(MainWindowViewModel).GetMethod("ReplaceSettingsData", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(restarted, [baseline]);
            GetField("_controllerConfigBaseline").SetValue(restarted, baseline);
            Assert.True(_store.TryLoad(out var stillRetained));
            Assert.NotNull(stillRetained.DraftSettings);
            await (Task)typeof(MainWindowViewModel).GetMethod("ReconcileIoFlowDraftAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(restarted, null)!;
            Assert.Equal(controllerChanged ? (restore ? 1 : 2) : 0, closer.ConfirmationCount);
            bool restored = !controllerChanged || restore;
            Assert.Equal(restored, restarted.HasPendingConfigChanges);
            Assert.Equal(restored, restarted.CanSendConfig);
            Assert.Equal(restored, restarted.HasIoFlowControllerMismatchWarning);
            if (restored)
            {
                Assert.Equal(73, restarted.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1);
                Assert.Equal(1450, restarted.SettingsDataView.CustomCanMessagesStaticData[0].TimeoutMs);
                AssertLocations(retained, restarted.IoFlowGraph.CreateSnapshot());
            }
            Assert.True(_store.TryLoad(out var reconciled));
            Assert.Equal(restored, reconciled.DraftSettings != null);
        }
        finally
        {
            foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
                Assert.IsType<System.Timers.Timer>(GetField(name).GetValue(restarted)).Dispose();
        }
    }

    [Fact]
    public void OrdinaryInputEditsPersistDraftAndRevertExplicitlyDiscardsIt()
    {
        _viewModel.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1 = 91;
        Assert.True(_viewModel.CanSendConfig);
        Assert.True(_viewModel.HasIoFlowControllerMismatchWarning);
        Assert.True(_store.TryLoad(out var draft));
        Assert.NotNull(draft.DraftSettings);
        _viewModel.RevertChanges();
        Assert.False(_viewModel.CanSendConfig);
        Assert.False(_viewModel.HasIoFlowControllerMismatchWarning);
        Assert.True(_store.TryLoad(out var reverted));
        Assert.Null(reverted.DraftSettings);
    }

    [Fact]
    public void MatchingSettingsSignatureCannotHideDifferentStandaloneCanvasRulesFromSave()
    {
        var snapshot = _viewModel.IoFlowGraph.CreateSnapshot();
        snapshot.ControllerSettingsSignature = (string)typeof(MainWindowViewModel)
            .GetMethod("CreateIoFlowControllerSignature", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null)!;
        snapshot.OutputRootLogicNodeByChannel[2] = 1;
        snapshot.StandaloneLogicRules.Single().OperandAConstant = 42;
        bool applied = (bool)typeof(MainWindowViewModel)
            .GetMethod("TryApplyIoFlowSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, [snapshot, true, true, null])!;
        Assert.True(applied);
        Assert.Equal(OutputChannel.ChannelType.MATH, _viewModel.SettingsDataView.ChannelsStaticData[1].ChanType);
        Assert.Equal(42, _viewModel.SettingsDataView.ChannelsStaticData[1].MathRules[0].OperandAConstant);
        Assert.True(_viewModel.CanSendConfig);
        typeof(MainWindowViewModel).GetMethod("UpdateIoFlowControllerMismatchWarning", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null);
        Assert.True(_viewModel.HasIoFlowControllerMismatchWarning);
        Assert.True(_store.TryLoad(out var saved));
        Assert.NotNull(saved.DraftSettings);
        typeof(MainWindowViewModel).GetMethod("CaptureIoFlowBaselineSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null);
        _viewModel.RevertChanges();
        Assert.Equal(0, _viewModel.SettingsDataView.ChannelsStaticData[1].MathRules[0].OperandAConstant);
        Assert.Equal(2, _viewModel.SettingsDataView.ChannelsStaticData[1].MathRuleCount);
        Assert.False(_viewModel.CanSendConfig);
    }

    [Fact]
    public async Task DraftAlreadyAppliedOnControllerRestoresLayoutWithoutAnotherSave()
    {
        _viewModel.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1 = 91;
        MoveAllNodes();
        Assert.True(_store.TryLoad(out var draft));
        var copy = typeof(MainWindowViewModel).GetMethod("DeepCopyDataStructures", BindingFlags.NonPublic | BindingFlags.Instance)!;
        GetField("_controllerConfigBaseline").SetValue(_viewModel, copy.Invoke(_viewModel, [_viewModel.SettingsDataView]));
        typeof(MainWindowViewModel).GetMethod("RememberIoFlowDraft", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, [draft]);
        await (Task)typeof(MainWindowViewModel).GetMethod("ReconcileIoFlowDraftAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null)!;
        Assert.Equal(0, ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfirmationCount);
        Assert.False(_viewModel.CanSendConfig);
        Assert.False(_viewModel.HasIoFlowControllerMismatchWarning);
        AssertLocations(draft, _viewModel.IoFlowGraph.CreateSnapshot());
        Assert.True(_store.TryLoad(out var stored));
        Assert.Null(stored.DraftSettings);
    }

    [Fact]
    public async Task CancellingRecoveryChoiceKeepsDraftUntouchedAndDoesNotRepeatedlyPrompt()
    {
        _viewModel.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1 = 91;
        Assert.True(_store.TryLoad(out var draft));
        var baseline = (DataStructures)GetField("_controllerConfigBaseline").GetValue(_viewModel)!;
        baseline.ChannelsStaticData[0].InrushDelay++;
        var closer = (TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!;
        closer.ConfirmationResult = false;
        closer.DiscardDraftConfirmationResult = false;
        typeof(MainWindowViewModel).GetMethod("ReplaceSettingsData", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, [baseline]);
        typeof(MainWindowViewModel).GetMethod("RecalculatePendingConfigChanges", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null);
        var reconcile = typeof(MainWindowViewModel).GetMethod("ReconcileIoFlowDraftAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)reconcile.Invoke(_viewModel, null)!;
        await (Task)reconcile.Invoke(_viewModel, null)!;
        Assert.Equal(2, closer.ConfirmationCount);
        Assert.False(_viewModel.CanSendConfig);
        Assert.Contains("retained without applying", _viewModel.IoFlowControllerMismatchWarning);
        MoveAllNodes();
        Assert.True(_store.TryLoad(out var retained));
        Assert.NotNull(retained.DraftSettings);
        Assert.Equal(draft.DraftBaselineSignature, retained.DraftBaselineSignature);
        AssertLocations(draft, retained);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FreshControllerReadbackPreservesAndRecoversPendingDraft(bool customCanReady)
    {
        var baseline = (DataStructures)GetField("_controllerConfigBaseline").GetValue(_viewModel)!;
        _viewModel.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1 = 91;
        _viewModel.SettingsDataView.ChannelsStaticData[0].Enabled ^= 1;
        MoveAllNodes();
        var expectedLayout = _viewModel.IoFlowGraph.CreateSnapshot();
        using var service = new SerialPortService("COM1");
        foreach (string name in new[] { "HasLoadedStaticSnapshot", "HasLoadedMathSnapshot", "HasLoadedCustomCanSnapshot" })
            typeof(SerialPortService).GetProperty(name)!.SetValue(service, true);
        typeof(SerialPortService).GetProperty("HasLoadedCustomCanSnapshot")!.SetValue(service, customCanReady);
        service.UpdateStaticData = false;
        GetField("_portService").SetValue(_viewModel, service);
        GetField("refreshStaticData").SetValue(_viewModel, true);
        typeof(MainWindowViewModel).GetMethod("UpdateUIWithData", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, [baseline]);
        if (!customCanReady)
        {
            Assert.False(_viewModel.CanSendConfig);
            Assert.True(_store.TryLoad(out var waiting));
            Assert.NotNull(waiting.DraftSettings);
            typeof(SerialPortService).GetProperty("HasLoadedCustomCanSnapshot")!.SetValue(service, true);
            typeof(MainWindowViewModel).GetMethod("UpdateUIWithData", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(_viewModel, [baseline]);
        }
        Assert.Equal(91, _viewModel.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1);
        Assert.True(_viewModel.CanSendConfig);
        Assert.True(_viewModel.HasIoFlowControllerMismatchWarning);
        AssertLocations(expectedLayout, _viewModel.IoFlowGraph.CreateSnapshot());
        Assert.Equal(0, ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfirmationCount);
        var confirmed = (DataStructures)GetField("_controllerConfigBaseline").GetValue(_viewModel)!;
        Assert.NotEqual(91, confirmed.AnalogueInputsStaticData[0].CalibrationValue1);
        Assert.True(_store.TryLoad(out var persisted));
        Assert.NotNull(persisted.DraftSettings);
        GetField("_portService").SetValue(_viewModel, null);
    }

    private void ConfigureBaseline()
    {
        DataStructures settings = _viewModel.SettingsDataView;
        settings.ChannelsStaticData[0].ChanType = OutputChannel.ChannelType.Digital;
        settings.ChannelsStaticData[0].InputControlPin = InputPinCatalog.DIChannelInputPins[0];
        OutputChannel mathOutput = settings.ChannelsStaticData[1];
        mathOutput.ChanType = OutputChannel.ChannelType.MATH;
        mathOutput.MathRuleCount = 2;
        mathOutput.EnsureMathRuleSlots();
        settings.CustomCanMessagesStaticData[0].Id = 0x97;
        settings.CustomCanMessagesStaticData[0].Flags = 0x05;
        settings.CustomCanMessagesStaticData[0].Dlc = 8;

        var baseline = new IoFlowGraphSnapshot
        {
            Nodes =
            [
                new() { Kind = "Digital", Id = 1 },
                new() { Kind = "Analogue", Id = 1 },
                new() { Kind = "Output", Id = 1 },
                new() { Kind = "Output", Id = 2 },
                new() { Kind = "Can", Id = 1 },
                new() { Kind = "CanTx", Id = 1 },
                new() { Kind = "Ignition", Id = 1 },
                new() { Kind = "Logic", Id = 1 },
                new() { Kind = "OutputMath", Id = 2, Option = 0 },
                new() { Kind = "OutputMath", Id = 2, Option = 1 },
            ],
        };
        for (int index = 0; index < baseline.Nodes.Count; index++)
        {
            baseline.Nodes[index].X = 100 + index * 75;
            baseline.Nodes[index].Y = 200 + index * 50;
        }

        _viewModel.IoFlowGraph.Build(settings, _viewModel.LiveDataView);
        _viewModel.IoFlowGraph.ApplySnapshot(baseline);
        Assert.Equal(baseline.Nodes.Count, _viewModel.IoFlowGraph.Nodes.Count);
        _viewModel.SelectedChannel = settings.ChannelsStaticData[0];
        MethodInfo copyMethod = typeof(MainWindowViewModel).GetMethod("DeepCopyDataStructures", BindingFlags.NonPublic | BindingFlags.Instance)!;
        GetField("_controllerConfigBaseline").SetValue(_viewModel, copyMethod.Invoke(_viewModel, [settings]));
        typeof(MainWindowViewModel).GetMethod("CaptureIoFlowBaselineSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null);
        _viewModel.HasPendingConfigChanges = false;
    }

    private void MoveAllNodes()
    {
        for (int index = 0; index < _viewModel.IoFlowGraph.Nodes.Count; index++)
        {
            _viewModel.IoFlowGraph.Nodes[index].Location += new Vector(315 + index * 21, -120 - index * 11);
        }

        _viewModel.IoFlowGraph.ItemsDragCompletedCommand.Execute(null);
    }

    private static (string Kind, int Id, byte Option) Key(IoFlowNodeSnapshot node) => (node.Kind, node.Id, node.Option);

    private static void AssertLocations(IoFlowGraphSnapshot expected, IoFlowGraphSnapshot actual)
    {
        Assert.Equal(expected.Nodes.Count, actual.Nodes.Count);
        foreach (IoFlowNodeSnapshot node in expected.Nodes)
        {
            IoFlowNodeSnapshot match = Assert.Single(actual.Nodes, item => Key(item) == Key(node));
            Assert.Equal(new Point(node.X, node.Y), new Point(match.X, match.Y));
        }
    }

    private static FieldInfo GetField(string name) =>
        typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!;

    public void Dispose()
    {
        foreach (string fieldName in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
        {
            Assert.IsType<System.Timers.Timer>(GetField(fieldName).GetValue(_viewModel)).Dispose();
        }
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class TestAppCloser : IAppCloser
    {
        public bool ConfirmationResult { get; set; } = true;
        public bool DiscardDraftConfirmationResult { get; set; } = true;
        public int ConfirmationCount { get; private set; }
        public void CloseApp() { }
        public Task<string?> OpenPdmFileContentAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseLocalLogFilePathAsync(string initialDirectory) => Task.FromResult<string?>(null);
        public Task<string?> BrowseDbcFilePathAsync() => Task.FromResult<string?>(null);
        public Task<bool> SavePdmFileContentAsync(string content) => Task.FromResult(true);
        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL")
        {
            ConfirmationCount++;
            return Task.FromResult(title == "Discard retained I/O Flow draft"
                ? DiscardDraftConfirmationResult : ConfirmationResult);
        }
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
        public Task<LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync() => Task.FromResult<LocalFirmwareUpdateSelection?>(null);
        public Task ShowAboutAsync() => Task.CompletedTask;
        public Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel) => Task.CompletedTask;
        public Task<PinSetupDialogResult?> ShowPinSetupWizardAsync(bool isPinAlreadyEnabled) => Task.FromResult<PinSetupDialogResult?>(null);
        public Task<bool> ShowRecoveryCodeAsync(string recoveryCode) => Task.FromResult(true);
        public Task<PinUnlockDialogResult?> ShowPinUnlockDialogAsync(bool allowRecovery, uint retryAfterMs, string? statusMessage) => Task.FromResult<PinUnlockDialogResult?>(null);
    }
}
