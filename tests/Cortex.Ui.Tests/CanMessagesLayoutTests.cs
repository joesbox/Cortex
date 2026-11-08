using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Cortex.Services;
using Cortex.Views;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class CanMessagesLayoutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cortex-can-layout-{Guid.NewGuid():N}");
    private string PreferencesPath => Path.Combine(_directory, "can-messages-layout.json");
    private PanelLayoutStore CreateStore() => new("can-messages-layout.json", 4.0 / 7.0, PreferencesPath);

    [AvaloniaFact]
    public void DefaultProportionsAndWindowResizingDoNotWritePreferences()
    {
        var view = new CanMessagesTabView(CreateStore());
        var window = Show(view);
        try
        {
            Assert.Equal(4.0 / 7.0, ImportFraction(view), 2);
            window.Height = 1100;
            window.UpdateLayout();
            Assert.Equal(4.0 / 7.0, ImportFraction(view), 2);
            Assert.False(File.Exists(PreferencesPath));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DraggingSplitterResizesBothContainersAndRestoresProportionsInANewView()
    {
        var view = new CanMessagesTabView(CreateStore());
        var window = Show(view);
        double savedFraction;
        try
        {
            double before = ImportContainer(view).Bounds.Height;
            double storedBefore = StoredContainer(view).Bounds.Height;
            Drag(window, view, -100);

            Assert.InRange(ImportContainer(view).Bounds.Height, before - 102, before - 98);
            Assert.InRange(StoredContainer(view).Bounds.Height, storedBefore + 98, storedBefore + 102);
            savedFraction = ImportFraction(view);
            Assert.Equal(savedFraction, CreateStore().LoadTopFraction(), 2);
            Assert.True(File.Exists(PreferencesPath));
        }
        finally
        {
            window.Close();
        }

        var restored = new CanMessagesTabView(CreateStore());
        var reopened = Show(restored, 1100);
        try
        {
            Assert.Equal(savedFraction, ImportFraction(restored), 2);
        }
        finally
        {
            reopened.Close();
        }
    }

    [AvaloniaFact]
    public void KeyboardAdjustmentIsPersisted()
    {
        var view = new CanMessagesTabView(CreateStore());
        var window = Show(view);
        try
        {
            GridSplitter splitter = view.FindControl<GridSplitter>("CanMessagesSplitter")!;
            double before = ImportContainer(view).Bounds.Height;
            Assert.True(splitter.Focus());
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            window.UpdateLayout();

            Assert.True(ImportContainer(view).Bounds.Height > before);
            Assert.True(File.Exists(PreferencesPath));
            Assert.Equal(ImportFraction(view), CreateStore().LoadTopFraction(), 2);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(-2000)]
    [InlineData(2000)]
    public void DraggingKeepsBothSectionsAboveTheirMinimumHeight(double delta)
    {
        var view = new CanMessagesTabView(CreateStore());
        var window = Show(view);
        try
        {
            Drag(window, view, delta);
            Assert.True(ImportContainer(view).Bounds.Height >= 120);
            Assert.True(StoredContainer(view).Bounds.Height >= 120);
            Assert.Equal(ImportFraction(view), CreateStore().LoadTopFraction(), 2);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void EscapeCancelsDragWithoutPersistingTheIntermediateProportions()
    {
        CreateStore().SaveTopFraction(0.45);
        var view = new CanMessagesTabView(CreateStore());
        var window = Show(view);
        try
        {
            GridSplitter splitter = view.FindControl<GridSplitter>("CanMessagesSplitter")!;
            Point start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 5), window)!.Value;
            window.MouseDown(start, MouseButton.Left);
            window.MouseMove(start + new Vector(0, 80));
            window.UpdateLayout();
            Assert.NotEqual(0.45, ImportFraction(view), 2);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.KeyRelease(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(start + new Vector(0, 80), MouseButton.Left);
            window.UpdateLayout();

            Assert.Equal(0.45, ImportFraction(view), 2);
            Assert.Equal(0.45, CreateStore().LoadTopFraction(), 2);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"ImportFraction\":-1}")]
    [InlineData("{\"ImportFraction\":2}")]
    [InlineData("{\"TopFraction\":-1}")]
    [InlineData("{\"TopFraction\":2}")]
    [InlineData("not json")]
    public void InvalidPreferencesUseDefaultAndLogTheFailure(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, json);
        Assert.Equal(4.0 / 7.0, CreateStore().LoadTopFraction());
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(LoggingService.LogEntries, entry =>
            entry.Contains("panel layout preferences") && entry.Contains("default proportions"));
    }

    [AvaloniaFact]
    public void UnwritablePreferencesAreReportedInTheLog()
    {
        Directory.CreateDirectory(PreferencesPath);
        CreateStore().SaveTopFraction(0.4);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains(LoggingService.LogEntries, entry =>
            entry.Contains("Failed to save panel layout preferences"));
    }

    [AvaloniaFact]
    public void InvalidSaveFractionsAreRejected()
    {
        PanelLayoutStore store = CreateStore();
        foreach (double fraction in new[] { -1, 2, double.NaN, double.PositiveInfinity })
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => store.SaveTopFraction(fraction));
        }

        Assert.False(File.Exists(PreferencesPath));
    }

    [AvaloniaFact]
    public void LegacyCanPreferencesAreStillRestored()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(PreferencesPath, "{\"ImportFraction\":0.45}");
        var view = new CanMessagesTabView(CreateStore());
        var window = Show(view);
        try
        {
            Assert.Equal(0.45, ImportFraction(view), 2);
        }
        finally
        {
            window.Close();
        }
    }

    private static Window Show(CanMessagesTabView view, double height = 900)
    {
        var window = new Window { Width = 1200, Height = height, Content = view };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static Border ImportContainer(CanMessagesTabView view) => view.FindControl<Border>("CanImportContainer")!;
    private static Border StoredContainer(CanMessagesTabView view) => view.FindControl<Border>("StoredCanMessagesContainer")!;

    private static double ImportFraction(CanMessagesTabView view)
    {
        double importHeight = ImportContainer(view).Bounds.Height;
        return importHeight / (importHeight + StoredContainer(view).Bounds.Height);
    }

    private static void Drag(Window window, CanMessagesTabView view, double delta)
    {
        GridSplitter splitter = view.FindControl<GridSplitter>("CanMessagesSplitter")!;
        Point start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, 5), window)!.Value;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Vector(0, delta));
        window.UpdateLayout();
        window.MouseUp(start + new Vector(0, delta), MouseButton.Left);
        window.UpdateLayout();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
