using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using Cortex.Views;
using System.Reflection;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class DbcImportFilterTests
{
    [AvaloniaFact]
    public async Task TypingFiltersLoadedSignalsClearsHiddenSelectionsAndAppliesToNewFiles()
    {
        string path = Path.GetTempFileName();
        var viewModel = new MainWindowViewModel(new TestAppCloser(path));
        System.Timers.Timer[] timers = new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" }
            .Select(name => Assert.IsType<System.Timers.Timer>(typeof(MainWindowViewModel)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel))).ToArray();
        foreach (var timer in timers)
        {
            timer.Stop();
        }

        var view = new CanMessagesTabView { DataContext = viewModel };
        var window = new Window { Width = 1200, Height = 900, Content = view };
        try
        {
            await File.WriteAllTextAsync(path, """
                BO_ 100 Engine_Status: 8 ECU
                 SG_ RPM : 0|16@1+ (1,0) [0|65535] "rpm" ECU
                 SG_ Temperature : 16|8@1+ (1,0) [0|255] "C" ECU
                BO_ 200 Vehicle_Status: 8 ECU
                 SG_ Speed : 0|16@1+ (1,0) [0|65535] "km/h" ECU
                """);
            window.Show();
            await viewModel.ImportDbcFileCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var filter = view.FindControl<TextBox>("DbcSignalNameFilterBox")!;
            var grid = view.FindControl<DataGrid>("ImportedDbcSignalsGrid")!;
            Assert.Same(viewModel.FilteredImportedDbcSignals, grid.ItemsSource);
            Assert.Equal(3, grid.ItemsSource.Cast<DbcCanSignalDefinition>().Count());
            grid.SelectedItem = viewModel.ImportedDbcSignals.Last();
            Assert.NotEmpty(viewModel.SelectedImportedDbcSignals);

            filter.Text = "rPm";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("rPm", viewModel.DbcSignalNameFilter);
            Assert.Equal("RPM", Assert.Single(grid.ItemsSource.Cast<DbcCanSignalDefinition>()).Name);
            Assert.Empty(viewModel.SelectedImportedDbcSignals);
            Assert.Null(viewModel.SelectedImportedDbcSignal);

            filter.Text = "not found";
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(grid.ItemsSource);

            filter.Text = string.Empty;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(3, grid.ItemsSource.Cast<DbcCanSignalDefinition>().Count());

            filter.Text = "rpm";
            Dispatcher.UIThread.RunJobs();
            await viewModel.ImportDbcFileCommand.ExecuteAsync(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("RPM", Assert.Single(grid.ItemsSource.Cast<DbcCanSignalDefinition>()).Name);
            Assert.Equal("RPM", viewModel.SelectedImportedDbcSignal?.Name);
            Assert.Equal(3, viewModel.ImportedDbcSignals.Count);
        }
        finally
        {
            window.Close();
            foreach (var timer in timers)
            {
                timer.Dispose();
            }
            File.Delete(path);
        }
    }

    private sealed class TestAppCloser(string path) : IAppCloser
    {
        public void CloseApp() { }
        public Task<string?> OpenPdmFileContentAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseLocalLogFilePathAsync(string initialDirectory) => Task.FromResult<string?>(null);
        public Task<string?> BrowseDbcFilePathAsync() => Task.FromResult<string?>(path);
        public Task<bool> SavePdmFileContentAsync(string content) => Task.FromResult(true);
        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL") => Task.FromResult(true);
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
        public Task<LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync() => Task.FromResult<LocalFirmwareUpdateSelection?>(null);
        public Task ShowAboutAsync() => Task.CompletedTask;
        public Task<ConfigurationBackupEntry?> ShowConfigurationBackupDialogAsync(IReadOnlyList<ConfigurationBackupEntry> backups) => Task.FromResult<ConfigurationBackupEntry?>(null);
        public Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel) => Task.CompletedTask;
        public Task<PinSetupDialogResult?> ShowPinSetupWizardAsync(bool isPinAlreadyEnabled) => Task.FromResult<PinSetupDialogResult?>(null);
        public Task<bool> ShowRecoveryCodeAsync(string recoveryCode) => Task.FromResult(true);
        public Task<PinUnlockDialogResult?> ShowPinUnlockDialogAsync(bool allowRecovery, uint retryAfterMs, string? statusMessage) => Task.FromResult<PinUnlockDialogResult?>(null);
    }
}
