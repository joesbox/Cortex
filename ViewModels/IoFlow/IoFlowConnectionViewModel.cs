using CommunityToolkit.Mvvm.ComponentModel;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowConnectionViewModel : ObservableObject
{
    private const string DefaultStrokeHex = "#FF8254B8";
    private const string SelectedStrokeHex = "#FFE0C8FF";
    private const string HoveredStrokeHex = "#FFB794EB";

    public IoFlowConnectionViewModel(IoFlowConnectorEndpoint source, IoFlowConnectorEndpoint target, string label)
    {
        Source = source;
        Target = target;
        _label = label;
        _stroke = DefaultStrokeHex;
        _strokeThickness = 4;
    }

    public IoFlowConnectorEndpoint Source { get; }

    public IoFlowConnectorEndpoint Target { get; }

    [ObservableProperty]
    private string _label;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private bool _isHovered;

    [ObservableProperty]
    private string _stroke;

    [ObservableProperty]
    private double _strokeThickness;

    partial void OnIsSelectedChanged(bool value)
    {
        UpdateAppearance();
    }

    partial void OnIsHoveredChanged(bool value)
    {
        UpdateAppearance();
    }

    private void UpdateAppearance()
    {
        Stroke = IsSelected ? SelectedStrokeHex : IsHovered ? HoveredStrokeHex : DefaultStrokeHex;
        StrokeThickness = IsSelected ? 7 : IsHovered ? 5 : 4;
    }
}
