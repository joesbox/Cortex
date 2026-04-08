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

        public const int LOG_FILE_NAME_LENGTH = 24;

        public const int LAST_CHANNEL_PARAM_INDEX = 27;

        public const int LAST_ANALOGUE_PARAM_INDEX = 12;

        public const int LAST_SYSTEM_PARAM_INDEX = 11;

        public const int SYSTEM_PARAM_TIME_ZONE_RULE = 11;

        public const int TIME_ZONE_RULE_LENGTH = 15;

        public const int LAST_DIGITAL_PARAM_INDEX = 0;
    }
}
