using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cortex.Controls;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using Cortex.ViewModels.IoFlow;
using Cortex.Views;
using System.Reflection;
using Xunit;

namespace Cortex.Ui.Tests;

public sealed class IoFlowSettingsParityTests
{
    [AvaloniaFact]
    public async Task SettingsDefaultsToFlowHidesOnlyLegacyIoAndBindsRelevantSettings()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"cortex-flow-parity-{Guid.NewGuid():N}");
        var vm = new MainWindowViewModel(new ChannelCanTxSettingsTests.TestAppCloser(),
            new MainWindowViewModelDependencies(ioFlowGraphStore: new IoFlowGraphStore(Path.Combine(directory, "graph.json"))));
        var timers = new[] { "_uiUpdateTimer", "_pollTimer", "_commsTimer" }
            .Select(name => (System.Timers.Timer)typeof(MainWindowViewModel).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(vm)!).ToArray();
        foreach (var timer in timers) timer.Stop();
        OutputChannel channel = vm.SettingsDataView.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.Enabled = 1;
        channel.MathRules[0].Operation = (byte)OutputChannel.MathOperation.GREATER_THAN;
        vm.IoFlowGraph.AddNextAnalogueNodeCommand.Execute(null);
        var view = new SettingsTabView { DataContext = vm };
        var window = new Window { Width = 2000, Height = 1500, Content = view };
        try
        {
            window.Show();
            Layout();
            typeof(MainWindowViewModel).GetField("_controllerConfigBaseline", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(vm, typeof(MainWindowViewModel).GetMethod("DeepCopyDataStructures", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(vm, [vm.SettingsDataView]));
            var tabs = view.FindControl<TabControl>("SettingsTabs")!;
            Assert.Same(view.FindControl<TabItem>("IoFlowTab"), tabs.SelectedItem);
            Assert.False(view.FindControl<TabItem>("LegacyIoTab")!.IsVisible);
            Assert.False(view.FindControl<TabItem>("LegacyIoTab")!.IsEnabled);
            Assert.Equal(new[] { "I/O Flow", "CAN Messages", "Connector", "System" },
                tabs.Items.OfType<TabItem>().Where(tab => tab.IsVisible).Select(tab => tab.Header?.ToString()));

            var flow = Assert.Single(view.GetVisualDescendants().OfType<IoFlowTabView>());
            MathRuleNodeViewModel rule = Assert.Single(vm.IoFlowGraph.MathRuleNodes.Where(node => node.OwnerChannelNumber == channel.ChannelNumber));
            Control[] ruleControls = flow.GetVisualDescendants().OfType<Control>()
                .Where(control => ReferenceEquals(control.DataContext, rule)).ToArray();
            ToggleSwitch hysteresis = Assert.Single(ruleControls.OfType<ToggleSwitch>(), control => control.Classes.Contains("mathHysteresis"));
            Assert.True(hysteresis.IsEffectivelyEnabled);
            Assert.False(Assert.Single(ruleControls.OfType<TextBox>(), control => control.Classes.Contains("mathOnThreshold")).IsEffectivelyVisible);
            hysteresis.IsChecked = true;
            Layout();
            Assert.True(rule.Rule.IsHysteresisEnabled);
            TextBox on = Assert.Single(ruleControls.OfType<TextBox>(), control => control.Classes.Contains("mathOnThreshold"));
            TextBox off = Assert.Single(ruleControls.OfType<TextBox>(), control => control.Classes.Contains("mathOffThreshold"));
            on.Text = "3.5";
            off.Text = "2.5";
            Layout();
            Assert.Equal(3.5f, rule.Rule.OnThreshold);
            Assert.Equal(2.5f, rule.Rule.OffThreshold);
            rule.Rule.Operation = (byte)OutputChannel.MathOperation.ADD;
            Layout();
            Assert.False(hysteresis.IsEffectivelyVisible);
            Assert.False(on.IsEffectivelyVisible);
            foreach (OutputChannel.MathOperation operation in Enum.GetValues<OutputChannel.MathOperation>())
            {
                rule.Rule.Operation = (byte)operation;
                Layout();
                bool comparison = operation >= OutputChannel.MathOperation.GREATER_THAN
                    && operation <= OutputChannel.MathOperation.LESS_EQUAL;
                Assert.Equal(comparison, hysteresis.IsEffectivelyVisible);
                Assert.Equal(comparison, on.IsEffectivelyVisible);
            }
            rule.Rule.Operation = (byte)OutputChannel.MathOperation.ADD;
            Layout();

            AnalogueInputNodeViewModel analogue = Assert.Single(vm.IoFlowGraph.AnalogueNodes);
            analogue.Input.ChanType = AnalogueInput.AnalogueChannelType.Active;
            Layout();
            SetText("analogueRangeMin", analogue, "-10");
            SetText("analogueRangeMax", analogue, "120");
            Assert.Equal(-10, analogue.Input.ConfigRangeMin);
            Assert.Equal(120, analogue.Input.ConfigRangeMax);
            analogue.Input.ChanType = AnalogueInput.AnalogueChannelType.NTC;
            Layout();
            SetText("analogueNtcBeta", analogue, "3950");
            SetText("analogueNtcResistance", analogue, "10000");
            Assert.Equal(3950, analogue.Input.NtcBeta);
            Assert.Equal(10000, analogue.Input.NtcNominalResistance);

            OutputNodeViewModel output = vm.IoFlowGraph.OutputNodes.Single(node => ReferenceEquals(node.Channel, channel));
            Assert.DoesNotContain(flow.GetVisualDescendants().OfType<ComboBox>(),
                control => control.Classes.Contains("outputChannelType"));
            Assert.DoesNotContain(flow.GetVisualDescendants().OfType<Button>(),
                control => control.Classes.Contains("addOutputMathRule"));
            ComboBox mathsMode = Assert.Single(flow.GetVisualDescendants().OfType<ComboBox>(),
                control => control.Classes.Contains("mathOutputMode") && ReferenceEquals(control.DataContext, output));
            Assert.DoesNotContain(flow.GetVisualDescendants().OfType<ToggleSwitch>(),
                control => control.Classes.Contains("outputEnabled") && ReferenceEquals(control.DataContext, output));
            Assert.True(mathsMode.IsEffectivelyEnabled);
            channel.Enabled = 0;
            Layout();
            Assert.True(mathsMode.IsEffectivelyEnabled);
            Assert.Equal(0, channel.Enabled);
            Assert.True(vm.HasPendingConfigChanges);
            channel.Enabled = 1;
            Assert.Equal(1, channel.Enabled);
            analogue.Input.ChanType = AnalogueInput.AnalogueChannelType.RawVoltage;
            await vm.IoFlowGraph.RemoveConnectionCommand.ExecuteAsync(vm.IoFlowGraph.Connections.Single(wire =>
                ReferenceEquals(wire.Target.Node, output)));
            await vm.IoFlowGraph.ConnectNodes(analogue, output);
            Layout();
            ListBox mode = Assert.Single(flow.GetVisualDescendants().OfType<ListBox>(),
                control => ReferenceEquals(control.ItemsSource, output.AvailableInputChannelTypes));
            Assert.Equal(2, mode.ItemCount);
            mode.SelectedItem = output.AvailableInputChannelTypes.Single(option => option.ChannelType == OutputChannel.ChannelType.AnalogueScaled);
            Layout();
            Assert.Equal(OutputChannel.ChannelType.AnalogueScaled, channel.ChanType);
            AssertSegmentedChoiceStyle(mode);
            SelectOutputTab("Analogue");
            Slider pwmMin = Assert.Single(flow.GetVisualDescendants().OfType<Slider>(),
                control => control.Classes.Contains("outputPwmMin") && ReferenceEquals(control.DataContext, output));
            Slider pwmMax = Assert.Single(flow.GetVisualDescendants().OfType<Slider>(),
                control => control.Classes.Contains("outputPwmMax") && ReferenceEquals(control.DataContext, output));
            pwmMax.Value = 80;
            pwmMin.Value = 20;
            Layout();
            Assert.Equal(20, channel.PWMMin);
            Assert.Equal(80, channel.PWMMax);

            mode.SelectedItem = output.AvailableInputChannelTypes.Single(option => option.ChannelType == OutputChannel.ChannelType.Analogue);
            channel.OnThreshold = 4;
            channel.OffThreshold = 1;
            Layout();
            ToggleSwitch direction = Assert.Single(flow.GetVisualDescendants().OfType<ToggleSwitch>(),
                control => control.Classes.Contains("analogueThresholdDirection") && ReferenceEquals(control.DataContext, output));
            direction.IsChecked = true;
            Layout();
            Assert.Equal(1, channel.OnThreshold);
            Assert.Equal(4, channel.OffThreshold);
            SelectOutputTab("Timing");
            Slider delay = Assert.Single(flow.GetVisualDescendants().OfType<Slider>(),
                control => ReferenceEquals(control.DataContext, output)
                    && Equals(ToolTip.GetTip(control), "Set how long to wait before turning the output on once delayed ON is enabled."));
            channel.IsDelayedOnEnabled = false;
            Layout();
            Assert.False(delay.IsEffectivelyVisible);
            channel.IsDelayedOnEnabled = true;
            Layout();
            Assert.True(delay.IsEffectivelyVisible);

            Assert.All(flow.GetVisualDescendants().OfType<Slider>().Where(slider => ReferenceEquals(slider.DataContext, output)),
                slider =>
                {
                    Assert.Contains("sliderWithValue", Assert.IsType<Grid>(slider.Parent).Classes);
                    Border group = slider.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("settingGroup"));
                    Assert.Contains(group.GetVisualDescendants().OfType<TextBlock>(), text => text.Classes.Contains("ioFlowFieldLabel"));
                });

            SelectOutputTab("Current");
            Slider thresholdHigh = Assert.Single(flow.GetVisualDescendants().OfType<Slider>(),
                control => ReferenceEquals(control.DataContext, output) && Equals(ToolTip.GetTip(control), "Turn off threshold high"));
            Assert.True(thresholdHigh.IsSnapToTickEnabled);
            SliderValueEditor thresholdHighValue = Assert.Single(((Grid)thresholdHigh.Parent!).Children.OfType<SliderValueEditor>());
            thresholdHigh.Value = 0;
            Layout();
            Assert.Equal("0.0 A", thresholdHighValue.Text);
            double sliderWidth = thresholdHigh.Bounds.Width;
            Rect sliderBounds = thresholdHigh.Bounds;
            Rect valueBounds = thresholdHighValue.Bounds;
            Assert.True(valueBounds.X >= sliderBounds.Right, "The slider value should sit beside the slider.");
            Assert.True(valueBounds.Y < sliderBounds.Bottom && valueBounds.Bottom > sliderBounds.Y, "The value should share the slider's row.");
            output.CurrentThresholdHighValue = 16.94;
            Layout();
            Assert.Equal(16.9f, channel.CurrentThresholdHigh);
            Assert.Equal("16.9 A", thresholdHighValue.Text);
            Assert.Equal(sliderWidth, thresholdHigh.Bounds.Width);
            Border thresholdGroup = thresholdHigh.GetVisualAncestors().OfType<Border>().First(border => border.Classes.Contains("settingGroup"));
            Point valueRight = thresholdHighValue.TranslatePoint(new Point(thresholdHighValue.Bounds.Width, 0), thresholdGroup)!.Value;
            Assert.Equal(thresholdGroup.Bounds.Width - thresholdGroup.Padding.Right - thresholdGroup.BorderThickness.Right, valueRight.X, 1);
            Point sliderLeft = thresholdHigh.TranslatePoint(default, thresholdGroup)!.Value;
            Assert.Equal(thresholdGroup.Padding.Left + thresholdGroup.BorderThickness.Left, sliderLeft.X, 1);
            output.InrushCurrentLimitValue = 23.46;
            Assert.Equal(23.5f, channel.InrushCurrentLimit);

            Point valueCentre = thresholdHighValue.TranslatePoint(new Point(thresholdHighValue.Bounds.Width / 2, thresholdHighValue.Bounds.Height / 2), window)!.Value;
            window.MouseDown(valueCentre, MouseButton.Left);
            window.MouseUp(valueCentre, MouseButton.Left);
            Layout();
            Assert.True(thresholdHighValue.IsEditing);
            Assert.Equal("16.9", thresholdHighValue.EditorBox.Text);
            Assert.Equal(sliderWidth, thresholdHigh.Bounds.Width);
            Assert.True(thresholdHighValue.EditorBox.IsFocused);
            var digitDown = new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.D7, PhysicalKey = PhysicalKey.Digit7 };
            thresholdHighValue.EditorBox.RaiseEvent(digitDown);
            Assert.False(digitDown.Handled, "Handled digit key downs stop Windows producing the typed character.");
            window.KeyTextInput("7");
            Layout();
            Assert.Equal("7", thresholdHighValue.EditorBox.Text);
            vm.IoFlowGraph.SelectedNodes.Add(output);
            int nodeCount = vm.IoFlowGraph.Nodes.Count;
            thresholdHighValue.EditorBox.CaretIndex = thresholdHighValue.EditorBox.Text!.Length;
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Assert.Equal(nodeCount, vm.IoFlowGraph.Nodes.Count);
            thresholdHighValue.EditorBox.SelectAll();
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Assert.Equal(nodeCount, vm.IoFlowGraph.Nodes.Count);
            vm.IoFlowGraph.SelectedNodes.Clear();
            thresholdHighValue.EditorBox.Text = "abc";
            Assert.False(thresholdHighValue.Commit());
            Assert.Equal("Enter a number", thresholdHighValue.ValidationMessage);
            Assert.True(thresholdHighValue.IsEditing);
            thresholdHighValue.EditorBox.Text = "20";
            Assert.False(thresholdHighValue.Commit());
            Assert.Equal("Enter a value between 0.0 and 17.0", thresholdHighValue.ValidationMessage);
            Layout();
            Assert.Contains("invalid", thresholdHighValue.EditorBox.Classes);
            Assert.Equal(16.9f, channel.CurrentThresholdHigh);
            thresholdHighValue.EditorBox.Text = "12.34 A";
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Layout();
            Assert.False(thresholdHighValue.IsEditing);
            Assert.Equal(12.3f, channel.CurrentThresholdHigh);
            Assert.Equal("12.3 A", thresholdHighValue.Text);
            Assert.True(thresholdHighValue.BeginEdit());
            thresholdHighValue.EditorBox.Text = "5";
            thresholdHighValue.Cancel();
            Assert.Equal(12.3f, channel.CurrentThresholdHigh);

            Border retryGroup = Assert.Single(flow.GetVisualDescendants().OfType<Border>(),
                border => border.Classes.Contains("retryCountGroup") && ReferenceEquals(border.DataContext, output));
            Grid retryRow = Assert.IsType<Grid>(retryGroup.Parent);
            Assert.False(output.ShowDutyConfiguration);
            Assert.Equal(retryRow.Bounds.Width, retryGroup.Bounds.Width, 1);
            channel.ChanType = OutputChannel.ChannelType.PWM;
            Layout();
            Assert.True(output.ShowDutyConfiguration);
            Assert.Equal((retryRow.Bounds.Width - retryRow.ColumnSpacing) / 2, retryGroup.Bounds.Width, 1);
            Assert.True(retryGroup.Bounds.X > retryRow.Bounds.Width / 2);
            channel.ChanType = OutputChannel.ChannelType.Analogue;
            Layout();

            void SetText(string cssClass, object node, string text)
            {
                TextBox control = Assert.Single(flow.GetVisualDescendants().OfType<TextBox>(),
                    box => box.Classes.Contains(cssClass) && ReferenceEquals(box.DataContext, node));
                Assert.True(control.IsEffectivelyVisible);
                control.Text = text;
                Layout();
            }

            void SelectOutputTab(string header)
            {
                TabItem tab = Assert.Single(flow.GetVisualDescendants().OfType<TabItem>(),
                    item => ReferenceEquals(item.DataContext, output) && Equals(item.Header, header));
                tab.GetVisualAncestors().OfType<TabControl>().First().SelectedItem = tab;
                Layout();
            }
        }
        finally
        {
            window.Close();
            view.DataContext = null;
            foreach (var timer in timers) timer.Dispose();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }

        void Layout()
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
    }

    private static void AssertSegmentedChoiceStyle(ListBox mode)
    {
        Assert.Contains("segmentedChoice", mode.Classes);
        var items = mode.GetVisualDescendants().OfType<ListBoxItem>().ToArray();
        Assert.Equal(mode.ItemCount, items.Length);
        foreach (var item in items)
        {
            var segment = Assert.Single(item.GetVisualDescendants().OfType<Border>(), border => border.Name == "PART_Segment");
            Assert.Equal(new CornerRadius(6), segment.CornerRadius);
            if (item.IsSelected)
            {
                var fill = Assert.IsType<LinearGradientBrush>(segment.Background);
                Assert.Equal(new[] { Color.Parse("#FF5A4280"), Color.Parse("#FF443061") }, fill.GradientStops.Select(stop => stop.Color));
                Assert.Equal(Color.Parse("#88C8A7FF"), Assert.IsAssignableFrom<ISolidColorBrush>(segment.BorderBrush).Color);
                Assert.Equal(FontWeight.SemiBold, item.FontWeight);
            }
            else
            {
                Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(segment.Background).Color);
                Assert.Equal(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(segment.BorderBrush).Color);
            }
        }
    }
}
