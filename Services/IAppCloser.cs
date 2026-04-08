using Cortex.Models;
using Cortex.ViewModels;
using System.Threading.Tasks;

namespace Cortex.Services
{
    public interface IAppCloser
    {
        void CloseApp();

        Task<string?> OpenPdmFileContentAsync();

        Task<string?> BrowseLocalLogFilePathAsync(string initialDirectory);

        Task<bool> SavePdmFileContentAsync(string content);

        Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL");

        Task OpenUrlAsync(string url);

        Task<LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync();

        Task ShowAboutAsync();

        Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel);
    }
}
