using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels.IoFlow;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.ViewModels;

public partial class MainWindowViewModel
{
    private readonly IoFlowGraphStore _ioFlowGraphStore;
    private IoFlowGraphSnapshot? _ioFlowBaselineSnapshot;
    private bool _isApplyingIoFlowSnapshot;
    private bool _deferIoFlowLayoutPersistence;
    private bool _deferredIoFlowLayoutReplace;
    private bool _ioFlowHasUnsavedControllerChanges;
    private string _ioFlowSnapshotMismatchWarning = string.Empty;
    private string _ioFlowControllerMismatchWarning = string.Empty;
    private IoFlowGraphViewModel _ioFlowGraph = new();
    public IoFlowGraphViewModel IoFlowGraph
    {
        get => _ioFlowGraph;
        private set => SetProperty(ref _ioFlowGraph, value);
    }

    public string IoFlowControllerMismatchWarning
    {
        get => _ioFlowControllerMismatchWarning;
        private set
        {
            if (SetProperty(ref _ioFlowControllerMismatchWarning, value))
            {
                OnPropertyChanged(nameof(HasIoFlowControllerMismatchWarning));
            }
        }
    }

    public bool HasIoFlowControllerMismatchWarning => !string.IsNullOrWhiteSpace(IoFlowControllerMismatchWarning);

    private void InitializeIoFlowGraph()
    {
        ConfigureIoFlowGraph(IoFlowGraph);
        IoFlowGraph.Build(SettingsDataView, LiveDataView);
        _ioFlowHasUnsavedControllerChanges = false;
        _ioFlowSnapshotMismatchWarning = string.Empty;
        LoadIoFlowGraphSnapshotIfAvailable();
        EnsureIoFlowGraphPopulatedFromSettings();
        UpdateIoFlowControllerMismatchWarning();
    }

    private void ConfigureIoFlowGraph(IoFlowGraphViewModel graph)
    {
        graph.GraphChangedAction = OnIoFlowGraphChanged;
        graph.ConfirmAction = (title, message, confirmText, cancelText) => _appCloser.ConfirmAsync(title, message, confirmText, cancelText);
        graph.SetCustomCanTxStateAction = SetCustomCanMessageTxStateAsync;
        graph.SetCustomCanTxModeAction = SetCustomCanMessageTxMode;
    }

    private void RefreshIoFlowGraphFromSettings()
    {
        IoFlowGraph.Build(SettingsDataView, LiveDataView);
        _ioFlowHasUnsavedControllerChanges = false;
        _ioFlowSnapshotMismatchWarning = string.Empty;

        LoadIoFlowGraphSnapshotIfAvailable();
        EnsureIoFlowGraphPopulatedFromSettings();
        UpdateIoFlowControllerMismatchWarning();
    }

    private void RefreshIoFlowConnections()
    {
        IoFlowGraph.SynchronizeCanTxNodesFromSettings(SettingsDataView);
        IoFlowGraph.RebuildConnections(SettingsDataView);
        UpdateIoFlowControllerMismatchWarning();
    }

    private void RefreshIoFlowLiveSamples()
    {
        IoFlowGraph.UpdateLiveChannelSamples(LiveDataView.ChannelsLiveData);
        OnPropertyChanged(nameof(SelectedMathRuntimeRootValueText));
        OnPropertyChanged(nameof(SelectedMathRuntimeRootStatusText));
        OnPropertyChanged(nameof(SelectedMathRuntimeAllRuleValuesText));
        OnPropertyChanged(nameof(SelectedMathRuntimeRuleValueText));
        OnPropertyChanged(nameof(SelectedMathRuntimeRuleEvaluationText));
        OnPropertyChanged(nameof(SelectedMathRuntimeOperandAText));
        OnPropertyChanged(nameof(SelectedMathRuntimeOperandBText));
        OnPropertyChanged(nameof(SelectedMathRuntimeResultText));
        OnPropertyChanged(nameof(SelectedMathRuntimeChannelErrorText));
        OnPropertyChanged(nameof(SelectedMathRuntimeChannelStatusBrush));
        OnPropertyChanged(nameof(SelectedMathRuntimeChannelStatusIcon));
        OnPropertyChanged(nameof(SelectedMathRuntimeRuleErrorText));
        OnPropertyChanged(nameof(SelectedMathRuntimeRuleStatusBrush));
        OnPropertyChanged(nameof(SelectedMathRuntimeRuleStatusIcon));
        OnPropertyChanged(nameof(SelectedMathRuntimeEvaluatedText));
        OnPropertyChanged(nameof(SelectedMathRuntimeEvaluatedStatusBrush));
        OnPropertyChanged(nameof(SelectedMathRuntimeEvaluatedStatusIcon));
    }

