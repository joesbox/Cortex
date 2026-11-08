using Avalonia;
using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using Cortex.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowGraphViewModel
{
    private const int UndoLimit = 10;
    private readonly List<UndoEntry> _undoHistory = [];
    private Dictionary<IoFlowNodeViewModel, Point>? _dragStartLocations;
    private bool _isEditingConnection;
    private bool _isUndoing;
    private int _undoGeneration;

    private sealed record UndoChange(Func<bool> CanApply, Action Apply);
    private sealed record UndoEntry(List<UndoChange> Changes, bool ControllerSettingsChanged,
        Func<Task<bool>>? RestoreController = null, Func<bool>? IsCurrent = null);

    public bool CanUndo => _undoHistory.Count > 0 && !_isEditingConnection && !_isUndoing && _dragStartLocations == null;

    public void ClearUndoHistory()
    {
        _undoHistory.Clear();
        _dragStartLocations = null;
        _undoGeneration++;
        UpdateUndoAvailability();
    }

    private void UpdateUndoAvailability()
    {
        OnPropertyChanged(nameof(CanUndo));
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void RecordUndo(UndoEntry? entry)
    {
        if (entry == null || entry.Changes.Count == 0)
        {
            return;
        }

        if (_undoHistory.Count == UndoLimit)
        {
            _undoHistory.RemoveAt(0);
        }

        _undoHistory.Add(entry);
        UpdateUndoAvailability();
    }

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task Undo()
    {
        if (!CanUndo || _settings == null)
        {
            return;
        }

        UndoEntry entry = _undoHistory[^1];
        if (entry.IsCurrent?.Invoke() == false || entry.Changes.Any(change => !change.CanApply()))
        {
            ClearUndoHistory();
            ReportUndoFailure("Undo history was cleared because the affected nodes or settings have changed.");
            return;
        }

        int generation = _undoGeneration;
        _isUndoing = true;
        UpdateUndoAvailability();
        try
        {
            if (entry.RestoreController != null && !await entry.RestoreController())
            {
                ReportUndoFailure("Unable to undo the CAN TX connection change. The controller update failed; you can retry Undo.");
                return;
            }

            if (generation != _undoGeneration)
            {
                ReportUndoFailure("Undo was cancelled because the graph was reloaded.");
                return;
            }

            bool wasApplying = _isApplyingMathRulesFromGraph;
            _isApplyingMathRulesFromGraph = true;
            try
            {
                foreach (UndoChange change in entry.Changes)
                {
                    change.Apply();
                }
                foreach (MathRuleNodeViewModel rule in MathRuleNodes)
                {
                    rule.AcceptCurrentRuleState();
                }
            }
            finally
            {
                _isApplyingMathRulesFromGraph = wasApplying;
            }

            _undoHistory.Remove(entry);
            SetSelectedConnection(null);
            RebuildConnections(_settings);
            ClearAddNodeWarning();
            NotifyGraphChanged(entry.ControllerSettingsChanged);
        }
        finally
        {
            _isUndoing = false;
            UpdateUndoAvailability();
        }
    }

    private void ReportUndoFailure(string message)
    {
        ShowAddNodeWarning(message);
        LoggingService.AddLog(message);
    }

    [RelayCommand]
    private void ItemsDragStarted()
    {
        _dragStartLocations = CaptureNodeLocations();
        UpdateUndoAvailability();
    }

    private Dictionary<IoFlowNodeViewModel, Point> CaptureNodeLocations() =>
        Nodes.ToDictionary(node => node, node => node.Location);

    private void RecordPositionUndo(Dictionary<IoFlowNodeViewModel, Point>? before)
    {
        if (before == null)
        {
            return;
        }

        List<UndoChange> changes = [];
        foreach ((IoFlowNodeViewModel node, Point previous) in before)
        {
            Point current = node.Location;
            if (Nodes.Contains(node) && previous != current)
            {
                changes.Add(new UndoChange(() => Nodes.Contains(node) && node.Location == current, () => node.Location = previous));
            }
        }

        RecordUndo(new UndoEntry(changes, ControllerSettingsChanged: false));
    }

    private async Task TrackConnectionEditAsync(Func<Task> edit, CanTxNodeViewModel? txNode)
    {
        if (_isEditingConnection || _isUndoing)
        {
            ReportUndoFailure("Wait for the current I/O Flow connection update to finish.");
            return;
        }

        int generation = _undoGeneration;
        Func<UndoEntry?> capture = CaptureConnectionUndo(txNode);
        _isEditingConnection = true;
        UpdateUndoAvailability();
        try
        {
            await edit();
            if (generation == _undoGeneration)
            {
                RecordUndo(capture());
            }
        }
        finally
        {
            _isEditingConnection = false;
            UpdateUndoAvailability();
        }
    }

    private static Func<UndoChange?> CaptureUndoValue<T>(Func<T> get, Action<T> set)
    {
        T before = get();
        return () =>
        {
            T after = get();
            return EqualityComparer<T>.Default.Equals(before, after)
                ? null
                : new UndoChange(() => EqualityComparer<T>.Default.Equals(get(), after), () => set(before));
        };
    }

    private Func<UndoEntry?> CaptureConnectionUndo(CanTxNodeViewModel? txNode)
    {
        List<Func<UndoChange?>> values = [];
        IEnumerable<OutputNodeViewModel> outputs = txNode == null ? OutputNodes : [];
        foreach (OutputNodeViewModel output in outputs)
        {
            OutputChannel channel = output.Channel;
            values.Add(CaptureUndoValue(() => channel.ChanType, value => channel.ChanType = value));
            values.Add(CaptureUndoValue(() => channel.InputControlPin, value => channel.InputControlPin = value));
            values.Add(CaptureUndoValue(() => channel.Category, value => channel.Category = value));
            values.Add(CaptureUndoValue(() => channel.PWMSetDuty, value => channel.PWMSetDuty = value));
            values.Add(CaptureUndoValue(() => channel.RetryCount, value => channel.RetryCount = value));
            values.Add(CaptureUndoValue(() => channel.CurrentThresholdLow, value => channel.CurrentThresholdLow = value));
            values.Add(CaptureUndoValue(() => channel.CurrentThresholdHigh, value => channel.CurrentThresholdHigh = value));
            values.Add(CaptureUndoValue(() => channel.InrushDelay, value => channel.InrushDelay = value));
            values.Add(CaptureUndoValue(() => channel.InrushCurrentLimit, value => channel.InrushCurrentLimit = value));
            values.Add(CaptureUndoValue(() => channel.OnThreshold, value => channel.OnThreshold = value));
            values.Add(CaptureUndoValue(() => channel.OffThreshold, value => channel.OffThreshold = value));
            values.Add(CaptureUndoValue(() => channel.ScaleMin, value => channel.ScaleMin = value));
            values.Add(CaptureUndoValue(() => channel.ScaleMax, value => channel.ScaleMax = value));
            values.Add(CaptureUndoValue(() => channel.PWMMin, value => channel.PWMMin = value));
            values.Add(CaptureUndoValue(() => channel.PWMMax, value => channel.PWMMax = value));
            values.Add(CaptureUndoValue(() => channel.IntermittentOnTime, value => channel.IntermittentOnTime = value));
            values.Add(CaptureUndoValue(() => channel.IntermittentOffTime, value => channel.IntermittentOffTime = value));
            values.Add(CaptureUndoValue(() => channel.DelayedOn, value => channel.DelayedOn = value));
            values.Add(CaptureUndoValue(() => channel.DelayedOnTime, value => channel.DelayedOnTime = value));
            values.Add(CaptureUndoValue(() => channel.DelayedOff, value => channel.DelayedOff = value));
            values.Add(CaptureUndoValue(() => channel.DelayedOffTime, value => channel.DelayedOffTime = value));
            values.Add(CaptureUndoValue(() => channel.DelayedOffTrigger, value => channel.DelayedOffTrigger = value));
            values.Add(CaptureUndoValue(() => channel.SoftStartEnabled, value => channel.SoftStartEnabled = value));
            values.Add(CaptureUndoValue(() => channel.SoftStartTime, value => channel.SoftStartTime = value));
            values.Add(CaptureUndoValue(() => channel.SoftStopEnabled, value => channel.SoftStopEnabled = value));
            values.Add(CaptureUndoValue(() => channel.SoftStopTime, value => channel.SoftStopTime = value));
            values.Add(CaptureUndoValue(() => channel.MathRuleCount, value => channel.MathRuleCount = value));
            values.Add(CaptureUndoValue(() => channel.MathRootRule, value => channel.MathRootRule = value));
            values.Add(CaptureUndoValue(() => channel.MathOutputMode, value => channel.MathOutputMode = value));

            int channelNumber = channel.ChannelNumber;
            values.Add(CaptureUndoValue(
                () => _outputRootLogicNodeByChannel.TryGetValue(channelNumber, out int root) ? (int?)root : null,
                value =>
                {
                    if (value is int root)
                    {
                        _outputRootLogicNodeByChannel[channelNumber] = root;
                    }
                    else
                    {
                        _outputRootLogicNodeByChannel.Remove(channelNumber);
                    }
                }));
        }

        IEnumerable<OutputChannel.MathRule> rules = txNode == null
            ? OutputNodes.SelectMany(node => node.Channel.MathRules).Concat(MathRuleNodes.Select(node => node.Rule)).Distinct()
            : [];
        foreach (OutputChannel.MathRule rule in rules)
        {
            values.Add(CaptureUndoValue(() => rule.Operation, value => rule.Operation = value));
            values.Add(CaptureUndoValue(() => rule.HysteresisEnabled, value => rule.HysteresisEnabled = value));
            values.Add(CaptureUndoValue(() => rule.OnThreshold, value => rule.OnThreshold = value));
            values.Add(CaptureUndoValue(() => rule.OffThreshold, value => rule.OffThreshold = value));
            values.Add(CaptureUndoValue(() => rule.OperandAType, value => rule.OperandAType = value));
            values.Add(CaptureUndoValue(() => rule.OperandAIndex, value => rule.OperandAIndex = value));
            values.Add(CaptureUndoValue(() => rule.OperandAConstant, value => rule.OperandAConstant = value));
            values.Add(CaptureUndoValue(() => rule.OperandBType, value => rule.OperandBType = value));
            values.Add(CaptureUndoValue(() => rule.OperandBIndex, value => rule.OperandBIndex = value));
            values.Add(CaptureUndoValue(() => rule.OperandBConstant, value => rule.OperandBConstant = value));
        }

        CustomCanMessage? txMessage = txNode?.Message;
        uint? txId = txMessage?.Id;
        bool? txExtended = txMessage?.IsExtended;
        byte previousTrigger = txMessage?.TxTrigger ?? 0;
        bool previouslyEnabled = txMessage?.IsEnabled ?? false;
        if (txMessage != null)
        {
            values.Add(CaptureUndoValue(() => txMessage.TxTrigger, value => txMessage.TxTrigger = value));
            values.Add(CaptureUndoValue(() => txMessage.IsEnabled, value => txMessage.IsEnabled = value));
        }

        return () =>
        {
            List<UndoChange> changes = values.Select(capture => capture()).OfType<UndoChange>().ToList();
            if (changes.Count == 0)
            {
                return null;
            }

            Func<Task<bool>>? restoreController = null;
            Func<bool>? isCurrent = null;
            if (txMessage != null)
            {
                isCurrent = () => txNode!.Message == txMessage && txMessage.IsTx &&
                    txMessage.Id == txId && txMessage.IsExtended == txExtended;
                restoreController = () => SetCustomCanTxStateAction?.Invoke(txMessage.Index, previousTrigger, previouslyEnabled)
                    ?? Task.FromResult(true);
            }

            return new UndoEntry(changes, ControllerSettingsChanged: true, restoreController, isCurrent);
        };
    }
}
