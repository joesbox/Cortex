using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using System;
using System.Globalization;

namespace Cortex.Models
{
    public partial class ChartSeriesToggleItem : ObservableObject
    {
        private readonly LineSeries<ObservablePoint> _series;
        private readonly Action<bool>? _onVisibilityChanged;

        public ChartSeriesToggleItem(string displayName, IBrush swatchBrush, LineSeries<ObservablePoint> series, bool isEnabled = true, Action<bool>? onVisibilityChanged = null)
        {
            _series = series;
            _onVisibilityChanged = onVisibilityChanged;
            this.displayName = displayName;
            this.swatchBrush = swatchBrush;
            this.isEnabled = isEnabled;
            _series.IsVisible = isEnabled;
        }

        [ObservableProperty]
        private string displayName;

        [ObservableProperty]
        private IBrush swatchBrush;

        [ObservableProperty]
        private bool isEnabled;

        private string _currentValueText = "--";

        public string CurrentValueText => _currentValueText;

        public void SetCurrentValue(double? value)
        {
            string nextValue = value.HasValue
                ? value.Value.ToString("F1", CultureInfo.InvariantCulture)
                : "--";

            if (string.Equals(_currentValueText, nextValue, StringComparison.Ordinal))
            {
                return;
            }

            _currentValueText = nextValue;
            OnPropertyChanged(nameof(CurrentValueText));
        }

        partial void OnIsEnabledChanged(bool value)
        {
            _series.IsVisible = value;
            _onVisibilityChanged?.Invoke(value);
        }
    }
}
