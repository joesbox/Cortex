using Cortex.Models;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace Cortex.Services;

public sealed class ConfigurationBackupStore
{
    public const int RetainedBackupCount = 10;

    private const string BackupFilePrefix = "controller-config-";
    private const string BackupTimestampFormat = "yyyyMMdd'T'HHmmssfffffff'Z'";

    private readonly string _directoryPath;

    public ConfigurationBackupStore(string? directoryPath = null)
    {
        _directoryPath = directoryPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Cortex",
            "configuration-backups");
    }

    public IReadOnlyList<ConfigurationBackupEntry> GetBackups()
    {
        try
        {
            if (!Directory.Exists(_directoryPath))
            {
                return Array.Empty<ConfigurationBackupEntry>();
            }

            return Directory.EnumerateFiles(_directoryPath, $"{BackupFilePrefix}*.pdm")
                .Select(TryCreateEntry)
                .Where(entry => entry != null)
                .Cast<ConfigurationBackupEntry>()
                .OrderByDescending(entry => entry.CreatedUtc)
                .ThenByDescending(entry => entry.FileName, StringComparer.Ordinal)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LoggingService.AddLog("Failed to list configuration backups.", details: _directoryPath, exception: ex);
            return Array.Empty<ConfigurationBackupEntry>();
        }
    }

    public bool TryCreateBackup(string content, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(content))
        {
            error = "The configuration backup is empty.";
            return false;
        }

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_directoryPath);
            DateTimeOffset createdUtc = DateTimeOffset.UtcNow;
            string fileName = $"{BackupFilePrefix}{createdUtc.ToString(BackupTimestampFormat, CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.pdm";
            string backupPath = Path.Combine(_directoryPath, fileName);
            temporaryPath = backupPath + "." + Guid.NewGuid().ToString("N") + ".tmp";

            File.WriteAllText(temporaryPath, content);
            File.Move(temporaryPath, backupPath);
            temporaryPath = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
            LoggingService.AddLog("Failed to create a configuration backup.", details: _directoryPath, exception: ex);
            return false;
        }
        finally
        {
            if (temporaryPath != null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LoggingService.AddLog("Failed to remove a temporary configuration backup.", details: temporaryPath, exception: ex);
                }
            }
        }

        PruneOldBackups();
        return true;
    }

    public string ReadBackup(ConfigurationBackupEntry backup)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (Path.GetFileName(backup.FileName) != backup.FileName ||
            !backup.FileName.StartsWith(BackupFilePrefix, StringComparison.Ordinal) ||
            !backup.FileName.EndsWith(".pdm", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The selected configuration backup is invalid.", nameof(backup));
        }

        return File.ReadAllText(Path.Combine(_directoryPath, backup.FileName));
    }

    private void PruneOldBackups()
    {
        IReadOnlyList<ConfigurationBackupEntry> backups = GetBackups();
        foreach (ConfigurationBackupEntry backup in backups.Skip(RetainedBackupCount))
        {
            try
            {
                File.Delete(Path.Combine(_directoryPath, backup.FileName));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LoggingService.AddLog("Failed to remove an expired configuration backup.", details: backup.FileName, exception: ex);
            }
        }
    }

    private static ConfigurationBackupEntry? TryCreateEntry(string path)
    {
        string fileName = Path.GetFileName(path);
        if (fileName.Length <= BackupFilePrefix.Length + ".pdm".Length)
        {
            return null;
        }

        string timestampAndId = fileName[BackupFilePrefix.Length..^".pdm".Length];
        int separator = timestampAndId.IndexOf('-');
        if (separator <= 0 ||
            !DateTimeOffset.TryParseExact(
                timestampAndId[..separator],
                BackupTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset createdUtc))
        {
            return null;
        }

        return new ConfigurationBackupEntry(fileName, createdUtc);
    }
}
