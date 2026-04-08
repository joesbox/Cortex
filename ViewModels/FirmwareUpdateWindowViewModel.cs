using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Cortex.ViewModels
{
    public partial class FirmwareUpdateWindowViewModel : ObservableObject
    {
        private readonly Func<IProgress<FirmwareUpdateProgressInfo>, CancellationToken, Task> _installAction;
        private CancellationTokenSource? _updateCancellationTokenSource;

        public FirmwareUpdateWindowViewModel(
            string availableVersionText,
            Func<IProgress<FirmwareUpdateProgressInfo>, CancellationToken, Task> installAction)
        {
            _installAction = installAction;

            AvailableVersionText = availableVersionText;
            WarningMessage = "WARNING - Updating the firmware on the PDM will disable all outputs. Ensure the vehicle is stationary and in a safe state before proceeding.";
            StatusMessage = "Ready to install firmware update.";
            ProgressPercent = 0;
            CanCancelDuringUpdate = true;
            IsUpdateComplete = false;
        }

        public event Action? CloseRequested;

        [ObservableProperty]
        private string availableVersionText = string.Empty;

        [ObservableProperty]
        private string warningMessage = string.Empty;

        [ObservableProperty]
        private string statusMessage = string.Empty;

        [ObservableProperty]
        private double progressPercent;

        [ObservableProperty]
        private bool isUpdateInProgress;

        [ObservableProperty]
        private bool canCancelDuringUpdate;

        [ObservableProperty]
        private bool isUpdateComplete;

        public bool CanStartUpdate => !IsUpdateInProgress && !IsUpdateComplete;

        public bool CanUseCancelButton => (!IsUpdateInProgress) || CanCancelDuringUpdate || IsUpdateComplete;

        public string CancelButtonText => IsUpdateComplete ? "Close" : "Cancel";

        partial void OnIsUpdateInProgressChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStartUpdate));
            OnPropertyChanged(nameof(CanUseCancelButton));
            OnPropertyChanged(nameof(CancelButtonText));
        }

        partial void OnCanCancelDuringUpdateChanged(bool value)
        {
            OnPropertyChanged(nameof(CanUseCancelButton));
        }

        partial void OnIsUpdateCompleteChanged(bool value)
        {
            OnPropertyChanged(nameof(CanStartUpdate));
            OnPropertyChanged(nameof(CanUseCancelButton));
            OnPropertyChanged(nameof(CancelButtonText));
        }

        [RelayCommand]
        private async Task UpdateNowAsync()
        {
            if (!CanStartUpdate)
            {
                return;
            }

            _updateCancellationTokenSource = new CancellationTokenSource();
            IsUpdateInProgress = true;
            CanCancelDuringUpdate = true;
            IsUpdateComplete = false;

            var progress = new Progress<FirmwareUpdateProgressInfo>(info =>
            {
                StatusMessage = info.StatusMessage;
                ProgressPercent = info.ProgressPercent;
                CanCancelDuringUpdate = info.CanCancel;
            });

            try
            {
                await _installAction(progress, _updateCancellationTokenSource.Token);
                StatusMessage = "Firmware update complete.";
                ProgressPercent = 100;
                IsUpdateComplete = true;
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Firmware update cancelled.";
                ProgressPercent = 0;
            }
            catch (Exception ex)
            {
                StatusMessage = GetFriendlyErrorMessage(ex);
            }
            finally
            {
                IsUpdateInProgress = false;
                CanCancelDuringUpdate = false;
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            if (IsUpdateComplete || !IsUpdateInProgress)
            {
                CloseRequested?.Invoke();
                return;
            }

            if (CanCancelDuringUpdate)
            {
                StatusMessage = "Cancelling firmware update...";
                _updateCancellationTokenSource?.Cancel();
            }
        }

        private static string GetFriendlyErrorMessage(Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(ex.Message))
            {
                return ex.Message;
            }

            return "The firmware update couldn't be completed. Check the connection and try again.";
        }
    }
}