using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Cortex.Converters
{
    public class ChannelInputConverter : IValueConverter
    {
        private static readonly byte[] DIChannelInputPins = { 79, 78, 77, 76, 75, 74, 73, 72 };
        private static readonly byte[] ANAChannelInputPins = { 208, 209, 210, 211, 212, 213, 214, 215 };

        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is byte pinNumber)
            {
                // Check Digital Input channels
                int diIndex = Array.IndexOf(DIChannelInputPins, pinNumber);
                if (diIndex >= 0)
                {
                    return $"Digital {diIndex + 1}";
                }

                // Check Analogue Input channels
                int anaIndex = Array.IndexOf(ANAChannelInputPins, pinNumber);
                if (anaIndex >= 0)
                {
                    return $"Analogue {anaIndex + 1}";
                }

                // If not found in either array, return the pin number
                return $"Pin {pinNumber}";
            }

            return value?.ToString() ?? string.Empty;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string str)
            {
                // Parse "Digital X" format
                if (str.StartsWith("Digital ", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(str.Substring(8), out int diNum) && diNum >= 1 && diNum <= 8)
                    {
                        return DIChannelInputPins[diNum - 1];
                    }
                }

                // Parse "Analogue X" format
                if (str.StartsWith("Analogue ", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(str.Substring(9), out int anaNum) && anaNum >= 1 && anaNum <= 8)
                    {
                        return ANAChannelInputPins[anaNum - 1];
                    }
                }

                // Parse "Pin X" format
                if (str.StartsWith("Pin ", StringComparison.OrdinalIgnoreCase))
                {
                    if (byte.TryParse(str.Substring(4), out byte pinNum))
                    {
                        return pinNum;
                    }
                }
            }

            return null;
        }
    }
}
