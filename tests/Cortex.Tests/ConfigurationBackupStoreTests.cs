using Cortex.Services;
using Xunit;

namespace Cortex.Tests;

public class ConfigurationBackupStoreTests
{
    [Fact]
    public void CreateBackup_RetainsTheTenNewestSnapshots()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Cortex-configuration-backups", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ConfigurationBackupStore(directory);
            for (int index = 0; index < 12; index++)
            {
                Assert.True(store.TryCreateBackup($"backup-{index}", out string? error), error);
                Thread.Sleep(2);
            }

            IReadOnlyList<Cortex.Models.ConfigurationBackupEntry> backups = store.GetBackups();

            Assert.Equal(ConfigurationBackupStore.RetainedBackupCount, backups.Count);
            Assert.Equal("backup-11", store.ReadBackup(backups[0]));
            Assert.Equal("backup-2", store.ReadBackup(backups[^1]));
            Assert.All(backups.Zip(backups.Skip(1)), pair =>
                Assert.True(pair.First.CreatedUtc >= pair.Second.CreatedUtc));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void CreateBackup_RejectsEmptyContent()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Cortex-configuration-backups", Guid.NewGuid().ToString("N"));
        var store = new ConfigurationBackupStore(directory);

        Assert.False(store.TryCreateBackup(" ", out string? error));
        Assert.Equal("The configuration backup is empty.", error);
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void GetBackups_IgnoresMalformedBackupNames()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Cortex-configuration-backups", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "controller-config-.pdm"), "invalid");

            Assert.Empty(new ConfigurationBackupStore(directory).GetBackups());
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
