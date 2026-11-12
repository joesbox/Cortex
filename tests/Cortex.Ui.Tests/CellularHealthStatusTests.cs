using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cortex.Services;
using Cortex.ViewModels;
using System.Reflection;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class CellularHealthStatusTests
{
    [AvaloniaFact]
    public void AutomaticStatusUpdatesDoNotAddSystemLogEntries()
    {
        using var vm = new MainWindowViewModel(new ChannelCanTxSettingsTests.TestAppCloser());
        foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
        {
            ((System.Timers.Timer)typeof(MainWindowViewModel)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).Stop();
        }

        vm.SettingsDataView.SystemParamsStaticData.AllowData = true;
        Dispatcher.UIThread.RunJobs();
        int before = LoggingService.LogEntries.Count(entry => entry.Contains("Connectivity status:"));
        var changes = new List<string?>();
        vm.PropertyChanged += (_, args) => changes.Add(args.PropertyName);
        MethodInfo update = typeof(MainWindowViewModel).GetMethod(
            "SetCellularConnectionHealthStatus", BindingFlags.Instance | BindingFlags.NonPublic)!;

        foreach (string status in new[] { "Checking", "Healthy", "Needs attention", "Offline" })
        {
            update.Invoke(vm, [status]);
            Assert.Equal(status, vm.CellularConnectionHealthStatus);
        }

        Dispatcher.UIThread.RunJobs();
        Assert.Equal(4, changes.Count(name => name == nameof(MainWindowViewModel.CellularConnectionHealthStatus)));
        Assert.Equal(before, LoggingService.LogEntries.Count(entry => entry.Contains("Connectivity status:")));
    }
}
