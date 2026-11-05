using CommunityToolkit.Mvvm.ComponentModel;
using System.ComponentModel;
using System.Text.Json.Serialization;

namespace Cortex.Models
{
    public enum CustomCanStatus : byte
    {
        Valid = 0,
        Disabled = 1,
        NotReceived = 2,
        Timeout = 3,
        InvalidFrame = 4,
        DecodeError = 5,
        InvalidConfig = 6,
    }

    public sealed class CustomCanMessage : ObservableObject
    {
        private int _index;
        private uint _id;
        private string _name = string.Empty;
        private ushort _timeoutMs;
        private byte _dlc;
        private byte _flags;
        private byte _txTrigger = Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS;
        private byte _status;
        private bool _isSelected;
        private int _signalCount;

        public int Index
        {
            get => _index;
            set
            {
                if (SetProperty(ref _index, value))
                {
                    OnPropertyChanged(nameof(SelectionSummary));
                    OnPropertyChanged(nameof(SlotNumber));
                }
            }
        }

        public uint Id
        {
            get => _id;
            set
            {
                if (SetProperty(ref _id, value))
                {
                    OnPropertyChanged(nameof(SelectionSummary));
                    OnPropertyChanged(nameof(IdHex));
                }
            }
        }

        /// <summary>UI-only: checked in the stored messages table (direction / delete target).</summary>
        [JsonIgnore]
        public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

        /// <summary>UI-only: number of enabled signals currently mapped to this message slot.</summary>
        [JsonIgnore]
        public int SignalCount { get => _signalCount; set => SetProperty(ref _signalCount, value); }

        public int SlotNumber => Index + 1;

        public string IdHex => $"0x{Id:X}";

        public string IdTypeLabel => IsExtended ? "EXT" : "STD";

        public string Name
        {
            get => _name;
            set
            {
                if (SetProperty(ref _name, value))
                {
                    OnPropertyChanged(nameof(SelectionSummary));
                }
            }
        }

        public ushort TimeoutMs
        {
            get => _timeoutMs;
            set
            {
                if (SetProperty(ref _timeoutMs, value))
                {
                    OnPropertyChanged(nameof(TimeoutDisplay));
                    OnPropertyChanged(nameof(PeriodDisplay));
                }
            }
        }

        public byte Dlc { get => _dlc; set => SetProperty(ref _dlc, value); }

        public byte Flags
        {
            get => _flags;
            set
            {
                if (SetProperty(ref _flags, value))
                {
                    OnPropertyChanged(nameof(IsEnabled));
                    OnPropertyChanged(nameof(IsExtended));
                    OnPropertyChanged(nameof(IdTypeLabel));
                    OnPropertyChanged(nameof(IsTx));
                    OnPropertyChanged(nameof(IsRx));
                    OnPropertyChanged(nameof(IsPeriodic));
                    OnPropertyChanged(nameof(DirectionLabel));
                    OnPropertyChanged(nameof(TriggerLabel));
                    OnPropertyChanged(nameof(TimeoutDisplay));
                    OnPropertyChanged(nameof(PeriodDisplay));
                }
            }
        }

        public byte TxTrigger
        {
            get => _txTrigger;
            set
            {
                if (SetProperty(ref _txTrigger, value))
                {
                    OnPropertyChanged(nameof(TriggerLabel));
                    OnPropertyChanged(nameof(IsAlwaysTrigger));
                }
            }
        }

        public byte Status { get => _status; set => SetProperty(ref _status, value); }

        public bool IsEnabled
        {
            get => (Flags & 0x01) != 0;
            set => Flags = value ? (byte)(Flags | 0x01) : (byte)(Flags & ~0x01);
        }

        public bool IsExtended
        {
            get => (Flags & 0x02) != 0;
            set => Flags = value ? (byte)(Flags | 0x02) : (byte)(Flags & ~0x02);
        }

        public bool IsTx
        {
            get => (Flags & 0x04) != 0;
            set => Flags = value ? (byte)(Flags | 0x04) : (byte)(Flags & ~0x04);
        }

        public bool IsRx => !IsTx;

        public bool IsPeriodic
        {
            get => (Flags & 0x08) != 0;
            set => Flags = value ? (byte)(Flags | 0x08) : (byte)(Flags & ~0x08);
        }

        public bool IsAlwaysTrigger => TxTrigger == Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS;

        public string DirectionLabel => IsTx
            ? (IsPeriodic ? "TX periodic" : "TX one-shot") + (IsEnabled ? string.Empty : " (off)")
            : "RX";

        public string TriggerLabel =>
            !IsTx || !IsEnabled || TxTrigger >= Constants.NUM_OUTPUT_CHANNELS
                ? "-"
                : $"CH{TxTrigger + 1}";

        public string SelectionSummary
        {
            get
            {
                string label = string.IsNullOrWhiteSpace(Name) ? $"Message {Index + 1}" : Name.Trim();
                return $"{label} (0x{Id:X})";
            }
        }

