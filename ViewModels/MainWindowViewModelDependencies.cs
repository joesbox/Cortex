using Cortex.Services;

namespace Cortex.ViewModels;

public sealed class MainWindowViewModelDependencies
{
    public MainWindowViewModelDependencies(
        AppUpdateService? appUpdateService = null,
        FirmwareUpdateService? firmwareUpdateService = null,
        ProvisioningApiClient? provisioningApiClient = null,
        ProvisioningSessionStore? provisioningSessionStore = null,
        IoFlowGraphStore? ioFlowGraphStore = null)
    {
        AppUpdateService = appUpdateService ?? new AppUpdateService();
        FirmwareUpdateService = firmwareUpdateService ?? new FirmwareUpdateService();
        ProvisioningApiClient = provisioningApiClient ?? new ProvisioningApiClient();
        ProvisioningSessionStore = provisioningSessionStore ?? new ProvisioningSessionStore();
        IoFlowGraphStore = ioFlowGraphStore ?? new IoFlowGraphStore();
    }

    public AppUpdateService AppUpdateService { get; }

    public FirmwareUpdateService FirmwareUpdateService { get; }

    public ProvisioningApiClient ProvisioningApiClient { get; }

    public ProvisioningSessionStore ProvisioningSessionStore { get; }

    public IoFlowGraphStore IoFlowGraphStore { get; }
}
