using Cortex.Models;

namespace Cortex.ViewModels.IoFlow;

public sealed class IgnitionOnNodeViewModel : IoFlowNodeViewModel
{
    public IgnitionOnNodeViewModel(Avalonia.Point location)
        : base("Ignition On", location, ConnectorPinPalette.Ignition)
    {
    }
}
