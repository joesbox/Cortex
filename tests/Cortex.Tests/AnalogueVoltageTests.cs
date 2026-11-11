using Avalonia.Media;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using Cortex.ViewModels.IoFlow;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Cortex.Tests;

public sealed class AnalogueVoltageTests
{
    [Fact]
    public void StatusConnectorAndLiveChartConsumeConnectorVoltsWithoutAnotherGain()
    {
        var viewModel = new MainWindowViewModel(new TestAppCloser());
        var timer = Assert.IsType<System.Timers.Timer>(typeof(MainWindowViewModel)
            .GetField("_uiUpdateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel));
        timer.Stop();
        try
        {
            var input = viewModel.LiveDataView.AnalogueInputsLiveData[0];
            input.ChanType = AnalogueInput.AnalogueChannelType.RawVoltage;
            var channel = viewModel.LiveDataView.ChannelsLiveData[0];
            channel.ChanType = OutputChannel.ChannelType.Analogue;
            channel.InputControlPin = InputPinCatalog.ANAChannelInputPins[0];
            var connector = viewModel.ConnectorPins.Single(pin => pin.PinNumber == 25);
            foreach (var (voltage, text) in new[] { (0f, "0.0 V"), (2.5f, "2.5 V"), (5f, "5.0 V") })
            {
                input.InputVoltage = voltage;
                input.InputValue = voltage;
                typeof(MainWindowViewModel).GetMethod("UpdateChannelAnalogueVoltageDisplayValues",
                    BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(viewModel, null);
                Assert.Equal(text, channel.AnalogueInputVoltageDisplay);
                Assert.Equal(text, connector.LiveValue);
                var chartValue = typeof(MainWindowViewModel).GetMethod("GetLiveAnalogueSeriesValue",
                    BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [input]);
                Assert.Equal(voltage, Assert.IsType<float>(chartValue));
            }
        }
        finally
        {
            timer.Dispose();
        }
    }

    [Fact]
    public void DigitalModeStatusShowsPolarityFromPullResistor()
    {
        var input = new AnalogueInput(1, false, false) { ChanType = AnalogueInput.AnalogueChannelType.RawVoltage };
        var node = new AnalogueInputNodeViewModel(input, default);
        Assert.Equal("RawVoltage", node.ModeText);

        var changed = new List<string?>();
        node.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        input.ChanType = AnalogueInput.AnalogueChannelType.Digital;
        input.PullDownEnable = true;
        Assert.Equal("Digital active high", node.ModeText);

        changed.Clear();
        input.PullUpEnable = true;
        Assert.False(input.PullDownEnable);
        Assert.Equal("Digital active low", node.ModeText);
        Assert.Contains(nameof(AnalogueInputNodeViewModel.ModeText), changed);
    }

    [Theory]
    [InlineData(AnalogueInput.AnalogueChannelType.RawVoltage)]
    [InlineData(AnalogueInput.AnalogueChannelType.Digital)]
    public void ConnectorVoltageDefaultsReadoutsAndOutputRangesUseFiveVolts(AnalogueInput.AnalogueChannelType mode)
    {
        var input = new AnalogueInput(1, false, false) { ChanType = mode, InputVoltage = 5, InputValue = 5 };
        var node = new AnalogueInputNodeViewModel(input, default);
        node.UpdateLiveState(input);
        Assert.True(input.ReportsConnectorVoltage);
        Assert.Equal(5, input.CalibrationVolt2);
        Assert.Equal(5, input.ConfigRangeMax);
        Assert.Equal("5.0 V", node.LiveVoltageText);
        Assert.Equal("5.0", node.LiveValueText);
        Assert.Equal("Connector voltage (0-5 V)", node.ChartLabel);

        input.ConfigRangeMax = 100;
        var channel = new OutputChannel { ChannelNumber = 1, InputControlPin = InputPinCatalog.ANAChannelInputPins[0] };
        var output = new OutputNodeViewModel(channel, default, [input], [channel]);
        Assert.Equal(0, output.InputMinimum);
        Assert.Equal(5, output.InputMaximum);
    }

    [Fact]
    public void MainLiveChartKeepsConnectorAxisAtZeroToFiveAcrossUpdatesAndPreservesOtherUnits()
    {
        var viewModel = new MainWindowViewModel(new TestAppCloser());
        var timer = Assert.IsType<System.Timers.Timer>(typeof(MainWindowViewModel)
            .GetField("_uiUpdateTimer", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(viewModel));
        timer.Stop();
        try
        {
            // Test-owned toggles have no preference-persistence callbacks.
            viewModel.ChannelLiveSeriesToggles.Clear();
            viewModel.AnalogueLiveSeriesToggles.Clear();
            int offset = viewModel.LiveDataView.ChannelsLiveData.Count;
            for (int i = 0; i < viewModel.SeriesCollection.Count; i++)
            {
                var line = Assert.IsType<LineSeries<ObservablePoint>>(viewModel.SeriesCollection[i]);
                var toggle = new ChartSeriesToggleItem("", Brushes.Green, line, false);
                if (i < offset)
                    viewModel.ChannelLiveSeriesToggles.Add(toggle);
                else
                    viewModel.AnalogueLiveSeriesToggles.Add(toggle);
            }
            var input = viewModel.LiveDataView.AnalogueInputsLiveData[0];
            input.ChanType = AnalogueInput.AnalogueChannelType.RawVoltage;
            viewModel.AnalogueLiveSeriesToggles[0].IsEnabled = true;
            RefreshAxes(viewModel);
            var series = Assert.IsType<LineSeries<ObservablePoint>>(
                viewModel.SeriesCollection[viewModel.LiveDataView.ChannelsLiveData.Count]);
            var axis = viewModel.YAxes[series.ScalesYAt];
            Assert.Equal(0, axis.MinLimit);
            Assert.Equal(5, axis.MaxLimit);
            ApplyLimits(viewModel, series.ScalesYAt, 2);
            Assert.Equal(0, axis.MinLimit);
            Assert.Equal(5, axis.MaxLimit);

            input.ChanType = AnalogueInput.AnalogueChannelType.Active;
            input.Units = AnalogueInput.AnalogueUnits.Percent;
            RefreshAxes(viewModel);
            axis = viewModel.YAxes[series.ScalesYAt];
            ApplyLimits(viewModel, series.ScalesYAt, 80);
            Assert.Null(axis.MinLimit);
            Assert.Equal(81, axis.MaxLimit);

            input.Units = AnalogueInput.AnalogueUnits.Volts;
            RefreshAxes(viewModel);
            axis = viewModel.YAxes[series.ScalesYAt];
            ApplyLimits(viewModel, series.ScalesYAt, 12);
            Assert.Null(axis.MinLimit);
            Assert.Equal(13, axis.MaxLimit);

            input.ChanType = AnalogueInput.AnalogueChannelType.Digital;
            RefreshAxes(viewModel);
            axis = viewModel.YAxes[series.ScalesYAt];
            Assert.Equal(0, axis.MinLimit);
            Assert.Equal(5, axis.MaxLimit);

            viewModel.ChannelLiveSeriesToggles[0].IsEnabled = true;
            RefreshAxes(viewModel);
            axis = viewModel.YAxes[series.ScalesYAt];
            Assert.Equal(2, viewModel.YAxes.Length);
            ApplyLimits(viewModel, 0, 10);
            Assert.Equal(11, viewModel.YAxes[0].MaxLimit);
            Assert.Equal(0, axis.MinLimit);
            Assert.Equal(5, axis.MaxLimit);

            var calibrated = viewModel.LiveDataView.AnalogueInputsLiveData[1];
            calibrated.ChanType = AnalogueInput.AnalogueChannelType.Active;
            calibrated.Units = AnalogueInput.AnalogueUnits.Volts;
            viewModel.AnalogueLiveSeriesToggles[1].IsEnabled = true;
            RefreshAxes(viewModel);
            axis = viewModel.YAxes[series.ScalesYAt];
            ApplyLimits(viewModel, series.ScalesYAt, 12);
            Assert.Null(axis.MinLimit);
            Assert.Equal(13, axis.MaxLimit);

            calibrated.Units = AnalogueInput.AnalogueUnits.Percent;
            RefreshAxes(viewModel);
            axis = viewModel.YAxes[series.ScalesYAt];
            Assert.Equal(3, viewModel.YAxes.Length);
            Assert.Equal(0, axis.MinLimit);
            Assert.Equal(5, axis.MaxLimit);
        }
        finally
        {
            timer.Dispose();
        }
    }

    private static void RefreshAxes(MainWindowViewModel viewModel) =>
        typeof(MainWindowViewModel).GetMethod("RefreshLiveChartAxes", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, null);

    private static void ApplyLimits(MainWindowViewModel viewModel, int axis, double maximum) =>
        typeof(MainWindowViewModel).GetMethod("ApplyLiveChartAxisLimits", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), new Dictionary<int, double> { [axis] = maximum }]);

    private sealed class TestAppCloser : IAppCloser
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
