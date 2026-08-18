using Cortex.Services;
using Xunit;

namespace Cortex.Tests;

public class ControllerProtocolCapabilitiesTests
{
    [Theory]
    [InlineData(ControllerProtocolCapabilities.V09StaticPacketLength)]
    [InlineData(ControllerProtocolCapabilities.V010StaticPacketMinimumLength - 1)]
    public void LegacySnapshotsUseV09ConfigLimits(int packetLength)
    {
        ControllerProtocolCapabilities capabilities = ControllerProtocolCapabilities.FromStaticPacketLength(packetLength);

        Assert.False(capabilities.SupportsDelayedChannelSettings);
        Assert.False(capabilities.SupportsCellularSettings);
        Assert.Equal(27, capabilities.LastChannelParameterIndex);
        Assert.Equal(13, capabilities.LastSystemParameterIndex);
    }

    [Fact]
    public void V010SnapshotsEnableExtendedConfig()
    {
        ControllerProtocolCapabilities capabilities = ControllerProtocolCapabilities.FromStaticPacketLength(
            ControllerProtocolCapabilities.V010StaticPacketMinimumLength);

        Assert.True(capabilities.SupportsDelayedChannelSettings);
        Assert.True(capabilities.SupportsCellularSettings);
        Assert.Equal(Models.Constants.LAST_CHANNEL_PARAM_INDEX, capabilities.LastChannelParameterIndex);
        Assert.Equal(Models.Constants.LAST_SYSTEM_PARAM_INDEX, capabilities.LastSystemParameterIndex);
    }

    [Fact]
    public void NegativePacketLength_UsesV09ConfigLimits()
    {
        ControllerProtocolCapabilities capabilities = ControllerProtocolCapabilities.FromStaticPacketLength(-1);

        Assert.False(capabilities.SupportsDelayedChannelSettings);
        Assert.False(capabilities.SupportsCellularSettings);
        Assert.Equal(27, capabilities.LastChannelParameterIndex);
        Assert.Equal(13, capabilities.LastSystemParameterIndex);
    }

    [Fact]
    public void VeryLargePacketLength_UsesV010ConfigLimits()
    {
        ControllerProtocolCapabilities capabilities = ControllerProtocolCapabilities.FromStaticPacketLength(int.MaxValue);

        Assert.True(capabilities.SupportsDelayedChannelSettings);
        Assert.True(capabilities.SupportsCellularSettings);
        Assert.Equal(Models.Constants.LAST_CHANNEL_PARAM_INDEX, capabilities.LastChannelParameterIndex);
        Assert.Equal(Models.Constants.LAST_SYSTEM_PARAM_INDEX, capabilities.LastSystemParameterIndex);
    }
}