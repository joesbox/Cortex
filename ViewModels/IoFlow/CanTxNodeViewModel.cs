using Cortex.Models;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;

namespace Cortex.ViewModels.IoFlow;

/// <summary>
/// Represents a Custom CAN TX message whose transmission is gated by an output
/// channel. The single input connector binds the message's TxTrigger to the
/// connected output channel; disconnecting disables the message.
/// </summary>
public sealed class CanTxNodeViewModel : IoFlowNodeViewModel
{
    private readonly Action<CanTxNodeViewModel, int, int>? _messageSelectionChangedAction;
    private bool _suppressMessageSelectionCallback;
    private bool _editIsPeriodic;
    private int _editTimeoutMs;
    private int _selectedMessageIndex;
    private string _validationMessage = string.Empty;
    private CustomCanMessage _message;
    private IReadOnlyList<CustomCanMessage> _availableMessages;

    public CanTxNodeViewModel(
        CustomCanMessage message,
        Avalonia.Point location,
        IReadOnlyList<CustomCanMessage> availableMessages,
        Action<CanTxNodeViewModel, int, int>? messageSelectionChangedAction)
        : base(BuildTitle(message), location, IoFlowNodePalette.CanTx)
    {
        _message = message;
        _availableMessages = availableMessages;
        _messageSelectionChangedAction = messageSelectionChangedAction;
        _message.PropertyChanged += Message_PropertyChanged;
        RefreshEditFieldsFromMessage();
        SetSelectedMessageIndex(_message.Index, notifyGraph: false);
    }

    public CustomCanMessage Message => _message;

    public int MessageIndex => _message.Index;

    public IReadOnlyList<CustomCanMessage> AvailableMessages => _availableMessages;

    public int SelectedMessageIndex
    {
        get => _selectedMessageIndex;
        set => SetSelectedMessageIndex(value, notifyGraph: true);
    }

    public bool EditIsPeriodic
    {
        get => _editIsPeriodic;
        set
        {
            if (_editIsPeriodic == value)
            {
                return;
            }

            _editIsPeriodic = value;
            OnPropertyChanged(nameof(EditIsPeriodic));
        }
    }

    public int EditTimeoutMs
    {
        get => _editTimeoutMs;
        set
        {
            if (_editTimeoutMs == value)
            {
                return;
            }

            _editTimeoutMs = value;
            OnPropertyChanged(nameof(EditTimeoutMs));
        }
    }

    public int PeriodMinimum => Constants.CUSTOM_CAN_TX_PERIOD_MIN_MS;
    public int PeriodMaximum => Constants.CUSTOM_CAN_TX_PERIOD_MAX_MS;
    public int PeriodStep => Constants.CUSTOM_CAN_TX_PERIOD_STEP_MS;

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (_validationMessage == value)
            {
                return;
            }

            _validationMessage = value;
            OnPropertyChanged(nameof(ValidationMessage));
            OnPropertyChanged(nameof(HasValidationMessage));
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public string ModeText => Message.IsPeriodic
        ? $"Periodic every {Message.TimeoutMs} ms while the channel is active"
        : "One-shot when the channel turns on";

    public string StatusText => Message.IsEnabled ? "Enabled" : "Disabled (connect a channel to enable)";

    public void SetValidationMessage(string message)
    {
        ValidationMessage = message;
    }

    public void ClearValidationMessage()
    {
        ValidationMessage = string.Empty;
    }

    public void RefreshEditFieldsFromMessage()
    {
        EditIsPeriodic = Message.IsPeriodic;
        EditTimeoutMs = Message.TimeoutMs;
    }

    public void SetAvailableMessages(IReadOnlyList<CustomCanMessage> availableMessages)
    {
        _availableMessages = availableMessages;
        OnPropertyChanged(nameof(AvailableMessages));

        if (_availableMessages.Any(message => message.Index == MessageIndex))
        {
            SetSelectedMessageIndex(MessageIndex, notifyGraph: false);
        }
    }

    public void RebindMessage(CustomCanMessage message)
    {
        if (ReferenceEquals(_message, message))
        {
            SetSelectedMessageIndex(_message.Index, notifyGraph: false);
            return;
        }

        _message.PropertyChanged -= Message_PropertyChanged;
        _message = message;
        _message.PropertyChanged += Message_PropertyChanged;

        Title = BuildTitle(_message);
        RefreshEditFieldsFromMessage();
        OnPropertyChanged(nameof(Message));
        OnPropertyChanged(nameof(MessageIndex));
        OnPropertyChanged(nameof(ModeText));
        OnPropertyChanged(nameof(StatusText));
        SetSelectedMessageIndex(_message.Index, notifyGraph: false);
    }

    public void RevertSelectedMessageToCurrent()
    {
        SetSelectedMessageIndex(_message.Index, notifyGraph: false);
    }

    private void SetSelectedMessageIndex(int value, bool notifyGraph)
    {
        if (_selectedMessageIndex == value)
        {
            return;
        }

        int previous = _selectedMessageIndex;
        _selectedMessageIndex = value;
        OnPropertyChanged(nameof(SelectedMessageIndex));

        if (!notifyGraph || _suppressMessageSelectionCallback)
        {
            return;
        }

        _messageSelectionChangedAction?.Invoke(this, previous, value);
    }

    private static string BuildTitle(CustomCanMessage message) => $"CAN TX 0x{message.Id:X}";

    private void Message_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(CustomCanMessage.Id):
                Title = BuildTitle(Message);
                break;
            case nameof(CustomCanMessage.Flags):
            case nameof(CustomCanMessage.TimeoutMs):
            case nameof(CustomCanMessage.IsEnabled):
            case nameof(CustomCanMessage.IsPeriodic):
                RefreshEditFieldsFromMessage();
                OnPropertyChanged(nameof(ModeText));
                OnPropertyChanged(nameof(StatusText));
                break;
        }
    }
}
