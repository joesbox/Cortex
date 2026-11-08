using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowCanvasViewModel : ObservableObject
{
    public IoFlowCanvasViewModel(int id, string name)
    {
        Id = id;
        _name = name;
    }

    public int Id { get; }

    [ObservableProperty]
    private Point _viewportLocation;

    [ObservableProperty]
    private double _viewportZoom = 1;

    private string _name;

    public string Name
    {
        get => _name;
        set => SetProperty(ref _name, string.IsNullOrWhiteSpace(value) ? $"Canvas {Id}" : value);
    }
}
