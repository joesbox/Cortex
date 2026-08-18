namespace Cortex.Models
{
    public static class Constants
    {
        public const int NUM_OUTPUT_CHANNELS = 14;

        public const int NUM_DIGITAL_INPUTS = 8;

        public const int NUM_ANALOGUE_INPUTS = 8;

        public const int SERIAL_TRAILER1 = 0x24;

        public const int SERIAL_TRAILER2 = 0x20;

        public const byte SERIAL_HEADER1 = 0x84;

        public const byte SERIAL_HEADER2 = 0x19;

        public const int CHANNEL_NAME_LENGTH = 3;

        public const char COMMAND_ID_BEGIN = 'b';

        public const char COMMAND_ID_CONFIM = 'c';

        public const char COMMAND_ID_REQUEST = 'r';

        public const char COMMAND_ID_REQUEST_STATIC = 'R';

        public const char COMMAND_ID_NEWCONFIG = 'n';

        public const char COMMAND_ID_SKIP = 'k';

        public const char COMMAND_ID_SEND = 's';

        public const char COMMAND_ID_CHECKSUM_FAIL = 'f';

        public const char COMMAND_ID_SAVECHANGES = 'S';

        public const char COMMAND_ID_FW_VER = 'v';

        public const char COMMAND_ID_BUILD_DATE = 'd';

        public const char COMMAND_ID_LOG_LIST = 'l';

        public const char COMMAND_ID_LOG_OPEN = 'o';

        public const char COMMAND_ID_LOG_CHUNK = 'p';

        public const char COMMAND_ID_LOG_STREAM = 'q';

        public const char COMMAND_ID_LOG_CANCEL = 'x';

        public const char COMMAND_ID_LOG_RESET = 'w';

        public const char COMMAND_ID_LOG_BULK = 'y';

        public const char COMMAND_ID_FW_UPLOAD_BEGIN = 'U';

        public const char COMMAND_ID_FW_UPLOAD_CHUNK = 'J';

        public const char COMMAND_ID_FW_UPLOAD_END = 'E';

        public const char COMMAND_ID_FW_UPLOAD_CANCEL = 'C';

        public const char COMMAND_ID_FW_INSTALL = 'I';

        public const char COMMAND_ID_FW_DIAGNOSTIC = 'Z';

        public const char COMMAND_ID_SET_RTC = 'T';

        public const char COMMAND_ID_FACTORY_RESET = 'P';

        public const char COMMAND_ID_CELLULAR_TEST = 'm';

        public const char COMMAND_ID_OPENREMOTE_PROVISION = 'M';

        public const int LOG_FILE_NAME_LENGTH = 24;

        public const int LAST_CHANNEL_PARAM_INDEX = 32;

        public const int LAST_ANALOGUE_PARAM_INDEX = 12;

        public const int SYSTEM_PARAM_TIME_ZONE_RULE = 13;

        public const uint CAN_BITRATE_125K = 125000;

        public const uint CAN_BITRATE_250K = 250000;

        public const uint CAN_BITRATE_500K = 500000;

        public const uint CAN_BITRATE_1M = 1000000;

        public const uint DEFAULT_CAN_BITRATE = CAN_BITRATE_500K;

        public const int SYSTEM_PARAM_CAN_BUS_BITRATE = 14;

        public const int LAST_SYSTEM_PARAM_INDEX = SYSTEM_PARAM_CAN_BUS_BITRATE;

        public const byte CELLULAR_CONFIG_VERSION = 6;

        public const byte CELLULAR_PROTOCOL_MQTT = 1;

        public const ushort CELLULAR_DEFAULT_MQTT_PORT = 1883;

        public const ushort CELLULAR_DEFAULT_MQTT_TLS_PORT = 8883;

        public const ushort CELLULAR_DEFAULT_KEEPALIVE_SECONDS = 60;

        public const uint CELLULAR_DEFAULT_PUBLISH_INTERVAL_MS = 5000;

        public const string CELLULAR_DEFAULT_OPENREMOTE_HOST = "remote.mannelectronics.uk";

        public const string OPENREMOTE_COMPATIBILITY_ATTRIBUTE = "SystemVoltage";

        public const uint TELEMETRY_UPLOAD_ANALOGUE1_VALUE = 1u << 0;
        public const uint TELEMETRY_UPLOAD_ANALOGUE2_VALUE = 1u << 1;
        public const uint TELEMETRY_UPLOAD_ANALOGUE3_VALUE = 1u << 2;
        public const uint TELEMETRY_UPLOAD_ANALOGUE4_VALUE = 1u << 3;
        public const uint TELEMETRY_UPLOAD_ANALOGUE5_VALUE = 1u << 4;
        public const uint TELEMETRY_UPLOAD_ANALOGUE6_VALUE = 1u << 5;
        public const uint TELEMETRY_UPLOAD_ANALOGUE7_VALUE = 1u << 6;
        public const uint TELEMETRY_UPLOAD_ANALOGUE8_VALUE = 1u << 7;
        public const uint TELEMETRY_UPLOAD_DIGITAL1_VALUE = 1u << 8;
        public const uint TELEMETRY_UPLOAD_DIGITAL2_VALUE = 1u << 9;
        public const uint TELEMETRY_UPLOAD_DIGITAL3_VALUE = 1u << 10;
        public const uint TELEMETRY_UPLOAD_DIGITAL4_VALUE = 1u << 11;
        public const uint TELEMETRY_UPLOAD_DIGITAL5_VALUE = 1u << 12;
        public const uint TELEMETRY_UPLOAD_DIGITAL6_VALUE = 1u << 13;
        public const uint TELEMETRY_UPLOAD_DIGITAL7_VALUE = 1u << 14;
        public const uint TELEMETRY_UPLOAD_DIGITAL8_VALUE = 1u << 15;
        public const uint TELEMETRY_UPLOAD_GPS_SPEED = 1u << 16;
        public const uint TELEMETRY_UPLOAD_IMU_DATA = 1u << 17;
        public const uint TELEMETRY_UPLOAD_LOCATION = 1u << 18;
        public const uint TELEMETRY_UPLOAD_SYSTEM_CURRENT = 1u << 19;
        public const uint TELEMETRY_UPLOAD_SYSTEM_TEMPERATURE = 1u << 20;
        public const uint TELEMETRY_UPLOAD_SYSTEM_VOLTAGE = 1u << 21;
        public const uint TELEMETRY_UPLOAD_UPTIME = 1u << 22;
        public const uint TELEMETRY_UPLOAD_CHANNEL_CURRENTS = 1u << 23;
        public const uint TELEMETRY_UPLOAD_DEFAULT_MASK = TELEMETRY_UPLOAD_SYSTEM_VOLTAGE;

        public const int CELLULAR_APN_LENGTH = 64;

        public const int CELLULAR_APN_USER_LENGTH = 32;

        public const int CELLULAR_APN_PASSWORD_LENGTH = 32;

        public const int CELLULAR_HOST_LENGTH = 64;

        public const int CELLULAR_CLIENT_ID_LENGTH = 48;

        public const int CELLULAR_MQTT_USERNAME_LENGTH = 48;

        public const int CELLULAR_MQTT_PASSWORD_LENGTH = 48;

        public const int CELLULAR_TOPIC_LENGTH = 128;

        public const int CELLULAR_OPENREMOTE_ASSET_NAME_LENGTH = 28;

        public const int CELLULAR_LEGACY_STATIC_PAYLOAD_BYTES = 3 + CELLULAR_APN_LENGTH + CELLULAR_APN_USER_LENGTH + CELLULAR_APN_PASSWORD_LENGTH + CELLULAR_HOST_LENGTH + 2 + CELLULAR_CLIENT_ID_LENGTH + CELLULAR_MQTT_USERNAME_LENGTH + CELLULAR_MQTT_PASSWORD_LENGTH + CELLULAR_TOPIC_LENGTH + CELLULAR_TOPIC_LENGTH + 2 + 4 + 4;

        public const int CELLULAR_STATIC_PAYLOAD_BYTES = CELLULAR_LEGACY_STATIC_PAYLOAD_BYTES + CELLULAR_OPENREMOTE_ASSET_NAME_LENGTH;

        public const int LAST_CELLULAR_PARAM_INDEX = 15;

        public const int TIME_ZONE_RULE_LENGTH = 15;

        public const int LAST_DIGITAL_PARAM_INDEX = 0;
    }
}