    private void LoadIoFlowGraphSnapshotIfAvailable()
    {
        IoFlowGraph.UseControllerConfigurationValues = false;
        if (_isApplyingIoFlowSnapshot) return;

        if (!_ioFlowGraphStore.TryLoad(out IoFlowGraphLayoutSnapshot snapshot))
        {
            return;
        }

        IoFlowGraph.ApplyNodePositions(snapshot);
    }

    private void PersistIoFlowGraphSnapshotForController()
    {
        PersistIoFlowGraphSnapshotLocally();
        IoFlowGraph.UseControllerConfigurationValues = true;
        CaptureIoFlowBaselineSnapshot();
        _ioFlowHasUnsavedControllerChanges = false;
        _ioFlowSnapshotMismatchWarning = string.Empty;
        UpdateIoFlowControllerMismatchWarning();
    }

    private void CaptureIoFlowBaselineSnapshot()
    {
        IoFlowGraph.ClearUndoHistory();
        if (_controllerConfigBaseline != null
            && CreateIoFlowDraftSignature(SettingsDataView) != CreateIoFlowDraftSignature(_controllerConfigBaseline))
        {
            var confirmedGraph = new IoFlowGraphViewModel();
            confirmedGraph.Build(DeepCopyDataStructures(_controllerConfigBaseline), LiveDataView);
            confirmedGraph.PopulateFromCurrentSettings();
            confirmedGraph.ApplyNodeLocations(IoFlowGraph.CreateSnapshot());
            _ioFlowBaselineSnapshot = confirmedGraph.CreateSnapshot();
            return;
        }
        _ioFlowBaselineSnapshot = IoFlowGraph.CreateSnapshot();
    }

    private void SyncIoFlowCanTxBaseline()
    {
        if (_ioFlowBaselineSnapshot == null)
        {
            return;
        }

        _ioFlowBaselineSnapshot.Nodes.RemoveAll(node => node.Kind == "CanTx");
        var txNodes = IoFlowGraph.CreateSnapshot().Nodes.Where(node => node.Kind == "CanTx").ToList();
        // Revert restores current canvas membership separately from baseline content.
        foreach (var node in txNodes)
            node.CanvasId = _ioFlowBaselineSnapshot.Canvases.FirstOrDefault()?.Id ?? 1;
        _ioFlowBaselineSnapshot.Nodes.AddRange(txNodes);
    }

    private void RevertIoFlowGraphToBaseline(IoFlowGraphSnapshot nodeLocations)
    {
        if (_ioFlowBaselineSnapshot == null)
        {
            return;
        }

        // Restore baseline content, but keep the current layout of surviving nodes.
        if (TryApplyIoFlowSnapshot(_ioFlowBaselineSnapshot, requireSignatureMatch: false, setMismatchWarning: false, nodeLocations: nodeLocations))
        {
            UpdateIoFlowControllerMismatchWarning();
        }
    }

    private void PersistIoFlowGraphSnapshotLocally(bool replaceLayout = false)
    {
        if (_isApplyingIoFlowSnapshot || _suppressDirtyTracking)
            return;
        if (_deferIoFlowLayoutPersistence)
        {
            _deferredIoFlowLayoutReplace |= replaceLayout;
            return;
        }
        // Until settings arrive, the graph lacks configured inputs and rule nodes.
        _ioFlowGraphStore.Save(IoFlowGraph.CreateLayoutSnapshot(),
            preserveUnmatchedNodePositions: !replaceLayout
                && (_controllerConfigBaseline == null || _awaitingInitialMathSettingsHydration));
    }

