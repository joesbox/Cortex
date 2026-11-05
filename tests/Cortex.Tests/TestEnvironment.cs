using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace Cortex.Tests;

internal static class TestEnvironment
{
    // Keep view-model tests away from the user's real %LOCALAPPDATA%\Cortex\custom-can-metadata.json.
    [ModuleInitializer]
    internal static void Initialize()
    {
        string directory = Path.Combine(Path.GetTempPath(), "cortex-tests");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"custom-can-metadata-{Environment.ProcessId}.json");
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        Environment.SetEnvironmentVariable("CORTEX_CUSTOM_CAN_METADATA_PATH", path);
    }
}
