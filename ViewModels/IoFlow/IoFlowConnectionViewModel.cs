using CommunityToolkit.Mvvm.ComponentModel;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowConnectionViewModel : ObservableObject
{
    public IoFlowConnectionViewModel(IoFlowConnectorEndpoint source, IoFlowConnectorEndpoint target, string label)
    {
        Source = source;
        Target = target;
        _label = label;
    }

    public IoFlowConnectorEndpoint Source { get; }

    public IoFlowConnectorEndpoint Target { get; }

    [ObservableProperty]
    private string _label;
}
