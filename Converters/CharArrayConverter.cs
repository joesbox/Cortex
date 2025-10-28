using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace Cortex.Converters
{
    public class CharArrayToStringConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is char[] charArray)
            {
                return new string(charArray).TrimEnd('\0');
            }
            return string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return Avalonia.Data.BindingNotification.UnsetValue;
        }
    }
}