        /// <summary>RX timeout column text: "-" for TX messages, "None" when disabled (0).</summary>
        public string TimeoutDisplay =>
            IsTx ? "-" : TimeoutMs == 0 ? "None" : $"{TimeoutMs} ms";

        /// <summary>TX period column text: "-" unless the message is periodic TX.</summary>
        public string PeriodDisplay =>
            IsTx && IsPeriodic ? $"{TimeoutMs} ms" : "-";
    }

    /// <summary>Manual-entry "Message" choice: a new frame, or an existing stored message to add a signal to.</summary>
    public sealed class CustomCanMessageTargetOption
    {
        public CustomCanMessageTargetOption(CustomCanMessage? message, string label)
        {
            Message = message;
            Label = label;
        }

        public CustomCanMessage? Message { get; }

        public string Label { get; }

        public override string ToString() => Label;
    }

    public sealed class CustomCanSignal : ObservableObject
    {
        private int _index;
        private byte _messageIndex;
        private byte _startBit;
        private byte _bitLength;
        private byte _flags;
        private byte _valueSource;
        private float _factor;
        private float _offset;
        private float _runtimeValue;
        private byte _status;
        private string _name = string.Empty;
        private string _messageName = string.Empty;
        private string _messageIdHex = string.Empty;
        private bool _isMarkedForDeletion;
        private float? _minValue;
        private float? _maxValue;
        private string _units = string.Empty;
        private CustomCanMessage? _ownerMessage;

        /// <summary>
        /// The message this signal currently belongs to (resolved from MessageIndex).
        /// Used by the stored table to show message-level direction/trigger/period columns.
        /// </summary>
        public CustomCanMessage? OwnerMessage
        {
            get => _ownerMessage;
            set
            {
                if (ReferenceEquals(_ownerMessage, value))
                {
                    return;
                }

                if (_ownerMessage != null)
                {
                    _ownerMessage.PropertyChanged -= OwnerMessage_PropertyChanged;
                }

                _ownerMessage = value;
                if (_ownerMessage != null)
                {
                    _ownerMessage.PropertyChanged += OwnerMessage_PropertyChanged;
                }

                OnPropertyChanged();
                RaiseOwnerMessageDisplayChanges();
            }
        }