    private void OnIoFlowGraphChanged(bool controllerSettingsChanged)
    {
        if (_isApplyingIoFlowSnapshot)
        {
            return;
        }

        if (!_suppressDirtyTracking)
        {
            RecalculatePendingConfigChanges();
            _ioFlowHasUnsavedControllerChanges = HasPendingConfigChanges;
            if (controllerSettingsChanged && SelectedChannel?.IsMathChannel == true)
            {
                SelectedMathRuleSlotIndex = Math.Clamp(SelectedMathRuleSlotIndex, 0, Math.Max(0, SelectedChannel.MathRuleCount - 1));
            }
            if (controllerSettingsChanged) RaiseMathRuleUiPropertiesChanged();
        }
        PersistIoFlowGraphSnapshotLocally();
        UpdateIoFlowControllerMismatchWarning();
    }

    private bool DoesCurrentSettingsDifferFromControllerBaseline()
    {
        if (_controllerConfigBaseline == null)
        {
            return false;
        }

        string current = ConfigFileSerializer.SerializeSettings(SettingsDataView, includeCustomCanMetadata: false);
        string baseline = ConfigFileSerializer.SerializeSettings(_controllerConfigBaseline, includeCustomCanMetadata: false);
        CellularParameters currentCellular = SettingsDataView.CellularParamsStaticData;
        CellularParameters baselineCellular = _controllerConfigBaseline.CellularParamsStaticData;
        return !string.Equals(current, baseline, StringComparison.Ordinal)
            || !string.Equals(currentCellular.APNPassword ?? string.Empty, baselineCellular.APNPassword ?? string.Empty, StringComparison.Ordinal)
            || !string.Equals(currentCellular.MQTTPassword ?? string.Empty, baselineCellular.MQTTPassword ?? string.Empty, StringComparison.Ordinal)
            || HasPendingCustomCanChanges();
    }

    private void UpdateIoFlowControllerMismatchWarning()
    {
        if (!string.IsNullOrWhiteSpace(_ioFlowSnapshotMismatchWarning))
        {
            IoFlowControllerMismatchWarning = _ioFlowSnapshotMismatchWarning;
            return;
        }

        if (HasPendingConfigChanges && IoFlowGraph.OutputNodes.Count > 0)
        {
            IoFlowControllerMismatchWarning = "I/O Flow differs from what is currently saved on the controller. Save to controller to persist these changes.";
            return;
        }

        IoFlowControllerMismatchWarning = string.Empty;
    }

