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
}
