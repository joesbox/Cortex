using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowConnectorEndpoint : ObservableObject
{
    public IoFlowConnectorEndpoint(IoFlowNodeViewModel node, bool isOutput, bool? operandA = null)
    {
        Node = node;
        IsOutput = isOutput;
        OperandA = operandA;
    }

    public IoFlowNodeViewModel Node { get; }

    public bool IsOutput { get; }

    public bool? OperandA { get; }

    [ObservableProperty]
    private Point _anchor;

    [ObservableProperty]
    private bool _isConnected;
}
