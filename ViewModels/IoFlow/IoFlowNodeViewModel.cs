using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cortex.ViewModels.IoFlow;

public abstract partial class IoFlowNodeViewModel : ObservableObject
{
    protected IoFlowNodeViewModel(string title, Point location, string borderHex)
    {
        _title = title;
        _location = location;
        _borderHex = borderHex;
        InputConnector = new IoFlowConnectorEndpoint(this, isOutput: false);
        OutputConnector = new IoFlowConnectorEndpoint(this, isOutput: true);
    }

    public IoFlowConnectorEndpoint InputConnector { get; }

    public IoFlowConnectorEndpoint OutputConnector { get; }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private string _borderHex;
}
