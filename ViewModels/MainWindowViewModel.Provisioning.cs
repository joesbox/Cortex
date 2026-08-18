using Cortex.Models;
using Cortex.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Cortex.ViewModels
{
    public partial class MainWindowViewModel
    {
        private const int MaxPdmNameLength = Constants.CELLULAR_OPENREMOTE_ASSET_NAME_LENGTH - 1;
        private const int ProvisioningPollAttempts = 45;
        private const int ProvisioningPollAttemptsAfterControllerFailure = 6;
        private const int ProvisioningPollDelayMs = 4000;
        private const int PdmReconnectSettleMs = 10000;
        private const int PdmReconnectRetryMs = 8000;

        private readonly ProvisioningApiClient _provisioningApiClient = new();
        private readonly ProvisioningSessionStore _provisioningSessionStore = new();

        [ObservableProperty]
        private string openRemoteRealmInput = string.Empty;

        [ObservableProperty]
        private string openRemoteUsernameInput = string.Empty;

        [ObservableProperty]
        private string openRemotePasswordInput = string.Empty;

        [ObservableProperty]
        private bool staySignedIn = true;

        [ObservableProperty]
        private string pdmNameInput = string.Empty;

        [ObservableProperty]
        private bool isOpenRemoteSignedIn;

        [ObservableProperty]
        private bool isOpenRemoteSigningIn;

        [ObservableProperty]
        private string openRemoteSignedInAs = string.Empty;

        [ObservableProperty]
        private bool isConnectedPdmRegistered;

        [ObservableProperty]
        private string connectedPdmStatusMessage = "Connect a PDM to see its telemetry service status.";

        public string OpenRemoteSignInButtonText => IsOpenRemoteSigningIn ? "Signing in..." : "Sign in";

        public bool CanSignInOpenRemote => !IsOpenRemoteSigningIn && !IsOpenRemoteSignedIn && IsInternetAvailable;

        public bool HasValidPdmName
        {
            get
            {
                string name = PdmNameInput?.Trim() ?? string.Empty;
                return name.Length > 0 && name.Length <= MaxPdmNameLength;
            }
        }

        public bool CanRenamePdm => IsOpenRemoteSignedIn && IsConnectedPdmRegistered && HasValidPdmName && !IsOpenRemoteProvisioningInProgress && CanUseOpenRemoteSettings;

        public bool CanUnregisterPdm => IsOpenRemoteSignedIn && IsConnectedPdmRegistered && !IsOpenRemoteProvisioningInProgress && CanUseOpenRemoteSettings;

        /// <summary>Restores a saved OpenRemote session, if the user chose to stay signed in.</summary>
        public async Task RestoreProvisioningSessionAsync()
        {
            if (!_provisioningSessionStore.TryLoad(out string realm, out string username, out string refreshToken))
            {
                return;
            }

            if (await _provisioningApiClient.TryResumeAsync(realm, username, refreshToken))
            {
                IsOpenRemoteSignedIn = true;
                OpenRemoteRealmInput = realm;
                OpenRemoteUsernameInput = username;
                OpenRemoteSignedInAs = $"{username} ({realm})";
                OpenRemoteProvisioningStatusMessage = "Signed in to the telemetry service.";
                _provisioningSessionStore.Save(realm, username, _provisioningApiClient.RefreshToken);
                await RefreshConnectedPdmStatusAsync();
            }
            else
            {
                _provisioningSessionStore.Clear();
                OpenRemoteProvisioningStatusMessage = "Your saved session expired. Sign in again.";
            }
        }

        partial void OnIsOpenRemoteSigningInChanged(bool value)
        {
            OnPropertyChanged(nameof(OpenRemoteSignInButtonText));
            OnPropertyChanged(nameof(CanSignInOpenRemote));
        }

        partial void OnIsOpenRemoteSignedInChanged(bool value)
        {
            OnPropertyChanged(nameof(CanSignInOpenRemote));
            OnPropertyChanged(nameof(CanProvisionOpenRemote));
            OnPropertyChanged(nameof(CanRenamePdm));
            OnPropertyChanged(nameof(CanUnregisterPdm));
        }

        partial void OnPdmNameInputChanged(string value)
        {
            OnPropertyChanged(nameof(HasValidPdmName));
            OnPropertyChanged(nameof(CanProvisionOpenRemote));
            OnPropertyChanged(nameof(CanRenamePdm));
        }

        partial void OnIsConnectedPdmRegisteredChanged(bool value)
        {
            OnPropertyChanged(nameof(CanRenamePdm));
            OnPropertyChanged(nameof(CanUnregisterPdm));

            if (value && IsConnected && CommsEstablished)
            {
                _suppressCellularNeedsAttentionUntilUtc = DateTime.UtcNow.AddMilliseconds(CellularHealthRegistrationWarmupMs);
                SetCellularConnectionHealthStatus("Checking", shouldLog: true);
                ScheduleCellularHealthPoll(immediate: true);
                _ = PollCellularHealthStatusAsync();
            }
            else
            {
                _suppressCellularNeedsAttentionUntilUtc = DateTime.MinValue;
                SetCellularConnectionHealthStatus("Offline", shouldLog: true);
                _nextCellularHealthPollUtc = DateTime.MinValue;
            }
        }

        [RelayCommand]
        private async Task SignInOpenRemote()
        {
            RefreshInternetAvailability();
            IsOpenRemoteSigningIn = true;

            try
            {
                await _provisioningApiClient.SignInAsync(OpenRemoteRealmInput, OpenRemoteUsernameInput, OpenRemotePasswordInput, StaySignedIn);

                // The password is not needed again; the access token is used from here on.
                OpenRemotePasswordInput = string.Empty;
                IsOpenRemoteSignedIn = true;
                OpenRemoteSignedInAs = $"{_provisioningApiClient.SignedInUsername} ({_provisioningApiClient.SignedInRealm})";
                OpenRemoteProvisioningStatusMessage = "Signed in to the telemetry service.";
                AddLog($"Signed in to the telemetry service, realm {_provisioningApiClient.SignedInRealm}.");

                if (StaySignedIn)
                {
                    _provisioningSessionStore.Save(
                        _provisioningApiClient.SignedInRealm,
                        _provisioningApiClient.SignedInUsername,
                        _provisioningApiClient.RefreshToken);
                }
                else
                {
                    _provisioningSessionStore.Clear();
                }

                await RefreshConnectedPdmStatusAsync();
            }
            catch (Exception ex)
            {
                IsOpenRemoteSignedIn = false;
                OpenRemoteSignedInAs = string.Empty;
                OpenRemoteProvisioningStatusMessage = ex.Message;
                AddLog("Telemetry service sign in failed.", exception: ex);
            }
            finally
            {
                IsOpenRemoteSigningIn = false;
            }
        }

        [RelayCommand]
        private async Task SignOutOpenRemote()
        {
            await _provisioningApiClient.SignOutAsync();
            _provisioningSessionStore.Clear();
            IsOpenRemoteSignedIn = false;
            OpenRemoteSignedInAs = string.Empty;
            OpenRemotePasswordInput = string.Empty;
            IsConnectedPdmRegistered = false;
            ConnectedPdmStatusMessage = "Sign in to see this PDM's telemetry service status.";
            OpenRemoteProvisioningStatusMessage = "Signed out of the telemetry service.";
        }

        [RelayCommand]
        private async Task RefreshConnectedPdmStatus()
        {
            await RefreshConnectedPdmStatusAsync();
        }

        /// <summary>
        /// Cortex may be moved between PDMs, so device state is always resolved from the client ID
        /// reported by the controller that is connected right now.
        /// </summary>
        private async Task RefreshConnectedPdmStatusAsync()
        {
            string clientId = SettingsDataView.CellularParamsStaticData.ClientID?.Trim() ?? string.Empty;

            if (!IsOpenRemoteSignedIn)
            {
                IsConnectedPdmRegistered = false;
                ConnectedPdmStatusMessage = "Sign in to see this PDM's telemetry service status.";
                return;
            }

            if (string.IsNullOrWhiteSpace(clientId))
            {
                IsConnectedPdmRegistered = false;
                ConnectedPdmStatusMessage = "Connect a PDM to see its telemetry service status.";
                return;
            }

            try
            {
                ProvisioningDeviceStatus status = await _provisioningApiClient.GetDeviceStatusAsync(clientId);

                if (status.Registered)
                {
                    IsConnectedPdmRegistered = true;
                    ConnectedPdmStatusMessage = $"Registered as \"{status.AssetName}\".";
                    ApplyRegisteredDeviceName(status.AssetName, isUserEdit: false);

                    if (string.IsNullOrWhiteSpace(PdmNameInput))
                    {
                        PdmNameInput = status.AssetName ?? string.Empty;
                    }
                }
                else if (status.ClaimedByAnotherRealm)
                {
                    IsConnectedPdmRegistered = false;
                    ConnectedPdmStatusMessage = "This PDM is registered to another organisation. Contact support to release it.";
                }
                else if (status.RegistrationIncomplete)
                {
                    IsConnectedPdmRegistered = false;
                    ConnectedPdmStatusMessage = "A previous registration did not finish. Check the name and click Register PDM to try again.";

                    if (string.IsNullOrWhiteSpace(PdmNameInput))
                    {
                        PdmNameInput = status.AssetName ?? string.Empty;
                    }
                }
                else
                {
                    IsConnectedPdmRegistered = false;
                    ConnectedPdmStatusMessage = "This PDM is not registered yet. Enter a name and click Register PDM.";
                }
            }
            catch (Exception ex)
            {
                IsConnectedPdmRegistered = false;
                ConnectedPdmStatusMessage = $"Couldn't read this PDM's status: {ex.Message}";
                Debug.WriteLine($"Provisioning status lookup failed: {ex}");
            }
        }

        [RelayCommand]
        private async Task ProvisionOpenRemote()
        {
            RefreshInternetAvailability();

            if (!IsOpenRemoteSignedIn)
            {
                SetProvisioningStatus("Sign in to the telemetry service before registering this PDM.");
                return;
            }

            if (_portService == null || !IsConnected || !CommsEstablished)
            {
                SetProvisioningStatus("Connect to the PDM before registering it.");
                return;
            }

            if (!CanUseOpenRemoteSettings)
            {
                SetProvisioningStatus(OpenRemoteAvailabilityMessage);
                return;
            }

            string clientId = SettingsDataView.CellularParamsStaticData.ClientID?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(clientId))
            {
                SetProvisioningStatus("The connected PDM did not report a client ID.");
                return;
            }

            string pdmName = PdmNameInput?.Trim() ?? string.Empty;
            if (!HasValidPdmName)
            {
                SetProvisioningStatus($"Enter a PDM name of 1 to {MaxPdmNameLength} characters.");
                return;
            }

            IsOpenRemoteProvisioningInProgress = true;
            bool registered = false;
            string registeredName = pdmName;

            try
            {
                SetProvisioningStatus("Registering this PDM...");
                ProvisioningRegistration registration = await _provisioningApiClient.CreateRegistrationAsync(clientId, pdmName);
                string jobId = registration.JobId ?? throw new InvalidOperationException("The provisioning service did not return a job ID.");

                string provisioningRequestJson = await _provisioningApiClient.GetProvisioningRequestAsync(jobId);

                SettingsDataView.CellularParamsStaticData.OpenRemoteRealm = _provisioningApiClient.SignedInRealm;
                PrepareCellularSettingsForSave();

                SetProvisioningStatus("Connecting the PDM to the telemetry service. This can take a few minutes...");
                string? controllerResult = await _portService.RequestOpenRemoteProvisioningAsync(
                    SettingsDataView.CellularParamsStaticData,
                    provisioningRequestJson);

                AddLog(
                    "Telemetry service provisioning controller response received.",
                    details: $"accepted={IsOpenRemoteProvisioningAccepted(controllerResult)}; response=\"{FormatDiagnosticPayload(controllerResult)}\"; {BuildOpenRemoteDiagnosticContext(provisioningRequestJson)}");

                ProvisioningRegistration completed = await WaitForProvisioningAsync(jobId, controllerResult);

                SetProvisioningStatus("Configuring the PDM...");
                ProvisioningCredentials credentials = await _provisioningApiClient.GetCredentialsAsync(jobId);
                ApplyProvisioningCredentials(credentials);

                SendConfig();
                await WaitForConfigSaveAsync();

                registeredName = completed.AssetName ?? pdmName;
                registered = true;
                IsConnectedPdmRegistered = true;
                ConnectedPdmStatusMessage = $"Registered as \"{registeredName}\".";
                SetProvisioningStatus($"\"{registeredName}\" is registered.");
            }
            catch (Exception ex)
            {
                SetProvisioningStatus($"Registration failed: {ex.Message}", ex);
            }
            finally
            {
                IsOpenRemoteProvisioningInProgress = false;
            }

            if (registered)
            {
                SetCellularConnectionHealthStatus("Checking", shouldLog: true);
            }
        }

        [RelayCommand]
        private async Task RenamePdm()
        {
            string clientId = SettingsDataView.CellularParamsStaticData.ClientID?.Trim() ?? string.Empty;
            string pdmName = PdmNameInput?.Trim() ?? string.Empty;

            if (!CanRenamePdm || string.IsNullOrWhiteSpace(clientId))
            {
                return;
            }

            IsOpenRemoteProvisioningInProgress = true;

            try
            {
                ProvisioningDeviceStatus status = await _provisioningApiClient.RenameDeviceAsync(clientId, pdmName);
                ApplyRegisteredDeviceName(status.AssetName, isUserEdit: true);
                ConnectedPdmStatusMessage = $"Registered as \"{status.AssetName}\".";
                SetProvisioningStatus($"PDM renamed to \"{status.AssetName}\".");

                SendConfig();
                await WaitForConfigSaveAsync();
            }
            catch (Exception ex)
            {
                SetProvisioningStatus($"Rename failed: {ex.Message}", ex);
            }
            finally
            {
                IsOpenRemoteProvisioningInProgress = false;
            }
        }

        [RelayCommand]
        private async Task UnregisterPdm()
        {
            string clientId = SettingsDataView.CellularParamsStaticData.ClientID?.Trim() ?? string.Empty;
            string currentName = SettingsDataView.CellularParamsStaticData.OpenRemoteAssetName?.Trim() ?? clientId;

            if (!CanUnregisterPdm || string.IsNullOrWhiteSpace(clientId))
            {
                return;
            }

            bool confirmed = await _appCloser.ConfirmAsync(
                "Unregister PDM",
                $"This removes \"{currentName}\" and its recorded history from the telemetry service, and stops it sending data.\n\n" +
                "The PDM can then be registered again by you or by a new owner. This cannot be undone.",
                "UNREGISTER",
                "CANCEL");

            if (!confirmed)
            {
                return;
            }

            IsOpenRemoteProvisioningInProgress = true;

            try
            {
                ProvisioningUnregisterResult result = await _provisioningApiClient.UnregisterDeviceAsync(clientId);

                ClearOpenRemoteCredentialsOnPdm();
                SendConfig();
                await WaitForConfigSaveAsync();

                IsConnectedPdmRegistered = false;
                PdmNameInput = string.Empty;
                ConnectedPdmStatusMessage = "This PDM is not registered yet. Enter a name and click Register PDM.";
                SetProvisioningStatus($"\"{currentName}\" was unregistered.");
                AddLog(
                    "PDM unregistered from the telemetry service.",
                    details: $"assetRemoved={result.AssetRemoved}; serviceUserRemoved={result.ServiceUserRemoved}; {BuildOpenRemoteDiagnosticContext()}");
            }
            catch (Exception ex)
            {
                SetProvisioningStatus($"Unregister failed: {ex.Message}", ex);
            }
            finally
            {
                IsOpenRemoteProvisioningInProgress = false;
            }
        }

        /// <summary>Wipes the OpenRemote credentials so the PDM stops trying to publish after unregistering.</summary>
        private void ClearOpenRemoteCredentialsOnPdm()
        {
            CellularParameters cellular = SettingsDataView.CellularParamsStaticData;
            cellular.MQTTUsername = string.Empty;
            cellular.MQTTPassword = string.Empty;
            cellular.PublishTopic = string.Empty;
            cellular.SubscribeTopic = string.Empty;
            cellular.OpenRemoteAssetId = string.Empty;
            cellular.OpenRemoteAssetName = string.Empty;

            ForceRefreshSettingsBindings();
            RecalculatePendingConfigChanges();
        }

        private async Task<ProvisioningRegistration> WaitForProvisioningAsync(string jobId, string? controllerResult)        {
            // If the PDM reported a failure there is little point waiting on OpenRemote, but allow a
            // short grace period because some firmware replies are ambiguous about success.
            bool controllerAccepted = IsOpenRemoteProvisioningAccepted(controllerResult);
            int maxAttempts = controllerAccepted
                ? ProvisioningPollAttempts
                : ProvisioningPollAttemptsAfterControllerFailure;

            for (int attempt = 0; attempt < maxAttempts; attempt++)
            {
                ProvisioningRegistration registration = await _provisioningApiClient.GetRegistrationAsync(jobId);

                switch (registration.State)
                {
                    case "complete":
                        return registration;

                    case "failed":
                    case "expired":
                        throw new InvalidOperationException(string.IsNullOrWhiteSpace(registration.Detail)
                            ? "The telemetry service did not finish registering this PDM."
                            : registration.Detail!);

                    default:
                        SetProvisioningStatus(DescribeProvisioningState(registration.State), log: false);
                        break;
                }

                await Task.Delay(ProvisioningPollDelayMs);
            }

            throw new InvalidOperationException(controllerAccepted
                ? "The PDM connected to the telemetry service, but registration did not finish in time. Try again."
                : $"The PDM could not reach the telemetry service. {DescribeControllerFailure(controllerResult)}");
        }

        private static string DescribeControllerFailure(string? controllerResult)
        {
            if (string.IsNullOrWhiteSpace(controllerResult))
            {
                return "It did not respond. Check mobile data and try again.";
            }

            return $"Controller reported: {FormatDiagnosticPayload(controllerResult)}";
        }

        private static string DescribeProvisioningState(string? state) => state switch
        {
            "awaiting-device" => "Waiting for the PDM to reach the telemetry service...",
            "linking" => "Linking the PDM to your account...",
            "verifying" => "Verifying the link...",
            "naming" => "Naming the PDM...",
            _ => "Registering this PDM...",
        };

        private void ApplyProvisioningCredentials(ProvisioningCredentials credentials)
        {
            CellularParameters cellular = SettingsDataView.CellularParamsStaticData;
            cellular.OpenRemoteRealm = credentials.Realm?.Trim();
            cellular.OpenRemoteAssetId = credentials.AssetId?.Trim();
            cellular.MQTTUsername = $"{credentials.Realm?.Trim()}:{credentials.ServiceUser?.Trim()}";
            cellular.MQTTPassword = credentials.ServiceUserSecret?.Trim();
            cellular.OpenRemoteAssetName = TrimOpenRemoteAssetNameForPdm(credentials.AssetName);
            cellular.EnsurePublishTopicFromOpenRemoteFields();

            ForceRefreshSettingsBindings();
            RecalculatePendingConfigChanges();
        }

        /// <param name="isUserEdit">
        /// False when the name was read back from the server, so it is not treated as an unsaved change.
        /// </param>
        private void ApplyRegisteredDeviceName(string? assetName, bool isUserEdit)
        {
            string trimmed = TrimOpenRemoteAssetNameForPdm(assetName);
            if (string.IsNullOrWhiteSpace(trimmed) ||
                string.Equals(trimmed, SettingsDataView.CellularParamsStaticData.OpenRemoteAssetName, StringComparison.Ordinal))
            {
                return;
            }

            if (isUserEdit)
            {
                SettingsDataView.CellularParamsStaticData.OpenRemoteAssetName = trimmed;
                ForceRefreshSettingsBindings();
                RecalculatePendingConfigChanges();
                return;
            }

            bool wasSuppressing = _suppressDirtyTracking;
            _suppressDirtyTracking = true;
            try
            {
                SettingsDataView.CellularParamsStaticData.OpenRemoteAssetName = trimmed;
                if (_controllerConfigBaseline != null)
                {
                    _controllerConfigBaseline.CellularParamsStaticData.OpenRemoteAssetName = trimmed;
                }

                ForceRefreshSettingsBindings();
            }
            finally
            {
                _suppressDirtyTracking = wasSuppressing;
            }
        }

        private async Task WaitForConfigSaveAsync()
        {
            for (int attempt = 0; attempt < 60 && IsSendingConfig; attempt++)
            {
                await Task.Delay(500);
            }
        }

        /// <summary>
        /// The modem drops its old MQTT session and re-authenticates after the new credentials are
        /// saved, so an immediate test reports the old session's failure rather than the new state.
        /// </summary>
        private async Task RunPostProvisioningConnectionTestAsync()
        {
            IsAutomaticCellularTestInProgress = true;

            try
            {
                await Task.Delay(PdmReconnectSettleMs);
                await RunCellularConnectionTestAsync(isAutomaticRun: true, maxAttempts: AutomaticCellularTestMaxAttempts);

                if (!LooksLikeConnectionTestFailure(CellularTestStatusMessage))
                {
                    return;
                }

                await Task.Delay(PdmReconnectRetryMs);
                await RunCellularConnectionTestAsync(isAutomaticRun: true, maxAttempts: AutomaticCellularTestMaxAttempts);
            }
            finally
            {
                IsAutomaticCellularTestInProgress = false;
            }
        }

        private static bool LooksLikeConnectionTestFailure(string? message)
        {
            return !string.IsNullOrWhiteSpace(message) &&
                (message.Contains("fail", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("did not respond", StringComparison.OrdinalIgnoreCase) ||
                 message.Contains("blocked", StringComparison.OrdinalIgnoreCase));
        }

        private void SetProvisioningStatus(string message, Exception? exception = null, bool log = true)
        {
            OpenRemoteProvisioningStatusMessage = message;

            if (log)
            {
                AddLog(message, details: BuildOpenRemoteDiagnosticContext(), exception: exception);
            }
        }

        private static bool IsOpenRemoteProvisioningAccepted(string? result)
        {
            return !string.IsNullOrWhiteSpace(result) &&
                (result.Contains("Provisioning: OK", StringComparison.OrdinalIgnoreCase) ||
                 result.Contains("provisioning accepted", StringComparison.OrdinalIgnoreCase));
        }

        private static string TrimOpenRemoteAssetNameForPdm(string? name)
        {
            string trimmed = name?.Trim() ?? string.Empty;
            int maxLength = Constants.CELLULAR_OPENREMOTE_ASSET_NAME_LENGTH - 1;
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
        }

        private string BuildOpenRemoteDiagnosticContext(string? provisioningRequestJson = null)
        {
            CellularParameters cellular = SettingsDataView.CellularParamsStaticData;
            int requestBytes = string.IsNullOrWhiteSpace(provisioningRequestJson)
                ? 0
                : Encoding.ASCII.GetByteCount(provisioningRequestJson);

            return $"connected={IsConnected}, commsEstablished={CommsEstablished}, internetAvailable={IsInternetAvailable}, " +
                   $"host=\"{cellular.OpenRemoteHost?.Trim() ?? string.Empty}\", port={cellular.OpenRemotePort}, tls={cellular.UseTLS}, " +
                   $"clientId=\"{cellular.ClientID?.Trim() ?? string.Empty}\", realm=\"{cellular.OpenRemoteRealm?.Trim() ?? string.Empty}\", " +
                   $"assetId=\"{cellular.OpenRemoteAssetId?.Trim() ?? string.Empty}\", topic=\"{cellular.PublishTopic?.Trim() ?? string.Empty}\", " +
                   $"mqttUser=\"{cellular.MQTTUsername?.Trim() ?? string.Empty}\", hasMqttPassword={!string.IsNullOrWhiteSpace(cellular.MQTTPassword)}, " +
                   $"requestBytes={requestBytes}";
        }

        private static string FormatDiagnosticPayload(string? payload)
        {
            if (string.IsNullOrWhiteSpace(payload))
            {
                return "<empty>";
            }

            string compact = Regex.Replace(payload, "\\s+", " ").Trim();
            const int maxLength = 600;
            return compact.Length <= maxLength
                ? compact
                : compact[..maxLength] + "... [truncated]";
        }
    }
}
