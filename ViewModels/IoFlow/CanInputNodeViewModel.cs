using Cortex.Models;

namespace Cortex.ViewModels.IoFlow;

public sealed class CanInputNodeViewModel : IoFlowNodeViewModel
{
    public CanInputNodeViewModel(Avalonia.Point location)
        : base("System CAN Input", location, ConnectorPinPalette.OutputCanDigital)
    {
    }
}
