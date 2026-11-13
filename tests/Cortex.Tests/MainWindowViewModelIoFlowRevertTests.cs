using Avalonia;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using Cortex.ViewModels.IoFlow;
using System.Reflection;
using System.Text.Json;
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigFileRestoresCanTxChangesAndEnablesSave(bool legacyFile)
    {
        var tx = Assert.Single(_viewModel.IoFlowGraph.CanTxNodes);
        await _viewModel.IoFlowGraph.ConnectNodes(
            _viewModel.IoFlowGraph.OutputNodes.Single(node => node.ChannelNumber == 1), tx);
        tx.EditIsPeriodic = true;
        tx.EditTimeoutMs = 1200;
        var flow = (IoFlowGraphConfigSnapshot)typeof(MainWindowViewModel)
            .GetMethod("CreateIoFlowConfigSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null)!;
        string content = ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView, flow);
        if (legacyFile)
        {
            var json = System.Text.Json.Nodes.JsonNode.Parse(content)!.AsObject();
            json.Remove("CanMessages");
            json.Remove("CanSignals");
            content = json.ToJsonString();
        }
        _viewModel.RevertChanges();
        ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfigContent = content;

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.True(_viewModel.CanSendConfig, _viewModel.IoFlowControllerMismatchWarning);
        Assert.DoesNotContain("does not match", _viewModel.IoFlowControllerMismatchWarning);
        var restored = Assert.Single(_viewModel.IoFlowGraph.CanTxNodes);
        Assert.True(restored.Message.IsPeriodic);
        Assert.Equal(1200, restored.Message.TimeoutMs);
        Assert.Equal(0, restored.Message.TxTrigger);
    }

    [Fact]
    public async Task InvalidConfigFileLeavesSettingsAndGraphUnchanged()
    {
        var originalSettings = _viewModel.SettingsDataView;
        uint originalId = originalSettings.CustomCanMessagesStaticData[0].Id;
        var originalGraph = _viewModel.IoFlowGraph.CreateSnapshot();
        var closer = (TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!;
        closer.ConfigContent = """{"CanMessages":[{"Index":0,"Id":801},{"Index":999,"Id":123}]}""";

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.Same(originalSettings, _viewModel.SettingsDataView);
        Assert.Equal(originalId, originalSettings.CustomCanMessagesStaticData[0].Id);
        Assert.Equal(JsonSerializer.Serialize(originalGraph), JsonSerializer.Serialize(_viewModel.IoFlowGraph.CreateSnapshot()));
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    public async Task InvalidCachedOrStoredLogReportsFailureWithoutEscaping(bool cached, bool locked, bool viaSelection)
    {
        string fileName = $"cortex-test-{Guid.NewGuid():N}.csv";
        string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Synapse PDM Logs", fileName);
        var file = new LogFile { FileName = fileName, IsControllerFile = true, IsDownloaded = true };
        GetField("selectedLogFile").SetValue(_viewModel, file);
        FileStream? fileLock = null;
        try
        {
            if (cached)
            {
                var cache = (Dictionary<string, string>)GetField("_downloadedLogCache").GetValue(_viewModel)!;
                string key = (string)typeof(MainWindowViewModel).GetMethod("BuildLogCacheKey", BindingFlags.NonPublic | BindingFlags.Static)!
                    .Invoke(null, [file])!;
                cache[key] = "invalid log";
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, "invalid log");
                if (locked) fileLock = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            }

            if (viaSelection)
                await (Task)typeof(MainWindowViewModel).GetMethod("HandleSelectedLogFileChangedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(_viewModel, [file])!;
            else
                await _viewModel.DownloadSelectedLogFileCommand.ExecuteAsync(null);

            Assert.Equal(cached ? "Couldn't open the cached log. Please try again."
                : "Couldn't open the saved log. The file may be invalid or unavailable.", _viewModel.LogStatusMessage);
            Assert.False(file.IsDownloaded);
            Assert.False(_viewModel.IsLogBusy);
            Assert.Equal(0, _viewModel.LogDownloadProgress);
        }
        finally
        {
            fileLock?.Dispose();
            if (!cached) File.Delete(path);
        }
    }

    [Fact]
    public async Task InvalidLocalLogSelectionShowsFriendlyError()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "invalid.csv");
        await File.WriteAllTextAsync(path, "invalid log");
        var file = new LogFile { FileName = "invalid.csv", FullPath = path };
        GetField("selectedLogFile").SetValue(_viewModel, file);

        await (Task)typeof(MainWindowViewModel).GetMethod("HandleSelectedLogFileChangedAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, [file])!;

        Assert.Equal("Couldn't open the selected log. The file is not a valid PDM log.", _viewModel.LogStatusMessage);
    }

    [Fact]
    public async Task InvalidGraphDoesNotApplyOtherwiseValidSettings()
    {
        var originalSettings = _viewModel.SettingsDataView;
        uint originalId = originalSettings.CustomCanMessagesStaticData[0].Id;
        var closer = (TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!;
        closer.ConfigContent = """{"CanMessages":[{"Index":0,"Id":801}],"IoFlowGraph":{"Nodes":[{"Kind":"Invalid","Id":1}]}}""";

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.Same(originalSettings, _viewModel.SettingsDataView);
        Assert.Equal(originalId, originalSettings.CustomCanMessagesStaticData[0].Id);
    }

    [Theory]
    [InlineData("""{"CanSignals":[{"Index":0,"MessageIndex":0,"StartBit":63,"BitLength":2,"Flags":1,"Factor":1}]}""")]
    [InlineData("""{"CanMessages":[{"Index":0,"Id":151,"Dlc":1,"Flags":5,"TxTrigger":255}],"CanSignals":[{"Index":0,"MessageIndex":0,"StartBit":8,"BitLength":1,"Flags":1,"Factor":1}]}""")]
    [InlineData("""{"Channels":[{"ChanType":7,"MathRuleCount":1,"MathRootRule":0,"MathRules":[{"OperandAType":1,"OperandAIndex":14}]}]}""")]
    [InlineData("""{"Channels":[{"ChanType":7,"MathRuleCount":1,"MathRootRule":0,"MathRules":[{"OperandAType":4,"OperandAIndex":0}]}]}""")]
    [InlineData("""{"IoFlowGraph":{"Nodes":[{"Kind":"Logic","Id":1}],"StandaloneLogicRules":[{"LogicNodeId":1,"OperandAType":4,"OperandAIndex":1}]}}""")]
    public async Task InvalidCrossReferencesLeaveSettingsAndGraphUnchanged(string json)
    {
        var originalSettings = _viewModel.SettingsDataView;
        string settingsBefore = ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView);
        string graphBefore = JsonSerializer.Serialize(_viewModel.IoFlowGraph.CreateSnapshot());
        ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfigContent = json;

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.Same(originalSettings, _viewModel.SettingsDataView);
        Assert.Equal(settingsBefore, ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView));
        Assert.Equal(graphBefore, JsonSerializer.Serialize(_viewModel.IoFlowGraph.CreateSnapshot()));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 7)]
    public async Task ValidCanSignalAtDlcBoundaryIsImported(int flags, int startBit)
    {
        var closer = (TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!;
        closer.ConfigContent = $$"""{"CanMessages":[{"Index":0,"Id":151,"Dlc":1,"Flags":5,"TxTrigger":255}],"CanSignals":[{"Index":0,"MessageIndex":0,"StartBit":{{startBit}},"BitLength":8,"Flags":{{flags | 1}},"Factor":1}]}""";

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.Equal(1, _viewModel.SettingsDataView.CustomCanMessagesStaticData[0].Dlc);
        Assert.Equal(startBit, _viewModel.SettingsDataView.CustomCanSignalsStaticData[0].StartBit);
        Assert.True(_viewModel.SettingsDataView.CustomCanSignalsStaticData[0].IsEnabled);
    }

    [Fact]
    public async Task PartialMessageImportChecksExistingSignalAgainstNewDlc()
    {
        var signal = _viewModel.SettingsDataView.CustomCanSignalsStaticData[0];
        signal.MessageIndex = 0;
        signal.StartBit = 8;
        signal.BitLength = 1;
        signal.Factor = 1;
        signal.IsEnabled = true;
        string settingsBefore = ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView);
        ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfigContent =
            """{"CanMessages":[{"Index":0,"Id":151,"Dlc":1,"Flags":5,"TxTrigger":255}]}""";

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.Equal(settingsBefore, ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView));
    }

    [Fact]
    public async Task GraphApplicationFailureRestoresSettingsAndGraph()
    {
        var originalPosition = _viewModel.IoFlowGraph.Nodes[0].Location;
        _viewModel.IoFlowGraph.ItemsDragStartedCommand.Execute(null);
        MoveAllNodes();
        Assert.True(_viewModel.IoFlowGraph.CanUndo);
        string settingsBefore = ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView);
        string graphBefore = JsonSerializer.Serialize(_viewModel.IoFlowGraph.CreateSnapshot());
        string layoutPath = Path.Combine(_directory, "ioflow-graph.json");
        File.AppendAllText(layoutPath, Environment.NewLine);
        string layoutBefore = File.ReadAllText(layoutPath);
        var originalGraph = _viewModel.IoFlowGraph;
        bool failureInjected = false;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IoFlowGraph) &&
                !ReferenceEquals(_viewModel.IoFlowGraph, originalGraph))
            {
                _viewModel.IoFlowGraph.Nodes.CollectionChanged += (_, nodeArgs) =>
                {
                    if (!failureInjected && nodeArgs.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Add)
                    {
                        failureInjected = true;
                        throw new InvalidOperationException("Injected graph application failure");
                    }
                };
            }
        };
        ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfigContent =
            """{"SystemParameters":{"CANResEnabled":true}}""";

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.True(failureInjected);
        Assert.Equal(settingsBefore, ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView));
        Assert.Equal(graphBefore, JsonSerializer.Serialize(_viewModel.IoFlowGraph.CreateSnapshot()));
        Assert.Same(originalGraph, _viewModel.IoFlowGraph);
        Assert.True(_viewModel.IoFlowGraph.CanUndo);
        Assert.Equal(layoutBefore, File.ReadAllText(layoutPath));
        await _viewModel.IoFlowGraph.UndoCommand.ExecuteAsync(null);
        Assert.Equal(originalPosition, _viewModel.IoFlowGraph.Nodes[0].Location);
    }

    [Fact]
    public async Task SettingsReplacementFailureRestoresSettingsAndGraph()
    {
        string settingsBefore = ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView);
        string graphBefore = JsonSerializer.Serialize(_viewModel.IoFlowGraph.CreateSnapshot());
        var originalSettings = _viewModel.SettingsDataView;
        bool failureInjected = false;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (!failureInjected && args.PropertyName == nameof(MainWindowViewModel.SettingsDataView) &&
                !ReferenceEquals(_viewModel.SettingsDataView, originalSettings))
            {
                failureInjected = true;
                throw new InvalidOperationException("Injected settings replacement failure");
            }
        };
        ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfigContent =
            """{"SystemParameters":{"CANResEnabled":true}}""";

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.True(failureInjected);
        Assert.Equal(settingsBefore, ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView));
        Assert.Equal(graphBefore, JsonSerializer.Serialize(_viewModel.IoFlowGraph.CreateSnapshot()));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(255)]
    public async Task GroupNumberRetainsFirmwareByteRange(byte group)
    {
        _viewModel.SettingsDataView.ChannelsStaticData[0].GroupNumber = 42;
        ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfigContent =
            $$"""{"Channels":[{"GroupNumber":{{group}}}]}""";

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.Equal(group, _viewModel.SettingsDataView.ChannelsStaticData[0].GroupNumber);
    }

    [Fact]
    public async Task PartialImportPreservesUnspecifiedThresholdsAndRuntimeValues()
    {
        var channel = _viewModel.SettingsDataView.ChannelsStaticData[0];
        channel.SetImportedThresholds(2, 4);
        channel.CurrentValue = 15;
        ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfigContent =
            """{"SystemParameters":{"CANResEnabled":true}}""";

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        channel = _viewModel.SettingsDataView.ChannelsStaticData[0];
        Assert.Equal(2, channel.OnThreshold);
        Assert.Equal(4, channel.OffThreshold);
        Assert.Equal(15, channel.CurrentValue);
        Assert.True(_viewModel.SettingsDataView.SystemParamsStaticData.CANResEnabled);
    }

    [Theory]
    [InlineData(false, null, "", "new-password")]
    [InlineData(true, null, "", "new-password")]
    [InlineData(false, "old-password", "old-password", "")]
    [InlineData(true, "old-password", "old-password", "")]
    public void PasswordOnlyChangesRemainPendingAfterRecalculation(bool mqtt, string? baselinePassword, string initialPassword, string editedPassword)
    {
        var baseline = ((DataStructures)GetField("_controllerConfigBaseline").GetValue(_viewModel)!).CellularParamsStaticData;
        var current = _viewModel.SettingsDataView.CellularParamsStaticData;
        if (mqtt)
        {
            baseline.MQTTPassword = baselinePassword;
            current.MQTTPassword = initialPassword;
        }
        else
        {
            baseline.APNPassword = baselinePassword;
            current.APNPassword = initialPassword;
        }

        var recalculate = typeof(MainWindowViewModel).GetMethod("RecalculatePendingConfigChanges", BindingFlags.NonPublic | BindingFlags.Instance)!;
        recalculate.Invoke(_viewModel, null);
        Assert.False(_viewModel.CanSendConfig);
        if (mqtt) current.MQTTPassword = editedPassword;
        else current.APNPassword = editedPassword;
        recalculate.Invoke(_viewModel, null);
        Assert.True(_viewModel.CanSendConfig);
        if (mqtt) current.MQTTPassword = baselinePassword;
        else current.APNPassword = baselinePassword;
        recalculate.Invoke(_viewModel, null);
        Assert.False(_viewModel.CanSendConfig);
    }

    [Fact]
    public async Task LegacyPasswordOnlyImportRemainsPending()
    {
        ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfigContent =
            """{"CellularParameters":{"APNPassword":"legacy-apn","MQTTPassword":"legacy-mqtt"}}""";

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.Equal("legacy-apn", _viewModel.SettingsDataView.CellularParamsStaticData.APNPassword);
        Assert.Equal("legacy-mqtt", _viewModel.SettingsDataView.CellularParamsStaticData.MQTTPassword);
        Assert.True(_viewModel.CanSendConfig);
    }

    [Fact]
    public async Task ConfigFileExportOmitsPasswordsFromSettingsAndGraph()
    {
        _viewModel.SettingsDataView.CellularParamsStaticData.APNPassword = "export-apn-secret";
        _viewModel.SettingsDataView.CellularParamsStaticData.MQTTPassword = "export-mqtt-secret";
        var closer = (TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!;

        await _viewModel.SaveConfigFileCommand.ExecuteAsync(null);

        Assert.NotNull(closer.SavedConfigContent);
        Assert.DoesNotContain("export-apn-secret", closer.SavedConfigContent);
        Assert.DoesNotContain("export-mqtt-secret", closer.SavedConfigContent);
        Assert.DoesNotContain("APNPassword", closer.SavedConfigContent);
        Assert.DoesNotContain("MQTTPassword", closer.SavedConfigContent);
        Assert.Contains("IoFlowGraph", closer.SavedConfigContent);
    }

    [Fact]
    public async Task LegacyFullSettingsSignatureIgnoresLiveValues()
    {
        var flow = (IoFlowGraphConfigSnapshot)typeof(MainWindowViewModel)
            .GetMethod("CreateIoFlowConfigSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null)!;
        var legacy = System.Text.Json.Nodes.JsonNode.Parse(ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView))!.AsObject();
        legacy["Channels"]![0]!["CurrentValue"] = 42;
        legacy["SystemParameters"]!["VBatt"] = 14;
        flow.ControllerSettingsSignature = legacy.ToJsonString();
        string content = ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView, flow);
        ((TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!).ConfigContent = content;

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.DoesNotContain("does not match", _viewModel.IoFlowControllerMismatchWarning);
    }

    [Fact]
    public void FailedConnectionDisposesServiceBeforeDroppingIt()
    {
        SerialPortService? attemptedService = null;
        _viewModel.IsConnected = true;
        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.IsConnected) && !_viewModel.IsConnected)
            {
                attemptedService = (SerialPortService?)GetField("_portService").GetValue(_viewModel);
            }
        };

        _viewModel.ConnectCommand.Execute("CORTEX_INVALID_TEST_PORT");

        Assert.NotNull(attemptedService);
        Assert.Null(GetField("_portService").GetValue(_viewModel));
        Assert.True((bool)typeof(SerialPortService).GetField("_isDisposed",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(attemptedService)!);
        Assert.True(((Task)typeof(SerialPortService).GetField("_parseLoopTask",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(attemptedService)!).IsCompleted);
        Assert.False(_viewModel.IsConnected);
    }

    [Fact]
    public async Task ConfigFileRestoresPendingLogicAndEnablesSave()
    {
        var graph = _viewModel.IoFlowGraph;
        var logic = Assert.Single(graph.MathRuleNodes, node => node.IsStandalone);
        logic.Rule.OperandAConstant = 42;
        await graph.ConnectNodes(logic, graph.OutputNodes.Single(node => node.ChannelNumber == 1));
        var flow = (IoFlowGraphConfigSnapshot)typeof(MainWindowViewModel)
            .GetMethod("CreateIoFlowConfigSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null)!;
        string content = ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView, flow);
        _viewModel.RevertChanges();
        var closer = (TestAppCloser)GetField("_appCloser").GetValue(_viewModel)!;
        closer.ConfigContent = content;

        await _viewModel.OpenConfigFileCommand.ExecuteAsync(null);

        Assert.True(_viewModel.CanSendConfig, _viewModel.IoFlowControllerMismatchWarning);
        Assert.Equal(42, _viewModel.SettingsDataView.ChannelsStaticData[0].MathRules[0].OperandAConstant);
        Assert.Contains("Save to controller", _viewModel.IoFlowControllerMismatchWarning);
        Assert.Contains(_viewModel.IoFlowGraph.MathRuleNodes, node => node.IsStandalone && node.Rule.OperandAConstant == 42);
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
            Assert.False(new Rect(nodes[0].Location, new Size(310, 300))
                .Intersects(new Rect(nodes[1].Location, new Size(310, 300))));
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
        Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot stored));
        Assert.DoesNotContain(stored.NodePositions, node => node.Kind == "CanTx");

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
        _viewModel.SettingsDataView.ChannelsStaticData[0].CurrentThresholdHigh += 1;

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
        Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot persisted));
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
        Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot persisted));
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
        Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot persisted));
        AssertLocations(expected, persisted);

        var reopenedGraph = new IoFlowGraphViewModel();
        reopenedGraph.Build(_viewModel.SettingsDataView, _viewModel.LiveDataView);
        reopenedGraph.PopulateFromCurrentSettings();
        reopenedGraph.ApplyNodePositions(persisted);
        IoFlowNodeSnapshot expectedOutput = Assert.Single(expected.Nodes, node => node.Kind == "Output" && node.Id == 1);
        OutputNodeViewModel restoredOutput = Assert.Single(reopenedGraph.OutputNodes, node => node.ChannelNumber == 1);
        Assert.Equal(new Point(expectedOutput.X, expectedOutput.Y), restoredOutput.Location);
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
        Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot persisted));
        AssertLocations(latestLayout, persisted);
    }

    [Fact]
    public async Task RestartDropsUncommittedControllerEditsButRestoresCanvasLayout()
    {
        var graph = _viewModel.IoFlowGraph;
        var first = graph.SelectedCanvas;
        first.Name = "Inputs";
        graph.AddCanvasCommand.Execute(null);
        var destination = graph.SelectedCanvas;
        destination.Name = "Lighting";
        destination.ViewportLocation = new Point(82, 91);
        destination.ViewportZoom = 0.6;
        graph.SelectedCanvas = first;
        foreach (var node in graph.VisibleNodes)
            graph.SelectedNodes.Add(node);
        await graph.MoveSelectedNodesToCanvasCommand.ExecuteAsync(destination);
        graph.AddCanvasCommand.Execute(null);
        graph.SelectedCanvas.Name = "Accessories";
        Assert.False(_viewModel.HasPendingConfigChanges);
        Assert.False(_viewModel.HasIoFlowControllerMismatchWarning);
        _viewModel.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1 = 91;
        MoveAllNodes();
        IoFlowGraphSnapshot expected = _viewModel.IoFlowGraph.CreateSnapshot();
        Assert.True(_viewModel.HasPendingConfigChanges);
        Assert.True(_viewModel.HasIoFlowControllerMismatchWarning);
        var restarted = new MainWindowViewModel(new TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: _store));
        foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
            Assert.IsType<System.Timers.Timer>(GetField(name).GetValue(restarted)).Stop();
        try
        {
            Assert.False(restarted.HasPendingConfigChanges);
            Assert.False(restarted.HasIoFlowControllerMismatchWarning);
            Assert.NotEqual(91, restarted.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1);
            IoFlowNodeSnapshot expectedOutput = Assert.Single(expected.Nodes, node => node.Kind == "Output" && node.Id == 1);
            OutputNodeViewModel restoredOutput = Assert.Single(restarted.IoFlowGraph.OutputNodes, node => node.ChannelNumber == 1);
            Assert.Equal(new Point(expectedOutput.X, expectedOutput.Y), restoredOutput.Location);
            Assert.Equal(destination.Id, restoredOutput.CanvasId);
            Assert.Equal(["Inputs", "Lighting", "Accessories"], restarted.IoFlowGraph.Canvases.Select(canvas => canvas.Name));
            Assert.Equal(expected.SelectedCanvasId, restarted.IoFlowGraph.SelectedCanvas.Id);
            Assert.Empty(restarted.IoFlowGraph.VisibleNodes);
            Assert.Equal(destination.ViewportLocation, restarted.IoFlowGraph.Canvases[1].ViewportLocation);
            Assert.Equal(destination.ViewportZoom, restarted.IoFlowGraph.Canvases[1].ViewportZoom);

            typeof(MainWindowViewModel).GetMethod("RefreshIoFlowGraphFromSettings", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(restarted, null);
            Assert.Equal(["Inputs", "Lighting", "Accessories"], restarted.IoFlowGraph.Canvases.Select(canvas => canvas.Name));
            Assert.Equal(expected.SelectedCanvasId, restarted.IoFlowGraph.SelectedCanvas.Id);
            Assert.Equal(destination.Id, restarted.IoFlowGraph.OutputNodes.First(node => node.ChannelNumber == 1).CanvasId);
            Assert.False(restarted.HasPendingConfigChanges);

            Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot stored));
            Assert.Equal(expected.Nodes.Count, stored.NodePositions.Count);
            string json = File.ReadAllText(Path.Combine(_directory, "ioflow-graph.json"));
            Assert.DoesNotContain("DraftSettings", json);
            Assert.DoesNotContain("StandaloneLogicRules", json);
            Assert.DoesNotContain("ControllerSettingsSignature", json);
            Assert.Contains("Canvases", json);
        }
        finally
        {
            foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
                Assert.IsType<System.Timers.Timer>(GetField(name).GetValue(restarted)).Dispose();
        }
    }

    [Fact]
    public void LegacyPersistedDraftIsIgnoredWhileItsNodePositionsAreMigrated()
    {
        _viewModel.IoFlowGraph.SelectedCanvas.Name = "Lighting";
        _viewModel.IoFlowGraph.AddCanvasCommand.Execute(null);
        _viewModel.IoFlowGraph.SelectedCanvas.Name = "Accessories";
        IoFlowGraphSnapshot graph = _viewModel.IoFlowGraph.CreateSnapshot();
        Assert.True(ConfigFileSerializer.TryDeserialize(
            ConfigFileSerializer.SerializeSettings(_viewModel.SettingsDataView),
            out ConfigSnapshot? draftSettings,
            out string? error), error);
        draftSettings!.AnalogueInputs![0].CalibrationValue1 = 91;
        string path = Path.Combine(_directory, "ioflow-graph.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            graph.Canvases,
            graph.SelectedCanvasId,
            graph.ControllerSettingsSignature,
            DraftSettings = new
            {
                InputsAndOutputs = draftSettings,
                CanMessages = Array.Empty<object>(),
                CanSignals = Array.Empty<object>(),
            },
            DraftBaselineSignature = "obsolete",
            graph.Nodes,
            graph.StandaloneLogicRules,
            graph.OutputRootLogicNodeByChannel,
        }));

        var restarted = new MainWindowViewModel(new TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: _store));
        foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
            Assert.IsType<System.Timers.Timer>(GetField(name).GetValue(restarted)).Stop();
        try
        {
            Assert.False(restarted.HasPendingConfigChanges);
            Assert.False(restarted.HasIoFlowControllerMismatchWarning);
            Assert.NotEqual(91, restarted.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1);
            IoFlowNodeSnapshot expectedOutput = Assert.Single(graph.Nodes, node => node.Kind == "Output" && node.Id == 1);
            OutputNodeViewModel restoredOutput = Assert.Single(restarted.IoFlowGraph.OutputNodes, node => node.ChannelNumber == 1);
            Assert.Equal(new Point(expectedOutput.X, expectedOutput.Y), restoredOutput.Location);
            Assert.Equal(["Lighting", "Accessories"], restarted.IoFlowGraph.Canvases.Select(canvas => canvas.Name));
            Assert.Equal(graph.SelectedCanvasId, restarted.IoFlowGraph.SelectedCanvas.Id);
            Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot migrated));
            Assert.Equal(graph.Nodes.Count, migrated.NodePositions.Count);
            Assert.Equal(2, migrated.Canvases.Count);
            Assert.DoesNotContain("DraftSettings", File.ReadAllText(path));
        }
        finally
        {
            foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
                Assert.IsType<System.Timers.Timer>(GetField(name).GetValue(restarted)).Dispose();
        }
    }

    [Fact]
    public void OrdinaryInputEditsWarnButDoNotPersistControllerDraft()
    {
        _viewModel.SettingsDataView.AnalogueInputsStaticData[0].CalibrationValue1 = 91;
        Assert.True(_viewModel.CanSendConfig);
        Assert.True(_viewModel.HasIoFlowControllerMismatchWarning);
        Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot layout));
        Assert.NotEmpty(layout.NodePositions);
        _viewModel.RevertChanges();
        Assert.False(_viewModel.CanSendConfig);
        Assert.False(_viewModel.HasIoFlowControllerMismatchWarning);
        Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot reverted));
        Assert.Equal(layout.NodePositions.Count, reverted.NodePositions.Count);
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
        Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot saved));
        var savedRule = Assert.Single(saved.NodePositions, node => node.Kind == "OutputMath" && node.Id == 2);
        var standalone = Assert.Single(snapshot.Nodes, node => node.Kind == "Logic");
        Assert.Equal(standalone.X, savedRule.X);
        Assert.Equal(standalone.Y, savedRule.Y);
        typeof(MainWindowViewModel).GetMethod("CaptureIoFlowBaselineSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(_viewModel, null);
        _viewModel.RevertChanges();
        Assert.Equal(0, _viewModel.SettingsDataView.ChannelsStaticData[1].MathRules[0].OperandAConstant);
        Assert.Equal(2, _viewModel.SettingsDataView.ChannelsStaticData[1].MathRuleCount);
        Assert.False(_viewModel.CanSendConfig);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void FreshControllerReadbackPreservesPendingChangesInCurrentSession(bool customCanReady)
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
            Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot waiting));
            Assert.NotEmpty(waiting.NodePositions);
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
        Assert.True(_store.TryLoad(out IoFlowGraphLayoutSnapshot persisted));
        Assert.NotEmpty(persisted.NodePositions);
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

    private static (string Kind, int Id, byte Option) Key(IoFlowNodePositionSnapshot node) => (node.Kind, node.Id, node.Option);

    private static void AssertLocations(IoFlowGraphSnapshot expected, IoFlowGraphSnapshot actual)
    {
        Assert.Equal(expected.Nodes.Count, actual.Nodes.Count);
        foreach (IoFlowNodeSnapshot node in expected.Nodes)
        {
            IoFlowNodeSnapshot match = Assert.Single(actual.Nodes, item => Key(item) == Key(node));
            Assert.Equal(new Point(node.X, node.Y), new Point(match.X, match.Y));
        }
    }

    private static void AssertLocations(IoFlowGraphSnapshot expected, IoFlowGraphLayoutSnapshot actual)
    {
        Assert.Equal(expected.Nodes.Count, actual.NodePositions.Count);
        foreach (IoFlowNodeSnapshot node in expected.Nodes)
        {
            IoFlowNodePositionSnapshot match = Assert.Single(actual.NodePositions, item => Key(item) == Key(node));
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
        public string? ConfigContent { get; set; }
        public string? SavedConfigContent { get; private set; }
        public bool ConfirmationResult { get; set; } = true;
        public int ConfirmationCount { get; private set; }
        public void CloseApp() { }
        public Task<string?> OpenPdmFileContentAsync() => Task.FromResult(ConfigContent);
        public Task<string?> BrowseLocalLogFilePathAsync(string initialDirectory) => Task.FromResult<string?>(null);
        public Task<string?> BrowseDbcFilePathAsync() => Task.FromResult<string?>(null);
        public Task<bool> SavePdmFileContentAsync(string content)
        {
            SavedConfigContent = content;
            return Task.FromResult(true);
        }
        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL")
        {
            ConfirmationCount++;
            return Task.FromResult(ConfirmationResult);
        }
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
        public Task<LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync() => Task.FromResult<LocalFirmwareUpdateSelection?>(null);
        public Task ShowAboutAsync() => Task.CompletedTask;
        public Task<ConfigurationBackupEntry?> ShowConfigurationBackupDialogAsync(IReadOnlyList<ConfigurationBackupEntry> backups) => Task.FromResult<ConfigurationBackupEntry?>(null);
        public Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel) => Task.CompletedTask;
        public Task<PinSetupDialogResult?> ShowPinSetupWizardAsync(bool isPinAlreadyEnabled) => Task.FromResult<PinSetupDialogResult?>(null);
        public Task<bool> ShowRecoveryCodeAsync(string recoveryCode) => Task.FromResult(true);
        public Task<PinUnlockDialogResult?> ShowPinUnlockDialogAsync(bool allowRecovery, uint retryAfterMs, string? statusMessage) => Task.FromResult<PinUnlockDialogResult?>(null);
    }
}
