using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Cortex.Views;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class RecoveryCodeDialogTests
{
    [AvaloniaFact]
    public async Task DoneRequiresAcknowledgementAndCloseButtonIsAbsent()
    {
        var owner = new Window();
        var dialog = new RecoveryCodeDialogWindow("test-recovery-code");
        owner.Show();
        try
        {
            Task<bool> result = dialog.ShowDialog<bool>(owner);
            Dispatcher.UIThread.RunJobs();
            var checkbox = dialog.FindControl<CheckBox>("AcknowledgeCheckBox")!;
            var done = dialog.FindControl<Button>("DoneButton")!;
            Assert.Null(dialog.FindControl<Button>("CloseButton"));
            Assert.False(done.IsEnabled);

            checkbox.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(done.IsEnabled);

            checkbox.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(done.IsEnabled);
            Assert.False(result.IsCompleted);

            checkbox.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            done.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(await result);
        }
        finally
        {
            dialog.Close();
            owner.Close();
        }
    }
}
