using CommunityToolkit.Mvvm.ComponentModel;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowConnectionViewModel : ObservableObject
{
    private const string DefaultStrokeHex = "#FFAF8CFF";
    private const string SelectedStrokeHex = "#FF5FE3A1";

    public IoFlowConnectionViewModel(IoFlowConnectorEndpoint source, IoFlowConnectorEndpoint target, string label)
    {
        Source = source;
        Target = target;
        _label = label;
        _stroke = DefaultStrokeHex;
        _strokeThickness = 2;
    }

    public IoFlowConnectorEndpoint Source { get; }

    public IoFlowConnectorEndpoint Target { get; }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private string _stroke;

    [ObservableProperty]
    private double _strokeThickness;

    partial void OnIsSelectedChanged(bool value)
    {
        Stroke = value ? SelectedStrokeHex : DefaultStrokeHex;
        StrokeThickness = value ? 4 : 2;
    }
}
