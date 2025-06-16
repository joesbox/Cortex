using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cortex.Models
{
    [Serializable]
    public class SystemParameters
    {
        public float SystemTemperature { get; set; }        // Internal system (Teensy processor) temperature
        public byte CANResEnabled { get; set; }             // CAN bus termination resistor enabled
        public float VBatt { get; set; }                    // Battery supply voltage
        public float SystemCurrent { get; set; }            // Total current draw for all enabled channels
        public byte ErrorFlags { get; set; }                // Bitmask for system error flags
        public int ChannelDataCANID { get; set; }           // Channel data CAN ID (transmit)
        public int SystemDataCANID { get; set; }            // System data CAN ID (transmit)
        public int ConfigDataCANID { get; set; }            // Configuration data CAN ID (receive)
        public byte SpeedUnitPref { get; set; }             // Speed unit preference (0 = mph, 1 = km/h)
        public byte DistanceUnitPref { get; set; }          // Distance unit preference (0 = metres, 1 = feet)
        public bool AllowData { get; set; }                 // Allow GSM data
        public bool AllowGPS { get; set; }                  // Allow GPS
    }
}
