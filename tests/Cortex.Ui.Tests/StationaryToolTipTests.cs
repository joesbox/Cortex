using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Cortex.Controls;
using Xunit;

namespace Cortex.Ui.Tests;

public class StationaryToolTipTests
{
    [AvaloniaFact]
    public async Task MovementWithinTable_HidesToolTipAndRestartsIdleDelay()
    {
        using var service = new StationaryToolTipService();
        var table = new DataGrid { Background = Brushes.Transparent, ItemsSource = new[] { new { Pin = 1 }, new { Pin = 2 } } };
        ToolTip.SetTip(table, "Pin summary");
        var window = new Window { Width = 600, Height = 400, Content = table };

        try
        {
            window.Show();
            window.UpdateLayout();
            window.MouseMove(new Point(50, 80));
            await Task.Delay(250);
            Assert.False(ToolTip.GetIsOpen(table));

            window.MouseMove(new Point(100, 80));
            await Task.Delay(250);
            Assert.False(ToolTip.GetIsOpen(table));
            await WaitForToolTip(table);

            window.MouseMove(new Point(150, 80));
            Assert.False(ToolTip.GetIsOpen(table));
            await Task.Delay(250);
            Assert.False(ToolTip.GetIsOpen(table));
            await WaitForToolTip(table);
            Assert.Equal("Pin summary", ToolTip.GetTip(table));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ChangingControls_DoesNotUseInstantBetweenShowShortcut()
    {
        using var service = new StationaryToolTipService();
        var first = new Border { Height = 150, Background = Brushes.Transparent };
        var second = new Border { Height = 150, Background = Brushes.Transparent };
        ToolTip.SetTip(first, "First");
        ToolTip.SetTip(second, "Second");
        ToolTip.SetShowDelay(first, 0);
        var window = new Window { Width = 400, Height = 400, Content = new StackPanel { Children = { first, second } } };

        try
        {
            window.Show();
            window.UpdateLayout();
            window.MouseMove(new Point(30, 40));
            Assert.False(ToolTip.GetIsOpen(first));
            await WaitForToolTip(first);
            window.MouseMove(new Point(30, 200));
            Assert.False(ToolTip.GetIsOpen(first));
            Assert.False(ToolTip.GetIsOpen(second));
            await Task.Delay(250);
            Assert.False(ToolTip.GetIsOpen(second));
            await WaitForToolTip(second);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ScrollingTypingAndLeaving_DismissToolTip()
    {
        using var service = new StationaryToolTipService();
        var control = new TextBox { Width = 400, Height = 400 };
        ToolTip.SetTip(control, "Help");
        var window = new Window { Width = 400, Height = 400, Content = control };

        try
        {
            window.Show();
            window.UpdateLayout();
            window.MouseMove(new Point(50, 50));
            await WaitForToolTip(control);
            window.MouseWheel(new Point(50, 50), new Vector(0, 1));
            Assert.False(ToolTip.GetIsOpen(control));

            window.MouseMove(new Point(100, 50));
            await WaitForToolTip(control);
            control.Focus();
            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            window.KeyRelease(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            Assert.False(ToolTip.GetIsOpen(control));

            window.MouseMove(new Point(150, 50));
            await WaitForToolTip(control);
            window.MouseMove(new Point(-10, -10));
            Assert.False(ToolTip.GetIsOpen(control));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task LongerControlDelayIsPreserved_AndIdenticalPointerEventsDoNotDismiss()
    {
        using var service = new StationaryToolTipService();
        var control = new Border { Background = Brushes.Transparent };
        ToolTip.SetTip(control, "Help");
        ToolTip.SetShowDelay(control, 800);
        var window = new Window { Width = 400, Height = 400, Content = control };

        try
        {
            window.Show();
            window.UpdateLayout();
            window.MouseMove(new Point(50, 50));
            await Task.Delay(500);
            Assert.False(ToolTip.GetIsOpen(control));
            await WaitForToolTip(control);
            window.MouseMove(new Point(50, 50));
            Assert.True(ToolTip.GetIsOpen(control));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DetachingHostCancelsHover_AndDisposalRemovesGlobalHandlers()
    {
        using var service = new StationaryToolTipService();
        var control = new Border { Background = Brushes.Transparent };
        ToolTip.SetTip(control, "Help");
        var window = new Window { Width = 400, Height = 400, Content = control };

        try
        {
            window.Show();
            window.UpdateLayout();
            window.MouseMove(new Point(50, 50));
            window.Content = null;
            await Task.Delay(500);
            Assert.False(ToolTip.GetIsOpen(control));

            window.Content = control;
            window.UpdateLayout();
            window.MouseMove(new Point(100, 50));
            service.Dispose();
            Assert.False(ToolTip.GetIsOpen(control));
            ToolTip.SetIsOpen(control, true);
            Assert.True(ToolTip.GetIsOpen(control));
            ToolTip.SetIsOpen(control, false);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DynamicToolTipsAndHandledMovement_WorkInPopup()
    {
        using var service = new StationaryToolTipService();
        var content = new Border { Width = 200, Height = 200, Background = Brushes.Transparent };
        content.AddHandler(InputElement.PointerMovedEvent, (_, _) => ToolTip.SetTip(content, "Updated"), handledEventsToo: true);
        content.PointerMoved += (_, args) => args.Handled = true;
        var anchor = new Button { Content = "Open" };
        var popup = new Popup { PlacementTarget = anchor, Child = content };
        var window = new Window { Width = 400, Height = 400, Content = new Grid { Children = { anchor, popup } } };

        try
        {
            window.Show();
            window.UpdateLayout();
            popup.IsOpen = true;
            Assert.IsAssignableFrom<TemplatedControl>(popup.Host).ApplyTemplate();
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var popupRoot = TopLevel.GetTopLevel(content);
            Assert.NotNull(popupRoot);
            popupRoot.UpdateLayout();
            popupRoot.MouseMove(content.TranslatePoint(new Point(30, 30), popupRoot)!.Value);
            await WaitForToolTip(content);
            popupRoot.MouseMove(content.TranslatePoint(new Point(60, 30), popupRoot)!.Value);
            Assert.False(ToolTip.GetIsOpen(content));
            await WaitForToolTip(content);
        }
        finally
        {
            popup.IsOpen = false;
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DisabledControls_RespectShowOnDisabled()
    {
        using var service = new StationaryToolTipService();
        var control = new Button { IsEnabled = false, Content = "Disabled", Width = 400, Height = 400 };
        ToolTip.SetTip(control, "Disabled help");
        ToolTip.SetShowOnDisabled(control, true);
        var window = new Window { Width = 400, Height = 400, Content = control };

        try
        {
            window.Show();
            window.UpdateLayout();
            window.MouseMove(new Point(50, 50));
            await WaitForToolTip(control);

            ToolTip.SetShowOnDisabled(control, false);
            window.MouseMove(new Point(100, 50));
            Assert.False(ToolTip.GetIsOpen(control));
            await Task.Delay(500);
            Assert.False(ToolTip.GetIsOpen(control));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClickingOrClosingWindow_CancelsPendingToolTip()
    {
        using var service = new StationaryToolTipService();
        var control = new Border { Background = Brushes.Transparent };
        ToolTip.SetTip(control, "Help");
        var window = new Window { Width = 400, Height = 400, Content = control };

        try
        {
            window.Show();
            window.UpdateLayout();
            window.MouseMove(new Point(50, 50));
            window.MouseDown(new Point(50, 50), MouseButton.Left);
            window.MouseUp(new Point(50, 50), MouseButton.Left);
            await Task.Delay(500);
            Assert.False(ToolTip.GetIsOpen(control));

            window.MouseMove(new Point(100, 50));
            window.Close();
            await Task.Delay(500);
            Assert.False(ToolTip.GetIsOpen(control));
        }
        finally
        {
            window.Close();
        }
    }

    private static async Task WaitForToolTip(Control control)
    {
        for (int attempt = 0; attempt < 40 && !ToolTip.GetIsOpen(control); attempt++)
        {
            await Task.Delay(25);
            Dispatcher.UIThread.RunJobs();
        }

        Assert.True(ToolTip.GetIsOpen(control));
    }
}
