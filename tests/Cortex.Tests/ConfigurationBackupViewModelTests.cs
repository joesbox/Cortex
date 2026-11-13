using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using System.Net;
using System.Net.Http;
using System.Reflection;
using Xunit;

namespace Cortex.Tests;

public class ConfigurationBackupViewModelTests
{
    [Theory]
    [InlineData("timeout")]
    [InlineData("http-error")]
    [InlineData("success")]
    public async Task SignOutClearsLocalStateBeforeRemoteRevocationFinishes(string outcome)
    {
        string directory = CreateTestDirectory();
        try
        {
            using var handler = new SignOutHandler();
            using var client = new HttpClient(handler);
            using var api = new ProvisioningApiClient(client, new Uri("https://keycloak.example/auth/"));
            Assert.True(await api.TryResumeAsync("realm", "user", "refresh"));
            string path = Path.Combine(directory, "session.dat");
            await File.WriteAllTextAsync(path, "test session");
            using var viewModel = new MainWindowViewModel(new TestAppCloser(null), new MainWindowViewModelDependencies(
                provisioningApiClient: api,
                provisioningSessionStore: new ProvisioningSessionStore(path),
                ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "ioflow.json"))));
            viewModel.IsOpenRemoteSignedIn = true;
            viewModel.OpenRemoteSignedInAs = "user";
            viewModel.OpenRemotePasswordInput = "temporary";

