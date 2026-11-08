using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Cortex.ViewModels;

public partial class MainWindowViewModel : INotifyDataErrorInfo
{
    private const int ClearAllCustomCanRetryDelayMs = 250;
    private const int SaveCustomCanRetryDelayMs = 250;
    internal const string CustomCanMetadataCachePathEnvironmentVariable = "CORTEX_CUSTOM_CAN_METADATA_PATH";
    private static readonly string DefaultCustomCanMetadataCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Cortex",
        "custom-can-metadata.json");

    private static string CustomCanMetadataCachePath
    {
        get
        {
            string? overridePath = Environment.GetEnvironmentVariable(CustomCanMetadataCachePathEnvironmentVariable);
            return string.IsNullOrWhiteSpace(overridePath) ? DefaultCustomCanMetadataCachePath : overridePath;
        }
    }

    // While > 0, programmatic Name changes on stored signals must not be treated as user renames.
    private int _customCanMetadataUpsertSuppression;
    private static readonly Regex DbcMessageRegex = new(@"^\s*BO_\s+(?<id>\d+)\s+(?<name>[A-Za-z0-9_]+)\s*:\s*(?<dlc>\d+)", RegexOptions.Compiled);
    private static readonly Regex DbcSignalRegex = new(@"^\s*SG_\s+(?<name>[A-Za-z0-9_]+)(?:\s+m\d+)?\s*:\s*(?<start>\d+)\|(?<len>\d+)@(?<endian>[01])(?<sign>[\+\-])\s*\((?<factor>[-+0-9Ee\.]+)\s*,\s*(?<offset>[-+0-9Ee\.]+)\)\s*(?:\[(?<min>[-+0-9Ee\.]+)\|(?<max>[-+0-9Ee\.]+)\])?\s*(?:""(?<unit>[^""]*)"")?", RegexOptions.Compiled);

    private sealed class CustomCanMetadataCache
    {
        public List<CustomCanSignalMetadata> Signals { get; set; } = [];
    }

    private readonly object _customCanPendingWriteLock = new();
    private readonly Dictionary<int, (ushort TimeoutMs, byte Flags, byte TxTrigger)> _pendingCustomCanMessageWrites = [];
    private readonly Dictionary<int, byte> _pendingCustomCanSignalValueSourceWrites = [];

    private void RegisterPendingCustomCanWrite(int messageIndex, ushort timeoutMs, byte flags, byte txTrigger, IReadOnlyDictionary<int, byte> signalValueSources)
    {
        lock (_customCanPendingWriteLock)
        {
            _pendingCustomCanMessageWrites[messageIndex] = (timeoutMs, flags, txTrigger);
            foreach ((int signalIndex, byte pendingValueSource) in signalValueSources)
            {
                _pendingCustomCanSignalValueSourceWrites[signalIndex] = pendingValueSource;
            }
        }
    }

    private void ClearPendingCustomCanWrite(int messageIndex, IEnumerable<int> signalIndexes)
    {
        lock (_customCanPendingWriteLock)
        {
            _pendingCustomCanMessageWrites.Remove(messageIndex);
            foreach (int signalIndex in signalIndexes)
            {
                _pendingCustomCanSignalValueSourceWrites.Remove(signalIndex);
            }
        }
    }

    private bool TryGetPendingCustomCanMessageWrite(int messageIndex, out ushort timeoutMs, out byte flags, out byte txTrigger)
    {
        lock (_customCanPendingWriteLock)
        {
            if (_pendingCustomCanMessageWrites.TryGetValue(messageIndex, out (ushort TimeoutMs, byte Flags, byte TxTrigger) pendingWrite))
            {
                timeoutMs = pendingWrite.TimeoutMs;
                flags = pendingWrite.Flags;
                txTrigger = pendingWrite.TxTrigger;
                return true;
            }
        }

        timeoutMs = 0;
        flags = 0;
        txTrigger = 0;
        return false;
    }

    private bool TryGetPendingCustomCanSignalValueSourceWrite(int signalIndex, out byte valueSource)
    {
        lock (_customCanPendingWriteLock)
        {
            return _pendingCustomCanSignalValueSourceWrites.TryGetValue(signalIndex, out valueSource);
        }
    }

    public ObservableCollection<DbcCanMessageDefinition> ImportedDbcMessages { get; } = [];

    public ObservableCollection<DbcCanSignalDefinition> ImportedDbcSignals { get; } = [];

    public ObservableCollection<CustomCanSignal> ControllerCustomCanSignals { get; } = new();

    /// <summary>Stored messages (CAN frames) shown in the master table; one row per enabled message slot.</summary>
    public ObservableCollection<CustomCanMessage> ControllerCustomCanMessages { get; } = new();

    /// <summary>Signals of <see cref="SelectedControllerCustomCanMessage"/>, shown in the detail table.</summary>
    public ObservableCollection<CustomCanSignal> SelectedMessageCustomCanSignals { get; } = new();

    private void RebuildControllerCustomCanSignalsView()
    {
        SyncObservableCollection(ControllerCustomCanSignals, SettingsDataView.CustomCanSignalsStaticData
            .Where(signal => signal.IsEnabled)
            .ToList());
    }

    private static void SyncObservableCollection<T>(ObservableCollection<T> target, IReadOnlyList<T> desired) where T : class
    {
        for (int i = target.Count - 1; i >= 0; i--)
        {
            if (!desired.Contains(target[i]))
            {
                target.RemoveAt(i);
            }
        }

        for (int index = 0; index < desired.Count; index++)
        {
            T item = desired[index];

            if (index < target.Count && ReferenceEquals(target[index], item))
            {
                continue;
            }

            int existingIndex = target.IndexOf(item);
            if (existingIndex >= 0)
            {
                target.Move(existingIndex, index);
            }
            else
            {
                target.Insert(index, item);
            }
        }
    }

    private bool IsStoredMessageVisible(CustomCanMessage message) => message.IsEnabled || message.SignalCount > 0;

    private void RebuildControllerCustomCanMessagesView()
    {
        List<CustomCanMessage> messages = SettingsDataView.CustomCanMessagesStaticData
            .Where(IsStoredMessageVisible)
            .OrderBy(message => message.IsExtended)
            .ThenBy(message => message.Id)
            .ThenBy(message => message.Index)
            .ToList();

        foreach (CustomCanMessage hidden in SettingsDataView.CustomCanMessagesStaticData.Where(message => message.IsSelected && !messages.Contains(message)))
        {
            hidden.IsSelected = false;
        }

        SyncObservableCollection(ControllerCustomCanMessages, messages);

        if (SelectedControllerCustomCanMessage == null || !messages.Contains(SelectedControllerCustomCanMessage))
        {
            SelectedControllerCustomCanMessage = messages.FirstOrDefault();
        }
        else
        {
            RebuildSelectedMessageSignalsView();
        }
    }

    private void RebuildSelectedMessageSignalsView()
    {
        CustomCanMessage? message = SelectedControllerCustomCanMessage;
        int messageIndex = message == null ? -1 : SettingsDataView.CustomCanMessagesStaticData.IndexOf(message);
        List<CustomCanSignal> signals = SettingsDataView.CustomCanSignalsStaticData
            .Where(signal => signal.IsEnabled && signal.MessageIndex == messageIndex)
            .OrderBy(signal => signal.StartBit)
            .ThenBy(signal => signal.Index)
            .ToList();

        // Delete-checks on signals that are no longer visible would otherwise be deleted unseen.
        foreach (CustomCanSignal hidden in SettingsDataView.CustomCanSignalsStaticData.Where(signal => signal.IsMarkedForDeletion && !signals.Contains(signal)))
        {
            hidden.IsMarkedForDeletion = false;
        }

        SyncObservableCollection(SelectedMessageCustomCanSignals, signals);
        OnPropertyChanged(nameof(SelectedMessageSignalsHeader));
    }

    private CustomCanMessage? _selectedControllerCustomCanMessage;
    public CustomCanMessage? SelectedControllerCustomCanMessage
    {
        get => _selectedControllerCustomCanMessage;
        set
        {
            if (SetProperty(ref _selectedControllerCustomCanMessage, value))
            {
                OnPropertyChanged(nameof(HasSelectedStoredMessage));
                RebuildSelectedMessageSignalsView();
            }
        }
    }

    public bool HasSelectedStoredMessage => SelectedControllerCustomCanMessage != null;

    public string SelectedMessageSignalsHeader
    {
        get
        {
            CustomCanMessage? message = SelectedControllerCustomCanMessage;
            if (message == null)
            {
                return "Signals (select a message above)";
            }

            return $"Signals in {message.IdHex} {message.Name} ({message.IdTypeLabel}, {message.DirectionLabel})";
        }
    }

    public bool HasCheckedStoredMessage => SettingsDataView.CustomCanMessagesStaticData
        .Any(message => message.IsSelected && IsStoredMessageVisible(message));

    private List<CustomCanMessage> GetCheckedStoredMessages() => SettingsDataView.CustomCanMessagesStaticData
        .Where(message => message.IsSelected && IsStoredMessageVisible(message))
        .ToList();

    private readonly HashSet<CustomCanMessage> _trackedStoredMessageSubscriptions = [];

    private void SyncStoredMessageSubscriptions()
    {
        foreach (CustomCanMessage message in _trackedStoredMessageSubscriptions.ToList())
        {
            if (SettingsDataView.CustomCanMessagesStaticData.Contains(message))
            {
                continue;
            }

            message.PropertyChanged -= StoredMessage_PropertyChanged;
            _trackedStoredMessageSubscriptions.Remove(message);
        }

        foreach (CustomCanMessage message in SettingsDataView.CustomCanMessagesStaticData)
        {
            if (_trackedStoredMessageSubscriptions.Add(message))
            {
                message.PropertyChanged += StoredMessage_PropertyChanged;
            }
        }
    }

    private void StoredMessage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CustomCanMessage message)
        {
            return;
        }

        if (e.PropertyName == nameof(CustomCanMessage.IsSelected))
        {
            if (message.IsSelected)
            {
                SelectedStoredDirectionIndex = message.IsTx ? 1 : 0;
            }

            OnPropertyChanged(nameof(HasCheckedStoredMessage));
            OnPropertyChanged(nameof(StoredDirectionScopeSummary));
            return;
        }

        if (ReferenceEquals(message, SelectedControllerCustomCanMessage) &&
            e.PropertyName is nameof(CustomCanMessage.Name) or nameof(CustomCanMessage.Id) or nameof(CustomCanMessage.Flags))
        {
            OnPropertyChanged(nameof(SelectedMessageSignalsHeader));
        }
    }

    private DbcCanSignalDefinition? _selectedImportedDbcSignal;
    public DbcCanSignalDefinition? SelectedImportedDbcSignal
    {
        get => _selectedImportedDbcSignal;
        set => SetProperty(ref _selectedImportedDbcSignal, value);
    }

    public ObservableCollection<DbcCanSignalDefinition> SelectedImportedDbcSignals { get; } = [];

    public void SetSelectedImportedDbcSignals(IEnumerable<DbcCanSignalDefinition> selectedSignals, DbcCanSignalDefinition? primarySignal = null)
    {
        SelectedImportedDbcSignals.Clear();
        foreach (DbcCanSignalDefinition signal in selectedSignals
                     .DistinctBy(BuildDbcSignalIdentity))
        {
            SelectedImportedDbcSignals.Add(signal);
        }

        // SelectedImportedDbcSignal is two-way bound to the grid's SelectedItem. Writing back anything other
        // than the grid's own SelectedItem collapses an extended (shift) selection to a single row.
        primarySignal ??= SelectedImportedDbcSignal;
        SelectedImportedDbcSignal = primarySignal != null && SelectedImportedDbcSignals.Contains(primarySignal)
            ? primarySignal
            : SelectedImportedDbcSignals.Count > 0
                ? SelectedImportedDbcSignals[0]
                : null;
    }

    private CustomCanSignal? _selectedControllerCustomCanSignal;
    public CustomCanSignal? SelectedControllerCustomCanSignal
    {
        get => _selectedControllerCustomCanSignal;
        set
        {
            if (SetProperty(ref _selectedControllerCustomCanSignal, value))
            {
                OnPropertyChanged(nameof(HasSelectedStoredSignal));
                OnPropertyChanged(nameof(HasCheckedStoredSignal));
            }
        }
    }

    public bool HasSelectedStoredSignal => SelectedControllerCustomCanSignal != null;

    public bool HasCheckedStoredSignal => SettingsDataView.CustomCanSignalsStaticData
        .Any(signal => signal.IsMarkedForDeletion);

    [RelayCommand]
    private void ToggleStoredSignalChecked(CustomCanSignal? signal)
    {
        if (signal == null)
        {
            return;
        }

        signal.IsMarkedForDeletion = !signal.IsMarkedForDeletion;

        List<CustomCanSignal> checkedSignals = SettingsDataView.CustomCanSignalsStaticData
            .Where(item => item.IsMarkedForDeletion)
            .ToList();

        if (signal.IsMarkedForDeletion)
        {
            SelectedControllerCustomCanSignal = signal;
        }
        else if (ReferenceEquals(SelectedControllerCustomCanSignal, signal))
        {
            SelectedControllerCustomCanSignal = checkedSignals.FirstOrDefault();
        }

        OnPropertyChanged(nameof(HasCheckedStoredSignal));
    }

    private const int ManualCanMaxNameLength = 64;
    private const int ManualCanMaxUnitsLength = 32;

    private static readonly CustomCanMessageTargetOption NewManualMessageTarget = new(null, "New message");

    /// <summary>"New message" followed by every stored message, so a signal can be added to an existing frame.</summary>
    public ObservableCollection<CustomCanMessageTargetOption> ManualCanMessageTargets { get; } = [NewManualMessageTarget];

    private CustomCanMessageTargetOption _manualCanMessageTarget = NewManualMessageTarget;
    public CustomCanMessageTargetOption? ManualCanMessageTarget
    {
        get => _manualCanMessageTarget;
        set
        {
            // The ComboBox briefly pushes null while its items are rebuilt; a target is always required.
            if (value == null || ReferenceEquals(value, _manualCanMessageTarget))
            {
                return;
            }

            _manualCanMessageTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsManualNewMessage));
            if (value.Message != null)
            {
                PopulateManualMessageFields(value.Message);
            }

            RevalidateManualCanSignal();
        }
    }

    /// <summary>True when the manual form defines a new frame; false when adding a signal to a stored message (frame fields locked).</summary>
    public bool IsManualNewMessage => _manualCanMessageTarget.Message == null;

    private void PopulateManualMessageFields(CustomCanMessage message)
    {
        ManualCanMessageIdHex = message.IdHex;
        ManualCanIsExtended = message.IsExtended;
        ManualCanDlc = Math.Clamp((int)message.Dlc, 1, 8).ToString(CultureInfo.InvariantCulture);
        ManualDirectionIndex = message.IsTx ? (message.IsPeriodic ? 2 : 1) : 0;
        ManualCanTimeoutMs = message.TimeoutMs.ToString(CultureInfo.InvariantCulture);
        ManualTriggerIndex = OptionIndexFromTrigger(message.TxTrigger);
        ManualMessageName = message.Name;
    }

    private void RebuildManualCanMessageTargets()
    {
        List<CustomCanMessage> messages = ControllerCustomCanMessages.ToList();
        static string Label(CustomCanMessage message) =>
            $"{message.IdHex} {message.Name} ({message.IdTypeLabel}, {message.DirectionLabel}, {message.SignalCount} signal{(message.SignalCount == 1 ? string.Empty : "s")})";

        bool unchanged = ManualCanMessageTargets.Count == messages.Count + 1;
        for (int i = 0; unchanged && i < messages.Count; i++)
        {
            CustomCanMessageTargetOption option = ManualCanMessageTargets[i + 1];
            unchanged = ReferenceEquals(option.Message, messages[i]) && option.Label == Label(messages[i]);
        }

        if (!unchanged)
        {
            CustomCanMessage? targetMessage = _manualCanMessageTarget.Message;
            while (ManualCanMessageTargets.Count > 1)
            {
                ManualCanMessageTargets.RemoveAt(ManualCanMessageTargets.Count - 1);
            }

            foreach (CustomCanMessage message in messages)
            {
                ManualCanMessageTargets.Add(new CustomCanMessageTargetOption(message, Label(message)));
            }

            CustomCanMessageTargetOption replacement = ManualCanMessageTargets
                .FirstOrDefault(option => option.Message != null && ReferenceEquals(option.Message, targetMessage)) ?? NewManualMessageTarget;
            _manualCanMessageTarget = replacement;
            OnPropertyChanged(nameof(ManualCanMessageTarget));
            OnPropertyChanged(nameof(IsManualNewMessage));
        }

        // Keep locked frame fields in step with the stored message (e.g. after a direction change).
        if (_manualCanMessageTarget.Message != null)
        {
            PopulateManualMessageFields(_manualCanMessageTarget.Message);
        }

        RevalidateManualCanSignal();
    }

    private void SelectManualMessageTarget(CustomCanMessage? message)
    {
        ManualCanMessageTarget = ManualCanMessageTargets
            .FirstOrDefault(option => option.Message != null && ReferenceEquals(option.Message, message)) ?? NewManualMessageTarget;
    }

    private string _manualMessageName = string.Empty;
    public string ManualMessageName
    {
        get => _manualMessageName;
        set => SetManualCanField(ref _manualMessageName, value);
    }

    // Manual-entry fields are string-backed so invalid text is reported via INotifyDataErrorInfo
    // instead of being silently clamped or discarded by the binding.
    private string _manualCanMessageIdHex = "0x000";
    public string ManualCanMessageIdHex
    {
        get => _manualCanMessageIdHex;
        set => SetManualCanField(ref _manualCanMessageIdHex, value);
    }

    private bool _manualCanIsExtended;
    public bool ManualCanIsExtended
    {
        get => _manualCanIsExtended;
        set => SetManualCanField(ref _manualCanIsExtended, value);
    }

    private string _manualCanTimeoutMs = "1000";
    public string ManualCanTimeoutMs
    {
        get => _manualCanTimeoutMs;
        set => SetManualCanField(ref _manualCanTimeoutMs, value);
    }

    private string _manualCanDlc = "8";
    public string ManualCanDlc
    {
        get => _manualCanDlc;
        set => SetManualCanField(ref _manualCanDlc, value);
    }

    private string _manualSignalStartBit = "0";
    public string ManualSignalStartBit
    {
        get => _manualSignalStartBit;
        set => SetManualCanField(ref _manualSignalStartBit, value);
    }

    private string _manualSignalBitLength = "8";
    public string ManualSignalBitLength
    {
        get => _manualSignalBitLength;
        set => SetManualCanField(ref _manualSignalBitLength, value);
    }

    private bool _manualSignalIsMotorola;
    public bool ManualSignalIsMotorola
    {
        get => _manualSignalIsMotorola;
        set => SetManualCanField(ref _manualSignalIsMotorola, value);
    }

    private bool _manualSignalIsSigned;
    public bool ManualSignalIsSigned
    {
        get => _manualSignalIsSigned;
        set => SetManualCanField(ref _manualSignalIsSigned, value);
    }

    private string _manualSignalFactor = "1";
    public string ManualSignalFactor
    {
        get => _manualSignalFactor;
        set => SetManualCanField(ref _manualSignalFactor, value);
    }

    private string _manualSignalOffset = "0";
    public string ManualSignalOffset
    {
        get => _manualSignalOffset;
        set => SetManualCanField(ref _manualSignalOffset, value);
    }

    private string _manualSignalName = "Manual";
    public string ManualSignalName
    {
        get => _manualSignalName;
        set => SetManualCanField(ref _manualSignalName, value);
    }

    private string _manualSignalMinValue = string.Empty;
    public string ManualSignalMinValue
    {
        get => _manualSignalMinValue;
        set => SetManualCanField(ref _manualSignalMinValue, value);
    }

    private string _manualSignalMaxValue = string.Empty;
    public string ManualSignalMaxValue
    {
        get => _manualSignalMaxValue;
        set => SetManualCanField(ref _manualSignalMaxValue, value);
    }

    private string _manualSignalUnits = string.Empty;
    public string ManualSignalUnits
    {
        get => _manualSignalUnits;
        set => SetManualCanField(ref _manualSignalUnits, value);
    }

    private Dictionary<string, string> _manualCanFieldErrors = new(StringComparer.Ordinal);

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public bool HasErrors => _manualCanFieldErrors.Count > 0;

    public IEnumerable GetErrors(string? propertyName)
    {
        if (string.IsNullOrEmpty(propertyName))
        {
            return _manualCanFieldErrors.Values.ToArray();
        }

        return _manualCanFieldErrors.TryGetValue(propertyName, out string? error)
            ? new[] { error }
            : Array.Empty<string>();
    }

    public bool HasManualCanValidationErrors => _manualCanFieldErrors.Count > 0;

    public string ManualCanValidationSummary => string.Join(Environment.NewLine, _manualCanFieldErrors.Values.Distinct());

    public bool CanSaveManualCanSignal => !IsCustomCanBusy && !HasManualCanValidationErrors;

    private bool SetManualCanField<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (!SetProperty(ref field, value, propertyName))
        {
            return false;
        }

        RevalidateManualCanSignal();
        return true;
    }

    private void RevalidateManualCanSignal()
    {
        _ = TryBuildManualCanSignal(out _, out _, out _, out Dictionary<string, string> errors);

        Dictionary<string, string> previous = _manualCanFieldErrors;
        _manualCanFieldErrors = errors;

        bool changed = false;
        foreach (string key in previous.Keys.Union(errors.Keys).ToList())
        {
            previous.TryGetValue(key, out string? oldError);
            errors.TryGetValue(key, out string? newError);
            if (!string.Equals(oldError, newError, StringComparison.Ordinal))
            {
                changed = true;
                ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(key));
            }
        }

        if (changed)
        {
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(HasManualCanValidationErrors));
            OnPropertyChanged(nameof(ManualCanValidationSummary));
            OnPropertyChanged(nameof(CanSaveManualCanSignal));
        }
    }

    private static bool TryParseManualInt(string? text, out int value) =>
        int.TryParse(text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryParseManualFloat(string? text, out float value)
    {
        string trimmed = text?.Trim() ?? string.Empty;
        bool parsed = float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value) ||
                      float.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value);
        return parsed && float.IsFinite(value);
    }

    /// <summary>
    /// Validates every manual-entry field against the same rules the firmware enforces and, when valid,
    /// produces the definition to save. Errors are keyed by the bound property name.
    /// </summary>
    private bool TryBuildManualCanSignal(
        out DbcCanSignalDefinition definition,
        out ushort timeoutMs,
        out CustomCanTxOptions txOptions,
        out Dictionary<string, string> errors)
    {
        errors = new Dictionary<string, string>(StringComparer.Ordinal);
        definition = new DbcCanSignalDefinition();
        timeoutMs = 0;
        txOptions = IsManualTx
            ? new CustomCanTxOptions(true, IsManualTxPeriodic,
                TriggerFromOptionIndex(ManualTriggerIndex), ValueSourceFromOptionIndex(ManualValueSourceIndex))
            : CustomCanTxOptions.Rx;

        uint messageId = 0;
        if (!TryParseManualCanMessageId(ManualCanMessageIdHex, out messageId))
        {
            errors[nameof(ManualCanMessageIdHex)] = "Message ID must be hex, e.g. 0x123 or 0x18FF50E5 (max 0x1FFFFFFF).";
        }
        else if (!ManualCanIsExtended && messageId > 0x7FF)
        {
            errors[nameof(ManualCanMessageIdHex)] = "Standard IDs are 0x000-0x7FF. Tick \"Extended (29-bit) ID\" for larger IDs.";
        }
        else if (_manualCanMessageTarget.Message is { } targetMessage)
        {
            if (!SettingsDataView.CustomCanMessagesStaticData.Contains(targetMessage) || !IsStoredMessageVisible(targetMessage))
            {
                errors[nameof(ManualCanMessageTarget)] = "The selected message is no longer stored. Choose another message or \"New message\".";
            }
        }
        else if (SettingsDataView.CustomCanMessagesStaticData.FirstOrDefault(message =>
                     IsStoredMessageVisible(message) && message.Id == messageId && message.IsExtended == ManualCanIsExtended) is { } existing)
        {
            errors[nameof(ManualCanMessageIdHex)] =
                $"{existing.IdHex} ({existing.IdTypeLabel}) is already stored as {existing.Name}. Choose it under Message to add a signal to that frame.";
        }

        string messageName = ManualMessageName?.Trim() ?? string.Empty;
        if (messageName.Length > ManualCanMaxNameLength)
        {
            errors[nameof(ManualMessageName)] = $"Message name must be {ManualCanMaxNameLength} characters or fewer.";
        }

        bool isOneShot = txOptions is { IsTx: true, IsPeriodic: false };
        int timeout = 0;
        if (isOneShot && string.IsNullOrWhiteSpace(ManualCanTimeoutMs))
        {
            timeout = 0;
        }
        else if (!TryParseManualInt(ManualCanTimeoutMs, out timeout) || timeout < 0 || timeout > 60000)
        {
            errors[nameof(ManualCanTimeoutMs)] = txOptions.IsPeriodic
                ? "Period must be a whole number between 1 and 60000 ms."
                : "Timeout must be a whole number between 0 (none) and 60000 ms.";
        }
        else if (txOptions.IsPeriodic && timeout < 1)
        {
            errors[nameof(ManualCanTimeoutMs)] = "Periodic TX needs a period between 1 and 60000 ms.";
        }

        bool dlcValid = TryParseManualInt(ManualCanDlc, out int dlc) && dlc is >= 1 and <= 8;
        if (!dlcValid)
        {
            errors[nameof(ManualCanDlc)] = "DLC must be a whole number between 1 and 8.";
        }

        bool startValid = TryParseManualInt(ManualSignalStartBit, out int startBit) && startBit is >= 0 and <= 63;
        if (!startValid)
        {
            errors[nameof(ManualSignalStartBit)] = "Start bit must be a whole number between 0 and 63.";
        }

        bool lengthValid = TryParseManualInt(ManualSignalBitLength, out int bitLength) && bitLength is >= 1 and <= 64;
        if (!lengthValid)
        {
            errors[nameof(ManualSignalBitLength)] = "Bit length must be a whole number between 1 and 64.";
        }

        if (dlcValid && startValid && lengthValid &&
            !IsCustomCanSignalBitRangeValid((byte)startBit, (byte)bitLength, (byte)(ManualSignalIsMotorola ? 0x02 : 0x00), (byte)dlc))
        {
            string layout = ManualSignalIsMotorola ? "Motorola" : "Intel";
            string fitError = $"Signal ({layout}, start {startBit}, {bitLength} bits) does not fit in a {dlc}-byte frame.";
            errors[nameof(ManualSignalStartBit)] = fitError;
            errors[nameof(ManualSignalBitLength)] = fitError;
        }

        bool constantTx = txOptions.IsTx && txOptions.ValueSource == Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT;
        if (!TryParseManualFloat(ManualSignalFactor, out float factor))
        {
            errors[nameof(ManualSignalFactor)] = "Factor must be a finite number.";
        }
        else if (factor == 0.0f && !constantTx)
        {
            errors[nameof(ManualSignalFactor)] = txOptions.IsTx
                ? "TX signals with a channel value source need a non-zero factor."
                : "Factor must be non-zero.";
        }

        if (!TryParseManualFloat(ManualSignalOffset, out float offset))
        {
            errors[nameof(ManualSignalOffset)] = "Offset must be a finite number.";
        }

        string name = ManualSignalName?.Trim() ?? string.Empty;
        if (name.Length == 0)
        {
            errors[nameof(ManualSignalName)] = "Signal name is required.";
        }
        else if (name.Length > ManualCanMaxNameLength)
        {
            errors[nameof(ManualSignalName)] = $"Signal name must be {ManualCanMaxNameLength} characters or fewer.";
        }

        float? minValue = null;
        if (!string.IsNullOrWhiteSpace(ManualSignalMinValue))
        {
            if (TryParseManualFloat(ManualSignalMinValue, out float parsedMin))
            {
                minValue = parsedMin;
            }
            else
            {
                errors[nameof(ManualSignalMinValue)] = "Min must be a finite number or left blank.";
            }
        }

        float? maxValue = null;
        if (!string.IsNullOrWhiteSpace(ManualSignalMaxValue))
        {
            if (TryParseManualFloat(ManualSignalMaxValue, out float parsedMax))
            {
                maxValue = parsedMax;
            }
            else
            {
                errors[nameof(ManualSignalMaxValue)] = "Max must be a finite number or left blank.";
            }
        }

        if (minValue.HasValue && maxValue.HasValue && minValue.Value > maxValue.Value)
        {
            errors[nameof(ManualSignalMinValue)] = "Min must be less than or equal to Max.";
            errors[nameof(ManualSignalMaxValue)] = "Max must be greater than or equal to Min.";
        }

        string units = ManualSignalUnits?.Trim() ?? string.Empty;
        if (units.Length > ManualCanMaxUnitsLength)
        {
            errors[nameof(ManualSignalUnits)] = $"Units must be {ManualCanMaxUnitsLength} characters or fewer.";
        }

        if (errors.Count > 0)
        {
            return false;
        }

        timeoutMs = (ushort)timeout;
        definition = new DbcCanSignalDefinition
        {
            Name = name,
            MessageId = messageId,
            IsExtended = ManualCanIsExtended,
            MessageName = messageName.Length > 0 ? messageName : $"MSG_{messageId:X}",
            StartBit = (byte)startBit,
            BitLength = (byte)bitLength,
            IsMotorola = ManualSignalIsMotorola,
            IsSigned = ManualSignalIsSigned,
            Factor = factor,
            Offset = offset,
            MinValue = minValue,
            MaxValue = maxValue,
            Units = units,
            Dlc = (byte)dlc,
        };
        return true;
    }

    public ObservableCollection<string> CustomCanDirectionOptions { get; } =
        ["RX (receive)", "TX one-shot", "TX periodic"];

    public ObservableCollection<string> CustomCanTriggerOptions { get; } = new(
        new[] { "Always" }.Concat(Enumerable.Range(1, Constants.NUM_OUTPUT_CHANNELS).Select(i => $"Channel {i}")));

    public ObservableCollection<string> CustomCanValueSourceOptions { get; } = new(
        new[] { "Constant" }.Concat(Enumerable.Range(1, Constants.NUM_OUTPUT_CHANNELS).Select(i => $"Channel {i}")));

    private int _manualDirectionIndex;
    public int ManualDirectionIndex
    {
        get => _manualDirectionIndex;
        set
        {
            if (SetProperty(ref _manualDirectionIndex, value))
            {
                OnPropertyChanged(nameof(IsManualTx));
                OnPropertyChanged(nameof(IsManualRx));
                OnPropertyChanged(nameof(IsManualTxPeriodic));
                OnPropertyChanged(nameof(ManualTimeoutLabel));
                RevalidateManualCanSignal();
            }
        }
    }

    public bool IsManualTx => ManualDirectionIndex > 0;

    public bool IsManualRx => ManualDirectionIndex == 0;

    public bool IsManualTxPeriodic => ManualDirectionIndex == 2;

    public string ManualTimeoutLabel => ManualDirectionIndex switch
    {
        1 => "Period ms (unused for one-shot)",
        2 => "Period ms",
        _ => "Timeout ms (0 = none)",
    };

    private int _manualTriggerIndex;
    public int ManualTriggerIndex
    {
        get => _manualTriggerIndex;
        set => SetProperty(ref _manualTriggerIndex, value);
    }

    private int _manualValueSourceIndex;
    public int ManualValueSourceIndex
    {
        get => _manualValueSourceIndex;
        set => SetManualCanField(ref _manualValueSourceIndex, value);
    }

    private static byte TriggerFromOptionIndex(int index) =>
        index <= 0 ? Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS : (byte)(index - 1);

    private static int OptionIndexFromTrigger(byte trigger) =>
        trigger == Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS || trigger >= Constants.NUM_OUTPUT_CHANNELS ? 0 : trigger + 1;

    private static byte ValueSourceFromOptionIndex(int index) =>
        index <= 0 ? Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT : (byte)(index - 1);

    private static int OptionIndexFromValueSource(byte source) =>
        source == Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT || source >= Constants.NUM_OUTPUT_CHANNELS ? 0 : source + 1;

    private readonly record struct CustomCanTxOptions(bool IsTx, bool IsPeriodic, byte TxTrigger, byte ValueSource)
    {
        public static CustomCanTxOptions Rx => new(false, false, Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT);
    }

    private bool _isCustomCanBusy;
    public bool IsCustomCanBusy
    {
        get => _isCustomCanBusy;
        set
        {
            if (SetProperty(ref _isCustomCanBusy, value))
            {
                OnPropertyChanged(nameof(CanSaveManualCanSignal));
            }
        }
    }

    private string _customCanStatusMessage = string.Empty;
    private bool _isCanMessagesTabVisible;
    private bool _isIoFlowTabVisible;
    private int _controllerCustomCanSignalsSignature = int.MinValue;
    private readonly HashSet<CustomCanSignal> _trackedStoredSignalSelectionSubscriptions = [];

    public string CustomCanStatusMessage
    {
        get => _customCanStatusMessage;
        set => SetProperty(ref _customCanStatusMessage, value);
    }

    public void SetCanMessagesTabVisible(bool isVisible)
    {
        if (_isCanMessagesTabVisible == isVisible)
        {
            return;
        }

        _isCanMessagesTabVisible = isVisible;
        UpdateCustomCanStatusPollingSelection();
    }

    public void SetIoFlowTabVisible(bool isVisible)
    {
        if (_isIoFlowTabVisible == isVisible)
        {
            return;
        }

        _isIoFlowTabVisible = isVisible;
        UpdateCustomCanStatusPollingSelection();
    }

    private void InitializeCustomCanFeature()
    {
        EnsureCustomCanCollectionsInitialized();
        LoadPersistedCustomCanSignalMetadata();
        SelectedControllerCustomCanSignal = null;
        UpdateCustomCanStatusPollingSelection();
    }

    private void EnsureCustomCanCollectionsInitialized()
    {
        if (SettingsDataView.CustomCanMessagesStaticData.Count == 0)
        {
            for (int i = 0; i < Constants.MAX_CUSTOM_CAN_MESSAGES; i++)
            {
                SettingsDataView.CustomCanMessagesStaticData.Add(new CustomCanMessage { Index = i, Dlc = 8, TimeoutMs = 1000 });
            }
        }

        if (SettingsDataView.CustomCanSignalsStaticData.Count == 0)
        {
            for (int i = 0; i < Constants.MAX_CUSTOM_CAN_SIGNALS; i++)
            {
                SettingsDataView.CustomCanSignalsStaticData.Add(new CustomCanSignal { Index = i, BitLength = 1, Factor = 1.0f, Name = $"Signal {i + 1}" });
            }
        }

        SettingsDataView.CustomCanSignalsMetadata ??= [];

        foreach (CustomCanSignal signal in SettingsDataView.CustomCanSignalsStaticData)
        {
            if (signal.BitLength == 0)
            {
                signal.BitLength = 1;
            }

            if (!float.IsFinite(signal.Factor))
            {
                signal.Factor = 1.0f;
            }

            if (!float.IsFinite(signal.Offset))
            {
                signal.Offset = 0.0f;
            }
        }
    }

    private void SyncStoredSignalSelectionSubscriptions()
    {
        foreach (CustomCanSignal signal in _trackedStoredSignalSelectionSubscriptions.ToList())
        {
            if (SettingsDataView.CustomCanSignalsStaticData.Contains(signal))
            {
                continue;
            }

            signal.PropertyChanged -= StoredSignal_PropertyChanged;
            _trackedStoredSignalSelectionSubscriptions.Remove(signal);
        }

        foreach (CustomCanSignal signal in SettingsDataView.CustomCanSignalsStaticData)
        {
            if (_trackedStoredSignalSelectionSubscriptions.Add(signal))
            {
                signal.PropertyChanged += StoredSignal_PropertyChanged;
            }
        }
    }

    private void StoredSignal_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CustomCanSignal signal)
        {
            return;
        }

        if (e.PropertyName == nameof(CustomCanSignal.IsMarkedForDeletion))
        {
            OnPropertyChanged(nameof(HasCheckedStoredSignal));
            return;
        }

        if (e.PropertyName == nameof(CustomCanSignal.Name) && signal.IsEnabled && _customCanMetadataUpsertSuppression == 0)
        {
            string trimmedName = signal.Name?.Trim() ?? string.Empty;
            if (!string.Equals(signal.Name, trimmedName, StringComparison.Ordinal))
            {
                signal.Name = trimmedName;
                return;
            }

            if (!ShouldApplyMetadataName(trimmedName, signal.Index))
            {
                UpsertSignalMetadata(signal);
            }
        }
    }

    private void RefreshCustomCanBindings()
    {
        EnsureCustomCanCollectionsInitialized();
        SyncStoredSignalSelectionSubscriptions();
        SyncStoredMessageSubscriptions();
        ReconcileCustomCanSignalMetadata();

        for (int i = 0; i < SettingsDataView.CustomCanSignalsStaticData.Count; i++)
        {
            CustomCanSignal signal = SettingsDataView.CustomCanSignalsStaticData[i];
            signal.Index = i;
            signal.MessageIdHex = ResolveMessageIdHex(signal.MessageIndex);
            signal.OwnerMessage = signal.MessageIndex < SettingsDataView.CustomCanMessagesStaticData.Count
                ? SettingsDataView.CustomCanMessagesStaticData[signal.MessageIndex]
                : null;
        }

        for (int i = 0; i < SettingsDataView.CustomCanMessagesStaticData.Count; i++)
        {
            CustomCanMessage message = SettingsDataView.CustomCanMessagesStaticData[i];
            List<CustomCanSignal> messageSignals = SettingsDataView.CustomCanSignalsStaticData
                .Where(signal => signal.IsEnabled && signal.MessageIndex == i)
                .ToList();
            message.Index = i;
            message.SignalCount = messageSignals.Count;

            string? messageName = messageSignals
                .Where(signal => !IsPlaceholderMessageName(signal.MessageName))
                .Select(signal => signal.MessageName.Trim())
                .FirstOrDefault();

            if (IsPlaceholderMessageName(messageName) && message.IsEnabled)
            {
                messageName = FindMetadataMessageName(message.Id, message.IsExtended);
            }

            if (IsPlaceholderMessageName(messageName))
            {
                messageName = messageSignals
                    .Where(signal => !ShouldApplyMetadataName(signal.Name, signal.Index))
                    .Select(signal => signal.Name.Trim())
                    .FirstOrDefault();
            }

            message.Name = string.IsNullOrWhiteSpace(messageName)
                ? $"Message {i + 1}"
                : messageName;
        }

        int signature = BuildControllerCustomCanSignalsSignature();
        bool signatureChanged = signature != _controllerCustomCanSignalsSignature;
        if (signatureChanged)
        {
            _controllerCustomCanSignalsSignature = signature;
            RebuildControllerCustomCanSignalsView();
            RefreshIoFlowConnections();
        }

        RebuildControllerCustomCanMessagesView();
        RebuildManualCanMessageTargets();
        OnPropertyChanged(nameof(HasCheckedStoredSignal));
        OnPropertyChanged(nameof(HasCheckedStoredMessage));
        OnPropertyChanged(nameof(StoredDirectionScopeSummary));

        NotifySelectedChannelCanTxChanged();
    }

    private CustomCanMessage? ResolveCustomCanMessage(byte messageIndex)
    {
        int resolvedIndex = messageIndex;
        if (resolvedIndex < 0 || resolvedIndex >= SettingsDataView.CustomCanMessagesStaticData.Count)
        {
            return null;
        }

        return SettingsDataView.CustomCanMessagesStaticData[resolvedIndex];
    }

    private bool TryResolveSignalMessageIdentity(CustomCanSignal signal, out uint messageId, out bool isExtended)
    {
        CustomCanMessage? message = ResolveCustomCanMessage(signal.MessageIndex);
        messageId = message?.Id ?? 0;
        isExtended = message?.IsExtended ?? false;
        return message != null;
    }

    private string? BuildStoredCustomCanSignalIdentityKey(CustomCanSignal signal)
    {
        if (!signal.IsEnabled || !TryResolveSignalMessageIdentity(signal, out uint messageId, out bool isExtended))
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{messageId:X8}|{isExtended}|{signal.StartBit}|{signal.BitLength}|{signal.Flags & 0x06}|{signal.Factor:R}|{signal.Offset:R}");
    }

    private static bool CustomCanFloatsMatch(float a, float b, float relativeTolerance)
    {
        if (a.Equals(b))
        {
            return true;
        }

        float scale = Math.Max(1.0f, Math.Max(Math.Abs(a), Math.Abs(b)));
        return Math.Abs(a - b) <= relativeTolerance * scale;
    }

    private static bool IsMetadataLayoutMatch(CustomCanSignal signal, CustomCanSignalMetadata metadata, float tolerance = 1e-5f)
    {
        return signal.StartBit == metadata.StartBit &&
               signal.BitLength == metadata.BitLength &&
               signal.IsMotorola == metadata.IsMotorola &&
               signal.IsSigned == metadata.IsSigned &&
               CustomCanFloatsMatch(signal.Factor, metadata.Factor, tolerance) &&
               CustomCanFloatsMatch(signal.Offset, metadata.Offset, tolerance);
    }

    private static bool IsMetadataIdentityMatch(CustomCanSignal signal, uint messageId, bool isExtended, CustomCanSignalMetadata metadata)
    {
        return metadata.MessageId == messageId &&
               metadata.IsExtended == isExtended &&
               IsMetadataLayoutMatch(signal, metadata);
    }

    // Legacy cache entries were keyed by slot and carried a fingerprint of the form
    // "{msgId:X8}|{dlc}|{msgFlags&0x0E}|...". Recover the message identity from it when possible.
    private static void MigrateLegacyMetadataIdentity(CustomCanSignalMetadata metadata)
    {
        if (metadata.MessageId.HasValue || string.IsNullOrWhiteSpace(metadata.IdentityFingerprint))
        {
            return;
        }

        string[] parts = metadata.IdentityFingerprint.Split('|');
        if (parts.Length < 3
            || !uint.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint messageId)
            || !int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int messageFlags))
        {
            return;
        }

        metadata.MessageId = messageId;
        metadata.IsExtended = (messageFlags & 0x02) != 0;
    }

    private CustomCanSignalMetadata? FindSignalMetadata(CustomCanSignal signal)
    {
        SettingsDataView.CustomCanSignalsMetadata ??= [];
        IList<CustomCanSignalMetadata> entries = SettingsDataView.CustomCanSignalsMetadata;

        if (TryResolveSignalMessageIdentity(signal, out uint messageId, out bool isExtended))
        {
            for (int i = entries.Count - 1; i >= 0; i--)
            {
                if (IsMetadataIdentityMatch(signal, messageId, isExtended, entries[i]))
                {
                    return entries[i];
                }
            }
        }

        // Fallbacks only consider legacy entries that never recorded a message identity.
        for (int i = entries.Count - 1; i >= 0; i--)
        {
            CustomCanSignalMetadata metadata = entries[i];
            if (!metadata.MessageId.HasValue
                && metadata.MessageIndex == signal.MessageIndex
                && IsMetadataLayoutMatch(signal, metadata, 1e-4f))
            {
                return metadata;
            }
        }

        for (int i = entries.Count - 1; i >= 0; i--)
        {
            CustomCanSignalMetadata metadata = entries[i];
            if (!metadata.MessageId.HasValue
                && metadata.SignalIndex == signal.Index
                && metadata.MessageIndex == signal.MessageIndex
                && metadata.StartBit == signal.StartBit
                && metadata.BitLength == signal.BitLength)
            {
                return metadata;
            }
        }

        return null;
    }

    private string? FindMetadataMessageName(uint messageId, bool isExtended)
    {
        SettingsDataView.CustomCanSignalsMetadata ??= [];
        return SettingsDataView.CustomCanSignalsMetadata
            .LastOrDefault(metadata => metadata.MessageId == messageId
                && metadata.IsExtended == isExtended
                && !IsPlaceholderMessageName(metadata.MessageName))
            ?.MessageName.Trim();
    }

    private static bool ShouldApplyMetadataName(string? currentName, int signalIndex)
    {
        if (string.IsNullOrWhiteSpace(currentName))
        {
            return true;
        }

        string trimmedName = currentName.Trim();
        if (string.Equals(trimmedName, $"Signal {signalIndex + 1}", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return Regex.IsMatch(trimmedName, @"^Signal\s+[A-Za-z]$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(trimmedName, @"^Signal\s+\d+$", RegexOptions.IgnoreCase)
            || Regex.IsMatch(trimmedName, @"^Signal[A-Za-z]$", RegexOptions.IgnoreCase);
    }

    private static bool IsPlaceholderMessageName(string? messageName)
    {
        if (string.IsNullOrWhiteSpace(messageName))
        {
            return true;
        }

        string trimmedName = messageName.Trim();
        if (trimmedName.StartsWith("Signal ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (trimmedName.StartsWith("Message ", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool HasStorableMetadata(CustomCanSignalMetadata metadata)
    {
        return !ShouldApplyMetadataName(metadata.Name, metadata.SignalIndex)
            || !IsPlaceholderMessageName(metadata.MessageName)
            || metadata.MinValue.HasValue
            || metadata.MaxValue.HasValue
            || !string.IsNullOrWhiteSpace(metadata.Units);
    }

    private static bool IsSameMetadataKey(CustomCanSignalMetadata a, CustomCanSignalMetadata b)
    {
        if (a.MessageId.HasValue != b.MessageId.HasValue)
        {
            return false;
        }

        bool layoutMatch = a.StartBit == b.StartBit &&
            a.BitLength == b.BitLength &&
            a.IsMotorola == b.IsMotorola &&
            a.IsSigned == b.IsSigned &&
            CustomCanFloatsMatch(a.Factor, b.Factor, 1e-5f) &&
            CustomCanFloatsMatch(a.Offset, b.Offset, 1e-5f);

        return a.MessageId.HasValue
            ? a.MessageId == b.MessageId && a.IsExtended == b.IsExtended && layoutMatch
            : a.SignalIndex == b.SignalIndex && a.MessageIndex == b.MessageIndex && layoutMatch;
    }

    private static List<CustomCanSignalMetadata> NormalizeCustomCanSignalMetadata(IEnumerable<CustomCanSignalMetadata> source)
    {
        var normalized = new List<CustomCanSignalMetadata>();
        foreach (CustomCanSignalMetadata metadata in source)
        {
            if (metadata == null)
            {
                continue;
            }

            CustomCanSignalMetadata sanitized = metadata.Clone();
            sanitized.SignalIndex = Math.Clamp(sanitized.SignalIndex, 0, Constants.MAX_CUSTOM_CAN_SIGNALS - 1);
            sanitized.MessageIndex = (byte)Math.Clamp((int)sanitized.MessageIndex, 0, Constants.MAX_CUSTOM_CAN_MESSAGES - 1);
            sanitized.StartBit = Math.Clamp(sanitized.StartBit, (byte)0, (byte)63);
            sanitized.BitLength = Math.Clamp(sanitized.BitLength, (byte)1, (byte)64);
            sanitized.Factor = float.IsFinite(sanitized.Factor) ? sanitized.Factor : 1.0f;
            sanitized.Offset = float.IsFinite(sanitized.Offset) ? sanitized.Offset : 0.0f;
            sanitized.Name = sanitized.Name?.Trim() ?? string.Empty;
            sanitized.MessageName = sanitized.MessageName?.Trim() ?? string.Empty;
            sanitized.Units = sanitized.Units ?? string.Empty;
            sanitized.IdentityFingerprint = sanitized.IdentityFingerprint ?? string.Empty;
            MigrateLegacyMetadataIdentity(sanitized);

            if (!HasStorableMetadata(sanitized))
            {
                continue;
            }

            int existingIndex = normalized.FindIndex(item => IsSameMetadataKey(item, sanitized));
            if (existingIndex >= 0)
            {
                normalized[existingIndex] = sanitized;
            }
            else
            {
                normalized.Add(sanitized);
            }
        }

        return normalized;
    }

    private void ReplaceCustomCanSignalMetadata(IEnumerable<CustomCanSignalMetadata> entries)
    {
        SettingsDataView.CustomCanSignalsMetadata ??= [];
        List<CustomCanSignalMetadata> normalized = NormalizeCustomCanSignalMetadata(entries);
        SettingsDataView.CustomCanSignalsMetadata.Clear();
        foreach (CustomCanSignalMetadata metadata in normalized)
        {
            SettingsDataView.CustomCanSignalsMetadata.Add(metadata);
        }
    }

    private void LoadPersistedCustomCanSignalMetadata()
    {
        try
        {
            if (!File.Exists(CustomCanMetadataCachePath))
            {
                return;
            }

            string json = File.ReadAllText(CustomCanMetadataCachePath);
            CustomCanMetadataCache? cache = JsonSerializer.Deserialize<CustomCanMetadataCache>(json);
            if (cache?.Signals == null)
            {
                return;
            }

            ReplaceCustomCanSignalMetadata(cache.Signals);
        }
        catch (Exception ex)
        {
            AddLog("Failed to load local Custom CAN metadata cache.", exception: ex);
        }
    }

    private void PersistCustomCanSignalMetadata()
    {
        try
        {
            string path = CustomCanMetadataCachePath;
            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var cache = new CustomCanMetadataCache
            {
                Signals = SettingsDataView.CustomCanSignalsMetadata
                    .Where(HasStorableMetadata)
                    .Select(metadata => metadata.Clone())
                    .ToList(),
            };

            string json = JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true });
            string tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AddLog("Failed to persist local Custom CAN metadata cache.", exception: ex);
        }
    }

    private void UpsertSignalMetadata(CustomCanSignal signal)
    {
        if (UpsertSignalMetadataCore(signal))
        {
            PersistCustomCanSignalMetadata();
        }
    }

    /// <summary>
    /// Records the signal's local-only details (names, limits, units) against its slot-independent
    /// identity. Placeholder-only signals are never recorded. Returns true when the store changed.
    /// </summary>
    private bool UpsertSignalMetadataCore(CustomCanSignal signal)
    {
        SettingsDataView.CustomCanSignalsMetadata ??= [];

        if (!signal.IsEnabled || !TryResolveSignalMessageIdentity(signal, out uint messageId, out bool isExtended))
        {
            return false;
        }

        string signalName = signal.Name?.Trim() ?? string.Empty;
        string messageName = signal.MessageName?.Trim() ?? string.Empty;
        bool hasSignalName = !ShouldApplyMetadataName(signalName, signal.Index);
        bool hasMessageName = !IsPlaceholderMessageName(messageName);
        bool hasLimits = signal.MinValue.HasValue || signal.MaxValue.HasValue || !string.IsNullOrWhiteSpace(signal.Units);

        CustomCanSignalMetadata? metadata = FindSignalMetadata(signal);
        if (metadata == null)
        {
            if (!hasSignalName && !hasMessageName && !hasLimits)
            {
                return false;
            }

            metadata = new CustomCanSignalMetadata();
            SettingsDataView.CustomCanSignalsMetadata.Add(metadata);
        }

        string resolvedSignalName = hasSignalName ? signalName : metadata.Name;
        string resolvedMessageName = hasMessageName ? messageName : metadata.MessageName;
        float? resolvedMin = hasLimits ? signal.MinValue : metadata.MinValue;
        float? resolvedMax = hasLimits ? signal.MaxValue : metadata.MaxValue;
        string resolvedUnits = hasLimits ? signal.Units ?? string.Empty : metadata.Units;

        bool changed =
            metadata.SignalIndex != signal.Index ||
            metadata.MessageIndex != signal.MessageIndex ||
            metadata.MessageId != messageId ||
            metadata.IsExtended != isExtended ||
            metadata.StartBit != signal.StartBit ||
            metadata.BitLength != signal.BitLength ||
            metadata.IsMotorola != signal.IsMotorola ||
            metadata.IsSigned != signal.IsSigned ||
            !metadata.Factor.Equals(signal.Factor) ||
            !metadata.Offset.Equals(signal.Offset) ||
            !string.Equals(metadata.Name, resolvedSignalName, StringComparison.Ordinal) ||
            !string.Equals(metadata.MessageName, resolvedMessageName, StringComparison.Ordinal) ||
            metadata.MinValue != resolvedMin ||
            metadata.MaxValue != resolvedMax ||
            !string.Equals(metadata.Units, resolvedUnits, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(metadata.IdentityFingerprint);

        metadata.SignalIndex = signal.Index;
        metadata.MessageIndex = signal.MessageIndex;
        metadata.MessageId = messageId;
        metadata.IsExtended = isExtended;
        metadata.StartBit = signal.StartBit;
        metadata.BitLength = signal.BitLength;
        metadata.IsMotorola = signal.IsMotorola;
        metadata.IsSigned = signal.IsSigned;
        metadata.Factor = signal.Factor;
        metadata.Offset = signal.Offset;
        metadata.Name = resolvedSignalName;
        metadata.MessageName = resolvedMessageName;
        metadata.MinValue = resolvedMin;
        metadata.MaxValue = resolvedMax;
        metadata.Units = resolvedUnits;
        metadata.IdentityFingerprint = string.Empty;

        if (changed)
        {
            // Drop any older entry that now describes the same identity.
            for (int i = SettingsDataView.CustomCanSignalsMetadata.Count - 1; i >= 0; i--)
            {
                CustomCanSignalMetadata other = SettingsDataView.CustomCanSignalsMetadata[i];
                if (!ReferenceEquals(other, metadata) && IsSameMetadataKey(other, metadata))
                {
                    SettingsDataView.CustomCanSignalsMetadata.RemoveAt(i);
                }
            }
        }

        return changed;
    }

    private void ReconcileCustomCanSignalMetadata()
    {
        SettingsDataView.CustomCanSignalsMetadata ??= [];

        foreach (CustomCanSignalMetadata metadata in SettingsDataView.CustomCanSignalsMetadata)
        {
            MigrateLegacyMetadataIdentity(metadata);
        }

        bool changed = false;
        _customCanMetadataUpsertSuppression++;
        try
        {
            foreach (CustomCanSignal signal in SettingsDataView.CustomCanSignalsStaticData.Where(item => item.IsEnabled))
            {
                CustomCanSignalMetadata? metadata = FindSignalMetadata(signal);
                if (metadata != null)
                {
                    if (ShouldApplyMetadataName(signal.Name, signal.Index) && !ShouldApplyMetadataName(metadata.Name, signal.Index))
                    {
                        signal.Name = metadata.Name.Trim();
                    }

                    if (IsPlaceholderMessageName(signal.MessageName) && !IsPlaceholderMessageName(metadata.MessageName))
                    {
                        signal.MessageName = metadata.MessageName.Trim();
                    }

                    bool signalHasLimits = signal.MinValue.HasValue || signal.MaxValue.HasValue || !string.IsNullOrWhiteSpace(signal.Units);
                    if (!signalHasLimits)
                    {
                        signal.MinValue = metadata.MinValue;
                        signal.MaxValue = metadata.MaxValue;
                        signal.Units = metadata.Units;
                    }
                }

                if (IsPlaceholderMessageName(signal.MessageName)
                    && TryResolveSignalMessageIdentity(signal, out uint messageId, out bool isExtended))
                {
                    string? messageName = FindMetadataMessageName(messageId, isExtended);
                    if (!string.IsNullOrWhiteSpace(messageName))
                    {
                        signal.MessageName = messageName;
                    }
                }

                changed |= UpsertSignalMetadataCore(signal);
            }
        }
        finally
        {
            _customCanMetadataUpsertSuppression--;
        }

        if (changed)
        {
            PersistCustomCanSignalMetadata();
        }
    }

    private static string BuildDbcSignalIdentity(DbcCanSignalDefinition signal)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{signal.MessageId}|{signal.IsExtended}|{signal.Dlc}|{signal.StartBit}|{signal.BitLength}|{signal.IsMotorola}|{signal.IsSigned}|{signal.Factor:R}|{signal.Offset:R}|{signal.MinValue?.ToString("R", CultureInfo.InvariantCulture) ?? ""}|{signal.MaxValue?.ToString("R", CultureInfo.InvariantCulture) ?? ""}|{signal.MessageName}|{signal.Name}|{signal.Units}");
    }

    private int BuildControllerCustomCanSignalsSignature()
    {
        unchecked
        {
            int hash = 17;
            foreach (CustomCanSignal signal in SettingsDataView.CustomCanSignalsStaticData)
            {
                if (!signal.IsEnabled)
                {
                    continue;
                }

                hash = (hash * 31) + signal.Index;
            }

            return hash;
        }
    }

    [RelayCommand]
    private async Task ImportDbcFile()
    {
        string? dbcPath = await _appCloser.BrowseDbcFilePathAsync();
        if (string.IsNullOrWhiteSpace(dbcPath) || !File.Exists(dbcPath))
        {
            return;
        }

        try
        {
            string content = await File.ReadAllTextAsync(dbcPath);
            ParseDbc(content, out List<DbcCanMessageDefinition> messages, out List<DbcCanSignalDefinition> signals);

            ImportedDbcMessages.Clear();
            foreach (DbcCanMessageDefinition message in messages.OrderBy(m => m.Id))
            {
                ImportedDbcMessages.Add(message);
            }

            ImportedDbcSignals.Clear();
            foreach (DbcCanSignalDefinition signal in signals.OrderBy(s => s.MessageId).ThenBy(s => s.Name))
            {
                ImportedDbcSignals.Add(signal);
            }

            SelectedImportedDbcSignals.Clear();
            SelectedImportedDbcSignal = ImportedDbcSignals.FirstOrDefault();
            if (SelectedImportedDbcSignal != null)
            {
                SelectedImportedDbcSignals.Add(SelectedImportedDbcSignal);
            }

            CustomCanStatusMessage = $"Loaded DBC: {ImportedDbcMessages.Count} messages, {ImportedDbcSignals.Count} signals.";
            AddLog(CustomCanStatusMessage);
        }
        catch (Exception ex)
        {
            CustomCanStatusMessage = "Failed to import DBC.";
            AddLog("Failed to import DBC file.", exception: ex);
        }
    }

    [RelayCommand]
    private async Task ImportSelectedDbcSignalToController()
    {
        IEnumerable<DbcCanSignalDefinition> selectedSignalCandidates = SelectedImportedDbcSignals.Count > 0
            ? SelectedImportedDbcSignals
            : SelectedImportedDbcSignal != null
                ? [SelectedImportedDbcSignal]
                : [];

        List<DbcCanSignalDefinition> selectedSignals = selectedSignalCandidates
            .DistinctBy(BuildDbcSignalIdentity)
            .ToList();
        if (selectedSignals.Count == 0)
        {
            CustomCanStatusMessage = "Select at least one DBC signal first.";
            return;
        }

        CustomCanStatusMessage = selectedSignals.Count == 1
            ? "Saving selected CAN signal..."
            : "Saving selected CAN signals...";

        int importedCount = 0;
        bool online = _portService != null && IsConnected && CommsEstablished;
        if (!online)
        {
            foreach (DbcCanSignalDefinition signal in selectedSignals)
            {
                if (!await SaveSignalToControllerWithRetryAsync(signal, signal.Dlc, timeoutMs: 1000, CustomCanTxOptions.Rx, isDbcImport: true))
                {
                    break;
                }

                importedCount++;
            }
        }
        else
        {
            bool sessionStarted = BeginCustomCanConfigSession(out bool previousSuspendLiveRequestPolling);
            IsCustomCanBusy = true;
            try
            {
                if (!await _portService!.RequestCustomCanSnapshotAsync())
                {
                    CustomCanStatusMessage = "No Custom CAN snapshot response from controller.";
                    AddLog(CustomCanStatusMessage);
                    return;
                }

                ApplyPendingCustomCanSnapshotIfAvailable();

                foreach (DbcCanSignalDefinition signal in selectedSignals)
                {
                    if (!await SaveSignalToControllerWithRetryAsync(signal, signal.Dlc, timeoutMs: 1000, CustomCanTxOptions.Rx, batched: true, isDbcImport: true))
                    {
                        break;
                    }

                    importedCount++;
                }

                // Commit everything the controller accepted in one EEPROM write, even after a partial failure,
                // so RAM and flash agree with what is displayed.
                if (importedCount > 0)
                {
                    string loopStatus = CustomCanStatusMessage;
                    if (!await _portService.SaveStandaloneChangesAsync())
                    {
                        CustomCanStatusMessage = "Controller accepted the signals but failed to save them to EEPROM.";
                        AddLog(CustomCanStatusMessage);
                        importedCount = 0;
                    }
                    else
                    {
                        CustomCanStatusMessage = loopStatus;
                    }
                }

                if (await _portService.RequestCustomCanSnapshotAsync())
                {
                    ApplyPendingCustomCanSnapshotIfAvailable();
                    RefreshCustomCanBindings();
                    RefreshIoFlowConnections();
                }
                else
                {
                    _portService.RequestCustomCanSnapshot();
                    StartBackgroundTask(RequestCustomCanSnapshotRetryAsync(), "Custom CAN snapshot retry");
                }
            }
            finally
            {
                EndCustomCanConfigSession(sessionStarted, previousSuspendLiveRequestPolling);
                IsCustomCanBusy = false;
            }
        }

        if (selectedSignals.Count == 1)
        {
            return;
        }

        if (importedCount == selectedSignals.Count)
        {
            CustomCanStatusMessage = $"Imported {importedCount} DBC signals.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        if (importedCount > 0)
        {
            string failureMessage = CustomCanStatusMessage;
            CustomCanStatusMessage = $"Imported {importedCount} of {selectedSignals.Count} DBC signals. {failureMessage}";
            AddLog(CustomCanStatusMessage);
        }
    }

    [RelayCommand]
    private async Task SaveManualCanSignalToController()
    {
        RevalidateManualCanSignal();
        if (!TryBuildManualCanSignal(out DbcCanSignalDefinition manual, out ushort timeout, out CustomCanTxOptions txOptions, out Dictionary<string, string> errors))
        {
            CustomCanStatusMessage = $"Manual signal not saved: {errors.Values.First()}";
            AddLog(CustomCanStatusMessage);
            return;
        }

        CustomCanStatusMessage = "Saving manual CAN signal...";
        CustomCanMessage? targetMessage = _manualCanMessageTarget.Message;
        int? targetSlot = targetMessage == null ? null : SettingsDataView.CustomCanMessagesStaticData.IndexOf(targetMessage);
        bool saved = await SaveSignalToControllerWithRetryAsync(manual, manual.Dlc, timeout, txOptions, targetMessageSlotIndex: targetSlot);
        if (saved && targetMessage == null)
        {
            // Further signals most likely belong to the frame just created (Kvaser-style message → signals workflow).
            SelectManualMessageTarget(SettingsDataView.CustomCanMessagesStaticData.FirstOrDefault(message =>
                message.IsEnabled && message.Id == manual.MessageId && message.IsExtended == manual.IsExtended));
        }
    }

    // --- Stored table editing: direction only (TX/RX) ---

    public ObservableCollection<string> StoredCustomCanDirectionOptions { get; } = ["RX", "TX"];

    private int _selectedStoredDirectionIndex;
    public int SelectedStoredDirectionIndex
    {
        get => _selectedStoredDirectionIndex;
        set => SetProperty(ref _selectedStoredDirectionIndex, Math.Clamp(value, 0, StoredCustomCanDirectionOptions.Count - 1));
    }

    /// <summary>
    /// Direction is a property of the CAN frame (message slot), so it is chosen per message in the messages
    /// table and applies to every signal in that frame. Describes that scope for the checked messages.
    /// </summary>
    public string StoredDirectionScopeSummary => DescribeDirectionScope(GetCheckedStoredMessages());

    private static string DescribeStoredMessage(CustomCanMessage message) =>
        string.IsNullOrWhiteSpace(message.Name) || message.Name.StartsWith("Message ", StringComparison.Ordinal)
            ? message.IdHex
            : $"{message.IdHex} ({message.Name})";

    private static string DescribeDirectionScope(IReadOnlyCollection<CustomCanMessage> messages)
    {
        if (messages.Count == 0)
        {
            return string.Empty;
        }

        IEnumerable<string> parts = messages.Select(message =>
            $"{DescribeStoredMessage(message)}: {message.SignalCount} signal{(message.SignalCount == 1 ? string.Empty : "s")}");
        return $"Applies to every signal in {(messages.Count == 1 ? "this message" : $"these {messages.Count} messages")}: {string.Join(", ", parts)}.";
    }

    [RelayCommand]
    private void ApplySelectedStoredDirection()
    {
        List<CustomCanMessage> checkedMessages = GetCheckedStoredMessages();

        if (checkedMessages.Count == 0)
        {
            CustomCanStatusMessage = "Tick at least one message in the Messages table first.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        bool makeTx = SelectedStoredDirectionIndex == 1;
        byte trigger = Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS;
        string scopeSummary = DescribeDirectionScope(checkedMessages);

        bool anyMessageChanged = false;

        foreach (CustomCanMessage message in checkedMessages)
        {
            int messageIndex = SettingsDataView.CustomCanMessagesStaticData.IndexOf(message);
            if (messageIndex < 0 || makeTx == message.IsTx)
            {
                continue;
            }

            byte newFlags = (byte)(message.Flags & 0x03);
            if (makeTx)
            {
                newFlags |= 0x04;
                if (message.IsPeriodic)
                {
                    newFlags |= 0x08;
                }
            }

            message.Flags = newFlags;
            message.TxTrigger = trigger;

            if (makeTx && message.IsPeriodic && message.TimeoutMs < 1)
            {
                message.TimeoutMs = 100;
            }

            foreach (CustomCanSignal relatedSignal in SettingsDataView.CustomCanSignalsStaticData
                         .Where(item => item.IsEnabled && item.MessageIndex == messageIndex))
            {
                if (relatedSignal.ValueSource != Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT && relatedSignal.Factor == 0.0f)
                {
                    relatedSignal.ValueSource = Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT;
                }
            }

            anyMessageChanged = true;
        }

        if (!anyMessageChanged)
        {
            CustomCanStatusMessage = $"The checked message{(checkedMessages.Count == 1 ? " is" : "s are")} already {(makeTx ? "TX" : "RX")}.";
            return;
        }

        RefreshCustomCanBindings();
        HasPendingConfigChanges = true;
        CustomCanStatusMessage = $"Queued {(makeTx ? "TX" : "RX")} for {checkedMessages.Count} message(s). {scopeSummary} Click SAVE CHANGES to apply.";
        AddLog(CustomCanStatusMessage);
    }

    [RelayCommand]
    private void DeleteCheckedStoredMessages()
    {
        List<CustomCanMessage> checkedMessages = GetCheckedStoredMessages();
        if (checkedMessages.Count == 0)
        {
            CustomCanStatusMessage = "Tick at least one message in the Messages table to delete.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        int signalCount = 0;
        foreach (CustomCanMessage message in checkedMessages)
        {
            int messageIndex = SettingsDataView.CustomCanMessagesStaticData.IndexOf(message);
            foreach (CustomCanSignal signal in SettingsDataView.CustomCanSignalsStaticData
                         .Where(item => item.IsEnabled && item.MessageIndex == messageIndex))
            {
                signal.Flags = (byte)(signal.Flags & ~0x01);
                signal.IsMarkedForDeletion = false;
                signalCount++;
            }

            RemoveStoredCanMessageAssignment(message);
            message.IsSelected = false;
        }

        RefreshCustomCanBindings();
        HasPendingConfigChanges = true;
        CustomCanStatusMessage = $"Queued deletion of {checkedMessages.Count} message(s) and their {signalCount} signal(s). Click SAVE CHANGES to apply.";
        AddLog(CustomCanStatusMessage);
    }

    private static void RemoveStoredCanMessageAssignment(CustomCanMessage message)
    {
        message.Flags = (byte)(message.Flags & 0x02);
        message.TxTrigger = Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS;
    }

    private async Task<bool> ApplyCustomCanMessageSettingsAsync(CustomCanSignal signal, bool isTx, bool isPeriodic, byte trigger, ushort timeoutMs, byte valueSource)
    {
        if (!CanEditStoredCustomCanSignals())
        {
            CustomCanStatusMessage = "Connect to controller to update stored Custom CAN messages.";
            AddLog(CustomCanStatusMessage);
            return false;
        }

        CustomCanMessage? message = signal.OwnerMessage;
        if (_portService == null || message == null)
        {
            return false;
        }

        int messageIndex = signal.MessageIndex;
        byte originalFlags = message.Flags;
        byte originalTrigger = message.TxTrigger;
        ushort originalTimeout = message.TimeoutMs;

        List<CustomCanSignal> relatedSignals = SettingsDataView.CustomCanSignalsStaticData
            .Where(item => item.IsEnabled && item.MessageIndex == messageIndex)
            .ToList();

        if (!relatedSignals.Contains(signal))
        {
            relatedSignals.Add(signal);
        }

        var originalValueSources = relatedSignals.ToDictionary(item => item, item => item.ValueSource);

        byte requestedFlags = (byte)(message.Flags & 0x03);
        if (isTx)
        {
            requestedFlags |= 0x04;
            if (isPeriodic)
            {
                requestedFlags |= 0x08;
            }
        }

        byte requestedTrigger = trigger;
        ushort requestedTimeout = timeoutMs;

        message.Flags = requestedFlags;
        message.TxTrigger = requestedTrigger;
        message.TimeoutMs = requestedTimeout;

        foreach (CustomCanSignal relatedSignal in relatedSignals)
        {
            byte adjustedValueSource = ReferenceEquals(relatedSignal, signal)
                ? valueSource
                : relatedSignal.ValueSource;

            if (isTx && adjustedValueSource != Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT && relatedSignal.Factor == 0.0f)
            {
                adjustedValueSource = Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT;
            }

            relatedSignal.ValueSource = adjustedValueSource;
        }

        Dictionary<int, byte> requestedSignalValueSources = relatedSignals
            .ToDictionary(item => item.Index, item => item.ValueSource);
        List<int> relatedSignalIndexes = requestedSignalValueSources.Keys.ToList();

        RegisterPendingCustomCanWrite(messageIndex, requestedTimeout, requestedFlags, requestedTrigger, requestedSignalValueSources);

        void Revert()
        {
            message.Flags = originalFlags;
            message.TxTrigger = originalTrigger;
            message.TimeoutMs = originalTimeout;

            foreach ((CustomCanSignal relatedSignal, byte originalSource) in originalValueSources)
            {
                relatedSignal.ValueSource = originalSource;
            }
        }

        try
        {
            if (!TryValidateCustomCanConfiguration(messageIndex, message, signal.Index, signal, out string validationError))
            {
                Revert();
                CustomCanStatusMessage = validationError;
                AddLog(CustomCanStatusMessage);
                return false;
            }

            CustomCanStatusMessage = "Saving CAN message changes...";
            IsCustomCanBusy = true;
            bool sessionStarted = BeginCustomCanConfigSession(out bool previousSuspendLiveRequestPolling);
            bool snapshotReceived = false;
            try
            {
                List<CustomCanSignal> changedSignals = relatedSignals
                    .Where(relatedSignal =>
                        originalValueSources.TryGetValue(relatedSignal, out byte originalValueSource) &&
                        requestedSignalValueSources.TryGetValue(relatedSignal.Index, out byte requestedValueSource) &&
                        requestedValueSource != originalValueSource)
                    .ToList();

                async Task<bool> WriteChangedSignalsAsync()
                {
                    foreach (CustomCanSignal relatedSignal in changedSignals)
                    {
                        if (!await WriteCustomCanSignalRecordAsync(relatedSignal.Index, relatedSignal))
                        {
                            return false;
                        }
                    }

                    return true;
                }

                // Going TX needs the signals' encode settings in place first; going RX relaxes them,
                // so the message record goes first. Either way each step is a valid whole configuration.
                bool persisted = isTx
                    ? await WriteChangedSignalsAsync() && await WriteCustomCanMessageRecordAsync(messageIndex, message)
                    : await WriteCustomCanMessageRecordAsync(messageIndex, message) && await WriteChangedSignalsAsync();

                if (persisted)
                {
                    persisted = await _portService.SaveStandaloneChangesAsync();
                }

                if (!persisted)
                {
                    string writeError = _portService.LastCustomCanWriteError ?? string.Empty;
                    Revert();
                    // Undo any records the controller already accepted so its RAM matches what we show.
                    _ = isTx
                        ? await WriteCustomCanMessageRecordAsync(messageIndex, message) && await WriteChangedSignalsAsync()
                        : await WriteChangedSignalsAsync() && await WriteCustomCanMessageRecordAsync(messageIndex, message);
                    CustomCanStatusMessage = string.IsNullOrWhiteSpace(writeError)
                        ? "Controller rejected Custom CAN message update."
                        : writeError;
                    AddLog(CustomCanStatusMessage);
                    return false;
                }

                snapshotReceived = await _portService.RequestCustomCanSnapshotAsync();
            }
            finally
            {
                EndCustomCanConfigSession(sessionStarted, previousSuspendLiveRequestPolling);
                IsCustomCanBusy = false;
            }

            if (snapshotReceived)
            {
                ApplyPendingCustomCanSnapshotIfAvailable();
            }
            else
            {
                _portService.RequestCustomCanSnapshot();
            }

            RefreshCustomCanBindings();
            RefreshIoFlowConnections();

            bool directionApplied = messageIndex >= 0 && messageIndex < SettingsDataView.CustomCanMessagesStaticData.Count &&
                SettingsDataView.CustomCanMessagesStaticData[messageIndex].IsTx == isTx;

            if (!directionApplied)
            {
                await Task.Delay(150);
                bool verifySnapshotReceived = await _portService.RequestCustomCanSnapshotAsync();
                if (verifySnapshotReceived)
                {
                    ApplyPendingCustomCanSnapshotIfAvailable();
                    RefreshCustomCanBindings();
                    RefreshIoFlowConnections();
                }

                directionApplied = messageIndex >= 0 && messageIndex < SettingsDataView.CustomCanMessagesStaticData.Count &&
                    SettingsDataView.CustomCanMessagesStaticData[messageIndex].IsTx == isTx;
            }

            if (!directionApplied)
            {
                CustomCanStatusMessage = $"Controller kept message 0x{message.Id:X} as {(message.IsTx ? "TX" : "RX")}.";
                AddLog(CustomCanStatusMessage);
                return false;
            }

            CustomCanStatusMessage = $"Updated message 0x{message.Id:X} ({message.DirectionLabel}).";
            AddLog(CustomCanStatusMessage);
            return true;
        }
        finally
        {
            ClearPendingCustomCanWrite(messageIndex, relatedSignalIndexes);
        }
    }

    /// <summary>
    /// Stages a TX message's trigger channel and enabled state for Save to controller.
    /// </summary>
    public Task<bool> SetCustomCanMessageTxStateAsync(int messageIndex, byte trigger, bool enabled) =>
        Task.FromResult(StageCustomCanTxMessageChange(messageIndex, message =>
        {
            message.TxTrigger = trigger;
            message.IsEnabled = enabled;
        }));

    public Task<bool> SetCustomCanMessageTxModeAsync(int messageIndex, bool isPeriodic, ushort timeoutMs)
    {
        if (isPeriodic && (timeoutMs < Constants.CUSTOM_CAN_TX_PERIOD_MIN_MS ||
                           timeoutMs > Constants.CUSTOM_CAN_TX_PERIOD_MAX_MS ||
                           timeoutMs % Constants.CUSTOM_CAN_TX_PERIOD_STEP_MS != 0))
        {
            CustomCanStatusMessage = "Periodic TX period must be between 100 and 30,000 ms in 100 ms steps.";
            AddLog(CustomCanStatusMessage);
            return Task.FromResult(false);
        }

        return Task.FromResult(StageCustomCanTxMessageChange(messageIndex, message =>
        {
            message.IsPeriodic = isPeriodic;
            if (isPeriodic)
            {
                message.TimeoutMs = timeoutMs;
            }
        }));
    }

    private bool StageCustomCanTxMessageChange(int messageIndex, Action<CustomCanMessage> apply)
    {
        if (messageIndex < 0 || messageIndex >= SettingsDataView.CustomCanMessagesStaticData.Count ||
            !SettingsDataView.CustomCanMessagesStaticData[messageIndex].IsTx)
        {
            CustomCanStatusMessage = "Select a configured TX message from CAN Messages.";
            AddLog(CustomCanStatusMessage);
            return false;
        }

        CustomCanMessage message = SettingsDataView.CustomCanMessagesStaticData[messageIndex];
        byte originalFlags = message.Flags;
        byte originalTrigger = message.TxTrigger;
        ushort originalTimeout = message.TimeoutMs;
        apply(message);

        if (!TryValidateCustomCanConfiguration(messageIndex, message, -1, new CustomCanSignal(), out string validationError))
        {
            message.Flags = originalFlags;
            message.TxTrigger = originalTrigger;
            message.TimeoutMs = originalTimeout;
            CustomCanStatusMessage = validationError;
            AddLog(CustomCanStatusMessage);
            return false;
        }

        RecalculatePendingConfigChanges();
        NotifySelectedChannelCanTxChanged();
        return true;
    }

    // --- Per-channel CAN TX section (Settings tab) ---

    /// <summary>Raised when the UI should navigate to the CAN Messages tab.</summary>
    public event EventHandler? CanMessagesNavigationRequested;

    private int SelectedChannelZeroBasedIndex => (SelectedChannel?.ChannelNumber ?? 0) - 1;

    private List<CustomCanMessage> ConfiguredTxMessagesForChannel(int channelIndex) =>
        SettingsDataView.CustomCanMessagesStaticData
            .Where(message => message.IsTx && message.TxTrigger == channelIndex && (message.IsEnabled || message.Id != 0))
            .ToList();

    private List<CustomCanMessage> AvailableCanMessagesForChannelCanTxSettings()
    {
        IEnumerable<CustomCanMessage> allMessages = SettingsDataView.CustomCanMessagesStaticData;
        int selectedChannelIndex = SelectedChannelZeroBasedIndex;

        HashSet<int> messageIndexes = SettingsDataView.CustomCanSignalsStaticData
            .Where(signal => signal.IsEnabled)
            .Select(signal => (int)signal.MessageIndex)
            .Concat(allMessages.Where(message => message.IsEnabled || (message.IsTx && message.Id != 0))
                .Select(message => message.Index))
            .Concat(selectedChannelIndex >= 0
                ? ConfiguredTxMessagesForChannel(selectedChannelIndex).Select(message => message.Index)
                : Enumerable.Empty<int>())
            .Where(index => index >= 0 && index < SettingsDataView.CustomCanMessagesStaticData.Count)
            .ToHashSet();

        List<CustomCanMessage> availableMessages = allMessages
            .Where(message => message.IsTx && message.Id != 0 && messageIndexes.Contains(message.Index))
            .ToList();

        return availableMessages;
    }

    public ObservableCollection<CustomCanMessage> SelectedChannelCanTxMessages { get; } = [];

    private CustomCanMessage? _selectedChannelCanTxMessage;
    private bool _suppressSelectedChannelCanTxEditTracking;

    public CustomCanMessage? SelectedChannelCanTxMessage
    {
        get => _selectedChannelCanTxMessage;
        set
        {
            if (SetProperty(ref _selectedChannelCanTxMessage, value))
            {
                OnPropertyChanged(nameof(HasSelectedChannelCanTxMessage));
                OnPropertyChanged(nameof(SelectedChannelCanTxControlsEnabled));
                RefreshSelectedChannelCanTxEditFields();
            }
        }
    }

    public bool HasSelectedChannelCanTxMessage => SelectedChannelCanTxMessage != null;

    public bool HasAvailableChannelCanTxMessage => SelectedChannelCanTxMessages.Count > 0;

    public bool SelectedChannelCanTxControlsEnabled =>
        SelectedChannelCanTxMessage != null &&
        SelectedChannelCanTxMessage.IsTx &&
        SelectedChannelCanTxMessage.TxTrigger == SelectedChannelZeroBasedIndex;

    public ObservableCollection<CustomCanMessage> AssignedChannelCanTxMessages { get; } = [];
    public bool HasAssignedChannelCanTxMessages => AssignedChannelCanTxMessages.Count > 0;

    private CustomCanMessage? _channelCanTxMessageToAdd;
    public CustomCanMessage? ChannelCanTxMessageToAdd
    {
        get => _channelCanTxMessageToAdd;
        set => SetProperty(ref _channelCanTxMessageToAdd, value);
    }

    public ObservableCollection<string> ChannelCanTxModeOptions { get; } = ["TX one-shot", "TX periodic"];

    private int _selectedChannelCanTxModeIndex;
    public int SelectedChannelCanTxModeIndex
    {
        get => _selectedChannelCanTxModeIndex;
        set
        {
            if (SetProperty(ref _selectedChannelCanTxModeIndex, Math.Clamp(value, 0, ChannelCanTxModeOptions.Count - 1)))
            {
                OnPropertyChanged(nameof(ShowSelectedChannelCanTxTiming));
                OnPropertyChanged(nameof(SelectedChannelCanTxTimeoutLabel));
                OnPropertyChanged(nameof(SelectedChannelCanTxTimeoutTooltip));
                ApplySelectedChannelCanTxSettingChanges();
            }
        }
    }

    private int _selectedChannelCanTxTimeoutMs = 100;
    public int SelectedChannelCanTxTimeoutMs
    {
        get => _selectedChannelCanTxTimeoutMs;
        set
        {
            int clamped = Math.Clamp(value, Constants.CUSTOM_CAN_TX_PERIOD_MIN_MS, Constants.CUSTOM_CAN_TX_PERIOD_MAX_MS);
            int snapped = (int)(Math.Round(clamped / (double)Constants.CUSTOM_CAN_TX_PERIOD_STEP_MS, MidpointRounding.AwayFromZero) * Constants.CUSTOM_CAN_TX_PERIOD_STEP_MS);

            if (SetProperty(ref _selectedChannelCanTxTimeoutMs, snapped))
            {
                ApplySelectedChannelCanTxSettingChanges();
            }
        }
    }

    public bool ShowSelectedChannelCanTxTiming => SelectedChannelCanTxModeIndex == 1;

    public string SelectedChannelCanTxTimeoutLabel => "Period ms";

    public string SelectedChannelCanTxTimeoutTooltip =>
        "Periodic TX: interval between repeated sends while the output remains active.";

    public bool SelectedChannelCanTxEnabled
    {
        get
        {
            int channelIndex = SelectedChannelZeroBasedIndex;
            return channelIndex >= 0 &&
                   SettingsDataView.CustomCanMessagesStaticData.Any(message =>
                       message.IsEnabled && message.IsTx && message.TxTrigger == channelIndex);
        }
    }

    public string SelectedChannelCanTxSummary
    {
        get
        {
            int channelIndex = SelectedChannelZeroBasedIndex;
            if (channelIndex < 0)
            {
                return string.Empty;
            }

            List<CustomCanMessage> messages = ConfiguredTxMessagesForChannel(channelIndex);
            if (messages.Count == 0)
            {
                return "No CAN messages are transmitted when this output is active.";
            }

            string list = string.Join(", ", messages.Select(message =>
            {
                string label = message.Name?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(label) || IsPlaceholderMessageName(label))
                {
                    label = $"Message {message.Index + 1}";
                }

                return message.IsEnabled ? label : $"{label} (off)";
            }));
            return $"Transmitted while this output is active: {list}";
        }
    }

    private void RefreshSelectedChannelCanTxMessages()
    {
        List<CustomCanMessage> availableTxMessages = AvailableCanMessagesForChannelCanTxSettings();
        int? selectedMessageIndex = SelectedChannelCanTxMessage?.Index;

        SyncObservableCollection(SelectedChannelCanTxMessages, availableTxMessages);
        SyncObservableCollection(AssignedChannelCanTxMessages,
            ConfiguredTxMessagesForChannel(SelectedChannelZeroBasedIndex));

        SelectedChannelCanTxMessage = AssignedChannelCanTxMessages.FirstOrDefault(message => message.Index == selectedMessageIndex)
            ?? AssignedChannelCanTxMessages.FirstOrDefault();

        if (ChannelCanTxMessageToAdd == null || !availableTxMessages.Contains(ChannelCanTxMessageToAdd))
        {
            ChannelCanTxMessageToAdd = availableTxMessages.FirstOrDefault();
        }
        OnPropertyChanged(nameof(HasAvailableChannelCanTxMessage));
        OnPropertyChanged(nameof(SelectedChannelCanTxControlsEnabled));
        OnPropertyChanged(nameof(HasAssignedChannelCanTxMessages));
    }

    private void RefreshSelectedChannelCanTxEditFields()
    {
        _suppressSelectedChannelCanTxEditTracking = true;
        try
        {
            if (SelectedChannelCanTxMessage == null)
            {
                SelectedChannelCanTxModeIndex = 0;
                SelectedChannelCanTxTimeoutMs = 100;
                OnPropertyChanged(nameof(ShowSelectedChannelCanTxTiming));
                return;
            }

            SelectedChannelCanTxModeIndex = SelectedChannelCanTxMessage.IsPeriodic ? 1 : 0;
            SelectedChannelCanTxTimeoutMs = SelectedChannelCanTxMessage.TimeoutMs;
            OnPropertyChanged(nameof(ShowSelectedChannelCanTxTiming));
        }
        finally
        {
            _suppressSelectedChannelCanTxEditTracking = false;
        }
    }

    private void ApplySelectedChannelCanTxSettingChanges()
    {
        if (_suppressSelectedChannelCanTxEditTracking || !SelectedChannelCanTxControlsEnabled)
        {
            return;
        }

        if (ApplySelectedChannelCanTxStateToMessage())
        {
            HasPendingConfigChanges = true;
            NotifySelectedChannelCanTxChanged();
        }
    }

    private bool ApplySelectedChannelCanTxStateToMessage()
    {
        int channelIndex = SelectedChannelZeroBasedIndex;
        CustomCanMessage? message = SelectedChannelCanTxMessage;
        if (channelIndex < 0 || message == null)
        {
            return false;
        }

        bool isPeriodic = SelectedChannelCanTxModeIndex == 1;

        ushort timeout = message.TimeoutMs;
        if (isPeriodic)
        {
            timeout = (ushort)SelectedChannelCanTxTimeoutMs;
            if (SelectedChannelCanTxTimeoutMs != timeout)
            {
                _suppressSelectedChannelCanTxEditTracking = true;
                try
                {
                    SelectedChannelCanTxTimeoutMs = timeout;
                }
                finally
                {
                    _suppressSelectedChannelCanTxEditTracking = false;
                }
            }
        }

        bool changed = false;
        if (!message.IsTx)
        {
            message.IsTx = true;
            changed = true;
        }

        if (message.TxTrigger != (byte)channelIndex)
        {
            message.TxTrigger = (byte)channelIndex;
            changed = true;
        }

        if (message.IsPeriodic != isPeriodic)
        {
            message.IsPeriodic = isPeriodic;
            changed = true;
        }

        if (message.TimeoutMs != timeout)
        {
            message.TimeoutMs = timeout;
            changed = true;
        }

        return changed;
    }

    public void NotifySelectedChannelCanTxChanged()
    {
        RefreshSelectedChannelCanTxMessages();
        RefreshSelectedChannelCanTxEditFields();
        RefreshIoFlowConnections();
        OnPropertyChanged(nameof(SelectedChannelCanTxEnabled));
        OnPropertyChanged(nameof(SelectedChannelCanTxSummary));
        OnPropertyChanged(nameof(HasAvailableChannelCanTxMessage));
        OnPropertyChanged(nameof(SelectedChannelCanTxControlsEnabled));
    }

    [RelayCommand]
    private void AddSelectedChannelCanTxMessage()
    {
        CustomCanMessage? message = ChannelCanTxMessageToAdd;
        int channelIndex = SelectedChannelZeroBasedIndex;
        if (message == null || channelIndex < 0)
        {
            CustomCanStatusMessage = "Select a stored CAN message and output channel before adding an assignment.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        if (!message.IsTx || message.Id == 0)
        {
            CustomCanStatusMessage = "Select a configured TX message from CAN Messages before adding an assignment.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        message.TxTrigger = (byte)channelIndex;
        message.IsEnabled = true;
        RecalculatePendingConfigChanges();
        NotifySelectedChannelCanTxChanged();
        SelectedChannelCanTxMessage = message;
    }

    [RelayCommand]
    private async Task RemoveSelectedChannelCanTxMessage()
    {
        CustomCanMessage? message = SelectedChannelCanTxMessage;
        if (message == null || !SelectedChannelCanTxControlsEnabled)
        {
            CustomCanStatusMessage = "Select a CAN TX assignment to remove.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        await SetCustomCanMessageTxStateAsync(message.Index, Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, false);
    }

    [RelayCommand]
    private void ToggleSelectedChannelCanTx()
    {
        int channelIndex = SelectedChannelZeroBasedIndex;
        if (channelIndex < 0)
        {
            return;
        }

        List<CustomCanMessage> configured = ConfiguredTxMessagesForChannel(channelIndex);
        if (configured.Count == 0)
        {
            CustomCanStatusMessage = "Use Add message to assign a CAN TX message to this channel first.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        bool enable = !SelectedChannelCanTxEnabled;
        bool changed = false;
        foreach (CustomCanMessage message in configured)
        {
            if (message.IsEnabled != enable)
            {
                message.IsEnabled = enable;
                changed = true;
            }
        }

        if (changed)
        {
            HasPendingConfigChanges = true;
            CustomCanStatusMessage = enable
                ? $"Queued CAN TX enable for channel {channelIndex + 1}. Click SAVE CHANGES to apply."
                : $"Queued CAN TX disable for channel {channelIndex + 1}. Click SAVE CHANGES to apply.";
            AddLog(CustomCanStatusMessage);
        }

        NotifySelectedChannelCanTxChanged();
    }

    [RelayCommand]
    private void OpenCanMessagesForSelectedChannel()
    {
        int channelIndex = SelectedChannelZeroBasedIndex;
        if (channelIndex >= 0)
        {
            ManualDirectionIndex = 1;
            ManualTriggerIndex = channelIndex + 1;
        }

        CanMessagesNavigationRequested?.Invoke(this, EventArgs.Empty);
    }

    private static bool TryParseManualCanMessageId(string? value, out uint messageId)
    {
        messageId = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string normalized = value.Trim();
        if (normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[2..];
        }

        if (string.IsNullOrWhiteSpace(normalized) ||
            !uint.TryParse(normalized, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed))
        {
            return false;
        }

        if (parsed > 0x1FFFFFFF)
        {
            return false;
        }

        messageId = parsed;
        return true;
    }

    [RelayCommand]
    private async Task ToggleStoredSignalEnabled(CustomCanSignal? signal)
    {
        if (signal == null)
        {
            return;
        }

        if (!CanEditStoredCustomCanSignals())
        {
            CustomCanStatusMessage = "Connect to controller to update stored Custom CAN signals.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        if (_portService == null)
        {
            return;
        }

        byte originalSignalFlags = signal.Flags;
        int messageIndex = signal.MessageIndex;
        if (messageIndex < 0 || messageIndex >= SettingsDataView.CustomCanMessagesStaticData.Count)
        {
            CustomCanStatusMessage = "Stored Custom CAN signal references an invalid message slot.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        CustomCanMessage message = SettingsDataView.CustomCanMessagesStaticData[messageIndex];
        byte originalMessageFlags = message.Flags;

        signal.IsEnabled = !signal.IsEnabled;

        var changedMessages = new List<CustomCanMessage>();
        if (signal.IsEnabled)
        {
            if (!message.IsEnabled)
            {
                message.Flags |= 0x01;
                changedMessages.Add(message);
            }
        }
        else if (message.IsEnabled && !HasEnabledSignalForMessage(messageIndex, signal.Index))
        {
            message.Flags = (byte)(message.Flags & ~0x01);
            changedMessages.Add(message);
        }

        CustomCanStatusMessage = "Saving CAN signal changes...";
        IsCustomCanBusy = true;
        bool sessionStarted = BeginCustomCanConfigSession(out bool previousSuspendLiveRequestPolling);
        try
        {
            bool persisted = await PersistCustomCanEnableStateAsync([signal], changedMessages);
            if (!persisted)
            {
                signal.Flags = originalSignalFlags;
                message.Flags = originalMessageFlags;
                CustomCanStatusMessage = string.IsNullOrWhiteSpace(_portService.LastCustomCanWriteError)
                    ? "Controller rejected Custom CAN signal update."
                    : _portService.LastCustomCanWriteError;
                AddLog(CustomCanStatusMessage);
                return;
            }
        }
        finally
        {
            EndCustomCanConfigSession(sessionStarted, previousSuspendLiveRequestPolling);
            IsCustomCanBusy = false;
        }

        _portService.RequestCustomCanSnapshot();
        RefreshCustomCanBindings();
        CustomCanStatusMessage = signal.IsEnabled
            ? $"Enabled stored Custom CAN signal {signal.SlotNumber}."
            : $"Disabled stored Custom CAN signal {signal.SlotNumber}.";
        AddLog(CustomCanStatusMessage);
    }

    [RelayCommand]
    private void DeleteSelectedStoredSignals()
    {
        List<CustomCanSignal> selectedSignals = SettingsDataView.CustomCanSignalsStaticData
            .Where(signal => signal.IsEnabled && signal.IsMarkedForDeletion)
            .ToList();

        if (selectedSignals.Count == 0)
        {
            CustomCanStatusMessage = "Tick at least one signal in the Signals table to delete.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        foreach (CustomCanSignal signal in selectedSignals)
        {
            signal.Flags = (byte)(signal.Flags & ~0x01);
            signal.IsMarkedForDeletion = false;
        }

        DisableOrphanedMessagesForSignals(selectedSignals);
        RefreshCustomCanBindings();
        HasPendingConfigChanges = true;
        CustomCanStatusMessage = $"Queued deletion for {selectedSignals.Count} stored Custom CAN signal(s). Click SAVE CHANGES to apply.";
        AddLog(CustomCanStatusMessage);
    }

    [RelayCommand]
    private void ClearAllStoredSignals()
    {
        List<CustomCanSignal> enabledSignals = SettingsDataView.CustomCanSignalsStaticData
            .Where(signal => signal.IsEnabled)
            .ToList();

        if (enabledSignals.Count == 0)
        {
            CustomCanStatusMessage = "No stored Custom CAN signals to clear.";
            AddLog(CustomCanStatusMessage);
            return;
        }

        foreach (CustomCanSignal signal in enabledSignals)
        {
            signal.Flags = (byte)(signal.Flags & ~0x01);
            signal.IsMarkedForDeletion = false;
        }

        foreach (CustomCanMessage message in SettingsDataView.CustomCanMessagesStaticData)
        {
            RemoveStoredCanMessageAssignment(message);
        }

        RefreshCustomCanBindings();
        HasPendingConfigChanges = true;
        CustomCanStatusMessage = "Queued clear-all for stored Custom CAN signals. Click SAVE CHANGES to apply.";
        AddLog(CustomCanStatusMessage);
    }

    private async Task RetryClearAllStoredSignalsOnceAsync(
        IReadOnlyCollection<CustomCanSignal> enabledSignals,
        IReadOnlyCollection<CustomCanMessage> changedMessages,
        IReadOnlyDictionary<int, byte> originalSignalFlags,
        IReadOnlyDictionary<int, byte> originalMessageFlags)
    {
        if (_portService == null)
        {
            return;
        }

        try
        {
            await Task.Delay(ClearAllCustomCanRetryDelayMs);

            IsCustomCanBusy = true;
            bool sessionStarted = BeginCustomCanConfigSession(out bool previousSuspendLiveRequestPolling);
            bool persisted;
            try
            {
                persisted = await PersistCustomCanEnableStateAsync(enabledSignals, changedMessages);
            }
            finally
            {
                EndCustomCanConfigSession(sessionStarted, previousSuspendLiveRequestPolling);
                IsCustomCanBusy = false;
            }

            if (!persisted)
            {
                RestoreCustomCanFlags(enabledSignals, originalSignalFlags);
                RestoreCustomCanFlags(changedMessages, originalMessageFlags);
                CustomCanStatusMessage = string.IsNullOrWhiteSpace(_portService.LastCustomCanWriteError)
                    ? "Controller rejected clear-all Custom CAN request after one retry."
                    : $"{_portService.LastCustomCanWriteError} (after one retry).";
                AddLog(CustomCanStatusMessage);
                return;
            }

            await CompleteClearAllStoredSignalsAsync();
        }
        catch (Exception ex)
        {
            CustomCanStatusMessage = $"Clear-all retry failed: {ex.Message}";
            AddLog(CustomCanStatusMessage);
        }
    }

    private async Task CompleteClearAllStoredSignalsAsync()
    {
        if (_portService == null)
        {
            return;
        }

        bool postSnapshotReceived = await _portService.RequestCustomCanSnapshotAsync();
        if (postSnapshotReceived)
        {
            ApplyPendingCustomCanSnapshotIfAvailable();
        }

        RefreshCustomCanBindings();

        int remainingSignals = ControllerCustomCanSignals.Count();
        CustomCanStatusMessage = remainingSignals == 0
            ? "Cleared all stored Custom CAN signals."
            : $"Clear-all completed but {remainingSignals} signal(s) remain stored.";
        AddLog(CustomCanStatusMessage);
    }

    private static void RestoreCustomCanFlags(IEnumerable<CustomCanSignal> signals, IReadOnlyDictionary<int, byte> originalFlags)
    {
        foreach (CustomCanSignal signal in signals)
        {
            if (originalFlags.TryGetValue(signal.Index, out byte flags))
            {
                signal.Flags = flags;
            }
        }
    }

    private static void RestoreCustomCanFlags(IEnumerable<CustomCanMessage> messages, IReadOnlyDictionary<int, byte> originalFlags)
    {
        foreach (CustomCanMessage message in messages)
        {
            if (originalFlags.TryGetValue(message.Index, out byte flags))
            {
                message.Flags = flags;
            }
        }
    }

    private bool HasPendingCustomCanChanges()
    {
        DataStructures baselineData = _controllerConfigBaseline ?? LiveDataView;

        int messageCount = Math.Min(SettingsDataView.CustomCanMessagesStaticData.Count, baselineData.CustomCanMessagesStaticData.Count);
        for (int i = 0; i < messageCount; i++)
        {
            CustomCanMessage current = SettingsDataView.CustomCanMessagesStaticData[i];
            CustomCanMessage baseline = baselineData.CustomCanMessagesStaticData[i];

            bool currentIsTx = (current.Flags & 0x04) != 0;
            bool baselineIsTx = (baseline.Flags & 0x04) != 0;
            bool compareTxTrigger = currentIsTx || baselineIsTx;

            if (current.TimeoutMs != baseline.TimeoutMs ||
                current.Flags != baseline.Flags ||
                (compareTxTrigger && current.TxTrigger != baseline.TxTrigger))
            {
                return true;
            }
        }

        int signalCount = Math.Min(SettingsDataView.CustomCanSignalsStaticData.Count, baselineData.CustomCanSignalsStaticData.Count);
        for (int i = 0; i < signalCount; i++)
        {
            CustomCanSignal current = SettingsDataView.CustomCanSignalsStaticData[i];
            CustomCanSignal baseline = baselineData.CustomCanSignalsStaticData[i];

            bool currentOnTxMessage = current.MessageIndex < SettingsDataView.CustomCanMessagesStaticData.Count &&
                                      SettingsDataView.CustomCanMessagesStaticData[current.MessageIndex].IsTx;
            bool baselineOnTxMessage = baseline.MessageIndex < baselineData.CustomCanMessagesStaticData.Count &&
                                       baselineData.CustomCanMessagesStaticData[baseline.MessageIndex].IsTx;

            bool valueSourceChanged = (currentOnTxMessage || baselineOnTxMessage) && current.ValueSource != baseline.ValueSource;
            bool signalFlagsChanged = current.Flags != baseline.Flags;
            if (valueSourceChanged || signalFlagsChanged)
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> PersistPendingCustomCanChangesAsync()
    {
        if (_portService == null || !IsConnected || !CommsEstablished)
        {
            CustomCanStatusMessage = "Connect to controller to save Custom CAN changes.";
            AddLog(CustomCanStatusMessage);
            return false;
        }

        DataStructures baselineData = _controllerConfigBaseline ?? LiveDataView;

        var changedMessageIndexes = new List<int>();
        int messageCount = Math.Min(SettingsDataView.CustomCanMessagesStaticData.Count, baselineData.CustomCanMessagesStaticData.Count);
        for (int i = 0; i < messageCount; i++)
        {
            CustomCanMessage current = SettingsDataView.CustomCanMessagesStaticData[i];
            CustomCanMessage baseline = baselineData.CustomCanMessagesStaticData[i];

            bool currentIsTx = (current.Flags & 0x04) != 0;
            bool baselineIsTx = (baseline.Flags & 0x04) != 0;
            bool compareTxTrigger = currentIsTx || baselineIsTx;

            if (current.TimeoutMs != baseline.TimeoutMs ||
                current.Flags != baseline.Flags ||
                (compareTxTrigger && current.TxTrigger != baseline.TxTrigger))
            {
                changedMessageIndexes.Add(i);
            }
        }

        var changedSignalIndexes = new List<int>();
        int signalCount = Math.Min(SettingsDataView.CustomCanSignalsStaticData.Count, baselineData.CustomCanSignalsStaticData.Count);
        for (int i = 0; i < signalCount; i++)
        {
            CustomCanSignal current = SettingsDataView.CustomCanSignalsStaticData[i];
            CustomCanSignal baseline = baselineData.CustomCanSignalsStaticData[i];

            bool currentOnTxMessage = current.MessageIndex < SettingsDataView.CustomCanMessagesStaticData.Count &&
                                      SettingsDataView.CustomCanMessagesStaticData[current.MessageIndex].IsTx;
            bool baselineOnTxMessage = baseline.MessageIndex < baselineData.CustomCanMessagesStaticData.Count &&
                                       baselineData.CustomCanMessagesStaticData[baseline.MessageIndex].IsTx;

            bool valueSourceChanged = (currentOnTxMessage || baselineOnTxMessage) && current.ValueSource != baseline.ValueSource;
            bool signalFlagsChanged = current.Flags != baseline.Flags;
            if (valueSourceChanged || signalFlagsChanged)
            {
                changedSignalIndexes.Add(i);
            }
        }

        if (changedMessageIndexes.Count == 0 && changedSignalIndexes.Count == 0)
        {
            return true;
        }

        CustomCanStatusMessage = "Saving Custom CAN changes...";
        IsCustomCanBusy = true;
        bool sessionStarted = BeginCustomCanConfigSession(out bool previousSuspendLiveRequestPolling);
        bool snapshotReceived = false;
        try
        {
            // Whole-record writes, ordered so every intermediate table state passes firmware validation:
            // A) messages that will not be enabled TX (disables first), B) signals (disables first),
            // C) messages that end up enabled TX, once their signals already encode cleanly.
            static bool EndsEnabledTx(CustomCanMessage m) => (m.Flags & 0x05) == 0x05;

            bool persisted = true;
            foreach (int messageIndex in changedMessageIndexes
                         .Where(i => !EndsEnabledTx(SettingsDataView.CustomCanMessagesStaticData[i]))
                         .OrderBy(i => SettingsDataView.CustomCanMessagesStaticData[i].IsEnabled ? 1 : 0))
            {
                persisted = await WriteCustomCanMessageRecordAsync(messageIndex, SettingsDataView.CustomCanMessagesStaticData[messageIndex]);
                if (!persisted)
                {
                    break;
                }
            }

            if (persisted)
            {
                foreach (int signalIndex in changedSignalIndexes
                             .OrderBy(i => (SettingsDataView.CustomCanSignalsStaticData[i].Flags & 0x01) != 0 ? 1 : 0))
                {
                    persisted = await WriteCustomCanSignalRecordAsync(signalIndex, SettingsDataView.CustomCanSignalsStaticData[signalIndex]);
                    if (!persisted)
                    {
                        break;
                    }
                }
            }

            if (persisted)
            {
                foreach (int messageIndex in changedMessageIndexes
                             .Where(i => EndsEnabledTx(SettingsDataView.CustomCanMessagesStaticData[i])))
                {
                    persisted = await WriteCustomCanMessageRecordAsync(messageIndex, SettingsDataView.CustomCanMessagesStaticData[messageIndex]);
                    if (!persisted)
                    {
                        break;
                    }
                }
            }

            if (persisted)
            {
                persisted = await _portService.SaveStandaloneChangesAsync();
            }

            if (!persisted)
            {
                CustomCanStatusMessage = string.IsNullOrWhiteSpace(_portService.LastCustomCanWriteError)
                    ? "Controller rejected Custom CAN update."
                    : _portService.LastCustomCanWriteError;
                AddLog(CustomCanStatusMessage);
                return false;
            }

            snapshotReceived = await _portService.RequestCustomCanSnapshotAsync();
        }
        finally
        {
            EndCustomCanConfigSession(sessionStarted, previousSuspendLiveRequestPolling);
            IsCustomCanBusy = false;
        }

        if (snapshotReceived)
        {
            ApplyPendingCustomCanSnapshotIfAvailable();
        }
        else
        {
            _portService.RequestCustomCanSnapshot();
        }

        RefreshCustomCanBindings();
        RefreshIoFlowConnections();
        CustomCanStatusMessage = $"Saved Custom CAN changes ({changedMessageIndexes.Count} message update(s), {changedSignalIndexes.Count} signal update(s)).";
        AddLog(CustomCanStatusMessage);
        return true;
    }

    private bool BeginCustomCanConfigSession(out bool previousSuspendLiveRequestPolling)
    {
        previousSuspendLiveRequestPolling = false;
        if (_portService == null)
        {
            return false;
        }

        previousSuspendLiveRequestPolling = _portService.BeginCustomCanConfigSession();
        return true;
    }

    private void EndCustomCanConfigSession(bool sessionStarted, bool previousSuspendLiveRequestPolling)
    {
        if (!sessionStarted || _portService == null)
        {
            return;
        }

        _portService.EndCustomCanConfigSession(previousSuspendLiveRequestPolling);
    }

    private async Task<bool> SaveSignalToControllerWithRetryAsync(DbcCanSignalDefinition signalDefinition, byte dlc, ushort timeoutMs, CustomCanTxOptions txOptions, bool batched = false, int? targetMessageSlotIndex = null, bool isDbcImport = false)
    {
        bool saved = await SaveSignalToControllerAsync(signalDefinition, dlc, timeoutMs, txOptions, batched, targetMessageSlotIndex, isDbcImport);
        if (saved || _portService == null || !IsConnected || !CommsEstablished)
        {
            return saved;
        }

        string retryReason = string.IsNullOrWhiteSpace(CustomCanStatusMessage)
            ? "Controller rejected Custom CAN update."
            : CustomCanStatusMessage;
        CustomCanStatusMessage = $"{retryReason} Retrying once...";
        AddLog(CustomCanStatusMessage);

        await Task.Delay(SaveCustomCanRetryDelayMs);

        bool snapshotReceived = await _portService.RequestCustomCanSnapshotAsync();
        if (snapshotReceived)
        {
            ApplyPendingCustomCanSnapshotIfAvailable();
        }

        return await SaveSignalToControllerAsync(signalDefinition, dlc, timeoutMs, txOptions, batched, targetMessageSlotIndex, isDbcImport);
    }

    /// <param name="batched">
    /// True when the caller already holds a config session, has applied a fresh snapshot, and will issue a
    /// single SAVECHANGES + snapshot after the whole batch. Skips the per-signal snapshots and EEPROM commit.
    /// </param>
    /// <param name="targetMessageSlotIndex">
    /// Existing message slot to add the signal to (manual entry). Used when it still holds the same CAN ID;
    /// otherwise a matching or free slot is found as usual.
    /// </param>
    /// <param name="isDbcImport">
    /// Match the stored frame by name and CAN identity, preserve its configuration, and reject occupied bits.
    /// </param>
    private async Task<bool> SaveSignalToControllerAsync(DbcCanSignalDefinition signalDefinition, byte dlc, ushort timeoutMs, CustomCanTxOptions txOptions, bool batched = false, int? targetMessageSlotIndex = null, bool isDbcImport = false)
    {
        if (txOptions is { IsTx: true, IsPeriodic: true } && timeoutMs < 1)
        {
            CustomCanStatusMessage = "Periodic TX messages need a transmit period between 1 and 60,000 ms.";
            return false;
        }

        if (timeoutMs > 60000)
        {
            CustomCanStatusMessage = "Message timeout must be between 0 (no timeout) and 60,000 ms.";
            return false;
        }

        byte effectiveDlc = dlc is >= 1 and <= 8
            ? dlc
            : signalDefinition.Dlc is >= 1 and <= 8
                ? signalDefinition.Dlc
                : (byte)8;

        EnsureCustomCanCollectionsInitialized();

        if (!batched && _portService != null && IsConnected && CommsEstablished)
        {
            bool sessionStarted = BeginCustomCanConfigSession(out bool previousSuspendLiveRequestPolling);
            try
            {
                bool snapshotReceived = await _portService.RequestCustomCanSnapshotAsync();
                if (!snapshotReceived)
                {
                    CustomCanStatusMessage = "No Custom CAN snapshot response from controller.";
                    AddLog(CustomCanStatusMessage);
                    return false;
                }

                ApplyPendingCustomCanSnapshotIfAvailable();
            }
            finally
            {
                EndCustomCanConfigSession(sessionStarted, previousSuspendLiveRequestPolling);
            }
        }

        bool isExtended = signalDefinition.IsExtended || signalDefinition.MessageId > 0x7FF;
        if (signalDefinition.MessageId > 0x1FFFFFFF)
        {
            CustomCanStatusMessage = $"CAN ID 0x{signalDefinition.MessageId:X} exceeds the 29-bit extended range.";
            return false;
        }

        bool preserveMessageConfiguration = false;
        int messageSlotIndex;
        if (isDbcImport)
        {
            if (!TryFindDbcMessageSlot(signalDefinition, isExtended, out messageSlotIndex, out preserveMessageConfiguration))
            {
                return false;
            }

            if (preserveMessageConfiguration)
            {
                effectiveDlc = SettingsDataView.CustomCanMessagesStaticData[messageSlotIndex].Dlc;
            }
        }
        else
        {
            messageSlotIndex = IsUsableTargetMessageSlot(targetMessageSlotIndex, signalDefinition.MessageId, isExtended)
                ? targetMessageSlotIndex!.Value
                : txOptions.IsTx
                    ? FindOrAllocateTxMessageSlot(signalDefinition.MessageId, isExtended, effectiveDlc, txOptions)
                    : FindOrAllocateMessageSlot(signalDefinition.MessageId, isExtended, effectiveDlc);
        }
        if (messageSlotIndex < 0)
        {
            CustomCanStatusMessage = "No free Custom CAN message slots are available.";
            return false;
        }

        byte signalFlags = 0x01;
        if (signalDefinition.IsMotorola)
        {
            signalFlags |= 0x02;
        }

        if (signalDefinition.IsSigned)
        {
            signalFlags |= 0x04;
        }

        int matchingSignalSlotIndex = FindMatchingSignalSlot(signalDefinition, messageSlotIndex, signalFlags);
        if (isDbcImport && GetDbcSignalPlacementError(signalDefinition, messageSlotIndex, matchingSignalSlotIndex, effectiveDlc) is string placementError)
        {
            CustomCanStatusMessage = placementError;
            AddLog(CustomCanStatusMessage);
            return false;
        }

        int signalSlotIndex = matchingSignalSlotIndex;
        if (signalSlotIndex < 0)
        {
            signalSlotIndex = FindOrAllocateSignalSlot();
            if (signalSlotIndex < 0)
            {
                CustomCanStatusMessage = "No free Custom CAN signal slots are available.";
                return false;
            }
        }

        byte messageFlags = 0x01;
        if (isExtended)
        {
            messageFlags |= 0x02;
        }

        if (txOptions.IsTx)
        {
            messageFlags |= 0x04;
            if (txOptions.IsPeriodic)
            {
                messageFlags |= 0x08;
            }
        }

        CustomCanMessage message = SettingsDataView.CustomCanMessagesStaticData[messageSlotIndex];
        CustomCanSignal signal = SettingsDataView.CustomCanSignalsStaticData[signalSlotIndex];
        CustomCanMessage originalMessage = CloneCustomCanMessage(message);
        CustomCanSignal originalSignal = CloneCustomCanSignal(signal);

        // Local state is edited up-front for validation; undo it if the controller never accepts it so the
        // UI cannot drift from what is actually stored.
        bool Fail(string? status = null)
        {
            _customCanMetadataUpsertSuppression++;
            try
            {
                CopyCustomCanMessage(originalMessage, message);
                CopyCustomCanSignal(originalSignal, signal);
            }
            finally
            {
                _customCanMetadataUpsertSuppression--;
            }

            if (status != null)
            {
                CustomCanStatusMessage = status;
            }

            if (!string.IsNullOrWhiteSpace(CustomCanStatusMessage))
            {
                AddLog(CustomCanStatusMessage);
            }

            return false;
        }

        if (!preserveMessageConfiguration)
        {
            message.Id = signalDefinition.MessageId;
            message.TimeoutMs = timeoutMs;
            message.Dlc = effectiveDlc;
            message.Flags = messageFlags;
            message.TxTrigger = txOptions.TxTrigger;
        }

        _customCanMetadataUpsertSuppression++;
        try
        {
            signal.Index = signalSlotIndex;
            signal.Name = signalDefinition.Name;
            signal.MessageName = signalDefinition.MessageName;
            signal.MessageIndex = (byte)messageSlotIndex;
            signal.StartBit = signalDefinition.StartBit;
            signal.BitLength = signalDefinition.BitLength;
            signal.Flags = signalFlags;
            signal.ValueSource = preserveMessageConfiguration && matchingSignalSlotIndex >= 0
                ? originalSignal.ValueSource
                : txOptions.IsTx ? txOptions.ValueSource : Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT;
            signal.Factor = signalDefinition.Factor;
            signal.Offset = signalDefinition.Offset;
            signal.MinValue = signalDefinition.MinValue;
            signal.MaxValue = signalDefinition.MaxValue;
            signal.Units = signalDefinition.Units;
            signal.MessageIdHex = $"0x{signalDefinition.MessageId:X}";
            signal.IsMarkedForDeletion = false;
        }
        finally
        {
            _customCanMetadataUpsertSuppression--;
        }

        if (_portService != null && IsConnected && CommsEstablished)
        {
            bool sessionStarted = BeginCustomCanConfigSession(out bool previousSuspendLiveRequestPolling);
            try
            {
                CustomCanStatusMessage = string.Empty;
                bool repaired = await RepairInvalidCustomCanConfigurationAsync(messageSlotIndex, message, signalSlotIndex, signal);
                if (!repaired)
                {
                    return Fail(string.IsNullOrWhiteSpace(CustomCanStatusMessage)
                        ? string.IsNullOrWhiteSpace(_portService.LastCustomCanWriteError)
                            ? "Controller rejected Custom CAN update while repairing invalid existing slots."
                            : _portService.LastCustomCanWriteError
                        : null);
                }
            }
            finally
            {
                EndCustomCanConfigSession(sessionStarted, previousSuspendLiveRequestPolling);
            }
        }

        if (!TryValidateCustomCanConfiguration(messageSlotIndex, message, signalSlotIndex, signal, out string validationError))
        {
            return Fail(validationError);
        }

        if (_portService != null && IsConnected && CommsEstablished)
        {
            bool sessionStarted = BeginCustomCanConfigSession(out bool previousSuspendLiveRequestPolling);
            IsCustomCanBusy = true;
            try
            {
                CustomCanStatusMessage = string.Empty;
                bool saved = await SaveCustomCanToControllerAsync(messageSlotIndex, signalSlotIndex, message, signal, originalMessage, commit: !batched);
                if (!saved)
                {
                    return Fail(string.IsNullOrWhiteSpace(_portService.LastCustomCanWriteError)
                        ? "Controller rejected Custom CAN update."
                        : _portService.LastCustomCanWriteError);
                }

                if (!batched)
                {
                    bool postSnapshotReceived = await _portService.RequestCustomCanSnapshotAsync();
                    if (postSnapshotReceived)
                    {
                        ApplyPendingCustomCanSnapshotIfAvailable();
                    }
                    else
                    {
                        _portService.RequestCustomCanSnapshot();
                        StartBackgroundTask(RequestCustomCanSnapshotRetryAsync(), "Custom CAN snapshot retry");
                    }
                }
            }
            finally
            {
                EndCustomCanConfigSession(sessionStarted, previousSuspendLiveRequestPolling);
                if (!batched)
                {
                    IsCustomCanBusy = false;
                }
            }
        }

        UpsertSignalMetadata(signal);

        if (signalSlotIndex < LiveDataView.CustomCanSignalsLiveData.Count)
        {
            CustomCanSignal liveSignal = LiveDataView.CustomCanSignalsLiveData[signalSlotIndex];
            liveSignal.Name = signal.Name;
            liveSignal.MessageName = signal.MessageName;
            liveSignal.MessageIndex = signal.MessageIndex;
            liveSignal.StartBit = signal.StartBit;
            liveSignal.BitLength = signal.BitLength;
            liveSignal.Flags = signal.Flags;
            liveSignal.ValueSource = signal.ValueSource;
            liveSignal.Factor = signal.Factor;
            liveSignal.Offset = signal.Offset;
            liveSignal.MinValue = signal.MinValue;
            liveSignal.MaxValue = signal.MaxValue;
            liveSignal.Units = signal.Units;
        }

        RaiseMathRuleUiPropertiesChanged(includeRuleSelectorOptions: false, includeOperandASelectorOptions: true, includeOperandBSelectorOptions: true);
        RefreshIoFlowConnections();
        RefreshCustomCanBindings();
        SelectedControllerCustomCanMessage = message;
        SelectedControllerCustomCanSignal = signal;

        CustomCanStatusMessage = $"Saved Custom CAN signal to slot {signalSlotIndex + 1} (ID 0x{signalDefinition.MessageId:X}).";
        AddLog(CustomCanStatusMessage);
        return true;
    }

    private static CustomCanMessage CloneCustomCanMessage(CustomCanMessage source)
    {
        var copy = new CustomCanMessage();
        CopyCustomCanMessage(source, copy);
        return copy;
    }

    private static void CopyCustomCanMessage(CustomCanMessage source, CustomCanMessage target)
    {
        target.Id = source.Id;
        target.TimeoutMs = source.TimeoutMs;
        target.Dlc = source.Dlc;
        target.Flags = source.Flags;
        target.TxTrigger = source.TxTrigger;
    }

    private static CustomCanSignal CloneCustomCanSignal(CustomCanSignal source)
    {
        var copy = new CustomCanSignal();
        CopyCustomCanSignal(source, copy);
        return copy;
    }

    private static void CopyCustomCanSignal(CustomCanSignal source, CustomCanSignal target)
    {
        target.Index = source.Index;
        target.Name = source.Name;
        target.MessageName = source.MessageName;
        target.MessageIndex = source.MessageIndex;
        target.StartBit = source.StartBit;
        target.BitLength = source.BitLength;
        target.Flags = source.Flags;
        target.ValueSource = source.ValueSource;
        target.Factor = source.Factor;
        target.Offset = source.Offset;
        target.MinValue = source.MinValue;
        target.MaxValue = source.MaxValue;
        target.Units = source.Units;
        target.MessageIdHex = source.MessageIdHex;
        target.IsMarkedForDeletion = source.IsMarkedForDeletion;
    }

    private async Task<bool> RepairInvalidCustomCanConfigurationAsync(int targetMessageIndex, CustomCanMessage targetMessage, int targetSignalIndex, CustomCanSignal targetSignal)
    {
        if (_portService == null)
        {
            return false;
        }

        bool IsMessageExtended(CustomCanMessage message) => (message.Flags & 0x02) != 0;
        CustomCanMessage GetMessage(int index) => index == targetMessageIndex
            ? targetMessage
            : SettingsDataView.CustomCanMessagesStaticData[index];
        CustomCanSignal GetSignal(int index) => index == targetSignalIndex
            ? targetSignal
            : SettingsDataView.CustomCanSignalsStaticData[index];

        for (int i = 0; i < SettingsDataView.CustomCanMessagesStaticData.Count; i++)
        {
            CustomCanMessage message = GetMessage(i);
            byte repairedFlags = (byte)(message.Flags & 0x0F);
            if ((repairedFlags & 0x08) != 0 && (repairedFlags & 0x04) == 0)
            {
                // Periodic flag requires TX.
                repairedFlags &= unchecked((byte)~0x08);
            }

            bool repairedIsTx = (repairedFlags & 0x04) != 0;
            bool repairedIsPeriodic = (repairedFlags & 0x08) != 0;
            byte repairedDlc = repairedIsTx
                ? Math.Min(message.Dlc, (byte)8)
                : Math.Clamp(message.Dlc, (byte)1, (byte)8);
            ushort repairedTimeout = Math.Min(message.TimeoutMs, (ushort)60000);
            if (repairedIsPeriodic && repairedTimeout < 1)
            {
                repairedTimeout = 1;
            }

            byte repairedTrigger = message.TxTrigger == Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS ||
                                   message.TxTrigger < Constants.NUM_OUTPUT_CHANNELS
                ? message.TxTrigger
                : Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS;
            uint maxId = IsMessageExtended(message) ? 0x1FFFFFFFu : 0x7FFu;
            uint repairedId = Math.Min(message.Id, maxId);

            bool changed = repairedFlags != message.Flags ||
                           repairedDlc != message.Dlc ||
                           repairedTimeout != message.TimeoutMs ||
                           repairedTrigger != message.TxTrigger ||
                           repairedId != message.Id;

            if (!changed)
            {
                continue;
            }

            if (i != targetMessageIndex)
            {
                var repairedMessage = new CustomCanMessage
                {
                    Id = repairedId,
                    TimeoutMs = repairedTimeout,
                    Dlc = repairedDlc,
                    Flags = repairedFlags,
                    TxTrigger = repairedTrigger
                };
                if (!await WriteCustomCanMessageRecordAsync(i, repairedMessage))
                {
                    return false;
                }
            }

            message.Flags = repairedFlags;
            message.Dlc = repairedDlc;
            message.TimeoutMs = repairedTimeout;
            message.TxTrigger = repairedTrigger;
            message.Id = repairedId;
        }

        for (int i = 0; i < SettingsDataView.CustomCanSignalsStaticData.Count; i++)
        {
            CustomCanSignal signal = GetSignal(i);
            int repairedMessageIndex = Math.Clamp(signal.MessageIndex, 0, SettingsDataView.CustomCanMessagesStaticData.Count - 1);
            byte repairedFlags = (byte)(signal.Flags & 0x07);
            byte repairedStartBit = signal.StartBit;
            byte repairedBitLength = signal.BitLength;
            float repairedFactor = float.IsFinite(signal.Factor) ? signal.Factor : 1.0f;
            float repairedOffset = float.IsFinite(signal.Offset) ? signal.Offset : 0.0f;
            byte repairedValueSource = signal.ValueSource == Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT ||
                                       signal.ValueSource < Constants.NUM_OUTPUT_CHANNELS
                ? signal.ValueSource
                : Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT;

            CustomCanMessage parentMessage = GetMessage(repairedMessageIndex);
            byte messageDlc = parentMessage.Dlc;
            if (messageDlc == 0)
            {
                // Firmware validates every signal's bit range against its message DLC, and a
                // data-less TX frame cannot host any signal. Repoint the signal at a message
                // that can carry data.
                for (int j = 0; j < SettingsDataView.CustomCanMessagesStaticData.Count; j++)
                {
                    if (GetMessage(j).Dlc is >= 1 and <= 8)
                    {
                        repairedMessageIndex = j;
                        parentMessage = GetMessage(j);
                        messageDlc = parentMessage.Dlc;
                        break;
                    }
                }
            }

            if (messageDlc is < 1 or > 8)
            {
                messageDlc = 8;
            }

            if (!IsCustomCanSignalBitRangeValid(repairedStartBit, repairedBitLength, repairedFlags, messageDlc))
            {
                repairedStartBit = 0;
                repairedBitLength = 1;
                repairedFlags &= unchecked((byte)~0x02);
            }

            // Enabled signals on TX messages need a usable encode unless the source is constant.
            bool onTxMessage = (parentMessage.Flags & 0x04) != 0;
            if (onTxMessage && (repairedFlags & 0x01) != 0 &&
                repairedValueSource != Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT && repairedFactor == 0.0f)
            {
                repairedFactor = 1.0f;
            }

            bool changed = repairedMessageIndex != signal.MessageIndex ||
                           repairedFlags != signal.Flags ||
                           repairedStartBit != signal.StartBit ||
                           repairedBitLength != signal.BitLength ||
                           repairedFactor != signal.Factor ||
                           repairedOffset != signal.Offset ||
                           repairedValueSource != signal.ValueSource;

            if (!changed)
            {
                continue;
            }

            if (i != targetSignalIndex)
            {
                var repairedSignal = new CustomCanSignal
                {
                    MessageIndex = (byte)repairedMessageIndex,
                    StartBit = repairedStartBit,
                    BitLength = repairedBitLength,
                    Flags = repairedFlags,
                    Factor = repairedFactor,
                    Offset = repairedOffset,
                    ValueSource = repairedValueSource
                };
                if (!await WriteCustomCanSignalRecordAsync(i, repairedSignal))
                {
                    return false;
                }
            }

            signal.MessageIndex = (byte)repairedMessageIndex;
            signal.Flags = repairedFlags;
            signal.StartBit = repairedStartBit;
            signal.BitLength = repairedBitLength;
            signal.Factor = repairedFactor;
            signal.Offset = repairedOffset;
            signal.ValueSource = repairedValueSource;
        }

        return true;
    }

    private static bool IsCustomCanSignalBitRangeValid(byte startBit, byte bitLength, byte flags, byte dlc)
    {
        if (startBit >= 64 || bitLength == 0 || bitLength > 64 || dlc == 0 || dlc > 8)
        {
            return false;
        }

        int bit = startBit;
        int maxBits = dlc * 8;
        bool motorola = (flags & 0x02) != 0;
        for (int i = 0; i < bitLength; i++)
        {
            if (bit < 0 || bit >= maxBits)
            {
                return false;
            }

            bit = motorola
                ? ((bit % 8 == 0) ? bit + 15 : bit - 1)
                : bit + 1;
        }

        return true;
    }

    private bool TryValidateCustomCanConfiguration(int targetMessageIndex, CustomCanMessage targetMessage, int targetSignalIndex, CustomCanSignal targetSignal, out string error)
    {
        error = string.Empty;

        bool IsEnabled(CustomCanMessage message) => (message.Flags & 0x01) != 0;
        bool IsExtended(CustomCanMessage message) => (message.Flags & 0x02) != 0;

        CustomCanMessage GetMessage(int index) => index == targetMessageIndex
            ? targetMessage
            : SettingsDataView.CustomCanMessagesStaticData[index];

        CustomCanSignal GetSignal(int index) => index == targetSignalIndex
            ? targetSignal
            : SettingsDataView.CustomCanSignalsStaticData[index];

        for (int i = 0; i < SettingsDataView.CustomCanMessagesStaticData.Count; i++)
        {
            CustomCanMessage message = GetMessage(i);
            if ((message.Flags & ~0x0F) != 0)
            {
                error = $"Custom CAN message slot {i + 1} has invalid flags ({message.Flags}).";
                return false;
            }

            bool isTx = (message.Flags & 0x04) != 0;
            bool isPeriodic = (message.Flags & 0x08) != 0;
            if (isPeriodic && !isTx)
            {
                error = $"Custom CAN message slot {i + 1} has the periodic flag without the TX flag.";
                return false;
            }

            uint maxId = IsExtended(message) ? 0x1FFFFFFFu : 0x7FFu;
            if (message.Id > maxId)
            {
                error = IsExtended(message)
                    ? $"Custom CAN message slot {i + 1} has an invalid extended ID ({message.Id})."
                    : $"Custom CAN message slot {i + 1} has an invalid standard ID ({message.Id}).";
                return false;
            }

            if (message.TimeoutMs > 60000)
            {
                error = $"Custom CAN message slot {i + 1} has an invalid timeout ({message.TimeoutMs} ms).";
                return false;
            }

            if (isPeriodic && message.TimeoutMs < 1)
            {
                error = $"Custom CAN message slot {i + 1} is periodic TX but has no transmit period.";
                return false;
            }

            if (message.Dlc > 8 || (!isTx && message.Dlc < 1))
            {
                error = $"Custom CAN message slot {i + 1} has an invalid DLC ({message.Dlc}).";
                return false;
            }

            if (message.TxTrigger != Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS &&
                message.TxTrigger >= Constants.NUM_OUTPUT_CHANNELS)
            {
                error = $"Custom CAN message slot {i + 1} has an invalid TX trigger ({message.TxTrigger}).";
                return false;
            }
        }

        for (int i = 0; i < SettingsDataView.CustomCanMessagesStaticData.Count; i++)
        {
            CustomCanMessage message = GetMessage(i);
            if (!IsEnabled(message) || (message.Flags & 0x04) != 0)
            {
                // TX definitions may legitimately share an ID, e.g. on/off command frames.
                continue;
            }

            for (int j = 0; j < i; j++)
            {
                CustomCanMessage other = GetMessage(j);
                if (!IsEnabled(other) || (other.Flags & 0x04) != 0)
                {
                    continue;
                }

                bool sameExtendedMode = IsExtended(message) == IsExtended(other);
                if (sameExtendedMode && message.Id == other.Id)
                {
                    error = $"Duplicate enabled Custom CAN IDs detected at message slots {j + 1} and {i + 1} (ID {message.Id}).";
                    return false;
                }
            }
        }

        for (int i = 0; i < SettingsDataView.CustomCanSignalsStaticData.Count; i++)
        {
            CustomCanSignal signal = GetSignal(i);
            if ((signal.Flags & ~0x07) != 0)
            {
                error = $"Custom CAN signal slot {i + 1} has invalid flags ({signal.Flags}).";
                return false;
            }

            if (signal.MessageIndex >= SettingsDataView.CustomCanMessagesStaticData.Count)
            {
                error = $"Custom CAN signal slot {i + 1} references invalid message slot {signal.MessageIndex + 1}.";
                return false;
            }

            if (signal.StartBit >= 64 || signal.BitLength is 0 or > 64)
            {
                error = $"Custom CAN signal slot {i + 1} has invalid bit range (start {signal.StartBit}, length {signal.BitLength}).";
                return false;
            }

            if (!float.IsFinite(signal.Factor) || !float.IsFinite(signal.Offset))
            {
                error = $"Custom CAN signal slot {i + 1} has non-finite factor/offset values.";
                return false;
            }

            if (signal.ValueSource != Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT &&
                signal.ValueSource >= Constants.NUM_OUTPUT_CHANNELS)
            {
                error = $"Custom CAN signal slot {i + 1} has an invalid value source ({signal.ValueSource}).";
                return false;
            }

            CustomCanMessage message = GetMessage(signal.MessageIndex);
            if ((message.Flags & 0x04) != 0 && (signal.Flags & 0x01) != 0 &&
                signal.ValueSource != Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT && signal.Factor == 0.0f)
            {
                error = $"Custom CAN signal slot {i + 1} is on a TX message and needs a non-zero factor (or a constant value source).";
                return false;
            }

            // Firmware only range-checks enabled signals; disabled (deleted) slots keep stale layouts.
            if ((signal.Flags & 0x01) == 0)
            {
                continue;
            }

            int bit = signal.StartBit;
            int maxBits = message.Dlc * 8;
            bool isMotorola = (signal.Flags & 0x02) != 0;
            for (int bitIndex = 0; bitIndex < signal.BitLength; bitIndex++)
            {
                if (bit < 0 || bit >= maxBits)
                {
                    error = $"Custom CAN signal slot {i + 1} exceeds message DLC bounds (message slot {signal.MessageIndex + 1}, DLC {message.Dlc}).";
                    return false;
                }

                bit = isMotorola
                    ? ((bit % 8 == 0) ? bit + 15 : bit - 1)
                    : bit + 1;
            }
        }

        return true;
    }

    private Task<bool> WriteCustomCanMessageRecordAsync(int messageIndex, CustomCanMessage message) =>
        _portService!.WriteCustomCanMessageRecordAsync(messageIndex, message.Id, message.TimeoutMs, message.Dlc, message.Flags, message.TxTrigger);

    private Task<bool> WriteCustomCanSignalRecordAsync(int signalIndex, CustomCanSignal signal) =>
        _portService!.WriteCustomCanSignalRecordAsync(signalIndex, signal.MessageIndex, signal.StartBit, signal.BitLength, signal.Flags, signal.Factor, signal.Offset, signal.ValueSource);

    /// <summary>
    /// Writes one message + signal pair as two atomic records. The message goes first so the signal is
    /// validated against its final parent. When <paramref name="commit"/> is false the caller is batching
    /// and issues a single SAVECHANGES afterwards.
    /// </summary>
    private async Task<bool> SaveCustomCanToControllerAsync(int messageSlotIndex, int signalSlotIndex, CustomCanMessage message, CustomCanSignal signal, CustomCanMessage originalMessage, bool commit)
    {
        if (_portService == null)
        {
            return false;
        }

        if (!await WriteCustomCanMessageRecordAsync(messageSlotIndex, message))
        {
            return false;
        }

        if (!await WriteCustomCanSignalRecordAsync(signalSlotIndex, signal))
        {
            // Put the message slot back so the controller is not left with an orphaned/changed message.
            string? signalError = _portService.LastCustomCanWriteError;
            _ = await WriteCustomCanMessageRecordAsync(messageSlotIndex, originalMessage);
            _portService.LastCustomCanWriteError = signalError;
            return false;
        }

        return !commit || await _portService.SaveStandaloneChangesAsync();
    }

    private bool TryFindDbcMessageSlot(DbcCanSignalDefinition definition, bool isExtended, out int slotIndex, out bool reuse)
    {
        slotIndex = -1;
        reuse = false;
        HashSet<int> occupiedSlots = SettingsDataView.CustomCanSignalsStaticData
            .Where(signal => signal.IsEnabled)
            .Select(signal => (int)signal.MessageIndex)
            .ToHashSet();
        List<int> sameIdSlots = Enumerable.Range(0, SettingsDataView.CustomCanMessagesStaticData.Count)
            .Where(index =>
            {
                CustomCanMessage message = SettingsDataView.CustomCanMessagesStaticData[index];
                return (message.IsEnabled || occupiedSlots.Contains(index)) &&
                    message.Id == definition.MessageId && message.IsExtended == isExtended;
            })
            .ToList();

        List<int> matchingSlots = sameIdSlots
            .Where(index => string.Equals(SettingsDataView.CustomCanMessagesStaticData[index].Name.Trim(),
                definition.MessageName.Trim(), StringComparison.Ordinal))
            .ToList();
        if (matchingSlots.Count == 0)
        {
            matchingSlots = sameIdSlots
                .Where(index => IsPlaceholderMessageName(SettingsDataView.CustomCanMessagesStaticData[index].Name))
                .ToList();
        }

        if (matchingSlots.Count == 1)
        {
            slotIndex = matchingSlots[0];
            reuse = true;
            return true;
        }

        if (sameIdSlots.Count > 0)
        {
            CustomCanStatusMessage = matchingSlots.Count > 1
                ? $"Cannot import {definition.DisplayLabel}: multiple stored messages match 0x{definition.MessageId:X} and the message name."
                : $"Cannot import {definition.DisplayLabel}: 0x{definition.MessageId:X} is stored with a different message name.";
            AddLog(CustomCanStatusMessage);
            return false;
        }

        slotIndex = Enumerable.Range(0, SettingsDataView.CustomCanMessagesStaticData.Count)
            .FirstOrDefault(index => !SettingsDataView.CustomCanMessagesStaticData[index].IsEnabled &&
                !occupiedSlots.Contains(index), -1);
        if (slotIndex >= 0)
        {
            return true;
        }

        CustomCanStatusMessage = "No free Custom CAN message slots are available.";
        AddLog(CustomCanStatusMessage);
        return false;
    }

    private string? GetDbcSignalPlacementError(DbcCanSignalDefinition definition, int messageSlotIndex, int matchingSignalSlotIndex, byte dlc)
    {
        IReadOnlyList<int> bits = CustomCanBitLayout.GetOccupiedBits(definition.StartBit, definition.BitLength, definition.IsMotorola);
        if (dlc is < 1 or > 8 || definition.BitLength is < 1 or > 64 ||
            bits.Count != definition.BitLength || bits.Any(bit => bit >= dlc * 8))
        {
            return $"Cannot import {definition.DisplayLabel}: its bit layout does not fit message 0x{definition.MessageId:X} (DLC {dlc}).";
        }

        HashSet<int> occupiedBits = bits.ToHashSet();
        for (int index = 0; index < SettingsDataView.CustomCanSignalsStaticData.Count; index++)
        {
            CustomCanSignal signal = SettingsDataView.CustomCanSignalsStaticData[index];
            if (index == matchingSignalSlotIndex || !signal.IsEnabled || signal.MessageIndex != messageSlotIndex)
            {
                continue;
            }

            if (CustomCanBitLayout.GetOccupiedBits(signal.StartBit, signal.BitLength, signal.IsMotorola).Any(occupiedBits.Contains))
            {
                return $"Cannot import {definition.DisplayLabel}: it overlaps stored signal {signal.Name} in message 0x{definition.MessageId:X}.";
            }
        }

        return null;
    }

    private int FindOrAllocateMessageSlot(uint messageId, bool isExtended, byte dlc)
    {
        for (int i = 0; i < SettingsDataView.CustomCanMessagesStaticData.Count; i++)
        {
            CustomCanMessage message = SettingsDataView.CustomCanMessagesStaticData[i];
            if (message.IsEnabled && !message.IsTx && message.Id == messageId &&
                message.IsExtended == isExtended && message.Dlc == dlc)
            {
                return i;
            }
        }

        for (int i = 0; i < SettingsDataView.CustomCanMessagesStaticData.Count; i++)
        {
            if (!SettingsDataView.CustomCanMessagesStaticData[i].IsEnabled)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindOrAllocateTxMessageSlot(uint messageId, bool isExtended, byte dlc, CustomCanTxOptions txOptions)
    {
        // Reuse an existing TX definition with the same identity so multi-signal
        // transmit frames (e.g. keypad LED messages) collect into one message slot.
        for (int i = 0; i < SettingsDataView.CustomCanMessagesStaticData.Count; i++)
        {
            CustomCanMessage message = SettingsDataView.CustomCanMessagesStaticData[i];
            if (message.IsEnabled && message.IsTx && message.Id == messageId &&
                message.IsExtended == isExtended && message.Dlc == dlc &&
                message.IsPeriodic == txOptions.IsPeriodic && message.TxTrigger == txOptions.TxTrigger)
            {
                return i;
            }
        }

        for (int i = 0; i < SettingsDataView.CustomCanMessagesStaticData.Count; i++)
        {
            if (!SettingsDataView.CustomCanMessagesStaticData[i].IsEnabled)
            {
                return i;
            }
        }

        return -1;
    }

    private bool IsUsableTargetMessageSlot(int? slotIndex, uint messageId, bool isExtended)
    {
        if (slotIndex is not int index || index < 0 || index >= SettingsDataView.CustomCanMessagesStaticData.Count)
        {
            return false;
        }

        CustomCanMessage message = SettingsDataView.CustomCanMessagesStaticData[index];
        return message.IsEnabled && message.Id == messageId && message.IsExtended == isExtended;
    }

    private int FindOrAllocateSignalSlot()
    {
        for (int i = 0; i < SettingsDataView.CustomCanSignalsStaticData.Count; i++)
        {
            if (!SettingsDataView.CustomCanSignalsStaticData[i].IsEnabled)
            {
                return i;
            }
        }

        return -1;
    }

    private int FindMatchingSignalSlot(DbcCanSignalDefinition signalDefinition, int messageSlotIndex, byte signalFlags)
    {
        for (int i = 0; i < SettingsDataView.CustomCanSignalsStaticData.Count; i++)
        {
            CustomCanSignal signal = SettingsDataView.CustomCanSignalsStaticData[i];
            if (!signal.IsEnabled)
            {
                continue;
            }

            if (signal.MessageIndex != messageSlotIndex ||
                signal.StartBit != signalDefinition.StartBit ||
                signal.BitLength != signalDefinition.BitLength ||
                signal.Flags != signalFlags)
            {
                continue;
            }

            if (!string.Equals(signal.Name, signalDefinition.Name, StringComparison.Ordinal) ||
                !string.Equals(signal.MessageName, signalDefinition.MessageName, StringComparison.Ordinal) ||
                signal.Factor != signalDefinition.Factor ||
                signal.Offset != signalDefinition.Offset ||
                signal.MinValue != signalDefinition.MinValue ||
                signal.MaxValue != signalDefinition.MaxValue ||
                !string.Equals(signal.Units, signalDefinition.Units, StringComparison.Ordinal))
            {
                continue;
            }

            return i;
        }

        return -1;
    }

    private string ResolveMessageIdHex(int messageIndex)
    {
        if (messageIndex < 0 || messageIndex >= SettingsDataView.CustomCanMessagesStaticData.Count)
        {
            return string.Empty;
        }

        CustomCanMessage message = SettingsDataView.CustomCanMessagesStaticData[messageIndex];
        return $"0x{message.Id:X}";
    }

    private void ApplyPendingCustomCanSnapshotIfAvailable()
    {
        DataStructures? pendingData = null;
        lock (_pendingDataLock)
        {
            if (_hasPendingData && _pendingLiveData != null)
            {
                pendingData = _pendingLiveData;
                _hasPendingData = false;
            }
        }

        if (pendingData != null)
        {
            UpdateLiveDataInPlace(pendingData);
        }
    }

    private void UpdateCustomCanStatusPollingSelection()
    {
        bool canPoll = _portService != null && IsConnected && CommsEstablished;
        _portService?.SetIoFlowStatusPollingEnabled(_isIoFlowTabVisible && canPoll);
        bool shouldPoll = _isCanMessagesTabVisible && canPoll;
        _portService?.SetCustomCanStatusPollingEnabled(shouldPoll);

        if (shouldPoll)
        {
            _portService?.RequestCustomCanSnapshot();
            StartBackgroundTask(RequestCustomCanSnapshotRetryAsync(), "Custom CAN snapshot retry");
        }
    }

    private async Task RequestCustomCanSnapshotRetryAsync()
    {
        await Task.Delay(300);

        if (_isCanMessagesTabVisible && _portService != null && IsConnected && CommsEstablished)
        {
            _portService.RequestCustomCanSnapshot();
        }
    }

    private bool CanEditStoredCustomCanSignals() =>
        _portService != null && IsConnected && CommsEstablished;

    private bool HasEnabledSignalForMessage(int messageIndex, int ignoreSignalIndex)
    {
        return SettingsDataView.CustomCanSignalsStaticData.Any(signal =>
            signal.Index != ignoreSignalIndex &&
            signal.IsEnabled &&
            signal.MessageIndex == messageIndex);
    }

    private List<CustomCanMessage> DisableOrphanedMessagesForSignals(IEnumerable<CustomCanSignal> changedSignals)
    {
        var changedMessages = new List<CustomCanMessage>();
        HashSet<int> impactedMessageIndexes = changedSignals
            .Select(signal => (int)signal.MessageIndex)
            .ToHashSet();

        foreach (int messageIndex in impactedMessageIndexes)
        {
            if (messageIndex < 0 || messageIndex >= SettingsDataView.CustomCanMessagesStaticData.Count)
            {
                continue;
            }

            CustomCanMessage message = SettingsDataView.CustomCanMessagesStaticData[messageIndex];
            bool anyEnabled = SettingsDataView.CustomCanSignalsStaticData.Any(signal => signal.IsEnabled && signal.MessageIndex == messageIndex);
            if (!anyEnabled)
            {
                RemoveStoredCanMessageAssignment(message);
                changedMessages.Add(message);
            }
        }

        return changedMessages;
    }

    private async Task<bool> PersistCustomCanEnableStateAsync(IEnumerable<CustomCanSignal> signals, IEnumerable<CustomCanMessage> messages)
    {
        if (_portService == null)
        {
            return false;
        }

        HashSet<int> writtenSignalSlots = [];
        foreach (CustomCanSignal signal in signals)
        {
            int slotIndex = signal.Index;
            bool indexMapsToSignal = slotIndex >= 0 &&
                                     slotIndex < SettingsDataView.CustomCanSignalsStaticData.Count &&
                                     ReferenceEquals(SettingsDataView.CustomCanSignalsStaticData[slotIndex], signal);
            if (!indexMapsToSignal)
            {
                slotIndex = SettingsDataView.CustomCanSignalsStaticData.IndexOf(signal);
            }

            if (slotIndex < 0 || !writtenSignalSlots.Add(slotIndex))
            {
                continue;
            }

            if (!await _portService.WriteCustomCanSignalParameterAsync(slotIndex, 3, signal.Flags))
            {
                return false;
            }
        }

        HashSet<int> writtenMessageSlots = [];
        foreach (CustomCanMessage message in messages)
        {
            int slotIndex = message.Index;
            bool indexMapsToMessage = slotIndex >= 0 &&
                                      slotIndex < SettingsDataView.CustomCanMessagesStaticData.Count &&
                                      ReferenceEquals(SettingsDataView.CustomCanMessagesStaticData[slotIndex], message);
            if (!indexMapsToMessage)
            {
                slotIndex = SettingsDataView.CustomCanMessagesStaticData.IndexOf(message);
            }

            if (slotIndex < 0 || !writtenMessageSlots.Add(slotIndex))
            {
                continue;
            }

            if (!await _portService.WriteCustomCanMessageParameterAsync(slotIndex, 3, message.Flags))
            {
                return false;
            }
        }

        // One EEPROM commit for the whole batch instead of one per slot.
        return writtenSignalSlots.Count + writtenMessageSlots.Count == 0 ||
               await _portService.SaveStandaloneChangesAsync();
    }

    private static (float minValue, float maxValue) ComputeSignalRange(byte bitLength, bool isSigned, float factor, float offset)
    {
        int clampedLength = Math.Clamp(bitLength, (byte)1, (byte)64);
        double rawMin;
        double rawMax;

        if (isSigned)
        {
            if (clampedLength == 64)
            {
                rawMin = long.MinValue;
                rawMax = long.MaxValue;
            }
            else
            {
                double signedMagnitude = Math.Pow(2.0, clampedLength - 1);
                rawMin = -signedMagnitude;
                rawMax = signedMagnitude - 1.0;
            }
        }
        else
        {
            if (clampedLength == 64)
            {
                rawMin = 0.0;
                rawMax = ulong.MaxValue;
            }
            else
            {
                rawMin = 0.0;
                rawMax = Math.Pow(2.0, clampedLength) - 1.0;
            }
        }

        double scaledA = (rawMin * factor) + offset;
        double scaledB = (rawMax * factor) + offset;
        float minValue = (float)Math.Min(scaledA, scaledB);
        float maxValue = (float)Math.Max(scaledA, scaledB);
        return (minValue, maxValue);
    }

    private static void ParseDbc(string dbcContent, out List<DbcCanMessageDefinition> messages, out List<DbcCanSignalDefinition> signals)
    {
        messages = [];
        signals = [];

        DbcCanMessageDefinition? currentMessage = null;
        Dictionary<(uint Id, bool IsExtended), DbcCanMessageDefinition> messageById = [];

        foreach (string rawLine in dbcContent.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            Match messageMatch = DbcMessageRegex.Match(line);
            if (messageMatch.Success)
            {
                if (!uint.TryParse(messageMatch.Groups["id"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint rawId))
                {
                    currentMessage = null;
                    continue;
                }

                // DBC marks 29-bit frames by setting bit 31 of the BO_ ID.
                uint id = rawId & 0x1FFFFFFFu;
                bool isExtended = (rawId & 0x80000000u) != 0 || id > 0x7FFu;
                if (!byte.TryParse(messageMatch.Groups["dlc"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte dlc))
                {
                    dlc = 8;
                }

                currentMessage = new DbcCanMessageDefinition
                {
                    Id = id,
                    IsExtended = isExtended,
                    Dlc = dlc is < 1 or > 8 ? (byte)8 : dlc,
                    Name = messageMatch.Groups["name"].Value,
                };

                messageById[(id, isExtended)] = currentMessage;
                continue;
            }

            Match signalMatch = DbcSignalRegex.Match(line);
            if (!signalMatch.Success || currentMessage == null)
            {
                continue;
            }

            if (!byte.TryParse(signalMatch.Groups["start"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte startBit))
            {
                continue;
            }

            if (!byte.TryParse(signalMatch.Groups["len"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out byte bitLength))
            {
                continue;
            }

            if (!float.TryParse(signalMatch.Groups["factor"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float factor))
            {
                factor = 1.0f;
            }

            if (!float.TryParse(signalMatch.Groups["offset"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float offset))
            {
                offset = 0.0f;
            }

            bool isSigned = signalMatch.Groups["sign"].Value == "-";
            float? minValue = null;
            float? maxValue = null;

            if (signalMatch.Groups["min"].Success &&
                float.TryParse(signalMatch.Groups["min"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedMin))
            {
                minValue = parsedMin;
            }

            if (signalMatch.Groups["max"].Success &&
                float.TryParse(signalMatch.Groups["max"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsedMax))
            {
                maxValue = parsedMax;
            }

            if (!minValue.HasValue || !maxValue.HasValue)
            {
                (float inferredMin, float inferredMax) = ComputeSignalRange(bitLength, isSigned, factor, offset);
                minValue ??= inferredMin;
                maxValue ??= inferredMax;
            }

            string units = signalMatch.Groups["unit"].Success
                ? signalMatch.Groups["unit"].Value.Trim()
                : string.Empty;

            if (string.IsNullOrWhiteSpace(units))
            {
                int firstQuote = line.IndexOf('"');
                if (firstQuote >= 0)
                {
                    int secondQuote = line.IndexOf('"', firstQuote + 1);
                    if (secondQuote > firstQuote)
                    {
                        units = line[(firstQuote + 1)..secondQuote].Trim();
                    }
                }
            }

            signals.Add(new DbcCanSignalDefinition
            {
                Name = signalMatch.Groups["name"].Value,
                MessageId = currentMessage.Id,
                IsExtended = currentMessage.IsExtended,
                MessageName = currentMessage.Name,
                Dlc = currentMessage.Dlc,
                StartBit = startBit,
                BitLength = bitLength,
                IsMotorola = signalMatch.Groups["endian"].Value == "0",
                IsSigned = isSigned,
                Factor = factor,
                Offset = offset,
                MinValue = minValue,
                MaxValue = maxValue,
                Units = units,
            });
        }

        messages = messageById.Values.ToList();
    }
}
