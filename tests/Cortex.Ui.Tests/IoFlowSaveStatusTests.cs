using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cortex.Services;
using Cortex.ViewModels;
using System.Reflection;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class IoFlowSaveStatusTests
{
    [AvaloniaFact]
    public async Task ConfirmedSaveFromWorkerThreadUpdatesStatusOnUiThread()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-save-status-{Guid.NewGuid():N}");
        var vm = new MainWindowViewModel(new ChannelCanTxSettingsTests.TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "graph.json"))));
        var timers = new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" }
            .Select(name => (System.Timers.Timer)typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).ToArray();
        foreach (var timer in timers) timer.Stop();
        try
        {
            bool? statusUpdatedOnUiThread = null;
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.SystemDateTime))
                    statusUpdatedOnUiThread = Dispatcher.UIThread.CheckAccess();
            };
            vm.IsSendingConfig = true;
            var saved = typeof(MainWindowViewModel).GetMethod("ConfigSaved", BindingFlags.NonPublic | BindingFlags.Instance)!;
            await Task.Run(() => saved.Invoke(vm, [null, EventArgs.Empty]));
            await Dispatcher.UIThread.InvokeAsync(() => { });
            Assert.True(statusUpdatedOnUiThread);
            Assert.False(vm.IsSendingConfig);
            Assert.True(vm.HasControllerSaveTimestamp);
            Assert.StartsWith("Last updated:", vm.ControllerSaveStatusText);
        }
        finally
        {
            foreach (var timer in timers) timer.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
