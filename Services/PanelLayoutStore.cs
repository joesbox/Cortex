using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cortex.Services;

public sealed class PanelLayoutStore
{
    private readonly string _path;
    private readonly double _defaultTopFraction;

    public PanelLayoutStore(string fileName, double defaultTopFraction, string? path = null)
    {
        if (!IsValidFraction(defaultTopFraction))
        {
            throw new ArgumentOutOfRangeException(nameof(defaultTopFraction));
        }

        _defaultTopFraction = defaultTopFraction;
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cortex",
            fileName);
    }

    private sealed class LayoutPreferences
    {
        public double? TopFraction { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public double? ImportFraction { get; set; }
    }

    public double LoadTopFraction()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return _defaultTopFraction;
            }

            LayoutPreferences? preferences = JsonSerializer.Deserialize<LayoutPreferences>(File.ReadAllText(_path));
            double? fraction = preferences?.TopFraction ?? preferences?.ImportFraction;
            if (fraction is not double topFraction || !IsValidFraction(topFraction))
            {
                LoggingService.AddLog("Invalid panel layout preferences; using the default proportions.", details: _path);
                return _defaultTopFraction;
            }

            return topFraction;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoggingService.AddLog("Failed to load panel layout preferences; using the default proportions.", details: _path, exception: ex);
            return _defaultTopFraction;
        }
    }

    public void SaveTopFraction(double fraction)
    {
        if (!IsValidFraction(fraction))
        {
            throw new ArgumentOutOfRangeException(nameof(fraction), "The top fraction must be finite and between zero and one.");
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            string json = JsonSerializer.Serialize(new LayoutPreferences { TopFraction = fraction });
            File.WriteAllText(_path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LoggingService.AddLog("Failed to save panel layout preferences.", details: _path, exception: ex);
        }
    }

    private static bool IsValidFraction(double fraction) =>
        double.IsFinite(fraction) && fraction >= 0 && fraction <= 1;
}
