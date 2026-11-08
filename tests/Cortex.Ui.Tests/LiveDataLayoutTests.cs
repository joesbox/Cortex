using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Cortex.Services;
using Cortex.Views;
using Xunit;
using Rectangle = Avalonia.Controls.Shapes.Rectangle;

namespace Cortex.Ui.Tests;

public sealed class LiveDataLayoutTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"cortex-live-layout-{Guid.NewGuid():N}");
    private string PreferencesPath => Path.Combine(_directory, "live-data-layout.json");
    private PanelLayoutStore CreateStore() => new("live-data-layout.json", 1.0 / 2.4, PreferencesPath);

    [AvaloniaFact]
    public void DefaultProportionsAndWindowResizingDoNotWritePreferences()
    {
        var view = new StatusTabView(CreateStore());
        var window = Show(view);
        try
        {
            Assert.Equal(1.0 / 2.4, TopFraction(view), 2);
            window.Height = 1100;
            window.UpdateLayout();
            Assert.Equal(1.0 / 2.4, TopFraction(view), 2);
            Assert.False(File.Exists(PreferencesPath));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void DraggingPersistsProportionsIndependentlyOfCanMessages()
    {
        var canPath = Path.Combine(_directory, "can-messages-layout.json");
        var canStore = new PanelLayoutStore("can-messages-layout.json", 4.0 / 7.0, canPath);
        canStore.SaveTopFraction(0.6);
        var view = new StatusTabView(CreateStore());
        var window = Show(view);
        double savedFraction;
        try
        {
            double before = TopContainer(view).Bounds.Height;
            double bottomBefore = BottomContainer(view).Bounds.Height;
            Drag(window, view, 100);

            Assert.InRange(TopContainer(view).Bounds.Height, before + 98, before + 102);
            Assert.InRange(BottomContainer(view).Bounds.Height, bottomBefore - 102, bottomBefore - 98);
            savedFraction = TopFraction(view);
            Assert.Equal(savedFraction, CreateStore().LoadTopFraction(), 2);
            Assert.Equal(0.6, canStore.LoadTopFraction());
        }
        finally
        {
            window.Close();
        }

        var restored = new StatusTabView(CreateStore());
        var reopened = Show(restored, 1100);
        try
        {
            Assert.Equal(savedFraction, TopFraction(restored), 2);
        }
        finally
        {
            reopened.Close();
        }
    }

    [AvaloniaFact]
    public void KeyboardAdjustmentIsPersisted()
    {
        var view = new StatusTabView(CreateStore());
        var window = Show(view);
        try
        {
            double before = TopContainer(view).Bounds.Height;
            Assert.True(view.FindControl<GridSplitter>("LiveDataSplitter")!.Focus());
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            window.UpdateLayout();

            Assert.True(TopContainer(view).Bounds.Height > before);
            Assert.True(File.Exists(PreferencesPath));
            Assert.Equal(TopFraction(view), CreateStore().LoadTopFraction(), 2);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(-2000)]
    [InlineData(2000)]
    public void ExistingResizeLimitsArePreservedAndCanBeSaved(double delta)
    {
        var view = new StatusTabView(CreateStore());
        var window = Show(view);
        try
        {
            Drag(window, view, delta);
            Assert.True(TopContainer(view).Bounds.Height >= 300);
            Assert.True(BottomContainer(view).Bounds.Height >= 0);
            Assert.Equal(TopFraction(view), CreateStore().LoadTopFraction(), 2);
            if (delta > 0)
            {
                Assert.Equal(0, BottomContainer(view).Bounds.Height);
                Assert.Equal(1, CreateStore().LoadTopFraction());
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void CanAndLiveDataDividersUseTheOriginalLiveDataAppearance()
    {
        var live = new StatusTabView(CreateStore());
        var can = new CanMessagesTabView(new PanelLayoutStore("can-messages-layout.json", 4.0 / 7.0,
            Path.Combine(_directory, "can-messages-layout.json")));
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*") };
        grid.Children.Add(live);
        Grid.SetColumn(can, 1);
        grid.Children.Add(can);
        var window = new Window { Width = 1600, Height = 900, Content = grid };
        try
        {
            window.Show();
            window.UpdateLayout();
            GridSplitter[] splitters =
            [
                live.FindControl<GridSplitter>("LiveDataSplitter")!,
                can.FindControl<GridSplitter>("CanMessagesSplitter")!,
            ];
            foreach (GridSplitter splitter in splitters)
            {
                Border track = Assert.Single(splitter.GetVisualDescendants().OfType<Border>());
                Rectangle grip = Assert.Single(splitter.GetVisualDescendants().OfType<Rectangle>());
                Assert.Equal(5, track.Height);
                Assert.Equal(Color.Parse("#30303B"), Assert.IsAssignableFrom<ISolidColorBrush>(track.Background).Color);
                Assert.Equal(12, grip.Width);
                Assert.Equal(2, grip.Height);
                Assert.Equal(new Thickness(2), grip.Margin);
                Assert.Equal(Color.Parse("#AAAAAA"), Assert.IsAssignableFrom<ISolidColorBrush>(grip.Fill).Color);

                Point center = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
                window.MouseMove(center);
                Assert.Equal(Color.Parse("#2A213B"), Assert.IsAssignableFrom<ISolidColorBrush>(track.Background).Color);
                window.MouseMove(new Point(0, 0));
                Assert.Equal(Color.Parse("#30303B"), Assert.IsAssignableFrom<ISolidColorBrush>(track.Background).Color);
            }
        }
        finally
        {
            window.Close();
        }
    }

    private static Window Show(StatusTabView view, double height = 900)
    {
        var window = new Window { Width = 1200, Height = height, Content = view };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static Grid TopContainer(StatusTabView view) => view.FindControl<Grid>("LiveDataTopContainer")!;
    private static Grid BottomContainer(StatusTabView view) => view.FindControl<Grid>("LiveDataBottomContainer")!;

    private static double TopFraction(StatusTabView view)
    {
        double topHeight = TopContainer(view).Bounds.Height;
        return topHeight / (topHeight + BottomContainer(view).Bounds.Height);
    }

    private static void Drag(Window window, StatusTabView view, double delta)
    {
        GridSplitter splitter = view.FindControl<GridSplitter>("LiveDataSplitter")!;
        Point start = splitter.TranslatePoint(new Point(splitter.Bounds.Width / 2, splitter.Bounds.Height / 2), window)!.Value;
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
