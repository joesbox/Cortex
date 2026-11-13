using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels.IoFlow;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Cortex.ViewModels;

public partial class MainWindowViewModel
{
    private sealed record IoFlowPendingDraft(
        IoFlowGraphSnapshot Snapshot,
        IoFlowDraftSettings Settings,
        string BaselineSignature);

    private IoFlowPendingDraft? _ioFlowDraftAwaitingReconciliation;
    private bool _reconcilingIoFlowDraft;
    private bool _ioFlowDraftDecisionDeferred;

    private static IoFlowDraftSettings CreateIoFlowDraftSettings(DataStructures settings)
    {
        ConfigSnapshot config = ConfigFileSerializer.CreateSettingsSnapshot(settings);
        config.SystemParameters = null;
        config.CellularParameters = null;
        config.IoFlowGraph = null;
        config.CanMessages = null;
        config.CanSignals = null;
        foreach (var channel in config.Channels!)
        {
            channel.AnalogRaw = null;
            channel.CurrentValue = null;
            channel.Override = null;
            channel.ErrorFlags = null;
            channel.MathCalculatedRootValue = null;
            channel.MathError = null;
            foreach (var rule in channel.MathRules!)
            {
                rule.RuntimeValue = null;
                rule.RuntimeError = null;
                rule.Evaluated = null;
            }
        }
        return new IoFlowDraftSettings
        {
            InputsAndOutputs = config,
            CanMessages = settings.CustomCanMessagesStaticData.Select(message => new CustomCanMessage
            {
                Index = message.Index, Id = message.Id, Dlc = message.Dlc, Name = message.Name,
                Flags = message.Flags, TimeoutMs = message.TimeoutMs, TxTrigger = message.TxTrigger,
            }).ToList(),
            CanSignals = settings.CustomCanSignalsStaticData.Select(signal => new CustomCanSignal
            {
                Index = signal.Index, MessageIndex = signal.MessageIndex, StartBit = signal.StartBit,
                BitLength = signal.BitLength, Flags = signal.Flags, ValueSource = signal.ValueSource,
                Factor = signal.Factor, Offset = signal.Offset,
                Name = signal.Name, MessageName = signal.MessageName, Units = signal.Units,
                MinValue = signal.MinValue, MaxValue = signal.MaxValue,
            }).ToList(),
        };
    }

    private static string CreateIoFlowDraftSignature(DataStructures settings) =>
        CreateIoFlowDraftSignature(CreateIoFlowDraftSettings(settings));

    private static string CreateIoFlowDraftSignature(IoFlowDraftSettings draft)
    {
        var canonical = JsonSerializer.Deserialize<IoFlowDraftSettings>(JsonSerializer.Serialize(draft))
            ?? throw new InvalidOperationException("Could not canonicalise I/O Flow settings.");
        canonical.InputsAndOutputs.CustomCanSignalMetadata = null;
        foreach (var message in canonical.CanMessages) message.Name = string.Empty;
        foreach (var signal in canonical.CanSignals)
        {
            signal.Name = string.Empty;
            signal.MessageName = string.Empty;
            signal.Units = string.Empty;
            signal.MinValue = null;
            signal.MaxValue = null;
        }
        return JsonSerializer.Serialize(canonical);
    }

    private void RememberIoFlowDraft(IoFlowGraphSnapshot snapshot)
    {
        if (_controllerConfigBaseline == null)
            return;

        _ioFlowDraftAwaitingReconciliation = new IoFlowPendingDraft(
            snapshot,
            CreateIoFlowDraftSettings(SettingsDataView),
            CreateIoFlowDraftSignature(_controllerConfigBaseline));
        _ioFlowDraftDecisionDeferred = false;
        _ioFlowSnapshotMismatchWarning = "Unsaved I/O Flow changes are being compared with the refreshed controller settings.";
    }

