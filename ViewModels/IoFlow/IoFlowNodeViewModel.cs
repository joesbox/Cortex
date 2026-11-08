using Avalonia;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using System;
using System.Collections.Generic;
using System.Linq;

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

    public Size MeasuredSize { get; set; }

    public Action<IoFlowNodeViewModel, int>? SourceSelectionChangedAction { get; set; }

    private IReadOnlyList<NodeSourceOption> _availableSources = [];
    public IReadOnlyList<NodeSourceOption> AvailableSources
    {
        get => _availableSources;
        private set => SetProperty(ref _availableSources, value);
    }

    private NodeSourceOption? _selectedSource;
    private bool _refreshingSources;
    private bool _selectingSource;
    private int _sourceRefreshVersion;

    public NodeSourceOption? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value) || _refreshingSources || value == null)
                return;
            _selectingSource = true;
            try
            {
                SourceSelectionChangedAction?.Invoke(this, value.Index);
            }
            finally
            {
                _selectingSource = false;
            }
        }
    }

    public void RefreshSourceOptions(IReadOnlyList<NodeSourceOption> options, int selectedIndex)
    {
        int version = ++_sourceRefreshVersion;
        if (_selectingSource)
            // Finish the ComboBox selection event before replacing its items.
            Dispatcher.UIThread.Post(Apply);
        else
            Apply();

        void Apply()
        {
            if (version != _sourceRefreshVersion) return;
            _refreshingSources = true;
            try
            {
                if (!AvailableSources.SequenceEqual(options)) AvailableSources = options;
                SelectedSource = AvailableSources.FirstOrDefault(option => option.Index == selectedIndex);
            }
            finally
            {
                _refreshingSources = false;
            }
        }
    }

    [ObservableProperty]
    private string _title;

    [ObservableProperty]
    private Point _location;

    [ObservableProperty]
    private int _canvasId;

    [ObservableProperty]
    private string _borderHex;

    [ObservableProperty]
    private bool _isSelected;

    [ObservableProperty]
    private double _borderThickness = 2;

    protected void SetDefaultBorderHex(string borderHex)
    {
        BorderHex = borderHex;
    }

}

public sealed record NodeSourceOption(int Index, string Label);
