using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using Cortex.Views;
using System.Reflection;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class ChannelCanTxSettingsTests
{
    [AvaloniaFact]
    public async Task SettingsControlsAddTwoAssignmentsSelectWithoutMutationAndRemoveOnlyOne()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-channel-tx-{Guid.NewGuid():N}");
        var viewModel = new MainWindowViewModel(new TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "graph.json"))));
        System.Timers.Timer[] timers = new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" }
            .Select(name => Assert.IsType<System.Timers.Timer>(typeof(MainWindowViewModel)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel))).ToArray();
        foreach (var timer in timers)
        {
            timer.Stop();
        }

        var view = new SettingsTabView { DataContext = viewModel };
        ShowRetainedLegacyTabForRegression(view);
        var window = new Window { Width = 1600, Height = 1100, Content = view };
        var flow = new IoFlowTabView { DataContext = viewModel };
        var flowWindow = new Window { Width = 1000, Height = 800, Content = flow };
        try
        {
            viewModel.SelectedChannel = viewModel.SettingsDataView.ChannelsStaticData[0];
            for (int index = 0; index < 2; index++)
            {
                CustomCanMessage message = viewModel.SettingsDataView.CustomCanMessagesStaticData[index];
                message.Id = (uint)(0x123 + index);
                message.Dlc = 8;
                message.Flags = 0x04;
                CustomCanSignal signal = viewModel.SettingsDataView.CustomCanSignalsStaticData[index];
                signal.IsEnabled = true;
                signal.MessageIndex = (byte)index;
            }
            CustomCanMessage rx = viewModel.SettingsDataView.CustomCanMessagesStaticData[2];
            rx.Id = 0x125;
            rx.Dlc = 8;
            rx.Flags = 0x01;
            CustomCanSignal rxSignal = viewModel.SettingsDataView.CustomCanSignalsStaticData[2];
            rxSignal.IsEnabled = true;
            rxSignal.MessageIndex = 2;
            viewModel.NotifySelectedChannelCanTxChanged();
            window.Show();
            UpdateLayout(window);
            Button add = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Add message"));
            Button remove = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                button => Equals(button.Content, "Remove selected message"));
            ListBox assigned = Assert.Single(view.GetVisualDescendants().OfType<ListBox>(),
                list => ReferenceEquals(list.ItemsSource, viewModel.AssignedChannelCanTxMessages));
            ComboBox candidate = Assert.Single(view.GetVisualDescendants().OfType<ComboBox>(),
                box => ReferenceEquals(box.ItemsSource, viewModel.SelectedChannelCanTxMessages));
            Assert.Equal(2, candidate.ItemCount);
            Assert.DoesNotContain(rx, candidate.Items.Cast<CustomCanMessage>());
            Assert.Empty(viewModel.IoFlowGraph.CanTxNodes);
            Assert.False(remove.IsEffectivelyEnabled);
            Assert.True(add.IsEffectivelyEnabled);
            Assert.Same(viewModel.AddSelectedChannelCanTxMessageCommand, add.Command);
            Assert.Same(viewModel.RemoveSelectedChannelCanTxMessageCommand, remove.Command);

            CustomCanMessage first = viewModel.SettingsDataView.CustomCanMessagesStaticData[0];
            CustomCanMessage second = viewModel.SettingsDataView.CustomCanMessagesStaticData[1];
            candidate.SelectedItem = first;
            add.Command!.Execute(add.CommandParameter);
            UpdateLayout(window);
            candidate.SelectedItem = second;
            add.Command!.Execute(add.CommandParameter);
            UpdateLayout(window);
            Assert.Equal(2, assigned.ItemCount);
            Assert.True(first.IsEnabled);
            Assert.True(second.IsEnabled);
            Assert.Equal(0, first.TxTrigger);
            Assert.Equal(0, second.TxTrigger);

            flowWindow.Show();
            UpdateLayout(flowWindow);
            viewModel.IoFlowGraph.SetNextNodeSpawnLocation(new Point(20, 20), default, new Size(600, 400));
            for (int cycle = 0; cycle < 3; cycle++)
            {
                foreach (var tx in viewModel.IoFlowGraph.CanTxNodes)
                {
                    viewModel.IoFlowGraph.SelectedNodes.Add(tx);
                }
                await viewModel.IoFlowGraph.RemoveSelectedNodesCommand.ExecuteAsync(null);
                UpdateLayout(window);
                UpdateLayout(flowWindow);
                Assert.Empty(viewModel.IoFlowGraph.CanTxNodes);

                candidate.SelectedItem = first;
                add.Command!.Execute(add.CommandParameter);
                candidate.SelectedItem = second;
                add.Command!.Execute(add.CommandParameter);
                UpdateLayout(window);
                UpdateLayout(flowWindow);
                Assert.Equal(2, assigned.ItemCount);
                Assert.Equal(2, viewModel.IoFlowGraph.CanTxNodes.Count);
                Assert.Equal(2, viewModel.IoFlowGraph.Connections.Count(wire =>
                    wire.Target.Node is Cortex.ViewModels.IoFlow.CanTxNodeViewModel));
                CanTxNodeRenderingTests.AssertNoTxOverlap(flow, viewModel.IoFlowGraph);
            }

            assigned.SelectedItem = first;
            UpdateLayout(window);
            Assert.Same(first, viewModel.SelectedChannelCanTxMessage);
            assigned.SelectedItem = second;
            UpdateLayout(window);
            Assert.Same(second, viewModel.SelectedChannelCanTxMessage);
            Assert.True(first.IsEnabled);
            Assert.Equal(0, first.TxTrigger);
            remove.Command!.Execute(remove.CommandParameter);
            if (viewModel.RemoveSelectedChannelCanTxMessageCommand.ExecutionTask is Task removal)
            {
                await removal;
            }
            UpdateLayout(window);
            Assert.Equal(1, assigned.ItemCount);
            Assert.Same(first, Assert.Single(viewModel.AssignedChannelCanTxMessages));
            Assert.False(second.IsEnabled);
            Assert.Equal(Constants.CUSTOM_CAN_TX_TRIGGER_ALWAYS, second.TxTrigger);

            first.IsSelected = true;
            viewModel.DeleteCheckedStoredMessagesCommand.Execute(null);
            UpdateLayout(window);
            UpdateLayout(flowWindow);

            ToggleSwitch txSwitch = Assert.Single(view.GetVisualDescendants().OfType<ToggleSwitch>(),
                control => ReferenceEquals(control.Command, viewModel.ToggleSelectedChannelCanTxCommand));
            Assert.Equal(0, assigned.ItemCount);
            Assert.False(txSwitch.IsChecked);
            Assert.False(txSwitch.IsEffectivelyEnabled);
            Assert.False(remove.IsEffectivelyEnabled);
            Assert.DoesNotContain(first, viewModel.SelectedChannelCanTxMessages);
            Assert.DoesNotContain(viewModel.IoFlowGraph.CanTxNodes, node => ReferenceEquals(node.Message, first));
            Assert.DoesNotContain(viewModel.IoFlowGraph.Connections, wire =>
                wire.Target.Node is Cortex.ViewModels.IoFlow.CanTxNodeViewModel);
        }
        finally
        {
            window.Close();
            flowWindow.Close();
            view.DataContext = null;
            flow.DataContext = null;
            foreach (var timer in timers)
            {
                timer.Dispose();
            }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [AvaloniaFact]
    public void MathRuntimePanelsKeepTheirWidthsWithSeparateReadableOperandAndResultSections()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-math-layout-{Guid.NewGuid():N}");
        var viewModel = new MainWindowViewModel(new TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "graph.json"))));
        System.Timers.Timer[] timers = new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" }
            .Select(name => Assert.IsType<System.Timers.Timer>(typeof(MainWindowViewModel)
                .GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel))).ToArray();
        foreach (var timer in timers)
        {
            timer.Stop();
        }

        var settingsView = new SettingsTabView { DataContext = viewModel };
        ShowRetainedLegacyTabForRegression(settingsView);
        var flowView = new IoFlowTabView { DataContext = viewModel };
        var settingsWindow = new Window { Width = 1600, Height = 1100, Content = settingsView };
        var flowWindow = new Window { Width = 1200, Height = 1100, Content = flowView };
        try
        {
            OutputChannel channel = viewModel.SettingsDataView.ChannelsStaticData[0];
            channel.ChanType = OutputChannel.ChannelType.MATH;
            channel.MathRules[0].Operation = (byte)OutputChannel.MathOperation.ADD;
            viewModel.SelectedChannel = channel;
            OutputChannel liveChannel = viewModel.LiveDataView.ChannelsLiveData[0];
            liveChannel.ChanType = OutputChannel.ChannelType.MATH;
            liveChannel.MathRules[0].Evaluated = 1;
            viewModel.IoFlowGraph.PopulateFromCurrentSettings();
            settingsWindow.Show();
            flowWindow.Show();
            UpdateLayout(settingsWindow);
            UpdateLayout(flowWindow);

            Border runtimePanel = Assert.Single(settingsView.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("mathRuntimePanel"));
            Border mathNode = Assert.Single(flowView.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("ioFlowNode")
                    && border.DataContext is Cortex.ViewModels.IoFlow.MathRuleNodeViewModel);
            double panelWidth = runtimePanel.Bounds.Width;
            double nodeWidth = mathNode.Bounds.Width;
            Border outputNode = Assert.Single(flowView.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("ioFlowOutputNode")
                    && border.DataContext is Cortex.ViewModels.IoFlow.OutputNodeViewModel output
                    && output.ChannelNumber == channel.ChannelNumber);
            double outputWidth = outputNode.Bounds.Width;
            Assert.Equal(510, panelWidth);
            Assert.Equal(340, nodeWidth);
            Assert.DoesNotContain(runtimePanel.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text == "All active rule results");

            foreach (float value in new[] { 0f, 9.9f, -123456.789f, float.MaxValue })
            {
                channel.MathRules[0].OperandAConstant = value;
                channel.MathRules[0].OperandBConstant = -value;
                liveChannel.MathRules[0].RuntimeValue = value;
                liveChannel.MathCalculatedRootValue = value;
                viewModel.IoFlowGraph.UpdateLiveChannelSamples(viewModel.LiveDataView.ChannelsLiveData);
                UpdateLayout(settingsWindow);
                UpdateLayout(flowWindow);

                Assert.Equal(panelWidth, runtimePanel.Bounds.Width);
                Assert.Equal(nodeWidth, mathNode.Bounds.Width);
                Assert.Equal(outputWidth, outputNode.Bounds.Width);
                foreach (Control panel in new Control[] { runtimePanel, mathNode })
                {
                    TextBlock[] values = panel.GetVisualDescendants().OfType<TextBlock>()
                        .Where(text => text.Classes.Contains("mathLiveValue")).ToArray();
                    Assert.Equal(3, values.Length);
                    Assert.All(values, text =>
                    {
                        Assert.True(text.FontSize >= 16);
                        Assert.Equal(Avalonia.Media.TextWrapping.Wrap, text.TextWrapping);
                    });
                    Assert.True(values[0].Bounds.Height > 0,
                        $"Panel {panel.GetType().Name} ({panel.DataContext?.GetType().Name}): value bounds {values[0].Bounds}; visible {values[0].IsEffectivelyVisible}");
                    Point? operandA = values[0].TranslatePoint(default, panel);
                    Point? operandB = values[1].TranslatePoint(default, panel);
                    Point? result = values[2].TranslatePoint(default, panel);
                    Assert.True(operandA!.Value.Y < operandB!.Value.Y);
                    Assert.True(operandB.Value.Y < result!.Value.Y);
                    Assert.Equal(viewModel.SelectedMathRuntimeOperandAText, values[0].Text);
                    Assert.Equal(viewModel.SelectedMathRuntimeOperandBText, values[1].Text);
                }
            }

            channel.MathRules[0].Operation = (byte)OutputChannel.MathOperation.ABS;
            UpdateLayout(settingsWindow);
            UpdateLayout(flowWindow);
            foreach (Control panel in new Control[] { runtimePanel, mathNode })
            {
                Assert.Equal(2, panel.GetVisualDescendants().OfType<TextBlock>()
                    .Count(text => text.Classes.Contains("mathLiveValue") && text.IsEffectivelyVisible));
            }
        }
        finally
        {
            settingsWindow.Close();
            flowWindow.Close();
            settingsView.DataContext = null;
            flowView.DataContext = null;
            foreach (var timer in timers)
            {
                timer.Dispose();
            }
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static void UpdateLayout(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ShowRetainedLegacyTabForRegression(SettingsTabView view)
    {
        TabItem legacy = view.FindControl<TabItem>("LegacyIoTab")!;
        legacy.IsVisible = true;
        legacy.IsEnabled = true;
        view.FindControl<TabControl>("SettingsTabs")!.SelectedItem = legacy;
    }

    internal sealed class TestAppCloser : IAppCloser
    {
        public void CloseApp() { }
        public Task<string?> OpenPdmFileContentAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseLocalLogFilePathAsync(string initialDirectory) => Task.FromResult<string?>(null);
        public Task<string?> BrowseDbcFilePathAsync() => Task.FromResult<string?>(null);
        public Task<bool> SavePdmFileContentAsync(string content) => Task.FromResult(true);
        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL") => Task.FromResult(true);
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
        public Task<LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync() => Task.FromResult<LocalFirmwareUpdateSelection?>(null);
        public Task ShowAboutAsync() => Task.CompletedTask;
        public Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel) => Task.CompletedTask;
        public Task<PinSetupDialogResult?> ShowPinSetupWizardAsync(bool isPinAlreadyEnabled) => Task.FromResult<PinSetupDialogResult?>(null);
        public Task<bool> ShowRecoveryCodeAsync(string recoveryCode) => Task.FromResult(true);
        public Task<PinUnlockDialogResult?> ShowPinUnlockDialogAsync(bool allowRecovery, uint retryAfterMs, string? statusMessage) => Task.FromResult<PinUnlockDialogResult?>(null);
    }
}
