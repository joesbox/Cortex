using CommunityToolkit.Mvvm.Input;
using Avalonia;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace Cortex.ViewModels.IoFlow;

public sealed partial class IoFlowGraphViewModel
{
    public const int MaximumCanvasCount = 20;
    public ObservableCollection<IoFlowCanvasViewModel> Canvases { get; } = [];
    public ObservableCollection<IoFlowNodeViewModel> VisibleNodes { get; } = [];
    public ObservableCollection<IoFlowConnectionViewModel> VisibleConnections { get; } = [];
    private IoFlowCanvasViewModel _selectedCanvas = new(1, "Canvas 1");
    private bool _restoringCanvasLayout;

    public IoFlowCanvasViewModel SelectedCanvas
    {
        get => _selectedCanvas;
        set
        {
            if (value == null || !Canvases.Contains(value) || !SetProperty(ref _selectedCanvas, value))
                return;
            ClearSelection();
            ClearUndoHistory();
            _nextNodeSpawnLocation = null;
            _spawnViewportSize = default;
            RefreshVisibleGraph();
            if (!_restoringCanvasLayout)
                NotifyGraphChanged(false);
        }
    }

    public bool CanAddCanvas => Canvases.Count < MaximumCanvasCount;
    public bool CanDeleteCanvas => Canvases.Count > 1 && !Nodes.Any(node => node.CanvasId == SelectedCanvas.Id);

    private void InitializeCanvases()
    {
        Canvases.CollectionChanged += (_, e) =>
        {
            if (e.OldItems != null)
                foreach (IoFlowCanvasViewModel canvas in e.OldItems)
                    canvas.PropertyChanged -= OnCanvasNameChanged;
            if (e.NewItems != null)
                foreach (IoFlowCanvasViewModel canvas in e.NewItems)
                    canvas.PropertyChanged += OnCanvasNameChanged;
            UpdateCanvasAvailability();
        };
        Canvases.Add(_selectedCanvas);
        Connections.CollectionChanged += (_, _) => RefreshVisibleGraph();
    }

