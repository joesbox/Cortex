using Cortex.Models;
using Cortex.ViewModels;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Cortex.Services
{
    public interface IAppCloser
    {
        void CloseApp();

        Task<string?> OpenPdmFileContentAsync();

        Task<string?> BrowseLocalLogFilePathAsync(string initialDirectory);

        Task<string?> BrowseDbcFilePathAsync();

        Task<bool> SavePdmFileContentAsync(string content);

        Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL");

        Task OpenUrlAsync(string url);

        Task<LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync();

        Task ShowAboutAsync();

        Task<ConfigurationBackupEntry?> ShowConfigurationBackupDialogAsync(IReadOnlyList<ConfigurationBackupEntry> backups);

        Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel);

        Task<PinSetupDialogResult?> ShowPinSetupWizardAsync(bool isPinAlreadyEnabled);

        Task<bool> ShowRecoveryCodeAsync(string recoveryCode);

        Task<PinUnlockDialogResult?> ShowPinUnlockDialogAsync(bool allowRecovery, uint retryAfterMs, string? statusMessage);
    }
}
