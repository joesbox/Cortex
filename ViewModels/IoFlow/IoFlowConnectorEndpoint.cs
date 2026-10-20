using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowConnectorEndpoint : ObservableObject
{
    public IoFlowConnectorEndpoint(IoFlowNodeViewModel node, bool isOutput)
    {
        Node = node;
        IsOutput = isOutput;
    }

    public IoFlowNodeViewModel Node { get; }

    public bool IsOutput { get; }

    [ObservableProperty]
    private Point _anchor;

    [ObservableProperty]
    private bool _isConnected;
}
