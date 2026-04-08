using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace Cortex.Models
{
    public partial class LogFile : ObservableObject
    {
        [ObservableProperty]
        private string fileName = string.Empty;

        [ObservableProperty]
        private string fullPath = string.Empty;

        [ObservableProperty]
        private DateTime lastWriteTimeUtc;

        [ObservableProperty]
        private long fileSizeBytes;

        [ObservableProperty]
        private bool isDownloaded;

        [ObservableProperty]
        private bool isControllerFile;

        [ObservableProperty]
        private int controllerIndex = -1;

        public string DownloadedIcon => IsDownloaded ? "✔" : string.Empty;

        public string FileSizeMbDisplay => $"({FileSizeBytes / (1024d * 1024d):F2} MB)";

        partial void OnFileSizeBytesChanged(long value)
        {
            OnPropertyChanged(nameof(FileSizeMbDisplay));
        }

        partial void OnIsDownloadedChanged(bool value)
        {
            OnPropertyChanged(nameof(DownloadedIcon));
        }
    }

    public partial class LogParameterSelection : ObservableObject
    {
        [ObservableProperty]
        private string key = string.Empty;

        [ObservableProperty]
        private string displayName = string.Empty;

        [ObservableProperty]
        private bool isSelected;
    }

    public partial class LogChannelSelection : ObservableObject
    {
        [ObservableProperty]
        private int channelNumber;

        [ObservableProperty]
        private bool isSelected;

        public string DisplayName => $"CH{ChannelNumber}";
    }

    public sealed class LogMetricRow
    {
        public string Metric { get; init; } = string.Empty;

        public string Value { get; init; } = string.Empty;
    }
}