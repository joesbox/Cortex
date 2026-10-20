using Cortex.ViewModels.IoFlow;

namespace Cortex.ViewModels;

public partial class MainWindowViewModel
{
    public IoFlowGraphViewModel IoFlowGraph { get; } = new();

    private void InitializeIoFlowGraph()
    {
        IoFlowGraph.Build(SettingsDataView, LiveDataView);
    }

    private void RefreshIoFlowGraphFromSettings()
    {
        IoFlowGraph.Build(SettingsDataView, LiveDataView);
    }

    private void RefreshIoFlowConnections()
    {
        IoFlowGraph.RebuildConnections(SettingsDataView);
    }

    private void RefreshIoFlowLiveSamples()
    {
        IoFlowGraph.UpdateLiveChannelSamples(LiveDataView.ChannelsLiveData);
    }
}
