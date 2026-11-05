using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using Cortex.Services;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Ports;
using System.Linq;
using System.Threading.Tasks;

namespace Cortex.ViewModels
{
    /// <summary>
    /// Partial class containing serial port communication and connection management functionality.
    /// Handles port enumeration, connection establishment, communication handshake, and data reception.
    /// </summary>
    public partial class MainWindowViewModel
    {
        /// <summary>
        /// Loads available serial ports from the system.
        /// Preserves the current selection if it's still available.
        /// </summary>
        private void LoadSerialPorts()
        {
            string? currentSelection = SelectedSerialPort;
            var availablePorts = SerialPort.GetPortNames();
            SerialPorts = new ObservableCollection<string>(availablePorts);

            if (!string.IsNullOrWhiteSpace(currentSelection) && availablePorts.Contains(currentSelection, StringComparer.Ordinal))
            {
                SelectedSerialPort = currentSelection;
                return;
            }

            SelectedSerialPort = SerialPorts.FirstOrDefault();
        }

        /// <summary>
        /// Refreshes the list of available serial ports.
        /// </summary>
        [RelayCommand]
        private void RefreshSerialPorts()
        {
            LoadSerialPorts();
        }

        /// <summary>
        /// Establishes a connection to the PDM via the specified serial port.
        /// Initializes the port service and begins communication handshake.
        /// </summary>
        [RelayCommand]
        private void Connect(string? selectedPort)
        {
            string? portName = string.IsNullOrWhiteSpace(selectedPort)
                ? SelectedSerialPort
                : selectedPort;

            if (string.IsNullOrWhiteSpace(portName))
            {
                AddLog("Select a PDM connection before connecting.");
                return;
            }

            _portService = new SerialPortService(portName);
            _portService.DataUpdated += _portService_DataUpdated;
            _portService.ConfigurationSaved += ConfigSaved;
            _portService.ConfigurationSaveCompleted += ConfigSaveCompleted;
            refreshStaticData = true;
            IsInitialSnapshotLoaded = false;
            IsConnected = _portService.Open();

            if (IsConnected)
            {
                Debug.WriteLine($"Connecting to PDM on {portName}.");
                AddLog("Connecting to PDM...");
                _portService.InitComms();
                UpdateMathStatusPollingSelection();
                UpdateCustomCanStatusPollingSelection();
                return;
            }

            Debug.WriteLine(_portService.LastError ?? $"Failed to open serial port {portName}.");
            AddLog("Couldn't connect to the PDM. Check the cable and try again.");
            _portService.DataUpdated -= _portService_DataUpdated;
            _portService.ConfigurationSaved -= ConfigSaved;
            _portService.ConfigurationSaveCompleted -= ConfigSaveCompleted;
            _portService = null;
        }

        /// <summary>
        /// Manages the communication establishment and live data polling.
        /// Called periodically via the comms timer to maintain connection state.
        /// </summary>
        private void HandleComms()
        {
            if (IsConnected)
            {
                if (!CommsEstablished)
                {
                    if (_portService != null)
                    {
                        CommsEstablished = _portService.InitComms();
                        if (CommsEstablished)
                        {
                            UpdateCustomCanStatusPollingSelection();
                            StartBackgroundTask(InitializeSecureSessionWorkflowAsync(), "PIN lock secure session workflow");
                        }
                    }
                }
                else if (_portService != null)
                {
                    _portService.EnsureLiveRequestPolling();

                    if (_portService.ShouldSendSecureKeepalive() && !_isSecureKeepaliveInProgress)
                    {
                        _isSecureKeepaliveInProgress = true;
                        StartBackgroundTask(SendSecureKeepaliveAsync(), "Secure session keepalive");
                    }

                    if (!IsConnectedPdmRegistered)
                    {
                        return;
                    }

                    if (!IsLogBusy && DateTime.UtcNow >= _nextCellularHealthPollUtc)
                    {
                        ScheduleCellularHealthPoll(immediate: false);
                        StartBackgroundTask(PollCellularHealthStatusAsync(), "Cellular health poll");
                    }
                }
            }
        }