    private bool IsIoFlowSnapshotStructurallyValid(IoFlowGraphSnapshot snapshot)
    {
        if (!IoFlowGraphViewModel.IsCanvasLayoutValid(snapshot))
            return false;
        HashSet<int> logicNodeIds = [];
        HashSet<int> outputChannelNumbers = [];

        foreach (IoFlowNodeSnapshot node in snapshot.Nodes)
        {
            switch (node.Kind)
            {
                case "Digital":
                    if (node.Id < 1 || node.Id > SettingsDataView.DigitalInputsStaticData.Count)
                    {
                        return false;
                    }
                    break;
                case "Analogue":
                    if (node.Id < 1 || node.Id > SettingsDataView.AnalogueInputsStaticData.Count)
                    {
                        return false;
                    }
                    break;
                case "Output":
                    if (node.Id < 1 || node.Id > SettingsDataView.ChannelsStaticData.Count)
                    {
                        return false;
                    }

                    outputChannelNumbers.Add(node.Id);
                    break;
                case "OutputMath":
                    if (node.Id < 1 || node.Id > SettingsDataView.ChannelsStaticData.Count)
                    {
                        return false;
                    }
                    break;
                case "Logic":
                    if (node.Id < 1 || !logicNodeIds.Add(node.Id))
                    {
                        return false;
                    }
                    break;
                case "Ignition":
                    if (node.Id != 1)
                    {
                        return false;
                    }
                    break;
                case "Can":
                    if (node.Id != 1)
                    {
                        return false;
                    }
                    break;
                case "CanTx":
                    if (node.Id < 1 || node.Id > SettingsDataView.CustomCanMessagesStaticData.Count)
                    {
                        return false;
                    }
                    break;
                case "CanRx":
                    if (node.Id < 0 || node.Id >= SettingsDataView.CustomCanSignalsStaticData.Count)
                    {
                        return false;
                    }
                    break;
                case "SystemValue":
                    if (node.Id < 0 || node.Id > byte.MaxValue || !Enum.IsDefined((Cortex.Models.OutputChannel.MathSystemValue)node.Id))
                    {
                        return false;
                    }
                    break;
                default:
                    return false;
            }
        }

        foreach (IoFlowStandaloneLogicRuleSnapshot rule in snapshot.StandaloneLogicRules)
        {
            if (!logicNodeIds.Contains(rule.LogicNodeId))
            {
                return false;
            }
        }

        foreach ((int channelNumber, int logicNodeId) in snapshot.OutputRootLogicNodeByChannel)
        {
            if (!outputChannelNumbers.Contains(channelNumber) || !logicNodeIds.Contains(logicNodeId))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SnapshotContainsOutputNode(IoFlowGraphSnapshot snapshot)
    {
        return snapshot.Nodes.Any(node => string.Equals(node.Kind, "Output", StringComparison.Ordinal));
    }

    private static bool AreImportedGraphRulesValid(IoFlowGraphSnapshot snapshot)
    {
        var logicIds = snapshot.Nodes.Where(node => node.Kind == "Logic").Select(node => node.Id).ToHashSet();
        var rules = new Dictionary<int, IoFlowStandaloneLogicRuleSnapshot>();
        bool ValidOperand(byte type, byte index) => (OutputChannel.MathOperandType)type switch
        {
            OutputChannel.MathOperandType.Constant => true,
            OutputChannel.MathOperandType.Channel => index < Constants.NUM_OUTPUT_CHANNELS,
            OutputChannel.MathOperandType.AnalogueInput => index < Constants.NUM_ANALOGUE_INPUTS,
            OutputChannel.MathOperandType.DigitalInput => index < Constants.NUM_DIGITAL_INPUTS,
            OutputChannel.MathOperandType.SystemValue => Enum.IsDefined((OutputChannel.MathSystemValue)index),
            OutputChannel.MathOperandType.CanSignal => index < Constants.MAX_CUSTOM_CAN_SIGNALS,
            OutputChannel.MathOperandType.Rule => logicIds.Contains(index),
            _ => false,
        };

        foreach (var rule in snapshot.StandaloneLogicRules)
        {
            bool unary = rule.Operation is (byte)OutputChannel.MathOperation.NOT or (byte)OutputChannel.MathOperation.ABS or (byte)OutputChannel.MathOperation.TOGGLE;
            if (!rules.TryAdd(rule.LogicNodeId, rule) || !logicIds.Contains(rule.LogicNodeId) ||
                rule.Operation > (byte)OutputChannel.MathOperation.TOGGLE || rule.HysteresisEnabled > 1 ||
                (rule.HysteresisEnabled != 0 && (rule.Operation < (byte)OutputChannel.MathOperation.GREATER_THAN ||
                    rule.Operation > (byte)OutputChannel.MathOperation.LESS_EQUAL)) ||
                !float.IsFinite(rule.OnThreshold) || !float.IsFinite(rule.OffThreshold) ||
                !float.IsFinite(rule.OperandAConstant) || !float.IsFinite(rule.OperandBConstant) ||
                !ValidOperand(rule.OperandAType, rule.OperandAIndex) ||
                (!unary && !ValidOperand(rule.OperandBType, rule.OperandBIndex)))
                return false;
        }

        var visiting = new HashSet<int>();
        var visited = new HashSet<int>();
        bool HasCycle(int id)
        {
            if (visited.Contains(id) || !rules.TryGetValue(id, out var rule)) return false;
            if (!visiting.Add(id)) return true;
            bool unary = rule.Operation is (byte)OutputChannel.MathOperation.NOT or (byte)OutputChannel.MathOperation.ABS or (byte)OutputChannel.MathOperation.TOGGLE;
            if ((rule.OperandAType == (byte)OutputChannel.MathOperandType.Rule && HasCycle(rule.OperandAIndex)) ||
                (!unary && rule.OperandBType == (byte)OutputChannel.MathOperandType.Rule && HasCycle(rule.OperandBIndex)))
                return true;
            visiting.Remove(id);
            visited.Add(id);
            return false;
        }
        return rules.Keys.All(id => !HasCycle(id));
    }

    private void EnsureIoFlowGraphPopulatedFromSettings()
    {
        if (IoFlowGraph.OutputNodes.Count > 0)
        {
            return;
        }

        _isApplyingIoFlowSnapshot = true;
        try
        {
            IoFlowGraph.PopulateFromCurrentSettings();
        }
        finally
        {
            _isApplyingIoFlowSnapshot = false;
        }

        _ioFlowHasUnsavedControllerChanges = false;
        _ioFlowSnapshotMismatchWarning = string.Empty;

        if (_ioFlowGraphStore.TryLoad(out IoFlowGraphLayoutSnapshot storedSnapshot))
        {
            IoFlowGraph.ApplyNodePositions(storedSnapshot);
        }
        else
        {
            PersistIoFlowGraphSnapshotLocally();
        }
    }

    private bool TryApplyIoFlowSnapshot(IoFlowGraphSnapshot snapshot, bool requireSignatureMatch, bool setMismatchWarning, IoFlowGraphSnapshot? nodeLocations = null)
    {
        bool snapshotContainsOutputNode = SnapshotContainsOutputNode(snapshot);

        if (!IsIoFlowSnapshotStructurallyValid(snapshot))
        {
            if (setMismatchWarning && snapshotContainsOutputNode)
            {
                _ioFlowSnapshotMismatchWarning = "Saved I/O Flow content was ignored because it could not be validated against this controller configuration.";
            }

            return false;
        }

        if (requireSignatureMatch)
        {
            string controllerSignature = CreateIoFlowControllerSignature();
            bool signatureMatches = !string.IsNullOrWhiteSpace(snapshot.ControllerSettingsSignature)
                && (string.Equals(snapshot.ControllerSettingsSignature, controllerSignature, StringComparison.Ordinal)
                    || string.Equals(snapshot.ControllerSettingsSignature,
                        ConfigFileSerializer.SerializeSettings(SettingsDataView), StringComparison.Ordinal)
                    || IsLegacyIoFlowSignatureMatch(snapshot.ControllerSettingsSignature, controllerSignature));

            if (!signatureMatches)
            {
                if (setMismatchWarning && snapshotContainsOutputNode)
                {
                    _ioFlowSnapshotMismatchWarning = "Saved I/O Flow content does not match the current controller configuration.";
                }

                return false;
            }
        }

        IoFlowGraph.UseControllerConfigurationValues = true;

        _isApplyingIoFlowSnapshot = true;
        try
        {
            IoFlowGraph.ApplySnapshot(snapshot);
            IoFlowGraph.ReconcileMathConfiguration();
            if (nodeLocations != null)
            {
                IoFlowGraph.ApplyNodeLocations(nodeLocations);
            }
        }
        finally
        {
            _isApplyingIoFlowSnapshot = false;
        }

        _ioFlowHasUnsavedControllerChanges = false;
        _ioFlowSnapshotMismatchWarning = string.Empty;
        RecalculatePendingConfigChanges();
        PersistIoFlowGraphSnapshotLocally(replaceLayout: true);
        return true;
    }

    private bool IsLegacyIoFlowSignatureMatch(string signature, string controllerSignature)
    {
        if (!ConfigFileSerializer.TryDeserialize(signature, out var legacy, out _) || legacy == null ||
            legacy.Channels?.Count != SettingsDataView.ChannelsStaticData.Count ||
            legacy.DigitalInputs?.Count != SettingsDataView.DigitalInputsStaticData.Count ||
            legacy.AnalogueInputs?.Count != SettingsDataView.AnalogueInputsStaticData.Count ||
            legacy.SystemParameters == null)
            return false;
        DataStructures settings = DeepCopyDataStructures(SettingsDataView);
        ConfigFileSerializer.ApplySnapshot(settings, legacy);
        return string.Equals(CreateIoFlowDraftSignature(settings), controllerSignature, StringComparison.Ordinal);
    }

    private void ApplyIoFlowSnapshotFromConfig(IoFlowGraphSnapshot? snapshot)
    {
        if (snapshot == null)
        {
            EnsureIoFlowGraphPopulatedFromSettings();
            UpdateIoFlowControllerMismatchWarning();
            return;
        }

        if (!TryApplyIoFlowSnapshot(snapshot, requireSignatureMatch: true, setMismatchWarning: true))
        {
            EnsureIoFlowGraphPopulatedFromSettings();
        }

        UpdateIoFlowControllerMismatchWarning();
    }

    private IoFlowGraphConfigSnapshot CreateIoFlowConfigSnapshot()
    {
        return CreateIoFlowConfigSnapshotForSettings(IoFlowGraph.CreateSnapshot(), SettingsDataView);
    }

    private IoFlowGraphConfigSnapshot CreateIoFlowConfigSnapshotForSettings(IoFlowGraphSnapshot snapshot, DataStructures settings)
    {
        snapshot.ControllerSettingsSignature = CreateIoFlowDraftSignature(settings);

        return new IoFlowGraphConfigSnapshot
        {
            Canvases = snapshot.Canvases.Select(canvas => new IoFlowCanvasConfigSnapshot
            {
                Id = canvas.Id, Name = canvas.Name,
                ViewportX = canvas.ViewportX, ViewportY = canvas.ViewportY, ViewportZoom = canvas.ViewportZoom,
            }).ToList(),
            SelectedCanvasId = snapshot.SelectedCanvasId,
            ControllerSettingsSignature = snapshot.ControllerSettingsSignature,
            Nodes = snapshot.Nodes.Select(node => new IoFlowNodeConfigSnapshot
            {
                Kind = node.Kind,
                CanvasId = node.CanvasId,
                Id = node.Id,
                X = node.X,
                Y = node.Y,
                Option = node.Option,
                Option2 = node.Option2,
            }).ToList(),
            StandaloneLogicRules = snapshot.StandaloneLogicRules.Select(rule => new IoFlowStandaloneLogicRuleConfigSnapshot
            {
                LogicNodeId = rule.LogicNodeId,
                Operation = rule.Operation,
                HysteresisEnabled = rule.HysteresisEnabled,
                OnThreshold = rule.OnThreshold,
                OffThreshold = rule.OffThreshold,
                OperandAType = rule.OperandAType,
                OperandAIndex = rule.OperandAIndex,
                OperandAConstant = rule.OperandAConstant,
                OperandBType = rule.OperandBType,
                OperandBIndex = rule.OperandBIndex,
                OperandBConstant = rule.OperandBConstant,
            }).ToList(),
            OutputRootLogicNodeByChannel = new Dictionary<int, int>(snapshot.OutputRootLogicNodeByChannel),
        };
    }

    private static IoFlowGraphSnapshot ConvertIoFlowConfigSnapshot(IoFlowGraphConfigSnapshot snapshot)
    {
        return new IoFlowGraphSnapshot
        {
            Canvases = snapshot.Canvases.Select(canvas => new IoFlowCanvasSnapshot
            {
                Id = canvas.Id, Name = canvas.Name,
                ViewportX = canvas.ViewportX, ViewportY = canvas.ViewportY, ViewportZoom = canvas.ViewportZoom,
            }).ToList(),
            SelectedCanvasId = snapshot.SelectedCanvasId,
            ControllerSettingsSignature = snapshot.ControllerSettingsSignature,
            Nodes = snapshot.Nodes.Select(node => new IoFlowNodeSnapshot
            {
                Kind = node.Kind,
                CanvasId = node.CanvasId,
                Id = node.Id,
                X = node.X,
                Y = node.Y,
                Option = node.Option,
                Option2 = node.Option2,
            }).ToList(),
            StandaloneLogicRules = snapshot.StandaloneLogicRules.Select(rule => new IoFlowStandaloneLogicRuleSnapshot
            {
                LogicNodeId = rule.LogicNodeId,
                Operation = rule.Operation,
                HysteresisEnabled = rule.HysteresisEnabled,
                OnThreshold = rule.OnThreshold,
                OffThreshold = rule.OffThreshold,
                OperandAType = rule.OperandAType,
                OperandAIndex = rule.OperandAIndex,
                OperandAConstant = rule.OperandAConstant,
                OperandBType = rule.OperandBType,
                OperandBIndex = rule.OperandBIndex,
                OperandBConstant = rule.OperandBConstant,
            }).ToList(),
            OutputRootLogicNodeByChannel = new Dictionary<int, int>(snapshot.OutputRootLogicNodeByChannel),
        };
    }

    private string CreateIoFlowControllerSignature()
    {
        return CreateIoFlowDraftSignature(SettingsDataView);
    }
}
