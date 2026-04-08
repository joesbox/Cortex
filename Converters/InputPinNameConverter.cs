using Avalonia.Data.Converters;
using Cortex.Models;
using System;
using System.Globalization;

namespace Cortex.Converters
{
    public class ChannelInputConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is byte pinNumber)
            {
                return InputPinCatalog.GetLabelForPin(pinNumber);
            }

            return value?.ToString() ?? string.Empty;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is string str)
            {
                return InputPinCatalog.TryParseLabel(str);
            }

            return null;
        }
    }
}
