using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cortex.Models
{
    [Serializable]
    public class DigitalInput
    {
        public bool ActiveLow { get; set; }          // Active low flag
    }
}