    private async Task ReconcileIoFlowDraftAsync()
    {
        if (_ioFlowDraftAwaitingReconciliation is not { } pending
            || _controllerConfigBaseline == null || _reconcilingIoFlowDraft || _ioFlowDraftDecisionDeferred) return;
        if (!IsIoFlowSnapshotStructurallyValid(pending.Snapshot))
        {
            _ioFlowDraftDecisionDeferred = true;
            _ioFlowSnapshotMismatchWarning = "The pending I/O Flow changes are invalid and have been left untouched.";
            AddLog("Cannot reconcile pending I/O Flow changes.", _ioFlowSnapshotMismatchWarning);
            UpdateIoFlowControllerMismatchWarning();
            return;
        }
        _reconcilingIoFlowDraft = true;
        try
        {
            bool matchesBaseline = pending.BaselineSignature == CreateIoFlowDraftSignature(_controllerConfigBaseline);
            bool alreadyApplied = CreateIoFlowDraftSignature(pending.Settings) == CreateIoFlowDraftSignature(_controllerConfigBaseline);
            bool restore = matchesBaseline || alreadyApplied;
            if (!restore)
            {
                restore = await _appCloser.ConfirmAsync("Pending I/O Flow changes",
                    "The controller settings changed while unsaved I/O Flow changes were pending. Restore the pending changes, or use the controller settings?",
                    "RESTORE CHANGES", "USE CONTROLLER");
                if (!restore && !await _appCloser.ConfirmAsync("Discard pending I/O Flow changes",
                    "Use the controller configuration and discard the pending I/O Flow changes?",
                    "USE CONTROLLER", "KEEP CHANGES"))
                {
                    _ioFlowDraftDecisionDeferred = true;
                    _ioFlowSnapshotMismatchWarning = "Pending I/O Flow changes were not applied. Reconnect to choose which configuration to use.";
                    UpdateIoFlowControllerMismatchWarning();
                    return;
                }
            }
            if (!ReferenceEquals(pending, _ioFlowDraftAwaitingReconciliation)) return;
            if (restore)
            {
                DataStructures settings = DeepCopyDataStructures(SettingsDataView);
                ConfigFileSerializer.ApplySnapshot(settings, pending.Settings.InputsAndOutputs);
                settings.CustomCanMessagesStaticData = new ObservableCollection<CustomCanMessage>(pending.Settings.CanMessages);
                settings.CustomCanSignalsStaticData = new ObservableCollection<CustomCanSignal>(pending.Settings.CanSignals);
                _isApplyingIoFlowSnapshot = true;
                bool wasSuppressing = _suppressDirtyTracking;
                _suppressDirtyTracking = true;
                try
                {
                    ReplaceSettingsData(settings);
                    IoFlowGraph.ApplySnapshot(pending.Snapshot);
                    IoFlowGraph.ReconcileMathConfiguration();
                    _awaitingInitialMathSettingsHydration = false;
                    _portService?.UpdateSettingsData(SettingsDataView);
                    OnSelectedChannelIndexChanged(SelectedChannelIndex, SelectedChannelIndex);
                    SelectedAnalogueInput = SettingsDataView.AnalogueInputsStaticData.FirstOrDefault();
                    SelectedDigitalInput = SettingsDataView.DigitalInputsStaticData.FirstOrDefault();
                }
                finally
                {
                    _suppressDirtyTracking = wasSuppressing;
                    _isApplyingIoFlowSnapshot = false;
                }
                RecalculatePendingConfigChanges();
                AddLog(alreadyApplied && !HasPendingConfigChanges ? "I/O Flow settings are already on the controller."
                    : "Restored pending I/O Flow changes after refreshing controller settings.");
            }
            else AddLog("Using controller settings; pending I/O Flow changes were discarded by confirmation.");
            _ioFlowDraftAwaitingReconciliation = null;
            _ioFlowSnapshotMismatchWarning = string.Empty;
            RecalculatePendingConfigChanges();
            _ioFlowHasUnsavedControllerChanges = HasPendingConfigChanges;
            PersistIoFlowGraphSnapshotLocally();
            UpdateIoFlowControllerMismatchWarning();
        }
        finally
        {
            _reconcilingIoFlowDraft = false;
        }
    }
}
