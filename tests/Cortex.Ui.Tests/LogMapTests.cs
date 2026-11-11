using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cortex.Services;
using Cortex.ViewModels;
using Cortex.Views;
using Mapsui.Extensions;
using Mapsui.Projections;
using System.Reflection;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class LogMapTests
{
    [AvaloniaTheory]
    [InlineData(false, 2)]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    [InlineData(true, 1)]
    public void GpsRouteLoadsAfterLogViewIsAttachedAgain(bool reattach, int pointCount)
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-log-map-{Guid.NewGuid():N}");
        using var vm = new MainWindowViewModel(new ChannelCanTxSettingsTests.TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "graph.json"))));
        foreach (string name in new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" })
        {
            ((System.Timers.Timer)Field(vm, name)!).Stop();
        }
        var view = new LogHistoryTabView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        try
        {
            window.Show();
            Layout(window);
            if (reattach)
            {
                window.Content = null;
                Layout(window);
                window.Content = view;
                Layout(window);
            }

            LoadGpsLog(vm, pointCount);
            Assert.Equal(pointCount, vm.BuildLogMapRows(int.MaxValue).Count);
            vm.SelectedLogDetailTabIndex = 1;
            Layout(window);

            Assert.Same(vm, Field(view, "_viewModel"));
            if (pointCount >= 2)
            {
                Assert.NotNull(Field(view, "_logRouteLayer"));
            }
            else
            {
                Assert.Null(Field(view, "_logRouteLayer"));
            }
            Assert.NotNull(Field(view, "_logPointLayer"));
            Assert.NotEmpty((IReadOnlyList<LogMapGridRow>)Field(view, "_displayedLogMapRows")!);
            var map = (Mapsui.UI.Avalonia.MapControl)Field(view, "_logMap")!;
            var viewport = map.Map.Navigator.Viewport;
            Assert.True(viewport.HasSize());
            var first = vm.BuildLogMapRows(int.MaxValue)[0];
            var (x, y) = SphericalMercator.FromLonLat(first.Longitude, first.Latitude);
            var screen = viewport.WorldToScreen(x, y);
            Assert.InRange(screen.X, 0, viewport.Width);
            Assert.InRange(screen.Y, 0, viewport.Height);
        }
        finally
        {
            window.Close();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void LoadGpsLog(MainWindowViewModel vm, int pointCount)
    {
        var header = (IReadOnlyList<string>)typeof(MainWindowViewModel).GetMethod("BuildExpectedLogHeaderColumns",
            BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var rows = new List<string> { string.Join(",", header) };
        for (int i = 0; i < pointCount; i++)
        {
            string[] columns = Enumerable.Repeat("0", header.Count).ToArray();
            columns[0] = "2026-11-11";
            columns[1] = $"21:15:{38 + i}.183";
            columns[header.ToList().IndexOf("Lat")] = i == 0 ? "51.5000" : "51.5001";
            columns[header.ToList().IndexOf("Lon")] = "-0.1200";
            rows.Add(string.Join(",", columns));
        }
        typeof(MainWindowViewModel).GetMethod("ParseLogContent", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(vm, [string.Join("\n", rows)]);
    }

    private static object? Field(object target, string name) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);

    private static void Layout(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
