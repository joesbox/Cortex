using Cortex.Services;
using Cortex.ViewModels;
using Cortex.ViewModels.IoFlow;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Xunit;

namespace Cortex.Tests;

public class MainWindowViewModelMathHydrationTests
{
    [Fact]
    public void LiveImuValuesPropagateToAllSystemNodeChartsWithoutEditingSettings()
    {
        var viewModel = CreateViewModel();
        try
        {
            var incoming = new Models.DataStructures();
            var system = incoming.SystemParams;
            system.AccelerationX = 1;
            system.AccelerationY = 2;
            system.AccelerationZ = 3;
            system.GyroscopeX = 4;
            system.GyroscopeY = 5;
            system.GyroscopeZ = 6;
            system.MagneticFieldX = 7;
            system.MagneticFieldY = 8;
            system.MagneticFieldZ = 9;
            for (int i = 0; i < 14; i++)
                viewModel.IoFlowGraph.AddSystemValueNodeCommand.Execute(null);

            var update = typeof(MainWindowViewModel).GetMethod("UpdateLiveDataInPlace", BindingFlags.NonPublic | BindingFlags.Instance)!;
            update.Invoke(viewModel, [incoming]);
            viewModel.IoFlowGraph.UpdateLiveChannelSamples(viewModel.LiveDataView.ChannelsLiveData);
            foreach (var value in System.Enum.GetValues<Models.OutputChannel.MathSystemValue>().Skip(5))
            {
                var node = viewModel.IoFlowGraph.MathSourceNodes.Single(node => node.SourceIndex == (byte)value);
                Assert.Equal((int)value - 4, node.GetLiveChartValue(viewModel.LiveDataView));
                Assert.Equal((int)value - 4, node.LiveHistory.Samples[^1].Value);
                Assert.DoesNotContain("--", node.LiveValueText);
                Assert.Null(viewModel.SettingsDataView.SystemParamsStaticData.GetMathSystemValue(value));
            }

            update.Invoke(viewModel, [new Models.DataStructures()]);
            foreach (var value in System.Enum.GetValues<Models.OutputChannel.MathSystemValue>().Skip(5))
                Assert.Null(viewModel.LiveDataView.SystemParams.GetMathSystemValue(value));
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void SelectedMathRuleRootToggleTracksTheRuleBeingEdited()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            var channel = viewModel.SettingsDataView.ChannelsStaticData[0];
            channel.ChanType = Models.OutputChannel.ChannelType.MATH;
            channel.MathRuleCount = 2;
            channel.MathRootRule = 0;
            channel.EnsureMathRuleSlots();
            viewModel.SelectedChannel = channel;
            viewModel.SelectedMathRuleSlotIndex = 1;

            Assert.Equal(2, viewModel.IoFlowGraph.MathRuleNodes.Count(node => node.OwnerChannelNumber == channel.ChannelNumber));
            Assert.False(viewModel.SelectedMathRuleIsRoot);
            Assert.True(viewModel.CanToggleSelectedMathRuleRoot);

            viewModel.SelectedMathRuleIsRoot = true;
            Assert.Equal((byte)1, channel.MathRootRule);
            Assert.True(viewModel.SelectedMathRuleIsRoot);
            Assert.Contains(viewModel.IoFlowGraph.Connections, connection =>
                connection.Target.Node is OutputNodeViewModel output
                && output.ChannelNumber == channel.ChannelNumber
                && connection.Source.Node is MathRuleNodeViewModel rule
                && rule.RuleIndex == 1
                && connection.Label == "Logic result");

            viewModel.SelectedMathRuleIsRoot = false;
            Assert.Equal((byte)0, channel.MathRootRule);
            Assert.False(viewModel.SelectedMathRuleIsRoot);
            Assert.Contains(viewModel.IoFlowGraph.Connections, connection =>
                connection.Target.Node is OutputNodeViewModel output
                && output.ChannelNumber == channel.ChannelNumber
                && connection.Source.Node is MathRuleNodeViewModel rule
                && rule.RuleIndex == 0
                && connection.Label == "Logic result");
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void SelectingMathChannelKeepsMathTypeAndShowsMathEditorState()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            var channel = viewModel.SettingsDataView.ChannelsStaticData[0];
            channel.InputControlPin = Models.InputPinCatalog.DIChannelInputPins[0];
            viewModel.SelectedChannel = channel;

            viewModel.SelectedChannelTypeDisplay = viewModel.ChannelTypes
                .Single(option => option.ChannelType == Models.OutputChannel.ChannelType.MATH);

            Assert.Equal(Models.OutputChannel.ChannelType.MATH, channel.ChanType);
            Assert.Equal((byte)0, channel.InputControlPin);
            Assert.True(viewModel.IsSelectedChannelMath);
            Assert.Null(viewModel.SelectedInputLabel);
            Assert.Empty(viewModel.InputDisplayList);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void SelectingExistingMathChannelDoesNotAssignPhysicalInput()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            var channel = viewModel.SettingsDataView.ChannelsStaticData[1];
            channel.ChanType = Models.OutputChannel.ChannelType.MATH;
            channel.InputControlPin = Models.InputPinCatalog.DIChannelInputPins[0];
            viewModel.SelectedChannelIndex = 1;

            Assert.Equal(Models.OutputChannel.ChannelType.MATH, channel.ChanType);
            Assert.Equal((byte)0, channel.InputControlPin);
            Assert.True(viewModel.IsSelectedChannelMath);
            Assert.Empty(viewModel.InputDisplayList);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void TraditionalMathRuntimeShowsResultsForEveryActiveRule()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            var liveChannel = viewModel.LiveDataView.ChannelsLiveData[0];
            liveChannel.ChanType = Models.OutputChannel.ChannelType.MATH;
            liveChannel.MathRuleCount = 2;
            liveChannel.MathRootRule = 1;
            liveChannel.MathOutputMode = (byte)Models.OutputChannel.MathOutputModeType.Digital;
            liveChannel.MathCalculatedRootValue = 1f;
            liveChannel.EnsureMathRuleSlots();
            liveChannel.MathRules[0].Operation = (byte)Models.OutputChannel.MathOperation.ADD;
            liveChannel.MathRules[0].RuntimeValue = 15.5f;
            liveChannel.MathRules[0].Evaluated = 1;
            liveChannel.MathRules[1].Operation = (byte)Models.OutputChannel.MathOperation.GREATER_THAN;
            liveChannel.MathRules[1].OperandAType = (byte)Models.OutputChannel.MathOperandType.Rule;
            liveChannel.MathRules[1].OperandAIndex = 0;
            liveChannel.MathRules[1].RuntimeValue = 1;
            liveChannel.MathRules[1].Evaluated = 1;
            var configuredChannel = viewModel.SettingsDataView.ChannelsStaticData[0];
            configuredChannel.ChanType = Models.OutputChannel.ChannelType.MATH;
            configuredChannel.MathRuleCount = 2;
            configuredChannel.MathRootRule = 1;
            configuredChannel.EnsureMathRuleSlots();
            configuredChannel.MathRules[0].Operation = (byte)Models.OutputChannel.MathOperation.ADD;
            configuredChannel.MathRules[1].Operation = (byte)Models.OutputChannel.MathOperation.GREATER_THAN;
            configuredChannel.MathRules[1].OperandAType = (byte)Models.OutputChannel.MathOperandType.Rule;
            configuredChannel.MathRules[1].OperandAIndex = 0;
            viewModel.SelectedChannel = configuredChannel;

            Assert.Contains("Rule 1: constant: 0 + constant: 0 = 15.5", viewModel.SelectedMathRuntimeAllRuleValuesText);
            Assert.Contains("feeds Rule 2", viewModel.SelectedMathRuntimeAllRuleValuesText);
            Assert.Contains("Rule 2: Rule 1: 15.5 > constant: 0 = True; Digital output: ON", viewModel.SelectedMathRuntimeAllRuleValuesText);
            Assert.Contains("zero = OFF, nonzero = ON", viewModel.SelectedMathRuntimeRootValueText);
            viewModel.SelectedMathRuleSlotIndex = 1;
            Assert.Contains("Digital output: ON", viewModel.SelectedMathRuntimeRuleValueText);
            Assert.Contains("Rule 1: 15.5 > constant: 0 = True", viewModel.SelectedMathRuntimeRuleEvaluationText);
            Assert.Equal("Rule 1: 15.5", viewModel.SelectedMathRuntimeOperandAText);
            Assert.Equal("constant: 0", viewModel.SelectedMathRuntimeOperandBText);
            Assert.Equal("True", viewModel.SelectedMathRuntimeResultText);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void TraditionalMathOperandSelectorsKeepAnalogueInputChoices()
    {
        MainWindowViewModel viewModel = CreateViewModel();

        try
        {
            var channel = viewModel.SettingsDataView.ChannelsStaticData[0];
            channel.ChanType = Models.OutputChannel.ChannelType.MATH;
            channel.MathRuleCount = 1;
            channel.EnsureMathRuleSlots();
            viewModel.SelectedChannel = channel;

            viewModel.SelectedMathOperandATypeIndex = (int)Models.OutputChannel.MathOperandType.AnalogueInput;
            viewModel.SelectedMathOperandBTypeIndex = (int)Models.OutputChannel.MathOperandType.AnalogueInput;

            var operandAOptions = viewModel.SelectedMathOperandAIndexOptions.ToArray();
            var operandBOptions = viewModel.SelectedMathOperandBIndexOptions.ToArray();
            Assert.Equal("Analogue input 1", operandAOptions[0].Label);
            Assert.Equal("Analogue input 3", operandAOptions[2].Label);
            Assert.Equal("Analogue input 1", operandBOptions[0].Label);
            Assert.Equal("Analogue input 3", operandBOptions[2].Label);

            viewModel.SelectedMathOperandAIndexOption = operandAOptions[0];
            MathOperandSourceOption? selectedOperandA = viewModel.SelectedMathOperandAIndexOption;
            Assert.NotNull(selectedOperandA);
            viewModel.SelectedMathOperandBIndexOption = operandBOptions[2];

            Assert.Equal((byte)0, channel.MathRules[0].OperandAIndex);
            Assert.Equal((byte)2, channel.MathRules[0].OperandBIndex);
            Assert.Same(selectedOperandA, viewModel.SelectedMathOperandAIndexOption);
            Assert.Equal(operandAOptions[0].Value, viewModel.SelectedMathOperandAIndexOption?.Value);
            Assert.Equal(operandBOptions[2].Value, viewModel.SelectedMathOperandBIndexOption?.Value);
            var flowRule = viewModel.IoFlowGraph.MathRuleNodes.Single(rule => rule.OwnerChannelNumber == channel.ChannelNumber);
            Assert.Contains(viewModel.IoFlowGraph.Connections, wire =>
                ReferenceEquals(wire.Target, flowRule.OperandAConnector)
                && wire.Source.Node is AnalogueInputNodeViewModel { InputNumber: 1 });
            Assert.Contains(viewModel.IoFlowGraph.Connections, wire =>
                ReferenceEquals(wire.Target, flowRule.OperandBConnector)
                && wire.Source.Node is AnalogueInputNodeViewModel { InputNumber: 3 });
            flowRule.SelectedOperandBSource = flowRule.ConnectedSourceOptions.Single(source =>
                source.Type == (byte)Models.OutputChannel.MathOperandType.AnalogueInput && source.Index == 0);
            Assert.Equal(0, viewModel.SelectedMathOperandBIndexOption?.Value);
            Assert.Equal(0, viewModel.SelectedMathOperandAIndexOption?.Value);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

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

    [Fact]
    public async Task TraditionalEditsReplaceCompiledFlowCopiesAndSubsequentFlowEditsUseTheSameRules()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        try
        {
            var graph = viewModel.IoFlowGraph;
            if (graph.OutputNodes.Count == 0) graph.AddNextOutputNodeCommand.Execute(null);
            graph.AddLogicNodeCommand.Execute(null);
            graph.AddLogicNodeCommand.Execute(null);
            var rules = graph.MathRuleNodes.Where(rule => rule.IsStandalone).TakeLast(2).ToArray();
            await graph.ConnectNodes(rules[0], rules[1]);
            await graph.ConnectNodes(rules[1], graph.OutputNodes[0]);
            var channel = graph.OutputNodes[0].Channel;
            viewModel.SelectedChannel = channel;
            var positions = rules.Select(rule => rule.Location).ToArray();

            viewModel.SelectedMathRuleSlotIndex = 0;
            viewModel.SelectedMathRule!.OperandAConstant = 27;

            var ownedRules = graph.MathRuleNodes.Where(rule => rule.OwnerChannelNumber == channel.ChannelNumber)
                .OrderBy(rule => rule.RuleIndex).ToArray();
            Assert.Equal(2, ownedRules.Length);
            Assert.DoesNotContain(rules[0], graph.MathRuleNodes);
            Assert.DoesNotContain(rules[1], graph.MathRuleNodes);
            Assert.Same(channel.MathRules[0], ownedRules[0].Rule);
            Assert.Equal(positions[0], ownedRules[0].Location);
            Assert.Equal(27, ownedRules[0].Rule.OperandAConstant);
            ownedRules[0].Rule.OperandBConstant = 42;
            Assert.Equal(42, viewModel.SelectedMathRule.OperandBConstant);
            Assert.Empty(graph.CreateSnapshot().OutputRootLogicNodeByChannel);

            graph.OutputNodes[0].Channel.MathOutputMode = (byte)Models.OutputChannel.MathOutputModeType.PWM;
            Assert.Equal(1, viewModel.SelectedMathOutputModeIndex);
            viewModel.SelectedMathOutputModeIndex = 0;
            Assert.Equal((byte)Models.OutputChannel.MathOutputModeType.Digital, graph.OutputNodes[0].Channel.MathOutputMode);

            rules[0].Rule.OperandAConstant = 99;
            Assert.Equal(27, channel.MathRules[0].OperandAConstant);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public async Task DeletingOwnedFlowRuleStagesTheCompactedConfigurationAndDoesNotReturnAfterSave()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        try
        {
            var channel = viewModel.SettingsDataView.ChannelsStaticData[0];
            channel.ChanType = Models.OutputChannel.ChannelType.MATH;
            channel.MathRuleCount = 3;
            channel.MathRootRule = 2;
            channel.MathRules[0].OperandAConstant = 11;
            channel.MathRules[1].OperandAConstant = 22;
            channel.MathRules[2].OperandAType = (byte)Models.OutputChannel.MathOperandType.Rule;
            channel.MathRules[2].OperandAIndex = 1;
            channel.MathRules[2].OperandBType = (byte)Models.OutputChannel.MathOperandType.Rule;
            channel.MathRules[2].OperandBIndex = 0;
            viewModel.SelectedChannel = channel;
            typeof(MainWindowViewModel).GetMethod("ConfigSaved", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewModel, new object?[] { null, System.EventArgs.Empty });

            var graph = viewModel.IoFlowGraph;
            var deleted = graph.MathRuleNodes.Single(rule => rule.OwnerChannelNumber == channel.ChannelNumber && rule.RuleIndex == 1);
            graph.SelectedNodes.Add(deleted);
            await graph.RemoveSelectedNodesCommand.ExecuteAsync(null);

            Assert.True(viewModel.HasPendingConfigChanges);
            Assert.Equal(2, viewModel.SelectedMathRuleCount);
            Assert.Equal((byte)1, channel.MathRootRule);
            Assert.Equal((byte)Models.OutputChannel.MathOperandType.Constant, channel.MathRules[1].OperandAType);
            Assert.Equal(0, channel.MathRules[1].OperandAConstant);
            Assert.Equal((byte)0, channel.MathRules[1].OperandBIndex);
            Assert.Equal(0, channel.MathRules[2].OperandAConstant);

            var liveChannel = viewModel.LiveDataView.ChannelsLiveData[0];
            liveChannel.ChanType = Models.OutputChannel.ChannelType.MATH;
            liveChannel.MathRuleCount = 3;
            liveChannel.MathRules[1].OperandAConstant = 22;
            typeof(MainWindowViewModel).GetMethod("ConfigSaved", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewModel, new object?[] { null, System.EventArgs.Empty });
            InvokeInstanceMethod(viewModel, "HydrateMathSettingsFromLiveDataIfClean");
            channel.MathRules[0].OperandBConstant = 7;

            Assert.Equal((byte)2, channel.MathRuleCount);
            Assert.Equal(2, graph.MathRuleNodes.Count(rule => rule.OwnerChannelNumber == channel.ChannelNumber));
            Assert.DoesNotContain(graph.MathRuleNodes, rule => rule.OwnerChannelNumber == channel.ChannelNumber && rule.Rule.OperandAConstant == 22);
            Assert.True(viewModel.HasPendingConfigChanges);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void SaveAcknowledgementDoesNotMarkEditsMadeDuringTheSaveAsCommitted()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        try
        {
            var channel = viewModel.SettingsDataView.ChannelsStaticData[0];
            channel.ChanType = Models.OutputChannel.ChannelType.MATH;
            channel.MathRules[0].OperandAConstant = 19;
            var savedConfiguration = typeof(MainWindowViewModel).GetMethod("DeepCopyDataStructures", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewModel, new object[] { viewModel.SettingsDataView });
            typeof(MainWindowViewModel).GetField("_configurationBeingSaved", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(viewModel, savedConfiguration);
            typeof(MainWindowViewModel).GetField("_ioFlowSnapshotBeingSaved", BindingFlags.NonPublic | BindingFlags.Instance)!
                .SetValue(viewModel, viewModel.IoFlowGraph.CreateSnapshot());
            channel.MathRules[0].OperandAConstant = 23;

            typeof(MainWindowViewModel).GetMethod("ConfigSaved", BindingFlags.NonPublic | BindingFlags.Instance)!
                .Invoke(viewModel, new object?[] { null, System.EventArgs.Empty });

            Assert.Equal(23, channel.MathRules[0].OperandAConstant);
            Assert.True(viewModel.HasPendingConfigChanges);
            Assert.True(viewModel.CanSendConfig);
        }
        finally
        {
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void MathPollingTracksControllerMathChannelsInsteadOfLegacyEditorSelection()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        using var service = new SerialPortService("COM1");
        FieldInfo portField = typeof(MainWindowViewModel).GetField("_portService", BindingFlags.NonPublic | BindingFlags.Instance)!;
        portField.SetValue(viewModel, service);
        try
        {
            viewModel.SelectedChannel = viewModel.SettingsDataView.ChannelsStaticData[0];
            Assert.False(viewModel.SelectedChannel.IsMathChannel);
            viewModel.LiveDataView.ChannelsLiveData[4].ChanType = Models.OutputChannel.ChannelType.MATH;
            InvokeInstanceMethod(viewModel, "UpdateMathStatusPollingSelection");
            FieldInfo pollField = typeof(SerialPortService).GetField("_mathStatusPollingChannelIndex", BindingFlags.NonPublic | BindingFlags.Instance)!;
            Assert.Equal(4, pollField.GetValue(service));

            viewModel.LiveDataView.ChannelsLiveData[4].ChanType = Models.OutputChannel.ChannelType.Digital;
            InvokeInstanceMethod(viewModel, "UpdateMathStatusPollingSelection");
            Assert.Equal(-1, pollField.GetValue(service));
        }
        finally
        {
            portField.SetValue(viewModel, null);
            StopUiUpdateTimer(viewModel);
        }
    }

    [Fact]
    public void MathSaveValidationRequiresAnEnabledDecodedRxSignal()
    {
        MainWindowViewModel viewModel = CreateViewModel();
        try
        {
            var channel = viewModel.SettingsDataView.ChannelsStaticData[0];
            channel.ChanType = Models.OutputChannel.ChannelType.MATH;
            channel.MathRules[0].OperandAType = (byte)Models.OutputChannel.MathOperandType.CanSignal;
            var signal = viewModel.SettingsDataView.CustomCanSignalsStaticData[0];
            signal.IsEnabled = true;
            var message = viewModel.SettingsDataView.CustomCanMessagesStaticData[0];
            message.IsTx = false;
            var validate = typeof(MainWindowViewModel).GetMethod("TryValidateMathConfiguration", BindingFlags.NonPublic | BindingFlags.Instance)!;
            object?[] arguments = [null];
            Assert.True((bool)validate.Invoke(viewModel, arguments)!);

            message.IsTx = true;
            Assert.False((bool)validate.Invoke(viewModel, arguments)!);
            Assert.Contains("CAN RX", Assert.IsType<string>(arguments[0]));
            message.IsTx = false;
            signal.IsEnabled = false;
            Assert.False((bool)validate.Invoke(viewModel, arguments)!);
            Assert.Contains("enabled", Assert.IsType<string>(arguments[0]));
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
        public Task<string?> BrowseDbcFilePathAsync() => Task.FromResult<string?>(null);
        public Task<bool> SavePdmFileContentAsync(string content) => Task.FromResult(true);
        public Task<bool> ConfirmAsync(string title, string message, string confirmButtonText = "CONFIRM", string cancelButtonText = "CANCEL") => Task.FromResult(true);
        public Task OpenUrlAsync(string url) => Task.CompletedTask;
        public Task<Models.LocalFirmwareUpdateSelection?> BrowseLocalFirmwareUpdateFilesAsync() => Task.FromResult<Models.LocalFirmwareUpdateSelection?>(null);
        public Task ShowAboutAsync() => Task.CompletedTask;
        public Task<Models.ConfigurationBackupEntry?> ShowConfigurationBackupDialogAsync(IReadOnlyList<Models.ConfigurationBackupEntry> backups) => Task.FromResult<Models.ConfigurationBackupEntry?>(null);
        public Task ShowFirmwareUpdateDialogAsync(FirmwareUpdateWindowViewModel viewModel) => Task.CompletedTask;
        public Task<Models.PinSetupDialogResult?> ShowPinSetupWizardAsync(bool isPinAlreadyEnabled) => Task.FromResult<Models.PinSetupDialogResult?>(null);
        public Task<bool> ShowRecoveryCodeAsync(string recoveryCode) => Task.FromResult(true);
        public Task<Models.PinUnlockDialogResult?> ShowPinUnlockDialogAsync(bool allowRecovery, uint retryAfterMs, string? statusMessage) => Task.FromResult<Models.PinUnlockDialogResult?>(null);
    }
}
