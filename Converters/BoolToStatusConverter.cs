using Avalonia.Data.Converters;
using Avalonia.Media;
using System;
using System.Globalization;

namespace Cortex.Converters
{
    public class BoolToStatusConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool hasError)
            {
                return hasError ? "FAULT" : "OK";
            }
            return "UNKNOWN";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToOverrideStatusConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool enabled)
            {
                return enabled ? "ACTIVE" : "INACTIVE";
            }
            return "UNKNOWN";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ByteToStatusConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is byte byteValue)
            {
                return byteValue == 0 ? "INACTIVE" : "ACTIVE";
            }
            return "UNKNOWN";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class ByteToBackgroundConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is byte byteValue)
            {
                return byteValue == 0 ? Brushes.Gray : new SolidColorBrush(Color.FromRgb(40, 167, 69));
                // Red: #DC3545, Green: #28A745 (Bootstrap colors)
            }
            return Brushes.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToColorConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool hasError)
            {
                return hasError ? Brushes.Red : Brushes.Green;
            }
            return Brushes.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToBackgroundConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value is bool hasError)
            {
                return hasError ? new SolidColorBrush(Color.FromRgb(220, 53, 69)) : new SolidColorBrush(Color.FromRgb(40, 167, 69));
                // Red: #DC3545, Green: #28A745 (Bootstrap colors)
            }
            return Brushes.Gray;
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }

    public class BoolToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
            (bool)value ? "Deactivate" : "Hold 2s to Activate";
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
    }

    public class ByteToTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is byte b)
            {
                return b != 0 ? "ON" : "OFF";
            }
            return "OFF";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is string s)
            {
                return s.Equals("ON", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
            }
            return (byte)0;
        }
    }

    public class ByteToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is byte b)
            {
                return b != 0 ? Brushes.LightGreen : Brushes.LightGray;
            }
            return Brushes.LightGray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is IBrush brush)
            {
                return brush == Brushes.LightGreen ? (byte)1 : (byte)0;
            }
            return (byte)0;
        }
    }
}
