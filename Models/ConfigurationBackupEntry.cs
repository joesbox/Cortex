using System;
using System.Globalization;

namespace Cortex.Models;

public sealed class ConfigurationBackupEntry
{
    public ConfigurationBackupEntry(string fileName, DateTimeOffset createdUtc)
    {
        FileName = fileName;
        CreatedUtc = createdUtc;
    }

    public string FileName { get; }

    public DateTimeOffset CreatedUtc { get; }

    public string DateText => CreatedUtc.LocalDateTime.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);

    public string TimeText => CreatedUtc.LocalDateTime.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
}
