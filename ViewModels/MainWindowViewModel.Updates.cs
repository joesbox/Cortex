using Avalonia.Media;
using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using Cortex.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cortex.ViewModels
{
    public partial class MainWindowViewModel
    {
        private readonly AppUpdateService _appUpdateService = new();
        private readonly FirmwareUpdateService _firmwareUpdateService = new();
        private CancellationTokenSource? _applicationUpdateCheckCts;
        private AppReleaseInfo? _latestApplicationRelease;
        private AppReleaseInfo? _availableApplicationRelease;
        private CancellationTokenSource? _firmwareCheckCts;
        private FirmwareReleaseInfo? _latestFirmwareRelease;
        private FirmwareReleaseInfo? _availableFirmwareRelease;

        private static readonly IBrush FirmwareIdleBrush = new SolidColorBrush(Color.Parse("#40404A"));
        private static readonly IBrush FirmwareAvailableBrush = new SolidColorBrush(Color.Parse("#2C8F57"));

        public bool CanOpenFirmwareUpdateDialog =>
            IsConnected &&
            CommsEstablished &&
            !IsCheckingFirmwareUpdate &&
            IsFirmwareUpdateAvailable &&
            _portService != null;

        public bool CanOpenLocalFirmwareUpdateDialog =>
            IsConnected &&
            CommsEstablished &&
            !IsCheckingFirmwareUpdate &&
            _portService != null;

        public bool CanCheckForApplicationUpdate => !IsCheckingApplicationUpdate;

        public string CurrentApplicationVersionDisplay => string.IsNullOrWhiteSpace(CurrentApplicationVersion)
            ? "Unknown"
            : CurrentApplicationVersion;

        public bool HasLatestApplicationGitHubLink => !string.IsNullOrWhiteSpace(_latestApplicationRelease?.GitHubUrl);

        public string LatestApplicationGitHubLinkText => _availableApplicationRelease != null
            ? $"Download Cortex {_availableApplicationRelease.Version}"
            : "Open Cortex releases";

        public string CurrentFirmwareVersionDisplay => string.IsNullOrWhiteSpace(ControllerFirmwareVersion)
            ? "Unknown"
            : ControllerFirmwareVersion;

        public bool HasLatestFirmwareGitHubLink => !string.IsNullOrWhiteSpace(_latestFirmwareRelease?.GitHubUrl);

        public string LatestFirmwareGitHubLinkText => string.IsNullOrWhiteSpace(_latestFirmwareRelease?.Version)
            ? "Latest firmware on GitHub"
            : $"Latest on GitHub: {_latestFirmwareRelease.Version}";

        partial void OnIsCheckingApplicationUpdateChanged(bool value)
        {
            OnPropertyChanged(nameof(CanCheckForApplicationUpdate));
            UpdateApplicationUpdateButtonPresentation();
        }

        partial void OnIsApplicationUpdateAvailableChanged(bool value)
        {
            UpdateApplicationUpdateButtonPresentation();
        }

        partial void OnCurrentApplicationVersionChanged(string value)
        {
            OnPropertyChanged(nameof(CurrentApplicationVersionDisplay));
        }

        private void UpdateFirmwareButtonPresentation()
        {
            UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForFirmware(
                IsConnected,
                CommsEstablished,
                IsCheckingFirmwareUpdate,
                ControllerFirmwareVersion,
                _availableFirmwareRelease?.Version);

            FirmwareUpdateButtonText = state.Text;
            FirmwareUpdateButtonBackground = state.IsHighlighted ? FirmwareAvailableBrush : FirmwareIdleBrush;
            OnPropertyChanged(nameof(CanOpenFirmwareUpdateDialog));
        }

        private void UpdateApplicationUpdateButtonPresentation()
        {
            UpdateButtonPresentation state = UpdateButtonPresentationFactory.ForApplication(
                IsCheckingApplicationUpdate,
                _availableApplicationRelease?.Version);

            ApplicationUpdateButtonText = state.Text;
            ApplicationUpdateButtonBackground = state.IsHighlighted ? FirmwareAvailableBrush : FirmwareIdleBrush;
        }

        private void NotifyApplicationUpdateInfoChanged()
        {
            OnPropertyChanged(nameof(CurrentApplicationVersionDisplay));
            OnPropertyChanged(nameof(HasLatestApplicationGitHubLink));
            OnPropertyChanged(nameof(LatestApplicationGitHubLinkText));
        }

        private async Task RefreshApplicationUpdateStateAsync(bool manualCheck = false)
        {
            _applicationUpdateCheckCts?.Cancel();
            var checkCts = new CancellationTokenSource();
            _applicationUpdateCheckCts = checkCts;

            IsCheckingApplicationUpdate = true;
            _availableApplicationRelease = null;
            IsApplicationUpdateAvailable = false;
            ApplicationUpdateStatusMessage = "Checking GitHub releases...";
            NotifyApplicationUpdateInfoChanged();

            try
            {
                _latestApplicationRelease = await _appUpdateService.GetLatestReleaseAsync(checkCts.Token);
                if (_applicationUpdateCheckCts != checkCts || checkCts.IsCancellationRequested)
                {
                    return;
                }

                if (_latestApplicationRelease == null)
                {
                    ApplicationUpdateStatusMessage = "No Cortex releases were found on GitHub.";
                    NotifyApplicationUpdateInfoChanged();
                    return;
                }

                _availableApplicationRelease = AppUpdateService.IsVersionNewerThanCurrent(_latestApplicationRelease.Version, CurrentApplicationVersion)
                    ? _latestApplicationRelease
                    : null;
                IsApplicationUpdateAvailable = _availableApplicationRelease != null;
                ApplicationUpdateStatusMessage = _availableApplicationRelease != null
                    ? $"Cortex {_availableApplicationRelease.Version} is available to download."
                    : $"Cortex is up to date. Latest release: {_latestApplicationRelease.Version}.";
                NotifyApplicationUpdateInfoChanged();

                if (_availableApplicationRelease != null)
                {
                    AddLog($"Cortex update available: {_availableApplicationRelease.Version} (current {CurrentApplicationVersion}).");
                    bool openReleasePage = await _appCloser.ConfirmAsync(
                        "Cortex Update Available",
                        $"Cortex {_availableApplicationRelease.Version} is available to download. Open the GitHub release page now?",
                        "OPEN RELEASE",
                        "LATER");

                    if (openReleasePage)
                    {
                        await OpenLatestApplicationOnGitHub();
                    }
                }
                else if (manualCheck)
                {
                    AddLog($"Cortex is up to date ({CurrentApplicationVersion}).");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _latestApplicationRelease = null;
                _availableApplicationRelease = null;
                IsApplicationUpdateAvailable = false;
                ApplicationUpdateStatusMessage = "Couldn't check Cortex releases right now.";
                NotifyApplicationUpdateInfoChanged();
                AddLog($"Cortex update check failed: {ex.Message}");
            }
            finally
            {
                if (_applicationUpdateCheckCts == checkCts)
                {
                    IsCheckingApplicationUpdate = false;
                }
            }
        }

        private void NotifyFirmwareInfoChanged()
        {
            OnPropertyChanged(nameof(CurrentFirmwareVersionDisplay));
            OnPropertyChanged(nameof(HasLatestFirmwareGitHubLink));
            OnPropertyChanged(nameof(LatestFirmwareGitHubLinkText));
        }

        private void ResetFirmwareUpdateState()
        {
            _firmwareCheckCts?.Cancel();
            _firmwareCheckCts = null;
            _latestFirmwareRelease = null;
            _availableFirmwareRelease = null;
            IsFirmwareUpdateAvailable = false;
            IsCheckingFirmwareUpdate = false;
            ControllerFirmwareVersion = string.Empty;
            NotifyFirmwareInfoChanged();
            UpdateFirmwareButtonPresentation();
        }

        private async Task RefreshFirmwareUpdateStateAsync()
        {
            if (!IsConnected || !CommsEstablished || _portService == null)
            {
                ResetFirmwareUpdateState();
                return;
            }

            _firmwareCheckCts?.Cancel();
            var checkCts = new CancellationTokenSource();
            _firmwareCheckCts = checkCts;

            IsCheckingFirmwareUpdate = true;
            _availableFirmwareRelease = null;
            IsFirmwareUpdateAvailable = false;
            UpdateFirmwareButtonPresentation();

            try
            {
                string? controllerVersion = await _portService.RequestFirmwareVersionAsync();
                if (_firmwareCheckCts != checkCts || checkCts.IsCancellationRequested)
                {
                    return;
                }

                ControllerFirmwareVersion = controllerVersion?.Trim() ?? string.Empty;
                _latestFirmwareRelease = await _firmwareUpdateService.GetLatestReleaseAsync(checkCts.Token);
                if (_firmwareCheckCts != checkCts || checkCts.IsCancellationRequested)
                {
                    return;
                }

                _availableFirmwareRelease = FirmwareUpdateService.IsVersionNewerThanCurrent(_latestFirmwareRelease?.Version, ControllerFirmwareVersion)
                    ? _latestFirmwareRelease
                    : null;
                IsFirmwareUpdateAvailable = _availableFirmwareRelease != null;
                NotifyFirmwareInfoChanged();
                if (_availableFirmwareRelease != null)
                {
                    AddLog($"Firmware update available: {_availableFirmwareRelease.Version} (controller {ControllerFirmwareVersion}).");
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _latestFirmwareRelease = null;
                _availableFirmwareRelease = null;
                IsFirmwareUpdateAvailable = false;
                NotifyFirmwareInfoChanged();
                AddLog($"Firmware update check failed: {ex.Message}");
            }
            finally
            {
                if (_firmwareCheckCts == checkCts)
                {
                    IsCheckingFirmwareUpdate = false;
                    UpdateFirmwareButtonPresentation();
                }
            }
        }

        [RelayCommand]
        private async Task CheckForApplicationUpdate()
        {
            await RefreshApplicationUpdateStateAsync(manualCheck: true);
        }

        [RelayCommand]
        private async Task OpenLatestApplicationOnGitHub()
        {
            string gitHubUrl = _availableApplicationRelease?.GitHubUrl
                ?? _latestApplicationRelease?.GitHubUrl
                ?? AppUpdateService.CortexGitHubReleasesUrl;

            try
            {
                await _appCloser.OpenUrlAsync(gitHubUrl);
            }
            catch (Exception ex)
            {
                AddLog($"Couldn't open Cortex release link: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task OpenFirmwareUpdate()
        {
            if (!CanOpenFirmwareUpdateDialog || _portService == null || _availableFirmwareRelease == null)
            {
                return;
            }

            var release = _availableFirmwareRelease;
            var dialogViewModel = new FirmwareUpdateWindowViewModel(
                $"Firmware {release.Version} available",
                (progress, cancellationToken) => _firmwareUpdateService.InstallReleaseAsync(release, _portService, progress, cancellationToken));
            await _appCloser.ShowFirmwareUpdateDialogAsync(dialogViewModel);

            if (dialogViewModel.IsUpdateComplete)
            {
                ControllerFirmwareVersion = release.Version;
                _availableFirmwareRelease = null;
                IsFirmwareUpdateAvailable = false;
                UpdateFirmwareButtonPresentation();
                AddLog($"Firmware update installed: {release.Version}");
            }
        }

        [RelayCommand]
        private async Task OpenLocalFirmwareUpdate()
        {
            if (!CanOpenLocalFirmwareUpdateDialog || _portService == null)
            {
                return;
            }

            LocalFirmwareUpdateSelection? selection;
            try
            {
                selection = await _appCloser.BrowseLocalFirmwareUpdateFilesAsync();
            }
            catch (Exception ex)
            {
                AddLog(ex.Message);
                return;
            }

            if (selection == null)
            {
                return;
            }

            var dialogViewModel = new FirmwareUpdateWindowViewModel(
                $"Local firmware: {selection.DisplayName}",
                (progress, cancellationToken) => _firmwareUpdateService.InstallLocalFilesAsync(selection, _portService, progress, cancellationToken));
            await _appCloser.ShowFirmwareUpdateDialogAsync(dialogViewModel);

            if (dialogViewModel.IsUpdateComplete)
            {
                AddLog($"Local firmware update installed from {selection.DisplayName}");
            }
        }

        [RelayCommand]
        private async Task OpenLatestFirmwareOnGitHub()
        {
            string? gitHubUrl = _latestFirmwareRelease?.GitHubUrl;
            if (string.IsNullOrWhiteSpace(gitHubUrl))
            {
                return;
            }

            try
            {
                await _appCloser.OpenUrlAsync(gitHubUrl);
            }
            catch (Exception ex)
            {
                AddLog($"Couldn't open firmware link: {ex.Message}");
            }
        }

        partial void OnIsCheckingFirmwareUpdateChanged(bool value)
        {
            OnPropertyChanged(nameof(CanOpenFirmwareUpdateDialog));
            OnPropertyChanged(nameof(CanOpenLocalFirmwareUpdateDialog));
            UpdateFirmwareButtonPresentation();
        }

        partial void OnIsFirmwareUpdateAvailableChanged(bool value)
        {
            OnPropertyChanged(nameof(CanOpenFirmwareUpdateDialog));
            UpdateFirmwareButtonPresentation();
        }

        partial void OnControllerFirmwareVersionChanged(string value)
        {
            OnPropertyChanged(nameof(CurrentFirmwareVersionDisplay));
        }
    }
}
