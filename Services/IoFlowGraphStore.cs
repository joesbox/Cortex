using Cortex.ViewModels.IoFlow;
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Cortex.Services;

public sealed class IoFlowGraphStore
{
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
    {
        WriteIndented = true,
    };

    private static readonly string SnapshotPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Cortex",
        "ioflow-graph.json");

    public void Save(IoFlowGraphSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotPath)!);
            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
            File.WriteAllText(SnapshotPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"Failed to save I/O Flow snapshot '{SnapshotPath}': {ex.Message}");
        }
    }

    public bool TryLoad(out IoFlowGraphSnapshot snapshot)
    {
        snapshot = new IoFlowGraphSnapshot();

        try
        {
            if (!File.Exists(SnapshotPath))
            {
                return false;
            }

            string json = File.ReadAllText(SnapshotPath);
            IoFlowGraphSnapshot? loaded = JsonSerializer.Deserialize<IoFlowGraphSnapshot>(json);
            if (loaded == null)
            {
                return false;
            }

            snapshot = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Trace.WriteLine($"Failed to load I/O Flow snapshot '{SnapshotPath}': {ex.Message}");
            return false;
        }
    }
}