        private void OwnerMessage_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(CustomCanMessage.Flags) or nameof(CustomCanMessage.TxTrigger) or
                nameof(CustomCanMessage.TimeoutMs) or nameof(CustomCanMessage.IsTx) or
                nameof(CustomCanMessage.IsPeriodic) or nameof(CustomCanMessage.DirectionLabel) or
                nameof(CustomCanMessage.TriggerLabel) or nameof(CustomCanMessage.TimeoutDisplay) or
                nameof(CustomCanMessage.PeriodDisplay))
            {
                RaiseOwnerMessageDisplayChanges();
            }
        }

        private void RaiseOwnerMessageDisplayChanges()
        {
            OnPropertyChanged(nameof(IsOnTxMessage));
            OnPropertyChanged(nameof(DirectionLabel));
            OnPropertyChanged(nameof(TriggerDisplay));
            OnPropertyChanged(nameof(TimeoutDisplay));
            OnPropertyChanged(nameof(PeriodDisplay));
            OnPropertyChanged(nameof(ValueSourceDisplay));
            OnPropertyChanged(nameof(IdTypeLabel));
        }

        public bool IsOnTxMessage => OwnerMessage?.IsTx == true;

        public string IdTypeLabel => OwnerMessage == null ? "-" : OwnerMessage.IsExtended ? "EXT" : "STD";

        public string DirectionLabel => IsOnTxMessage ? "TX" : "RX";

        public string TriggerDisplay => OwnerMessage?.TriggerLabel ?? "-";

        public string TimeoutDisplay => OwnerMessage?.TimeoutDisplay ?? "-";

        public string PeriodDisplay => OwnerMessage?.PeriodDisplay ?? "-";

        public string ValueSourceDisplay => IsOnTxMessage ? SourceLabel : "-";

        public int Index
        {
            get => _index;
            set
            {
                if (SetProperty(ref _index, value))
                {
                    OnPropertyChanged(nameof(SlotNumber));
                    OnPropertyChanged(nameof(DisplayLabel));
                }
            }
        }

        public byte MessageIndex
        {
            get => _messageIndex;
            set
            {
                if (SetProperty(ref _messageIndex, value))
                {
                    OnPropertyChanged(nameof(MessageSlotNumber));
                    OnPropertyChanged(nameof(DisplayLabel));
                }
            }
        }

        public byte StartBit { get => _startBit; set => SetProperty(ref _startBit, value); }
        public byte BitLength { get => _bitLength; set => SetProperty(ref _bitLength, value); }

        public byte Flags
        {
            get => _flags;
            set
            {
                if (SetProperty(ref _flags, value))
                {
                    OnPropertyChanged(nameof(IsEnabled));
                    OnPropertyChanged(nameof(IsMotorola));
                    OnPropertyChanged(nameof(IsSigned));
                    OnPropertyChanged(nameof(EndianLabel));
                    OnPropertyChanged(nameof(SignLabel));
                }
            }
        }

        public byte ValueSource
        {
            get => _valueSource;
            set
            {
                if (SetProperty(ref _valueSource, value))
                {
                    OnPropertyChanged(nameof(IsConstantSource));
                    OnPropertyChanged(nameof(SourceLabel));
                    OnPropertyChanged(nameof(ValueSourceDisplay));
                }
            }
        }

        public bool IsConstantSource => ValueSource == Constants.CUSTOM_CAN_VALUE_SOURCE_CONSTANT;

        public string SourceLabel => IsConstantSource ? "Constant" : $"CH{ValueSource + 1}";

        public float Factor { get => _factor; set => SetProperty(ref _factor, value); }
        public float Offset { get => _offset; set => SetProperty(ref _offset, value); }
        public float RuntimeValue { get => _runtimeValue; set => SetProperty(ref _runtimeValue, value); }
        public byte Status
        {
            get => _status;
            set
            {
                if (SetProperty(ref _status, value))
                {
                    OnPropertyChanged(nameof(IsDetected));
                }
            }
        }

        public string Name { get => _name; set => SetProperty(ref _name, value); }
        public string MessageName { get => _messageName; set => SetProperty(ref _messageName, value); }
        public string MessageIdHex { get => _messageIdHex; set => SetProperty(ref _messageIdHex, value); }
        public bool IsMarkedForDeletion { get => _isMarkedForDeletion; set => SetProperty(ref _isMarkedForDeletion, value); }
        public float? MinValue { get => _minValue; set => SetProperty(ref _minValue, value); }
        public float? MaxValue { get => _maxValue; set => SetProperty(ref _maxValue, value); }
        public string Units { get => _units; set => SetProperty(ref _units, value); }

        public int SlotNumber => Index + 1;

        public int MessageSlotNumber => MessageIndex + 1;

        public string EndianLabel => IsMotorola ? "Motorola" : "Intel";

        public string SignLabel => IsSigned ? "Signed" : "Unsigned";

        public bool IsDetected => Status == (byte)CustomCanStatus.Valid;

        public bool IsEnabled
        {
            get => (Flags & 0x01) != 0;
            set => Flags = value ? (byte)(Flags | 0x01) : (byte)(Flags & ~0x01);
        }

        public bool IsMotorola
        {
            get => (Flags & 0x02) != 0;
            set => Flags = value ? (byte)(Flags | 0x02) : (byte)(Flags & ~0x02);
        }

        public bool IsSigned
        {
            get => (Flags & 0x04) != 0;
            set => Flags = value ? (byte)(Flags | 0x04) : (byte)(Flags & ~0x04);
        }

        public string DisplayLabel =>
            string.IsNullOrWhiteSpace(Name)
                ? $"Signal {Index + 1} (Msg {MessageIndex + 1})"
                : $"{Name} (Msg {MessageIndex + 1})";
    }

    public sealed class DbcCanMessageDefinition
    {
        public uint Id { get; set; }

        public bool IsExtended { get; set; }

        public byte Dlc { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    public sealed class DbcCanSignalDefinition
    {
        public string Name { get; set; } = string.Empty;

        public uint MessageId { get; set; }

        public string MessageIdHex => $"0x{MessageId:X}";

        /// <summary>
        /// 29-bit identifier. Kept separate from the ID because an extended frame may use an ID of 0x7FF or less.
        /// </summary>
        public bool IsExtended { get; set; }

        public string IdTypeLabel => IsExtended ? "EXT" : "STD";

        public string MessageName { get; set; } = string.Empty;

        public byte StartBit { get; set; }

        public byte BitLength { get; set; }

        public bool IsMotorola { get; set; }

        public bool IsSigned { get; set; }

        public float Factor { get; set; }

        public float Offset { get; set; }

        public float? MinValue { get; set; }

        public float? MaxValue { get; set; }

        public string Units { get; set; } = string.Empty;

        public byte Dlc { get; set; }

        public string DisplayLabel => $"{MessageName}.{Name}";
    }

    public sealed class CustomCanSignalMetadata
    {
        public int SignalIndex { get; set; }

        public byte MessageIndex { get; set; }

        /// <summary>
        /// CAN identifier of the owning message. Together with the layout fields this forms the
        /// slot-independent identity used to match local names to controller signals.
        /// Null for legacy entries that were keyed by slot only.
        /// </summary>
        public uint? MessageId { get; set; }

        public bool IsExtended { get; set; }

        public byte StartBit { get; set; }

        public byte BitLength { get; set; }

        public bool IsMotorola { get; set; }

        public bool IsSigned { get; set; }

        public float Factor { get; set; }

        public float Offset { get; set; }

        public string Name { get; set; } = string.Empty;

        public string MessageName { get; set; } = string.Empty;

        public float? MinValue { get; set; }

        public float? MaxValue { get; set; }

        public string Units { get; set; } = string.Empty;

        public string IdentityFingerprint { get; set; } = string.Empty;

        public CustomCanSignalMetadata Clone() => (CustomCanSignalMetadata)MemberwiseClone();
    }
}