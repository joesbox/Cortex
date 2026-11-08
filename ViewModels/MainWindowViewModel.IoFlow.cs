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
    private bool _ioFlowHasUnsavedControllerChanges;
    private string _ioFlowSnapshotMismatchWarning = string.Empty;
    private string _ioFlowControllerMismatchWarning = string.Empty;
    public IoFlowGraphViewModel IoFlowGraph { get; } = new();

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
        IoFlowGraph.GraphChangedAction = OnIoFlowGraphChanged;
        IoFlowGraph.ConfirmAction = (title, message, confirmText, cancelText) => _appCloser.ConfirmAsync(title, message, confirmText, cancelText);
        IoFlowGraph.SetCustomCanTxStateAction = SetCustomCanMessageTxStateAsync;
        IoFlowGraph.SetCustomCanTxModeAction = SetCustomCanMessageTxModeAsync;
        IoFlowGraph.Build(SettingsDataView, LiveDataView);
        _ioFlowHasUnsavedControllerChanges = false;
        _ioFlowSnapshotMismatchWarning = string.Empty;
        LoadIoFlowGraphSnapshotIfAvailable();
        EnsureIoFlowGraphPopulatedFromSettings();
        UpdateIoFlowControllerMismatchWarning();
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

        if (!_ioFlowGraphStore.TryLoad(out IoFlowGraphSnapshot snapshot))
        {
            return;
        }

        if (snapshot.DraftSettings != null)
        {
            RememberIoFlowDraft(snapshot);
            return;
        }

        // Restore local layout only when it matches the current controller configuration.
        TryApplyIoFlowSnapshot(snapshot, requireSignatureMatch: true, setMismatchWarning: false);
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

    private void PersistIoFlowGraphSnapshotLocally()
    {
        if (_isApplyingIoFlowSnapshot || _suppressDirtyTracking || _ioFlowDraftAwaitingReconciliation != null)
            return;
        IoFlowGraphSnapshot snapshot = IoFlowGraph.CreateSnapshot();
        snapshot.ControllerSettingsSignature = CreateIoFlowControllerSignature();
        if (_controllerConfigBaseline != null && HasPendingConfigChanges)
        {
            snapshot.DraftSettings = CreateIoFlowDraftSettings(SettingsDataView);
            snapshot.DraftBaselineSignature = CreateIoFlowDraftSignature(_controllerConfigBaseline);
        }
        _ioFlowGraphStore.Save(snapshot);
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
        return !string.Equals(current, baseline, StringComparison.Ordinal) || HasPendingCustomCanChanges();
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
        if (_ioFlowDraftAwaitingReconciliation == null) _ioFlowSnapshotMismatchWarning = string.Empty;

        // Keep the layout stable across sessions: when a stored snapshot exists but could not
        // be applied (e.g. signature mismatch at startup before connecting), reuse its node
        // locations and leave the stored snapshot untouched instead of overwriting it with
        // the auto-arranged default layout produced above.
        if (_ioFlowGraphStore.TryLoad(out IoFlowGraphSnapshot storedSnapshot))
        {
            IoFlowGraph.ApplyNodeLocations(storedSnapshot);
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
                        ConfigFileSerializer.SerializeSettings(SettingsDataView), StringComparison.Ordinal));

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
        PersistIoFlowGraphSnapshotLocally();
        return true;
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
        IoFlowGraphSnapshot snapshot = IoFlowGraph.CreateSnapshot();
        snapshot.ControllerSettingsSignature = CreateIoFlowControllerSignature();

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
