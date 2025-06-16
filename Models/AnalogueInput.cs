using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cortex.Models
{
    [Serializable]
    public class AnalogueInput
    {
        public bool PullUpEnable { get; set; }      // Pull-up resistor enable flag
        public bool PullDownEnable { get; set; }    // Pull-down resistor enable flag
        public bool IsDigital { get; set; }         // Flag to indicate if the input is digital
    }
}
