using Cortex.ViewModels.IoFlow;
using System;
using System.IO;
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

    public void Save(IoFlowGraphSnapshot snapshot)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
            string json = JsonSerializer.Serialize(snapshot, JsonOptions);
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
            LoggingService.AddLog("Could not save the local I/O Flow draft. Unsaved changes may not survive restart.", ex.Message);
        }
    }

    public bool TryLoad(out IoFlowGraphSnapshot snapshot)
    {
        snapshot = new IoFlowGraphSnapshot();

        try
        {
            if (!File.Exists(_snapshotPath))
            {
                return false;
            }

            string json = File.ReadAllText(_snapshotPath);
            IoFlowGraphSnapshot? loaded = JsonSerializer.Deserialize<IoFlowGraphSnapshot>(json);
            if (loaded == null)
            {
                return false;
            }
            if (!IoFlowGraphViewModel.IsCanvasLayoutValid(loaded))
            {
                LoggingService.AddLog("Could not load the local I/O Flow draft: invalid canvas layout.");
                return false;
            }

            snapshot = loaded;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoggingService.AddLog("Could not load the local I/O Flow draft.", ex.Message);
            return false;
        }
    }
}
