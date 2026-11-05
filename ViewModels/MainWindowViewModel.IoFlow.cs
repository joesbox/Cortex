using Cortex.Services;
using Cortex.ViewModels.IoFlow;
using System;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.ViewModels;

public partial class MainWindowViewModel
{
    private readonly IoFlowGraphStore _ioFlowGraphStore;
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
        IoFlowGraph.RebuildConnections(SettingsDataView);
        UpdateIoFlowControllerMismatchWarning();
    }

    private void RefreshIoFlowLiveSamples()
    {
        IoFlowGraph.UpdateLiveChannelSamples(LiveDataView.ChannelsLiveData);
    }

    private void LoadIoFlowGraphSnapshotIfAvailable()
    {
        IoFlowGraph.UseControllerConfigurationValues = false;

        if (!_ioFlowGraphStore.TryLoad(out IoFlowGraphSnapshot snapshot))
        {
            return;
        }

        // Restore local layout only when it matches the current controller configuration.
        TryApplyIoFlowSnapshot(snapshot, requireSignatureMatch: true, setMismatchWarning: false);
    }

    private void PersistIoFlowGraphSnapshotForController()
    {
        PersistIoFlowGraphSnapshotLocally();
        IoFlowGraph.UseControllerConfigurationValues = true;
        _ioFlowHasUnsavedControllerChanges = false;
        _ioFlowSnapshotMismatchWarning = string.Empty;
        UpdateIoFlowControllerMismatchWarning();
    }

    private void PersistIoFlowGraphSnapshotLocally()
    {
        IoFlowGraphSnapshot snapshot = IoFlowGraph.CreateSnapshot();
        snapshot.ControllerSettingsSignature = CreateIoFlowControllerSignature();
        _ioFlowGraphStore.Save(snapshot);
    }

    private void OnIoFlowGraphChanged(bool controllerSettingsChanged)
    {
        if (_isApplyingIoFlowSnapshot)
        {
            return;
        }

        PersistIoFlowGraphSnapshotLocally();

        if (controllerSettingsChanged)
        {
            _ioFlowHasUnsavedControllerChanges = DoesCurrentSettingsDifferFromControllerBaseline();
        }
        else if (!DoesCurrentSettingsDifferFromControllerBaseline())
        {
            _ioFlowHasUnsavedControllerChanges = false;
        }

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
        return !string.Equals(current, baseline, StringComparison.Ordinal);
    }

    private void UpdateIoFlowControllerMismatchWarning()
    {
        if (!string.IsNullOrWhiteSpace(_ioFlowSnapshotMismatchWarning))
        {
            IoFlowControllerMismatchWarning = _ioFlowSnapshotMismatchWarning;
            return;
        }

        if (_ioFlowHasUnsavedControllerChanges && IoFlowGraph.OutputNodes.Count > 0)
        {
            IoFlowControllerMismatchWarning = "I/O Flow differs from what is currently saved on the controller. Save to controller to persist these changes.";
            return;
        }

        IoFlowControllerMismatchWarning = string.Empty;
    }

    private bool IsIoFlowSnapshotStructurallyValid(IoFlowGraphSnapshot snapshot)
    {
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
        _ioFlowSnapshotMismatchWarning = string.Empty;
        PersistIoFlowGraphSnapshotLocally();
    }

    private bool TryApplyIoFlowSnapshot(IoFlowGraphSnapshot snapshot, bool requireSignatureMatch, bool setMismatchWarning)
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
                && string.Equals(snapshot.ControllerSettingsSignature, controllerSignature, StringComparison.Ordinal);

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
        }
        finally
        {
            _isApplyingIoFlowSnapshot = false;
        }

        _ioFlowHasUnsavedControllerChanges = false;
        _ioFlowSnapshotMismatchWarning = string.Empty;
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
            ControllerSettingsSignature = snapshot.ControllerSettingsSignature,
            Nodes = snapshot.Nodes.Select(node => new IoFlowNodeConfigSnapshot
            {
                Kind = node.Kind,
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
            ControllerSettingsSignature = snapshot.ControllerSettingsSignature,
            Nodes = snapshot.Nodes.Select(node => new IoFlowNodeSnapshot
            {
                Kind = node.Kind,
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
        return ConfigFileSerializer.SerializeSettings(SettingsDataView);
    }
}
