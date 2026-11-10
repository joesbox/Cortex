using Avalonia.Media;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Cortex.Tests;

public class ConnectorPinProjectionTests
{
    [Fact]
    public void ConnectorPinCatalog_DefinesAll35PinsWithoutExternalReferenceLabels()
    {
        Assert.Equal(35, ConnectorPinCatalog.Pins.Count);
    }

    [Fact]
    public void ConnectorPins_ExposePinNumbersAndConfiguredColors()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            OutputChannel settingsChannel = viewModel.SettingsDataView.ChannelsStaticData[2];
            settingsChannel.ChanType = OutputChannel.ChannelType.MATH;

            Assert.All(
                viewModel.ConnectorPins,
                pin => Assert.Equal(pin.PinNumber.ToString(CultureInfo.InvariantCulture), pin.PinNumberText));

            ConnectorPinDisplayItem pin = viewModel.ConnectorPins.Single(item => item.PinNumber == 1);
            SolidColorBrush pinBrush = Assert.IsType<SolidColorBrush>(pin.PinBrush);
            Assert.Equal(Color.Parse(ConnectorPinPalette.OutputMath), pinBrush.Color);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Theory]
    [InlineData(OutputChannel.ChannelType.Digital)]
    [InlineData(OutputChannel.ChannelType.PWM)]
    [InlineData(OutputChannel.ChannelType.Intermittent)]
    [InlineData(OutputChannel.ChannelType.Analogue)]
    [InlineData(OutputChannel.ChannelType.AnalogueScaled)]
    [InlineData(OutputChannel.ChannelType.CAN)]
    [InlineData(OutputChannel.ChannelType.CAN_PWM)]
    [InlineData(OutputChannel.ChannelType.MATH)]
    public void ConnectorOutputPalette_UsesGlobalGreenAcrossPinAndLegend(OutputChannel.ChannelType type)
    {
        MainWindowViewModel viewModel = CreateViewModel();
        try
        {
            viewModel.SettingsDataView.ChannelsStaticData[2].ChanType = type;
            ConnectorPinDisplayItem pin = viewModel.ConnectorPins.Single(item => item.PinNumber == 1);
            ConnectorLegendItem legend = viewModel.ConnectorLegendItems.Single(item =>
                item.Text == ConnectorPinDisplayItem.FormatOutputChannelType(type));
            Assert.Equal(Color.Parse("#68C579"), Assert.IsType<SolidColorBrush>(pin.PinBrush).Color);
            Assert.Equal(Color.Parse("#68C579"), Assert.IsType<SolidColorBrush>(legend.Background).Color);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void ConnectorPins_ProjectOutputSettingsAndLiveValues()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            OutputChannel settingsChannel = viewModel.SettingsDataView.ChannelsStaticData[2];
            OutputChannel liveChannel = viewModel.LiveDataView.ChannelsLiveData[2];

            settingsChannel.ChanType = OutputChannel.ChannelType.PWM;
            settingsChannel.Enabled = 1;
            settingsChannel.PWMMin = 20;
            settingsChannel.PWMMax = 95;
            settingsChannel.CurrentThresholdLow = 0.25f;
            settingsChannel.CurrentThresholdHigh = 8.75f;
            settingsChannel.InputControlPin = InputPinCatalog.DIChannelInputPins[0];
            settingsChannel.Name = ['F', 'U', 'L'];

            liveChannel.Enabled = 1;
            liveChannel.CurrentValue = 2.5f;
            liveChannel.PWMSetDuty = 45;

            ConnectorPinDisplayItem pin = viewModel.ConnectorPins.Single(item => item.PinNumber == 1);

            Assert.Equal("OUT 3", pin.BadgeText);
            Assert.Equal("PWM output", pin.ConfiguredType);
            Assert.Equal("Active", pin.ActiveState);
            Assert.Equal("2.5 A / 45%", pin.LiveValue);
            Assert.Contains("Name: FUL", pin.SettingsSummary);
            Assert.Contains("Control input: Digital 1", pin.SettingsSummary);
            Assert.Contains("PWM 20-95%", pin.LimitsSummary);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void ConnectorPins_ProjectInactiveOutputFromLiveEnabledState()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            OutputChannel settingsChannel = viewModel.SettingsDataView.ChannelsStaticData[2];
            OutputChannel liveChannel = viewModel.LiveDataView.ChannelsLiveData[2];
            settingsChannel.Enabled = 1;
            liveChannel.Enabled = 0;
            liveChannel.CurrentValue = 0f;
            liveChannel.PWMSetDuty = 0;
            liveChannel.Override = false;

            ConnectorPinDisplayItem pin = viewModel.ConnectorPins.Single(item => item.PinNumber == 1);

            Assert.Equal("Inactive", pin.ActiveState);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void ConnectorPins_ProjectAnalogueAndDigitalInputFallbacks()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            AnalogueInput settingsAnalogue = viewModel.SettingsDataView.AnalogueInputsStaticData[0];
            AnalogueInput liveAnalogue = viewModel.LiveDataView.AnalogueInputsLiveData[0];
            DigitalInput settingsDigital = viewModel.SettingsDataView.DigitalInputsStaticData[0];

            settingsAnalogue.ChanType = AnalogueInput.AnalogueChannelType.Active;
            settingsAnalogue.Units = AnalogueInput.AnalogueUnits.PSI;
            settingsAnalogue.ConfigRangeMin = 10f;
            settingsAnalogue.ConfigRangeMax = 80f;
            liveAnalogue.InputValue = 42.5f;
            liveAnalogue.InputVoltage = 2.2f;

            settingsDigital.IsActiveHigh = false;

            ConnectorPinDisplayItem analoguePin = viewModel.ConnectorPins.Single(item => item.PinNumber == 25);
            ConnectorPinDisplayItem digitalPin = viewModel.ConnectorPins.Single(item => item.PinNumber == 14);

            Assert.Equal("AIN 1", analoguePin.BadgeText);
            Assert.Equal("Active sensor input", analoguePin.ConfiguredType);
            Assert.Equal("42.5 psi", analoguePin.LiveValue);
            Assert.Contains("Range 10.00 to 80.00", analoguePin.LimitsSummary);

            Assert.Equal("DIN 1", digitalPin.BadgeText);
            Assert.Equal("Digital input", digitalPin.ConfiguredType);
            Assert.Equal("Unavailable", digitalPin.ActiveState);
            Assert.Equal("No configurable settings", digitalPin.SettingsSummary);
            Assert.Equal("Raw electrical input", digitalPin.LimitsSummary);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void ConnectorPins_ProjectRawDigitalInputLiveState()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            DigitalInput settingsDigital = viewModel.SettingsDataView.DigitalInputsStaticData[0];
            DigitalInput liveDigital = viewModel.LiveDataView.DigitalInputsLiveData[0];
            settingsDigital.IsActiveHigh = true;
            liveDigital.LiveElectricalState = true;

            ConnectorPinDisplayItem digitalPin = viewModel.ConnectorPins.Single(item => item.PinNumber == 14);

            Assert.Equal("High", digitalPin.ActiveState);
            Assert.Equal("Electrical HIGH", digitalPin.LiveValue);
            Assert.Equal("No configurable settings", digitalPin.SettingsSummary);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void ConnectorLegendItems_ShowOnlyConfiguredOutputSubtypes()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            int[] connectorOutputChannelIndexes = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
            foreach (int channelIndex in connectorOutputChannelIndexes)
            {
                viewModel.SettingsDataView.ChannelsStaticData[channelIndex].ChanType = OutputChannel.ChannelType.PWM;
            }

            viewModel.SettingsDataView.ChannelsStaticData[0].ChanType = OutputChannel.ChannelType.MATH;

            string[] legendTexts = viewModel.ConnectorLegendItems.Select(item => item.Text).ToArray();

            Assert.Contains("MATH output", legendTexts);
            Assert.Contains("PWM output", legendTexts);
            Assert.DoesNotContain("Digital output", legendTexts);
            Assert.Equal(1, legendTexts.Count(text => text == "PWM output"));

            ConnectorLegendItem mathLegend = viewModel.ConnectorLegendItems.Single(item => item.Text == "MATH output");
            ConnectorLegendItem pwmLegend = viewModel.ConnectorLegendItems.Single(item => item.Text == "PWM output");
            SolidColorBrush mathBrush = Assert.IsType<SolidColorBrush>(mathLegend.Background);
            SolidColorBrush pwmBrush = Assert.IsType<SolidColorBrush>(pwmLegend.Background);

            Assert.Equal(Color.Parse(ConnectorPinPalette.OutputMath), mathBrush.Color);
            Assert.Equal(Color.Parse(ConnectorPinPalette.OutputPwm), pwmBrush.Color);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    private static MainWindowViewModel CreateViewModel()
    {
        MainWindowViewModel viewModel = new(new TestAppCloser());
        StopUiUpdateTimer(viewModel);
        return viewModel;
    }

    private static void StopUiUpdateTimer(MainWindowViewModel viewModel)
    {
        FieldInfo? field = typeof(MainWindowViewModel).GetField("_uiUpdateTimer", BindingFlags.NonPublic | BindingFlags.Instance);
        object? timerObject = field?.GetValue(viewModel);
        if (timerObject is System.Timers.Timer timer)
        {
            timer.Stop();
        }
    }

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
