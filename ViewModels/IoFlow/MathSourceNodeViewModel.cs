using Avalonia;
using Cortex.Models;

namespace Cortex.ViewModels.IoFlow;

public sealed class MathSourceNodeViewModel : IoFlowNodeViewModel
{
    public MathSourceNodeViewModel(OutputChannel.MathOperandType operandType, byte index, string title, Point location)
        : base(title, location, operandType == OutputChannel.MathOperandType.CanSignal
            ? IoFlowNodePalette.CanRx
            : IoFlowNodePalette.SystemValue)
    {
        OperandType = operandType;
        SourceIndex = index;
    }

    public OutputChannel.MathOperandType OperandType { get; }

    public byte SourceIndex { get; private set; }

    public void AssignSource(byte index)
    {
        SourceIndex = index;
        OnPropertyChanged(nameof(SourceIndex));
        LiveValueText = "--";
    }

    public string SourceDescription => !IsAvailable
        ? "Unavailable: enable a decoded signal on a CAN RX message before wiring it."
        : OperandType == OutputChannel.MathOperandType.CanSignal
        ? "Decoded CAN RX signal. Connect only to logic rule operands."
        : "Controller system value. Connect only to logic rule operands.";

    private bool _isAvailable = true;

    public bool IsAvailable
    {
        get => _isAvailable;
        private set
        {
            if (SetProperty(ref _isAvailable, value))
            {
                OnPropertyChanged(nameof(SourceDescription));
            }
        }
    }

    public void UpdateSource(string title, bool available)
    {
        Title = title;
        IsAvailable = available;
    }

    private string _liveValueText = "--";

    public string LiveValueText
    {
        get => _liveValueText;
        private set => SetProperty(ref _liveValueText, value);
    }

    public void UpdateLiveState(DataStructures live)
    {
        var operand = new OutputChannel.MathRule { OperandAType = (byte)OperandType, OperandAIndex = SourceIndex };
        LiveValueText = IsAvailable ? MathRuntimeDisplayFormatter.FormatOperand(operand, true, live, 1, null) : "--";
    }
}
