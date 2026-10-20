using Cortex.Services;
using Cortex.ViewModels;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Cortex.Tests;

public class MainWindowViewModelMathHydrationTests
{
    [Fact]
    public void HydrateMathSettingsFromLiveDataIfClean_CopiesMathRuleConfigurationIntoSettings()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            var liveChannel = viewModel.LiveDataView.ChannelsLiveData[0];
            var settingsChannel = viewModel.SettingsDataView.ChannelsStaticData[0];

            liveChannel.ChanType = Models.OutputChannel.ChannelType.MATH;
            liveChannel.MathRuleCount = 2;
            liveChannel.MathRootRule = 1;
            liveChannel.MathOutputMode = (byte)Models.OutputChannel.MathOutputModeType.PWM;
            liveChannel.EnsureMathRuleSlots();

            liveChannel.MathRules[0].Operation = (byte)Models.OutputChannel.MathOperation.ADD;
            liveChannel.MathRules[0].OperandAType = (byte)Models.OutputChannel.MathOperandType.SystemValue;
            liveChannel.MathRules[0].OperandAIndex = (byte)Models.OutputChannel.MathSystemValue.SystemTemperature;
            liveChannel.MathRules[0].OperandBType = (byte)Models.OutputChannel.MathOperandType.Constant;
            liveChannel.MathRules[0].OperandBConstant = 35f;

            liveChannel.MathRules[1].Operation = (byte)Models.OutputChannel.MathOperation.GREATER_THAN;
            liveChannel.MathRules[1].HysteresisEnabled = 1;
            liveChannel.MathRules[1].OnThreshold = 2f;
            liveChannel.MathRules[1].OffThreshold = 1f;

            settingsChannel.MathRuleCount = 1;
            settingsChannel.MathRootRule = 0;
            settingsChannel.MathOutputMode = (byte)Models.OutputChannel.MathOutputModeType.Digital;
            settingsChannel.EnsureMathRuleSlots();

            viewModel.HasPendingConfigChanges = false;
            SetPrivateBooleanField(viewModel, "_awaitingInitialMathSettingsHydration", true);
            InvokeInstanceMethod(viewModel, "HydrateMathSettingsFromLiveDataIfClean");

            Assert.Equal(liveChannel.MathRuleCount, settingsChannel.MathRuleCount);
            Assert.Equal(liveChannel.MathRootRule, settingsChannel.MathRootRule);
            Assert.Equal(liveChannel.MathOutputMode, settingsChannel.MathOutputMode);
            Assert.Equal(liveChannel.MathRules[0].Operation, settingsChannel.MathRules[0].Operation);
            Assert.Equal(liveChannel.MathRules[0].OperandAType, settingsChannel.MathRules[0].OperandAType);
            Assert.Equal(liveChannel.MathRules[0].OperandAIndex, settingsChannel.MathRules[0].OperandAIndex);
            Assert.Equal(liveChannel.MathRules[0].OperandBType, settingsChannel.MathRules[0].OperandBType);
            Assert.Equal(liveChannel.MathRules[0].OperandBConstant, settingsChannel.MathRules[0].OperandBConstant);
            Assert.Equal(liveChannel.MathRules[1].Operation, settingsChannel.MathRules[1].Operation);
            Assert.Equal(liveChannel.MathRules[1].HysteresisEnabled, settingsChannel.MathRules[1].HysteresisEnabled);
            Assert.Equal(liveChannel.MathRules[1].OnThreshold, settingsChannel.MathRules[1].OnThreshold);
            Assert.Equal(liveChannel.MathRules[1].OffThreshold, settingsChannel.MathRules[1].OffThreshold);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void HydrateMathSettingsFromLiveDataIfClean_DoesNotOverwriteWhenPendingChangesExist()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            var liveChannel = viewModel.LiveDataView.ChannelsLiveData[0];
            var settingsChannel = viewModel.SettingsDataView.ChannelsStaticData[0];

            liveChannel.ChanType = Models.OutputChannel.ChannelType.MATH;
            liveChannel.MathRuleCount = 2;
            liveChannel.EnsureMathRuleSlots();
            liveChannel.MathRules[0].Operation = (byte)Models.OutputChannel.MathOperation.MULTIPLY;

            settingsChannel.MathRuleCount = 1;
            settingsChannel.EnsureMathRuleSlots();
            settingsChannel.MathRules[0].Operation = (byte)Models.OutputChannel.MathOperation.ADD;

            viewModel.HasPendingConfigChanges = true;
            SetPrivateBooleanField(viewModel, "_awaitingInitialMathSettingsHydration", true);
            InvokeInstanceMethod(viewModel, "HydrateMathSettingsFromLiveDataIfClean");

            Assert.Equal((byte)1, settingsChannel.MathRuleCount);
            Assert.Equal((byte)Models.OutputChannel.MathOperation.ADD, settingsChannel.MathRules[0].Operation);
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

    private static object? InvokeInstanceMethod(object instance, string methodName)
    {
        MethodInfo? method = instance.GetType().GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        return method!.Invoke(instance, null);
    }

    private static void SetPrivateBooleanField(object instance, string fieldName, bool value)
    {
        FieldInfo? field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(instance, value);
    }

    private sealed class TestAppCloser : IAppCloser
    {
        public void CloseApp() { }
        public Task<string?> OpenPdmFileContentAsync() => Task.FromResult<string?>(null);
        public Task<string?> BrowseLocalLogFilePathAsync(string initialDirectory) => Task.FromResult<string?>(null);
        public Task<bool> SavePdmFileContentAsync(string content) => Task.FromResult(true);
        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL") => Task.FromResult(true);
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
        public Task<Models.LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync() => Task.FromResult<Models.LocalFirmwareUpdateSelection?>(null);
        public Task ShowAboutAsync() => Task.CompletedTask;
        public Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel) => Task.CompletedTask;
    }
}