    private void OnCanvasNameChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IoFlowCanvasViewModel.Name)
            or nameof(IoFlowCanvasViewModel.ViewportLocation)
            or nameof(IoFlowCanvasViewModel.ViewportZoom) && !_restoringCanvasLayout)
            NotifyGraphChanged(false);
    }

    [RelayCommand(CanExecute = nameof(CanAddCanvas))]
    private void AddCanvas()
    {
        if (!CanAddCanvas)
        {
            ShowAddNodeWarning("A maximum of 20 canvases is allowed.");
            return;
        }
        ClearAddNodeWarning();
        int id = Enumerable.Range(1, MaximumCanvasCount).First(candidate => Canvases.All(canvas => canvas.Id != candidate));
        var canvas = new IoFlowCanvasViewModel(id, $"Canvas {id}");
        Canvases.Add(canvas);
        SelectedCanvas = canvas;
    }

    [RelayCommand(CanExecute = nameof(CanDeleteCanvas))]
    private void DeleteCanvas()
    {
        DeleteCanvasTab(SelectedCanvas);
    }

    [RelayCommand]
    private void DeleteCanvasTab(IoFlowCanvasViewModel? canvas)
    {
        if (canvas == null || !Canvases.Contains(canvas))
            return;
        if (Nodes.Any(node => node.CanvasId == canvas.Id))
        {
            ShowAddNodeWarning($"Cannot delete \"{canvas.Name}\" because it is not empty. Move or remove all of its nodes first.");
            return;
        }
        if (Canvases.Count == 1)
        {
            ShowAddNodeWarning("At least one canvas must remain.");
            return;
        }
        ClearAddNodeWarning();
        if (canvas == SelectedCanvas)
            SelectedCanvas = Canvases.First(item => item != canvas);
        Canvases.Remove(canvas);
        NotifyGraphChanged(false);
    }

    [RelayCommand]
    private async Task MoveSelectedNodesToCanvas(IoFlowCanvasViewModel? destination)
    {
        if (destination == null || !Canvases.Contains(destination) || destination == SelectedCanvas)
            return;
        var selected = SelectedNodes.OfType<IoFlowNodeViewModel>().Where(VisibleNodes.Contains).ToHashSet();
        if (selected.Count == 0)
            return;
        var moving = GetConnectedNodes(selected);
        int sourceCanvasId = SelectedCanvas.Id;
        int additional = moving.Count - selected.Count;
        if (additional > 0)
        {
            if (ConfirmAction == null)
            {
                ShowAddNodeWarning("Cannot move connected nodes: confirmation is unavailable.");
                return;
            }
            if (!await ConfirmAction("Move connected nodes",
                $"{additional} connected node(s) are not selected. All {moving.Count} nodes in the connected group will move to \"{destination.Name}\". Continue?",
                "Move all connected nodes", "Cancel"))
                return;
        }
        if (!Canvases.Contains(destination) || moving.Any(node => !Nodes.Contains(node) || node.CanvasId != sourceCanvasId)
            || !moving.SetEquals(GetConnectedNodes(selected)))
        {
            ShowAddNodeWarning("The graph changed while confirming the move. Please select the nodes again.");
            return;
        }
        ClearSelection();
        ClearAddNodeWarning();
        Rect bounds = moving.Select(node =>
        {
            NodeSize size = EstimateNodeSize(node);
            return new Rect(node.Location, new Size(size.Width, size.Height));
        }).Aggregate((left, right) => left.Union(right));
        Point location = FindNearestAvailableSpawnLocation(
            destination.ViewportLocation + new Vector(40, 40), bounds.Width, bounds.Height,
            Nodes.Where(node => node.CanvasId == destination.Id).ToList(),
            Connections.Where(connection => connection.Source.Node.CanvasId == destination.Id
                && connection.Target.Node.CanvasId == destination.Id).ToList(), clampToViewport: false);
        Vector offset = location - bounds.Position;
        foreach (IoFlowNodeViewModel node in moving)
        {
            node.CanvasId = destination.Id;
            node.Location += offset;
        }
        ClearUndoHistory();
        RefreshVisibleGraph();
        NotifyGraphChanged(false);
    }

    private HashSet<IoFlowNodeViewModel> GetConnectedNodes(IEnumerable<IoFlowNodeViewModel> startingNodes)
    {
        var result = startingNodes.ToHashSet();
        var pending = new Queue<IoFlowNodeViewModel>(result);
        var neighbors = Nodes.ToDictionary(node => node, _ => new List<IoFlowNodeViewModel>());
        foreach (var connection in Connections)
        {
            neighbors[connection.Source.Node].Add(connection.Target.Node);
            neighbors[connection.Target.Node].Add(connection.Source.Node);
        }
        while (pending.TryDequeue(out var node))
            foreach (var neighbor in neighbors[node])
                if (result.Add(neighbor))
                    pending.Enqueue(neighbor);
        return result;
    }

    private void RefreshVisibleGraph()
    {
        if (_selectedCanvas == null)
            return;
        ReconcileVisible(VisibleNodes, Nodes.Where(node => node.CanvasId == SelectedCanvas.Id));
        ReconcileVisible(VisibleConnections, Connections.Where(connection =>
            VisibleNodes.Contains(connection.Source.Node) && VisibleNodes.Contains(connection.Target.Node)));
        UpdateCanvasAvailability();
    }

    private static void ReconcileVisible<T>(ObservableCollection<T> visible, IEnumerable<T> items)
    {
        var desired = items.ToHashSet();
        foreach (T item in visible.Where(item => !desired.Contains(item)).ToList())
            visible.Remove(item);
        foreach (T item in items)
            if (!visible.Contains(item))
                visible.Add(item);
    }

    private void UpdateCanvasAvailability()
    {
        OnPropertyChanged(nameof(CanAddCanvas));
        OnPropertyChanged(nameof(CanDeleteCanvas));
        AddCanvasCommand.NotifyCanExecuteChanged();
        DeleteCanvasCommand.NotifyCanExecuteChanged();
    }

    private void ResetCanvases()
    {
        RestoreCanvasLayout(new IoFlowGraphSnapshot());
    }

    private void RestoreCanvasLayout(IoFlowGraphSnapshot snapshot)
    {
        _restoringCanvasLayout = true;
        try
        {
            ClearSelection();
            foreach (var canvas in Canvases.ToList())
                Canvases.Remove(canvas);
            foreach (var canvas in snapshot.Canvases)
                Canvases.Add(new IoFlowCanvasViewModel(canvas.Id, canvas.Name)
                {
                    ViewportLocation = new Avalonia.Point(canvas.ViewportX, canvas.ViewportY),
                    ViewportZoom = canvas.ViewportZoom,
                });
            if (Canvases.Count == 0)
                Canvases.Add(new IoFlowCanvasViewModel(1, "Canvas 1"));
            foreach (var node in Nodes)
                node.CanvasId = Canvases[0].Id;
            SelectedCanvas = Canvases.FirstOrDefault(canvas => canvas.Id == snapshot.SelectedCanvasId) ?? Canvases[0];
            RefreshVisibleGraph();
        }
        finally
        {
            _restoringCanvasLayout = false;
        }
    }

    public static bool IsCanvasLayoutValid(IoFlowGraphSnapshot snapshot)
    {
        if (snapshot.Canvases == null || snapshot.Nodes == null || snapshot.Canvases.Count > MaximumCanvasCount
            || snapshot.Canvases.Any(canvas => canvas == null) || snapshot.Nodes.Any(node => node == null))
            return false;
        if (snapshot.Canvases.Count == 0)
            return snapshot.Nodes.All(node => node.CanvasId == 1 && double.IsFinite(node.X) && double.IsFinite(node.Y));
        var ids = snapshot.Canvases.Select(canvas => canvas.Id).ToHashSet();
        return ids.Count == snapshot.Canvases.Count
            && snapshot.Canvases.All(canvas => canvas.Id > 0 && !string.IsNullOrWhiteSpace(canvas.Name))
            && snapshot.Canvases.All(canvas => double.IsFinite(canvas.ViewportX) && double.IsFinite(canvas.ViewportY)
                && double.IsFinite(canvas.ViewportZoom) && canvas.ViewportZoom > 0)
            && ids.Contains(snapshot.SelectedCanvasId)
            && snapshot.Nodes.All(node => ids.Contains(node.CanvasId) && double.IsFinite(node.X) && double.IsFinite(node.Y));
    }

    public static bool IsNodePositionsValid(IoFlowGraphLayoutSnapshot snapshot)
    {
        if (snapshot.NodePositions == null || snapshot.NodePositions.Any(position => position == null))
            return false;

        var identities = new HashSet<(string Kind, int Id, byte Option)>();
        return IsCanvasLayoutValid(snapshot.ToGraphSnapshot()) && snapshot.NodePositions.All(position =>
            (position.Kind is "Digital" or "Analogue" or "Output" or "Can" or "CanTx" or "Ignition"
                or "Logic" or "OutputMath" or "CanRx" or "SystemValue")
            && double.IsFinite(position.X)
            && double.IsFinite(position.Y)
            && identities.Add((position.Kind, position.Id, position.Option)));
    }

    private void KeepConnectedNodesOnSameCanvas()
    {
        var visited = new HashSet<IoFlowNodeViewModel>();
        foreach (var node in Nodes)
        {
            if (visited.Contains(node))
                continue;
            var component = GetConnectedNodes([node]);
            visited.UnionWith(component);
            if (component.Select(item => item.CanvasId).Distinct().Count() <= 1)
                continue;
            int canvasId = component.OfType<OutputNodeViewModel>().OrderBy(item => item.ChannelNumber)
                .Select(item => item.CanvasId).FirstOrDefault(node.CanvasId);
            foreach (var item in component)
                item.CanvasId = canvasId;
        }
        RefreshVisibleGraph();
    }
}
