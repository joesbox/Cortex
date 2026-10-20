using Cortex.Models;
using Cortex.Services;
using Xunit;

namespace Cortex.Tests;

public class ConfigFileSerializerTests
{
    // TryDeserialize

    [Fact]
    public void TryDeserialize_ReturnsFalse_ForEmptyString()
    {
        bool result = ConfigFileSerializer.TryDeserialize("", out _, out string? error);

        Assert.False(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryDeserialize_ReturnsFalse_ForInvalidJson()
    {
        bool result = ConfigFileSerializer.TryDeserialize("not json at all", out _, out string? error);

        Assert.False(result);
        Assert.NotNull(error);
        Assert.Contains("Failed to parse", error);
    }

    [Fact]
    public void TryDeserialize_ReturnsFalse_WhenNoKnownSections()
    {
        string json = """{"FormatVersion":1}""";

        bool result = ConfigFileSerializer.TryDeserialize(json, out _, out string? error);

        Assert.False(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void TryDeserialize_ReturnsTrue_WhenAtLeastOneSectionPresent()
    {
        string json = """{"Channels":[]}""";

        bool result = ConfigFileSerializer.TryDeserialize(json, out var snapshot, out string? error);

        Assert.True(result);
        Assert.Null(error);
        Assert.NotNull(snapshot);
    }

    // SerializeSettings / roundtrip

    [Fact]
    public void SerializeSettings_ProducesValidJson_ThatDeserializesSuccessfully()
    {
        var data = new DataStructures();

        string json = ConfigFileSerializer.SerializeSettings(data);

        bool result = ConfigFileSerializer.TryDeserialize(json, out var snapshot, out _);
        Assert.True(result);
        Assert.NotNull(snapshot);
    }

    [Fact]
    public void SerializeSettings_IncludesExpectedSections()
    {
        var data = new DataStructures();

        string json = ConfigFileSerializer.SerializeSettings(data);

        Assert.Contains("Channels", json);
        Assert.Contains("DigitalInputs", json);
        Assert.Contains("AnalogueInputs", json);
        Assert.Contains("SystemParameters", json);
        Assert.Contains("CellularParameters", json);
    }

    // ApplySnapshot — channel name

    [Fact]
    public void ApplySnapshot_SetsChannelName_WhenProvided()
    {
        var data = new DataStructures();
        var snapshot = new ConfigSnapshot
        {
            Channels = [new OutputChannelSnapshot { Name = "Fan" }]
        };

        ConfigFileSerializer.ApplySnapshot(data, snapshot);

        string name = new string(data.ChannelsStaticData[0].Name).TrimEnd('\0');
        Assert.Equal("Fan", name);
    }

    [Fact]
    public void ApplySnapshot_LeavesChannelNameNull_WhenNameNotProvided()
    {
        var data = new DataStructures();
        var snapshot = new ConfigSnapshot
        {
            Channels = [new OutputChannelSnapshot { Name = null }]
        };

        ConfigFileSerializer.ApplySnapshot(data, snapshot);

        Assert.Null(data.ChannelsStaticData[0].Name);
    }

    // ApplySnapshot — legacy RunOn → DelayedOff migration

    [Fact]
    public void ApplySnapshot_MigratesLegacyRunOn_ToDelayedOff()
    {
        var data = new DataStructures();
        var snapshot = new ConfigSnapshot
        {
            Channels = [new OutputChannelSnapshot { RunOn = 1, RunOnTime = 30 }]
        };

        ConfigFileSerializer.ApplySnapshot(data, snapshot);

        var ch = data.ChannelsStaticData[0];
        Assert.Equal(1, ch.DelayedOff);
        Assert.Equal(30, ch.DelayedOffTime);
        Assert.Equal((byte)OutputChannel.DelayedOffTriggerMode.IgnitionOff, ch.DelayedOffTrigger);
    }

    [Fact]
    public void ApplySnapshot_DoesNotMigrateRunOn_WhenRunOnIsZero()
    {
        var data = new DataStructures();
        var snapshot = new ConfigSnapshot
        {
            Channels = [new OutputChannelSnapshot { RunOn = 0, RunOnTime = 30 }]
        };

        ConfigFileSerializer.ApplySnapshot(data, snapshot);

        var ch = data.ChannelsStaticData[0];
        Assert.Equal(0, ch.DelayedOff);
    }

    [Fact]
    public void ApplySnapshot_ExplicitDelayedOff_TakesPrecedenceOverRunOnMigration()
    {
        var data = new DataStructures();
        var snapshot = new ConfigSnapshot
        {
            Channels = [new OutputChannelSnapshot { DelayedOff = 0, RunOn = 1, RunOnTime = 99 }]
        };

        ConfigFileSerializer.ApplySnapshot(data, snapshot);

        var ch = data.ChannelsStaticData[0];
        Assert.Equal(0, ch.DelayedOff);
    }

    // ApplySnapshot — InputControlPin vs InputControlLabel

    [Fact]
    public void ApplySnapshot_SetsInputControlPin_WhenExplicitPinProvided()
    {
        var data = new DataStructures();
        var snapshot = new ConfigSnapshot
        {
            Channels = [new OutputChannelSnapshot { InputControlPin = 66 }]
        };

        ConfigFileSerializer.ApplySnapshot(data, snapshot);

        Assert.Equal(66, data.ChannelsStaticData[0].InputControlPin);
    }

    [Fact]
    public void ApplySnapshot_ResolvesInputControlPin_FromLabel()
    {
        var data = new DataStructures();
        var snapshot = new ConfigSnapshot
        {
            Channels = [new OutputChannelSnapshot { InputControlLabel = "Ignition" }]
        };

        ConfigFileSerializer.ApplySnapshot(data, snapshot);

        Assert.Equal(InputPinCatalog.IgnitionInputPin, data.ChannelsStaticData[0].InputControlPin);
    }

    [Fact]
    public void SerializeAndApplySnapshot_RoundTripsMathChannelAndRuleFields()
    {
        var source = new DataStructures();
        OutputChannel channel = source.ChannelsStaticData[0];
        channel.ChanType = OutputChannel.ChannelType.MATH;
        channel.MathRuleCount = 3;
        channel.MathRootRule = 2;
        channel.MathOutputMode = (byte)OutputChannel.MathOutputModeType.PWM;
        channel.MathCalculatedRootValue = 12.5f;
        channel.MathError = 1;

        channel.EnsureMathRuleSlots();
        OutputChannel.MathRule rule0 = channel.MathRules[0];
        rule0.Operation = (byte)OutputChannel.MathOperation.ADD;
        rule0.HysteresisEnabled = 1;
        rule0.OnThreshold = 10.0f;
        rule0.OffThreshold = 8.0f;
        rule0.OperandAType = (byte)OutputChannel.MathOperandType.SystemValue;
        rule0.OperandAIndex = (byte)OutputChannel.MathSystemValue.MagneticFieldZ;
        rule0.OperandAConstant = 2.5f;
        rule0.OperandBType = (byte)OutputChannel.MathOperandType.Channel;
        rule0.OperandBIndex = 4;
        rule0.OperandBConstant = -1.0f;
        rule0.RuntimeValue = 6.75f;
        rule0.RuntimeError = 0;
        rule0.Evaluated = 1;

        OutputChannel.MathRule rule1 = channel.MathRules[1];
        rule1.Operation = (byte)OutputChannel.MathOperation.AND;
        rule1.OperandAType = (byte)OutputChannel.MathOperandType.DigitalInput;
        rule1.OperandAIndex = 0;
        rule1.OperandBType = (byte)OutputChannel.MathOperandType.DigitalInput;
        rule1.OperandBIndex = (byte)(Constants.NUM_DIGITAL_INPUTS - 1);

        string json = ConfigFileSerializer.SerializeSettings(source);
        Assert.True(ConfigFileSerializer.TryDeserialize(json, out ConfigSnapshot? snapshot, out string? error));
        Assert.Null(error);
        Assert.NotNull(snapshot);

        var target = new DataStructures();
        ConfigFileSerializer.ApplySnapshot(target, snapshot!);

        OutputChannel applied = target.ChannelsStaticData[0];
        Assert.Equal(OutputChannel.ChannelType.MATH, applied.ChanType);
        Assert.Equal((byte)3, applied.MathRuleCount);
        Assert.Equal((byte)2, applied.MathRootRule);
        Assert.Equal((byte)OutputChannel.MathOutputModeType.PWM, applied.MathOutputMode);
        Assert.Equal(12.5f, applied.MathCalculatedRootValue);
        Assert.Equal((byte)1, applied.MathError);

        OutputChannel.MathRule appliedRule0 = applied.MathRules[0];
        Assert.Equal((byte)OutputChannel.MathOperation.ADD, appliedRule0.Operation);
        Assert.Equal((byte)1, appliedRule0.HysteresisEnabled);
        Assert.Equal(10.0f, appliedRule0.OnThreshold);
        Assert.Equal(8.0f, appliedRule0.OffThreshold);
        Assert.Equal((byte)OutputChannel.MathOperandType.SystemValue, appliedRule0.OperandAType);
        Assert.Equal((byte)OutputChannel.MathSystemValue.MagneticFieldZ, appliedRule0.OperandAIndex);
        Assert.Equal(2.5f, appliedRule0.OperandAConstant);
        Assert.Equal((byte)OutputChannel.MathOperandType.Channel, appliedRule0.OperandBType);
        Assert.Equal((byte)4, appliedRule0.OperandBIndex);
        Assert.Equal(-1.0f, appliedRule0.OperandBConstant);
        Assert.Equal(6.75f, appliedRule0.RuntimeValue);
        Assert.Equal((byte)0, appliedRule0.RuntimeError);
        Assert.Equal((byte)1, appliedRule0.Evaluated);
        Assert.Equal(Constants.NUM_MATH_RULE_SLOTS, applied.MathRules.Count);

        OutputChannel.MathRule appliedRule1 = applied.MathRules[1];
        Assert.Equal((byte)OutputChannel.MathOperation.AND, appliedRule1.Operation);
        Assert.Equal((byte)5, appliedRule1.OperandAType);
        Assert.Equal((byte)0, appliedRule1.OperandAIndex);
        Assert.Equal((byte)5, appliedRule1.OperandBType);
        Assert.Equal((byte)(Constants.NUM_DIGITAL_INPUTS - 1), appliedRule1.OperandBIndex);
    }
}