            Task signOut = viewModel.SignOutOpenRemoteCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsOpenRemoteSignedIn);
            Assert.False(api.IsSignedIn);
            Assert.False(File.Exists(path));
            Assert.Empty(viewModel.OpenRemotePasswordInput);
            Assert.False(signOut.IsCompleted);
            Assert.Equal("Signed out on this computer. Ending the telemetry service session...", viewModel.OpenRemoteProvisioningStatusMessage);
            if (outcome == "timeout") handler.Logout.SetException(new TaskCanceledException("Timeout"));
            else handler.Logout.SetResult(new HttpResponseMessage(
                outcome == "success" ? HttpStatusCode.NoContent : HttpStatusCode.ServiceUnavailable));
            await signOut;

            if (outcome == "success")
                Assert.Equal("Signed out of the telemetry service.", viewModel.OpenRemoteProvisioningStatusMessage);
            else
                Assert.Equal("Signed out on this computer, but the telemetry service couldn't end the session. It may still be active on the service.",
                    viewModel.OpenRemoteProvisioningStatusMessage);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SignOutReportsBothSavedSignInAndRemoteSessionFailures()
    {
        string directory = CreateTestDirectory();
        try
        {
            using var handler = new SignOutHandler();
            using var client = new HttpClient(handler);
            using var api = new ProvisioningApiClient(client, new Uri("https://keycloak.example/auth/"));
            Assert.True(await api.TryResumeAsync("realm", "user", "refresh"));
            using var viewModel = new MainWindowViewModel(new TestAppCloser(null), new MainWindowViewModelDependencies(
                provisioningApiClient: api,
                provisioningSessionStore: new ProvisioningSessionStore(directory),
                ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "ioflow.json"))));
            viewModel.IsOpenRemoteSignedIn = true;

            Task signOut = viewModel.SignOutOpenRemoteCommand.ExecuteAsync(null);
            handler.Logout.SetException(new TaskCanceledException("Timeout"));
            await signOut;

            Assert.Equal("Signed out on this computer, but the saved sign-in couldn't be removed and the service session may still be active. Cortex may sign you in again next time.",
                viewModel.OpenRemoteProvisioningStatusMessage);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task SignOutReportsSavedSessionRemovalFailure()
    {
        string directory = CreateTestDirectory();
        try
        {
            using var viewModel = new MainWindowViewModel(new TestAppCloser(null), new MainWindowViewModelDependencies(
                provisioningSessionStore: new ProvisioningSessionStore(directory),
                ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "ioflow.json"))));
            viewModel.IsOpenRemoteSignedIn = true;

            await viewModel.SignOutOpenRemoteCommand.ExecuteAsync(null);

            Assert.False(viewModel.IsOpenRemoteSignedIn);
            Assert.Equal("Signed out on this computer, but the saved sign-in couldn't be removed. Cortex may sign you in again next time.",
                viewModel.OpenRemoteProvisioningStatusMessage);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RestoreConfigurationBackup_StagesSettingsWithoutSendingThemToController()
    {
        string directory = CreateTestDirectory();
        try
        {
            var backupStore = new ConfigurationBackupStore(Path.Combine(directory, "backups"));
            var backupSettings = new DataStructures();
            backupSettings.ChannelsStaticData[0].RetryCount = 3;
            Assert.True(backupStore.TryCreateBackup(ConfigFileSerializer.SerializeSettings(backupSettings), out string? error), error);

            var closer = new TestAppCloser(backupStore.GetBackups()[0]);
            var dependencies = new MainWindowViewModelDependencies(
                ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "ioflow.json")),
                configurationBackupStore: backupStore);
            using var viewModel = new MainWindowViewModel(closer, dependencies);
            SetField(viewModel, "_controllerConfigBaseline", new DataStructures());
            viewModel.SettingsDataView.CellularParamsStaticData.APNPassword = "current-apn";
            viewModel.SettingsDataView.CellularParamsStaticData.MQTTPassword = "current-mqtt";
            viewModel.IsConnected = true;
            viewModel.CommsEstablished = true;
            viewModel.IsInitialSnapshotLoaded = true;

            await viewModel.RestoreConfigurationBackupCommand.ExecuteAsync(null);

            Assert.Equal(3, viewModel.SettingsDataView.ChannelsStaticData[0].RetryCount);
            Assert.Equal("current-apn", viewModel.SettingsDataView.CellularParamsStaticData.APNPassword);
            Assert.Equal("current-mqtt", viewModel.SettingsDataView.CellularParamsStaticData.MQTTPassword);
            Assert.True(viewModel.HasPendingConfigChanges);
            Assert.True(viewModel.CanSendConfig);
            Assert.Null(ReadField(viewModel, "_portService"));
            Assert.NotNull(closer.ShownBackups);
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
    public async Task RestoreConfigurationBackup_RejectsInvalidCrossReferencesWithoutChanges()
    {
        string directory = CreateTestDirectory();
        try
        {
            var backupStore = new ConfigurationBackupStore(Path.Combine(directory, "backups"));
            const string content = """{"CanMessages":[{"Index":0,"Id":151,"Dlc":1,"Flags":5,"TxTrigger":255}],"CanSignals":[{"Index":0,"MessageIndex":0,"StartBit":8,"BitLength":1,"Flags":1,"Factor":1}]}""";
            Assert.True(backupStore.TryCreateBackup(content, out string? error), error);
            var closer = new TestAppCloser(backupStore.GetBackups()[0]);
            using var viewModel = new MainWindowViewModel(closer, new MainWindowViewModelDependencies(
                ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "ioflow.json")),
                configurationBackupStore: backupStore));
            SetField(viewModel, "_controllerConfigBaseline", new DataStructures());
            viewModel.IsConnected = true;
            viewModel.CommsEstablished = true;
            viewModel.IsInitialSnapshotLoaded = true;
            var originalSettings = viewModel.SettingsDataView;
            string originalGraph = System.Text.Json.JsonSerializer.Serialize(viewModel.IoFlowGraph.CreateSnapshot());

            await viewModel.RestoreConfigurationBackupCommand.ExecuteAsync(null);

            Assert.Same(originalSettings, viewModel.SettingsDataView);
            Assert.Equal(originalGraph, System.Text.Json.JsonSerializer.Serialize(viewModel.IoFlowGraph.CreateSnapshot()));
            Assert.Null(ReadField(viewModel, "_portService"));
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
    public void CreateControllerSessionBackup_CreatesOnlyOneSnapshotPerSession()
    {
        string directory = CreateTestDirectory();
        try
        {
            var store = new ConfigurationBackupStore(directory);
            using var viewModel = new MainWindowViewModel(
                new TestAppCloser(null),
                new MainWindowViewModelDependencies(
                    ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "ioflow.json")),
                    configurationBackupStore: store));
            var baseline = new DataStructures();
            baseline.CellularParamsStaticData.APNPassword = "backup-apn-secret";
            baseline.CellularParamsStaticData.MQTTPassword = "backup-mqtt-secret";
            SetField(viewModel, "_controllerConfigBaseline", baseline);

            InvokePrivate(viewModel, "CreateControllerSessionBackup");
            InvokePrivate(viewModel, "CreateControllerSessionBackup");

            var backup = Assert.Single(store.GetBackups());
            string content = store.ReadBackup(backup);
            Assert.DoesNotContain("backup-apn-secret", content);
            Assert.DoesNotContain("backup-mqtt-secret", content);
            Assert.DoesNotContain("APNPassword", content);
            Assert.DoesNotContain("MQTTPassword", content);

            viewModel.IsInitialSnapshotLoaded = true;
            viewModel.IsInitialSnapshotLoaded = false;
            InvokePrivate(viewModel, "CreateControllerSessionBackup");

            Assert.Equal(2, store.GetBackups().Count);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string CreateTestDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "Cortex-configuration-backup-viewmodel", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void SetField(object instance, string name, object? value)
    {
        GetFieldInfo(instance, name).SetValue(instance, value);
    }

    private static object? ReadField(object instance, string name)
    {
        return GetFieldInfo(instance, name).GetValue(instance);
    }

    private static FieldInfo GetFieldInfo(object instance, string name)
    {
        return instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    private static void InvokePrivate(object instance, string name)
    {
        instance.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(instance, null);
    }

    private sealed class SignOutHandler : HttpMessageHandler
    {
        public TaskCompletionSource<HttpResponseMessage> Logout { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/token", StringComparison.Ordinal))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"access_token":"access","refresh_token":"refresh","expires_in":300}""")
                });
            return Logout.Task;
        }
    }

    private sealed class TestAppCloser(ConfigurationBackupEntry? selectedBackup) : IAppCloser
    {
        public IReadOnlyList<ConfigurationBackupEntry>? ShownBackups { get; private set; }

        public void CloseApp() { }
        public Task<string?> OpenPdmFileContentAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseLocalLogFilePathAsync(string initialDirectory) => Task.FromResult<string?>(null);
        public Task<string?> BrowseDbcFilePathAsync() => Task.FromResult<string?>(null);
        public Task<bool> SavePdmFileContentAsync(string content) => Task.FromResult(false);
        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL") => Task.FromResult(false);
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
        public Task<LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync() => Task.FromResult<LocalFirmwareUpdateSelection?>(null);
        public Task ShowAboutAsync() => Task.CompletedTask;

        public Task<ConfigurationBackupEntry?> ShowConfigurationBackupDialogAsync(IReadOnlyList<ConfigurationBackupEntry> backups)
        {
            ShownBackups = backups;
            return Task.FromResult(selectedBackup);
        }

        public Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel) => Task.CompletedTask;
        public Task<PinSetupDialogResult?> ShowPinSetupWizardAsync(bool isPinAlreadyEnabled) => Task.FromResult<PinSetupDialogResult?>(null);
        public Task<bool> ShowRecoveryCodeAsync(string recoveryCode) => Task.FromResult(false);
        public Task<PinUnlockDialogResult?> ShowPinUnlockDialogAsync(bool allowRecovery, uint retryAfterMs, string? statusMessage) => Task.FromResult<PinUnlockDialogResult?>(null);
    }
}
