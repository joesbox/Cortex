namespace Cortex.Services
{
    public readonly record struct ControllerProtocolCapabilities(
        int LastChannelParameterIndex,
        int LastSystemParameterIndex,
        bool SupportsDelayedChannelSettings,
        bool SupportsCellularSettings)
    {
        public const int V09StaticPacketLength = 1482;
        public const int V010StaticPacketMinimumLength = 1570;

        public static ControllerProtocolCapabilities V09 { get; } = new(
            LastChannelParameterIndex: 27,
            LastSystemParameterIndex: 13,
            SupportsDelayedChannelSettings: false,
            SupportsCellularSettings: false);

        public static ControllerProtocolCapabilities V010 { get; } = new(
            LastChannelParameterIndex: Models.Constants.LAST_CHANNEL_PARAM_INDEX,
            LastSystemParameterIndex: Models.Constants.LAST_SYSTEM_PARAM_INDEX,
            SupportsDelayedChannelSettings: true,
            SupportsCellularSettings: true);

        public static ControllerProtocolCapabilities FromStaticPacketLength(int packetLength)
        {
            return packetLength >= V010StaticPacketMinimumLength ? V010 : V09;
        }
    }
}