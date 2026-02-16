using Avalonia;
using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Cortex.Converters
{
    public class HexStringToInt16Converter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is ushort u)
            {
                return (u & 0x7FF).ToString("X3");
            }

            if (value is int i)
            {
                return (i & 0x7FF).ToString("X3");
            }

            return "000";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string s)
                return AvaloniaProperty.UnsetValue;

            s = s.Trim();

            if (!ushort.TryParse(s, NumberStyles.HexNumber, culture, out var hex))
                return AvaloniaProperty.UnsetValue;

            return (ushort)(hex & 0x7FF);
        }
    }
}