        private async Task InitializeSecureSessionWorkflowAsync()
        {
            if (_portService == null || !IsConnected || !CommsEstablished || _isSecureUnlockWorkflowActive)
            {
                return;
            }

            _isSecureUnlockWorkflowActive = true;
            try
            {
                SecureInfoResponse? secureInfo = await _portService.GetSecureInfoAsync();
                if (secureInfo == null || secureInfo.ProtocolVersion != Constants.SECURE_PROTOCOL_VERSION)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        IsPinLockSupported = false;
                        IsPinLockEnabled = false;
                        PinLockRetryAfterMs = 0;
                        PinLockStatusMessage = "Controller does not report secure PIN protocol support.";
                    });

                    _portService.RequestStaticSnapshot();
                    return;
                }

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    IsPinLockSupported = true;
                    IsPinLockEnabled = secureInfo.PinEnabled;
                    PinLockRetryAfterMs = secureInfo.RetryAfterMs;
                    PinLockStatusMessage = secureInfo.StorageFault
                        ? "PIN lock storage fault detected on controller."
                        : secureInfo.PinEnabled
                            ? "PIN lock is enabled on this PDM."
                            : "PIN lock is not enabled on this PDM.";
                });

                if (!secureInfo.PinEnabled)
                {
                    _portService.RequestStaticSnapshot();
                    return;
                }

                bool secureReady = await _portService.EnsureSecureSessionAsync();
                if (!secureReady)
                {
                    await RefreshPinLockStateAsync();
                    _portService.RequestStaticSnapshot();
                    return;
                }

                await RefreshPinLockStateAsync();
                if (!IsPinLockEnabled || _portService.IsSecureSessionAuthorized)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        PinLockStatusMessage = IsPinLockEnabled
                            ? "PIN lock enabled and session unlocked."
                            : "PIN lock is not enabled on this PDM.";
                    });

                    _portService.RequestStaticSnapshot();
                    return;
                }

                string? unlockStatus = null;
                while (IsConnected && CommsEstablished && _portService != null && IsPinLockEnabled && !_portService.IsSecureSessionAuthorized)
                {
                    PinUnlockDialogResult? dialogResult = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                        await _appCloser.ShowPinUnlockDialogAsync(
                            allowRecovery: true,
                            retryAfterMs: _portService.PinRetryAfterMs,
                            statusMessage: unlockStatus));

                    if (dialogResult == null || !dialogResult.Confirmed)
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            PinLockStatusMessage = "PIN lock remains active. Unlock was cancelled.";
                        });
                        return;
                    }

                    SecureManagementResponse? unlockResponse = await _portService.SecureUnlockAsync(dialogResult.Pin);
                    if (unlockResponse != null && unlockResponse.Result == SecureControlResult.Ok && unlockResponse.IsAuthorized)
                    {
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            PinLockRetryAfterMs = 0;
                            PinLockStatusMessage = "PIN accepted. PDM unlocked for this session.";
                            OnPropertyChanged(nameof(CanAccessOperationalTabs));
                            OnPropertyChanged(nameof(CanManagePinLock));
                        });

                        await RefreshPinLockStateAsync();
                        _portService.RequestStaticSnapshot();
                        AddLog("Secure session unlocked with PIN.");
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(dialogResult.RecoveryKey) && !string.IsNullOrWhiteSpace(dialogResult.NewPin))
                    {
                        PinSetupResult recovery = await _portService.SecureRecoverAsync(dialogResult.RecoveryKey, dialogResult.NewPin);
                        if (recovery.Success)
                        {
                            bool acknowledged = await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
                                await _appCloser.ShowRecoveryCodeAsync(recovery.RecoveryKey ?? string.Empty));
                            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                            {
                                PinLockStatusMessage = acknowledged
                                    ? "Recovery successful. Session unlocked with new PIN."
                                    : "Recovery successful. Save the new recovery code.";
                                PinLockRetryAfterMs = 0;
                                OnPropertyChanged(nameof(CanAccessOperationalTabs));
                                OnPropertyChanged(nameof(CanManagePinLock));
                            });

                            await RefreshPinLockStateAsync();
                            _portService.RequestStaticSnapshot();
                            return;
                        }

                        unlockStatus = recovery.ErrorMessage ?? "Recovery failed.";
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            PinLockRetryAfterMs = recovery.RetryAfterMs;
                            PinLockStatusMessage = unlockStatus;
                        });
                        continue;
                    }

                    unlockStatus = unlockResponse == null
                        ? "PIN unlock failed."
                        : $"PIN unlock returned {unlockResponse.Result}.";
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        PinLockRetryAfterMs = unlockResponse?.RetryAfterMs ?? _portService.PinRetryAfterMs;
                        PinLockStatusMessage = unlockStatus;
                    });
                }
            }
            catch (Exception ex)
            {
                AddLog($"PIN unlock workflow failed: {ex.Message}");
            }
            finally
            {
                _isSecureUnlockWorkflowActive = false;
            }
        }

        private async Task SendSecureKeepaliveAsync()
        {
            try
            {
                if (_portService == null || !IsConnected || !CommsEstablished)
                {
                    return;
                }

                SecureManagementResponse? status = await _portService.GetSecureStatusAsync();
                if (status == null)
                {
                    return;
                }

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    PinLockRetryAfterMs = status.RetryAfterMs;
                    if (status.PinEnabled)
                    {
                        IsPinLockSupported = true;
                        IsPinLockEnabled = true;
                        if (!status.IsAuthorized)
                        {
                            PinLockStatusMessage = "PIN lock enabled. Session requires unlock.";
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                AddLog($"Secure keepalive failed: {ex.Message}");
            }
            finally
            {
                _isSecureKeepaliveInProgress = false;
            }
        }

        /// <summary>
        /// Disconnects from the PDM and cleans up all communication resources.
        /// Resets connection and communication state, stopping any pending operations.
        /// </summary>
        [RelayCommand]
        private void Disconnect()
        {
            Disconnect(disposePortServiceInBackground: false);
        }

        private void Disconnect(bool disposePortServiceInBackground)
        {
            if (_portService != null)
            {
                SerialPortService portService = _portService;
                _portService.SetMathStatusPollingChannel(-1);
                _portService.SetCustomCanStatusPollingEnabled(false);
                _portService.DataUpdated -= _portService_DataUpdated;
                _portService.ConfigurationSaved -= ConfigSaved;
                _portService.ConfigurationSaveCompleted -= ConfigSaveCompleted;
                _portService = null;

                if (disposePortServiceInBackground)
                {
                    _ = Task.Run(() =>
                    {
                        try
                        {
                            portService.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Serial port disposal during shutdown failed: {ex.Message}");
                        }
                    });
                }
                else
                {
                    portService.Dispose();
                }
            }

            IsSendingConfig = false;
            CommsEstablished = false;
            IsInitialSnapshotLoaded = false;
            IsConnected = false;
            _isSecureUnlockWorkflowActive = false;
            _isSecureKeepaliveInProgress = false;
            _hasReceivedLiveData = false;
            ResetLiveChartHistory();
            ResetLiveChartSeries();
            AddLog("Disconnected from PDM.");
        }

        /// <summary>
        /// Event handler for when the port service receives updated data from the PDM.
        /// Queues the data for UI update on the next UI thread cycle in a thread-safe manner.
        /// </summary>
        private void _portService_DataUpdated(DataStructures obj)
        {
            _hasReceivedLiveData = true;
            lock (_pendingDataLock)
            {
                _pendingLiveData = obj;
                _hasPendingData = true;
            }
        }
    }
}

