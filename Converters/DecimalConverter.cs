using Avalonia.Data.Converters;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Cortex.Converters
{
    public class OneDecimalConverter : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            if (value == null) return null;

            if (value is IFormattable f)
                return f.ToString("F1", culture);

            if (double.TryParse(value.ToString(), out var d))
                return d.ToString("F1", culture);

            return value.ToString();
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => double.TryParse(value?.ToString(), out var result) ? result : 0;
    }

}
