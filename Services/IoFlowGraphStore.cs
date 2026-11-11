using Cortex.ViewModels.IoFlow;
using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Cortex.Services;

public sealed class IoFlowGraphStore
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    private readonly string _snapshotPath;

    public IoFlowGraphStore(string? snapshotPath = null)
    {
        _snapshotPath = snapshotPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cortex",
            "ioflow-graph.json");
    }

    public void Save(IoFlowGraphSnapshot snapshot, bool preserveUnmatchedNodePositions = false)
        => Save(snapshot.ToLayoutSnapshot(), preserveUnmatchedNodePositions);

    public void Save(IoFlowGraphLayoutSnapshot layout, bool preserveUnmatchedNodePositions = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
            if (preserveUnmatchedNodePositions && TryLoad(out var previous))
            {
                var identities = layout.NodePositions.Select(node => (node.Kind, node.Id, node.Option)).ToHashSet();
                var canvasIds = layout.Canvases.Select(canvas => canvas.Id).ToHashSet();
                layout.AnalogueInputNodes ??= previous.AnalogueInputNodes;
                layout.NodePositions.AddRange(previous.NodePositions.Where(node =>
                    canvasIds.Contains(node.CanvasId) && !identities.Contains((node.Kind, node.Id, node.Option))));
            }
            string json = JsonSerializer.Serialize(layout, JsonOptions);
            string temporaryPath = _snapshotPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, _snapshotPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoggingService.AddLog("Could not save the local I/O Flow layout.", ex.Message);
        }
    }

    public bool TryLoad(out IoFlowGraphLayoutSnapshot snapshot)
    {
        snapshot = new IoFlowGraphLayoutSnapshot();

        try
        {
            if (!File.Exists(_snapshotPath))
            {
                return false;
            }

            string json = File.ReadAllText(_snapshotPath);
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                LoggingService.AddLog("Could not load the local I/O Flow layout: invalid snapshot format.");
                return false;
            }

            if (document.RootElement.TryGetProperty(nameof(IoFlowGraphLayoutSnapshot.NodePositions), out _))
            {
                IoFlowGraphLayoutSnapshot? loadedLayout = JsonSerializer.Deserialize<IoFlowGraphLayoutSnapshot>(json);
                if (loadedLayout == null || !IoFlowGraphViewModel.IsNodePositionsValid(loadedLayout))
                {
                    LoggingService.AddLog("Could not load the local I/O Flow layout: invalid canvas layout or node positions.");
                    return false;
                }

                snapshot = loadedLayout;
                return true;
            }

            IoFlowGraphSnapshot? legacy = JsonSerializer.Deserialize<IoFlowGraphSnapshot>(json);
            if (legacy == null || !IoFlowGraphViewModel.IsCanvasLayoutValid(legacy))
            {
                LoggingService.AddLog("Could not load the local I/O Flow layout: invalid canvas layout.");
                return false;
            }

            IoFlowGraphLayoutSnapshot migratedLayout = legacy.ToLayoutSnapshot();
            if (!IoFlowGraphViewModel.IsNodePositionsValid(migratedLayout))
            {
                LoggingService.AddLog("Could not load the local I/O Flow layout: invalid node positions.");
                return false;
            }

            migratedLayout.AnalogueInputNodes = legacy.Nodes.Where(node => node.Kind == "Analogue").Select(node => node.Id).Distinct().ToList();
            Save(migratedLayout);
            snapshot = migratedLayout;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoggingService.AddLog("Could not load the local I/O Flow layout.", ex.Message);
            return false;
        }
    }
}
