using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Cortex.Converters
{
    public class HexStringToInt16Converter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null) return "000";

            // Convert int16 to hex string (without 0x prefix)
            if (value is short shortValue)
            {
                // Ensure the value is within 11-bit range and format as 3-digit hex
                int maskedValue = shortValue & 0x7FF;
                return maskedValue.ToString("X3");
            }
            else if (value is int intValue)
            {
                // Handle int values too
                int maskedValue = intValue & 0x7FF;
                return maskedValue.ToString("X3");
            }

            return "000";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string stringValue)
                return (short)0;

            string text = stringValue.Trim();

            if (string.IsNullOrWhiteSpace(text))
                return (short)0;

            // Parse hex string to integer
            try
            {
                int hexValue = System.Convert.ToInt32(text, 16);

                // Ensure it's within 11-bit range
                hexValue = hexValue & 0x7FF;

                // Convert to int16
                return (short)hexValue;
            }
            catch
            {
                return (short)0;
            }
        }
    }
}