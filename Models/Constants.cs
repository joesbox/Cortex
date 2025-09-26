using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

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

        public const char COMMAND_ID_NEWCONFIG = 'n';

        public const char COMMAND_ID_SEND = 's';

        public const char COMMAND_ID_SENDING = 't';

        public const char COMMAND_ID_CHECKSUM_FAIL = 'f';

        public const char COMMAND_ID_SAVECHANGES = 'S';

        public const char COMMAND_ID_FW_VER = 'v';

        public const char COMMAND_ID_BUILD_DATE = 'd';        
    }
}
