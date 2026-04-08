using System;
using System.Linq;

namespace Cortex.Models
{
    public static class InputPinCatalog
    {
        public const byte IgnitionInputPin = 66;

        public static readonly byte[] DIChannelInputPins = { 79, 78, 77, 76, 75, 74, 73, 72 };

        public static readonly byte[] ANAChannelInputPins = { 208, 209, 210, 211, 212, 213, 214, 215 };

        public static readonly byte[] AllInputPins = DIChannelInputPins
            .Concat(new[] { IgnitionInputPin })
            .Concat(ANAChannelInputPins)
            .ToArray();

        public static string GetLabelForPin(byte pin)
        {
            if (pin == IgnitionInputPin)
            {
                return "Ignition";
            }

            int digitalIndex = Array.IndexOf(DIChannelInputPins, pin);
            if (digitalIndex >= 0)
            {
                return $"Digital {digitalIndex + 1}";
            }

            int analogueIndex = Array.IndexOf(ANAChannelInputPins, pin);
            if (analogueIndex >= 0)
            {
                return $"Ana/Dig {analogueIndex + 1}";
            }

            return $"Pin {pin}";
        }

        public static byte? TryParseLabel(string label)
        {
            if (string.Equals(label, "Ignition", StringComparison.OrdinalIgnoreCase))
            {
                return IgnitionInputPin;
            }

            if (label.StartsWith("Digital ", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(label.Substring(8), out int digitalInputNumber) &&
                digitalInputNumber >= 1 &&
                digitalInputNumber <= DIChannelInputPins.Length)
            {
                return DIChannelInputPins[digitalInputNumber - 1];
            }

            if ((label.StartsWith("Analogue ", StringComparison.OrdinalIgnoreCase) ||
                 label.StartsWith("Ana/Dig ", StringComparison.OrdinalIgnoreCase)) &&
                int.TryParse(label.Substring(label.IndexOf(' ') + 1), out int analogueInputNumber) &&
                analogueInputNumber >= 1 &&
                analogueInputNumber <= ANAChannelInputPins.Length)
            {
                return ANAChannelInputPins[analogueInputNumber - 1];
            }

            if (label.StartsWith("Pin ", StringComparison.OrdinalIgnoreCase) &&
                byte.TryParse(label.Substring(4), out byte pinNumber))
            {
                return pinNumber;
            }

            return null;
        }
    }
}