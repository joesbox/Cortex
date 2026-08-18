using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cortex.Models;
using LiveChartsCore;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.Measure;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Cortex.ViewModels
{
    public partial class MainWindowViewModel
    {
        [ObservableProperty]
        private bool isLogBusy;

        [ObservableProperty]
        private string logStatusMessage = string.Empty;

        [ObservableProperty]
        private double logDownloadProgress;

        [ObservableProperty]
        private bool isLogProgressIndeterminate;

        [ObservableProperty]
        private ObservableCollection<LogFile> availableLogFiles = new();

        [ObservableProperty]
        private LogFile? selectedLogFile;

        [ObservableProperty]
        private DateTimeOffset? logRangeStartDate;

        [ObservableProperty]
        private TimeSpan? logRangeStartTime;

        [ObservableProperty]
        private DateTimeOffset? logRangeEndDate;

        [ObservableProperty]
        private TimeSpan? logRangeEndTime;

        [ObservableProperty]
        private ObservableCollection<LogParameterSelection> systemParameterSelections = new();

        [ObservableProperty]
        private bool areAllSystemParametersSelected;

        [ObservableProperty]
        private ObservableCollection<LogChannelSelection> channelSelections = new();

        [ObservableProperty]
        private bool areAllChannelsSelected;

        [ObservableProperty]
        private ObservableCollection<LogParameterSelection> channelFieldSelections = new();

        [ObservableProperty]
        private bool areAllChannelFieldsSelected;

        [ObservableProperty]
        private ObservableCollection<LogParameterSelection> digitalInputSelections = new();

        [ObservableProperty]
        private bool areAllDigitalInputsSelected;

        [ObservableProperty]
        private ObservableCollection<LogParameterSelection> analogueInputSelections = new();

        [ObservableProperty]
        private bool areAllAnalogueInputsSelected;

        [ObservableProperty]
        private bool isLogCrosshairEnabled = true;

        [ObservableProperty]
        private ObservableCollection<ISeries> logSeriesCollection = new();

        [ObservableProperty]
        private ObservableCollection<LogMetricRow> logMetricRows = new();

        public bool CanDownloadSelectedLog => !IsLogBusy && SelectedLogFile is not null && !SelectedLogFile.IsDownloaded;

        public bool CanCancelLogDownload => IsLogBusy;

        public bool CanResetLogs => !IsLogBusy && IsConnected;

        public bool CanAccessOperationalTabs => IsConnected && !IsLogBusy;

        public FindingStrategy LogFindingStrategy => FindingStrategy.CompareOnlyXTakeClosest;

        public FindingStrategy LiveFindingStrategy => FindingStrategy.CompareOnlyXTakeClosest;

        public ICartesianAxis[] LogYAxes { get; set; } =
        [
            new Axis
            {
                Name = "Value",
                Labeler = value => value.ToString("F2"),
                SeparatorsPaint = new SolidColorPaint
                {
                    StrokeThickness = 1,
                    Color = new SKColor(200, 200, 200),
                },
                SubseparatorsPaint = new SolidColorPaint
                {
                    Color = new SKColor(50, 50, 50),
                    StrokeThickness = 0.5f,
                },
                SubseparatorsCount = 9,
                ZeroPaint = new SolidColorPaint
                {
                    Color = new SKColor(200, 200, 200),
                    StrokeThickness = 2,
                },
                TicksPaint = new SolidColorPaint
                {
                    Color = new SKColor(200, 200, 200),
                    StrokeThickness = 1.5f,
                },
                SubticksPaint = new SolidColorPaint
                {
                    Color = new SKColor(50, 50, 50),
                    StrokeThickness = 1,
                },
            }
        ];

        public ICartesianAxis[] LogXAxes { get; set; } =
        [
            new Axis
            {
                Name = "Time",
                Labeler = value => value.ToString("F0"),
                SeparatorsPaint = new SolidColorPaint
                {
                    StrokeThickness = 1,
                    Color = new SKColor(200, 200, 200),
                },
                SubseparatorsPaint = new SolidColorPaint
                {
                    Color = new SKColor(50, 50, 50),
                    StrokeThickness = 0.5f,
                },
                SubseparatorsCount = 9,
                ZeroPaint = new SolidColorPaint
                {
                    Color = new SKColor(200, 200, 200),
                    StrokeThickness = 2,
                },
                TicksPaint = new SolidColorPaint
                {
                    Color = new SKColor(200, 200, 200),
                    StrokeThickness = 1.5f,
                },
                SubticksPaint = new SolidColorPaint
                {
                    Color = new SKColor(50, 50, 50),
                    StrokeThickness = 1,
                },
            }
        ];

        private readonly List<ParsedLogRow> _parsedLogRows = [];
        private readonly Dictionary<string, List<LogSeriesPoint>> _parsedLogSeries = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _parsedLogSeriesUnits = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _downloadedLogCache = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, List<LogSeriesPoint>> _activeFilteredLogSeries = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _activeLogSeriesKeys = [];
        private IReadOnlyDictionary<string, string> _activeSystemDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _logLoadCts;
        private int _selectedLogLoadVersion;
        private bool _updatingLogSelectionMasterState;
        private bool _bulkUpdatingLogSelections;
        private const int MaxRenderedLogPointsPerSeries = 1800;
        private const int MinimumViewportRenderedPointsPerSeries = 600;
        private static readonly SKColor[] LogSeriesPalette =
        {
            new(33, 150, 243),
            new(244, 67, 54),
            new(76, 175, 80),
            new(255, 193, 7),
            new(156, 39, 176),
            new(255, 87, 34),
            new(0, 188, 212),
            new(205, 220, 57),
        };

        private static readonly string[] SystemHeaderFields =
        {
            "Date",
            "Time",
            "System Temp",
            "SIM Module Temp",
            "IMU Temp",
            "System Voltage",
            "System Current",
            "Error Flags",
            "IMU Accel X",
            "IMU Accel Y",
            "IMU Accel Z",
            "IMU Gyro X",
            "IMU Gyro Y",
            "IMU Gyro Z",
            "IMU Mag X",
            "IMU Mag Y",
            "IMU Mag Z",
            "Lat",
            "Lon",
            "Alt",
            "Speed",
            "Accuracy"
        };

        private static readonly string[] ChannelHeaderFields =
        {
            "Channel Type",
            "Enabled",
            "Current Value",
            "Current Threshold High",
            "Current Threshold Low",
            "Multi-Channel",
            "Group Number",
            "Channel Error Flags",
            "Analogue Input"
        };

        private static readonly string[] DigitalInputHeaderFields =
        {
            "Digital Input 1",
            "Digital Input 2",
            "Digital Input 3",
            "Digital Input 4",
            "Digital Input 5",
            "Digital Input 6",
            "Digital Input 7",
            "Digital Input 8"
        };

        private static readonly string[] AnalogueInputHeaderFields =
        {
            "Analogue Input 1",
            "Analogue Input 2",
            "Analogue Input 3",
            "Analogue Input 4",
            "Analogue Input 5",
            "Analogue Input 6",
            "Analogue Input 7",
            "Analogue Input 8"
        };

        private static readonly Regex NumericWithUnitRegex = new(
            @"^\s*(?<value>[+-]?(?:\d+(?:\.\d+)?|\.\d+)(?:[eE][+-]?\d+)?)\s*(?<unit>.*)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex LogFileTimestampRegex = new(
            @"(?<timestamp>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(?:\.[^.]+)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly Regex LegacyLogFileTimestampRegex = new(
            @"(?<date>\d{6})-(?<time>\d{6})(?:\.[^.]+)?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        private static readonly string PreferredDownloadedLogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "Synapse PDM Logs");

        partial void OnSelectedLogFileChanged(LogFile? value)
        {
            OnPropertyChanged(nameof(CanDownloadSelectedLog));
            _ = HandleSelectedLogFileChangedAsync(value);
        }

        partial void OnIsLogBusyChanged(bool value)
        {
            OnPropertyChanged(nameof(CanDownloadSelectedLog));
            OnPropertyChanged(nameof(CanCancelLogDownload));
            OnPropertyChanged(nameof(CanResetLogs));
            OnPropertyChanged(nameof(CanAccessOperationalTabs));
            OnPropertyChanged(nameof(CanSetControllerRtc));
            OnPropertyChanged(nameof(CanFactoryReset));
            OnPropertyChanged(nameof(CanTestCellularConnection));
            OnPropertyChanged(nameof(CanProvisionOpenRemote));
        }

        partial void OnAreAllSystemParametersSelectedChanged(bool value)
        {
            if (_updatingLogSelectionMasterState)
            {
                return;
            }

            SetAllSelections(SystemParameterSelections, value);
        }

        partial void OnAreAllChannelsSelectedChanged(bool value)
        {
            if (_updatingLogSelectionMasterState)
            {
                return;
            }

            SetAllSelections(ChannelSelections, value);
        }

        partial void OnAreAllChannelFieldsSelectedChanged(bool value)
        {
            if (_updatingLogSelectionMasterState)
            {
                return;
            }

            SetAllSelections(ChannelFieldSelections, value);
        }

        partial void OnAreAllDigitalInputsSelectedChanged(bool value)
        {
            if (_updatingLogSelectionMasterState)
            {
                return;
            }

            SetAllSelections(DigitalInputSelections, value);
        }

        partial void OnAreAllAnalogueInputsSelectedChanged(bool value)
        {
            if (_updatingLogSelectionMasterState)
            {
                return;
            }

            SetAllSelections(AnalogueInputSelections, value);
        }

        partial void OnIsLogCrosshairEnabledChanged(bool value)
        {
            UpdateLogCrosshairState();
        }

        [RelayCommand]
        private async Task RefreshAvailableLogFilesAsync()
        {
            if (IsLogBusy)
            {
                return;
            }

            IsLogBusy = true;
            LogStatusMessage = "Loading available log files...";
            LogDownloadProgress = 0;
            IsLogProgressIndeterminate = true;

            try
            {
                List<LogFile> files;
                if (IsConnected && _portService != null)
                {
                    var controllerFiles = await _portService.RequestLogFileListAsync(5000);
                    files = controllerFiles
                        .Select((file, index) => new LogFile
                        {
                            FileName = file.FileName,
                            FullPath = file.FileName,
                            LastWriteTimeUtc = TryParseLogFileTimestampUtc(file.FileName) ?? DateTime.MinValue,
                            FileSizeBytes = file.FileSizeBytes,
                            IsDownloaded = HasDownloadedControllerLog(file.FileName, file.FileSizeBytes),
                            IsControllerFile = true,
                            ControllerIndex = index,
                        })
                        .ToList();
                }
                else
                {
                    files = [];
                }

                files = files
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .ThenByDescending(file => file.ControllerIndex)
                    .ThenByDescending(file => file.FileName, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                AvailableLogFiles = new ObservableCollection<LogFile>(files);

                if (AvailableLogFiles.Count == 0)
                {
                    SelectedLogFile = null;
                    LogStatusMessage = "Click refresh to retrieve PDM logs.";
                    LogSeriesCollection = new ObservableCollection<ISeries>();
                    _parsedLogRows.Clear();
                    return;
                }

                LogStatusMessage = $"Found {AvailableLogFiles.Count} log files.";

                if (SelectedLogFile == null || !AvailableLogFiles.Any(f => f.FullPath == SelectedLogFile.FullPath))
                {
                    SelectedLogFile = AvailableLogFiles.FirstOrDefault();
                }
            }
            catch (Exception ex)
            {
                LogStatusMessage = "Failed to load log file list.";
                AddLog($"Log list load failed: {ex.Message}");
            }
            finally
            {
                IsLogBusy = false;
                IsLogProgressIndeterminate = false;
            }
        }

        [RelayCommand]
        private async Task DownloadSelectedLogFileAsync()
        {
            if (SelectedLogFile == null)
            {
                return;
            }

            string cacheKey = BuildLogCacheKey(SelectedLogFile);
            if (_downloadedLogCache.TryGetValue(cacheKey, out string? cachedContent))
            {
                EnsureValidLogContent(cachedContent);
                ParseLogContent(cachedContent);
                ApplyLogFilters();
                SelectedLogFile.IsDownloaded = true;
                LogStatusMessage = $"Loaded cached {SelectedLogFile.FileName}.";
                LogDownloadProgress = 100;
                IsLogProgressIndeterminate = false;
                OnPropertyChanged(nameof(CanDownloadSelectedLog));
                return;
            }

            if (SelectedLogFile.IsControllerFile && TryGetStoredControllerLogCopy(SelectedLogFile, out string? storedLogPath))
            {
                string storedContent = await File.ReadAllTextAsync(storedLogPath!, CancellationToken.None);
                EnsureValidLogContent(storedContent);
                _downloadedLogCache[cacheKey] = storedContent;
                ParseLogContent(storedContent);
                ApplyLogFilters();
                SelectedLogFile.IsDownloaded = true;
                LogStatusMessage = $"Loaded saved copy of {SelectedLogFile.FileName}.";
                LogDownloadProgress = 100;
                IsLogProgressIndeterminate = false;
                OnPropertyChanged(nameof(CanDownloadSelectedLog));
                return;
            }

            _logLoadCts?.Cancel();
            _logLoadCts = new CancellationTokenSource();
            var token = _logLoadCts.Token;

            _pauseLiveUiUpdates = true;
            IsLogBusy = true;
            LogStatusMessage = $"Downloading {SelectedLogFile.FileName}...";
            LogDownloadProgress = 0;
            IsLogProgressIndeterminate = true;

            try
            {
                string content;
                if (IsConnected && _portService != null && SelectedLogFile.IsControllerFile)
                {
                    int selectedIndex = SelectedLogFile.ControllerIndex;
                    if (selectedIndex < 0)
                    {
                        throw new InvalidOperationException("Selected controller log index is invalid.");
                    }

                    AddLog($"Opening controller log: {SelectedLogFile.FileName}");

                    _portService.BeginLogTransferSession();
                    bool transferOpened = false;
                    try
                    {
                        bool opened = await _portService.OpenLogTransferAsync((byte)selectedIndex);
                        if (!opened)
                        {
                            throw new InvalidOperationException("Controller refused log transfer open request.");
                        }

                        transferOpened = true;
                        content = await ReadLogFromControllerAsync(token);
                    }
                    finally
                    {
                        if (transferOpened)
                        {
                            _portService.CancelLogTransfer();
                        }

                        _portService.EndLogTransferSession();
                    }
                }
                else
                {
                    if (IsConnected && _portService != null)
                    {
                        AddLog("Selected file is local; reading from disk.");
                    }
                    content = await ReadLogFileWithProgressAsync(SelectedLogFile.FullPath, token);
                }

                EnsureValidLogContent(content);

                if (SelectedLogFile.IsControllerFile)
                {
                    await PersistDownloadedControllerLogAsync(SelectedLogFile, content, token);
                }

                _downloadedLogCache[cacheKey] = content;
                SelectedLogFile.IsDownloaded = true;
                OnPropertyChanged(nameof(CanDownloadSelectedLog));

                ParseLogContent(content);
                ApplyLogFilters();
                LogStatusMessage = $"Loaded {SelectedLogFile.FileName}.";
                LogDownloadProgress = 100;
                IsLogProgressIndeterminate = false;
            }
            catch (OperationCanceledException)
            {
                LogStatusMessage = "Log download cancelled.";
                _portService?.CancelLogTransfer();
                _portService?.EndLogTransferSession();
                IsLogProgressIndeterminate = false;
            }
            catch (Exception ex)
            {
                _parsedLogRows.Clear();
                LogSeriesCollection = new ObservableCollection<ISeries>();
                LogStatusMessage = "Failed to download selected log file.";
                AddLog($"Log download failed: {ex.Message}");
                LogDownloadProgress = 0;
                _portService?.CancelLogTransfer();
                _portService?.EndLogTransferSession();
                IsLogProgressIndeterminate = false;
            }
            finally
            {
                IsLogBusy = false;
                _pauseLiveUiUpdates = false;
                OnPropertyChanged(nameof(CanDownloadSelectedLog));
            }
        }

        private async Task HandleSelectedLogFileChangedAsync(LogFile? selectedFile)
        {
            int loadVersion = Interlocked.Increment(ref _selectedLogLoadVersion);

            if (selectedFile == null)
            {
                ResetParsedLogContent();
                LogStatusMessage = "No log file selected.";
                return;
            }

            if (IsLogBusy)
            {
                return;
            }

            string cacheKey = BuildLogCacheKey(selectedFile);
            if (_downloadedLogCache.TryGetValue(cacheKey, out string? cachedContent))
            {
                if (!IsSelectedLogLoadCurrent(selectedFile, loadVersion))
                {
                    return;
                }

                EnsureValidLogContent(cachedContent);
                ParseLogContent(cachedContent);
                ApplyLogFilters();
                selectedFile.IsDownloaded = true;
                LogStatusMessage = $"Loaded {selectedFile.FileName}.";
                return;
            }

            if (selectedFile.IsControllerFile && TryGetStoredControllerLogCopy(selectedFile, out string? storedLogPath))
            {
                try
                {
                    string content = await File.ReadAllTextAsync(storedLogPath!);
                    if (!IsSelectedLogLoadCurrent(selectedFile, loadVersion))
                    {
                        return;
                    }

                    EnsureValidLogContent(content);
                    _downloadedLogCache[cacheKey] = content;
                    selectedFile.IsDownloaded = true;
                    ParseLogContent(content);
                    ApplyLogFilters();
                    LogStatusMessage = $"Loaded {selectedFile.FileName}.";
                }
                catch (Exception ex)
                {
                    if (!IsSelectedLogLoadCurrent(selectedFile, loadVersion))
                    {
                        return;
                    }

                    ResetParsedLogContent();
                    LogStatusMessage = "Failed to load saved log copy.";
                    AddLog($"Saved log load failed: {ex.Message}");
                }

                return;
            }

            if (!selectedFile.IsControllerFile && File.Exists(selectedFile.FullPath))
            {
                try
                {
                    string content = await File.ReadAllTextAsync(selectedFile.FullPath);
                    if (!IsSelectedLogLoadCurrent(selectedFile, loadVersion))
                    {
                        return;
                    }

                    EnsureValidLogContent(content);
                    _downloadedLogCache[cacheKey] = content;
                    selectedFile.IsDownloaded = true;
                    ParseLogContent(content);
                    ApplyLogFilters();
                    LogStatusMessage = $"Loaded {selectedFile.FileName}.";
                }
                catch (InvalidDataException ex)
                {
                    if (!IsSelectedLogLoadCurrent(selectedFile, loadVersion))
                    {
                        return;
                    }

                    ResetParsedLogContent();
                    LogStatusMessage = ex.Message;
                    AddLog($"Local log validation failed: {ex.Message}");
                }
                catch (Exception ex)
                {
                    if (!IsSelectedLogLoadCurrent(selectedFile, loadVersion))
                    {
                        return;
                    }

                    ResetParsedLogContent();
                    LogStatusMessage = "Failed to load selected local log file.";
                    AddLog($"Local log load failed: {ex.Message}");
                }

                return;
            }

            if (!IsSelectedLogLoadCurrent(selectedFile, loadVersion))
            {
                return;
            }

            ResetParsedLogContent();
            LogStatusMessage = $"Selected {selectedFile.FileName}. Download to load chart data.";
        }

        [RelayCommand]
        private void CancelLogDownload()
        {
            if (!IsLogBusy)
            {
                return;
            }

            _logLoadCts?.Cancel();
            _portService?.CancelLogTransfer();
            LogStatusMessage = "Cancelling log download...";
        }

        [RelayCommand]
        private async Task BrowseLocalLogFileAsync()
        {
            if (IsLogBusy)
            {
                return;
            }

            string? selectedPath = await _appCloser.BrowseLocalLogFilePathAsync(PreferredDownloadedLogDirectory);
            if (string.IsNullOrWhiteSpace(selectedPath))
            {
                return;
            }

            try
            {
                var fileInfo = new FileInfo(selectedPath);
                if (!fileInfo.Exists)
                {
                    throw new FileNotFoundException("Selected log file could not be found.", selectedPath);
                }

                string content = await File.ReadAllTextAsync(fileInfo.FullName);
                EnsureValidLogContent(content);

                var logFile = new LogFile
                {
                    FileName = fileInfo.Name,
                    FullPath = fileInfo.FullName,
                    LastWriteTimeUtc = fileInfo.LastWriteTimeUtc,
                    FileSizeBytes = fileInfo.Length,
                    IsDownloaded = true,
                    IsControllerFile = false,
                    ControllerIndex = -1,
                };

                _downloadedLogCache[BuildLogCacheKey(logFile)] = content;
                ParseLogContent(content);
                ApplyLogFilters();
                LogDownloadProgress = 100;
                IsLogProgressIndeterminate = false;
                LogStatusMessage = $"Local file {logFile.FileName} loaded";
            }
            catch (InvalidDataException ex)
            {
                LogStatusMessage = ex.Message;
                AddLog($"Local log validation failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                LogStatusMessage = "Failed to load selected local log file.";
                AddLog($"Local log browse failed: {ex.Message}");
            }
        }

        [RelayCommand]
        private async Task ResetLogsAsync()
        {
            if (IsLogBusy || !IsConnected || _portService == null)
            {
                return;
            }

            bool confirmed = await _appCloser.ConfirmAsync(
                "Reset Logs",
                "This will erase all log files on the controller SD card and clear the log list. Continue?");

            if (!confirmed)
            {
                return;
            }

            IsLogBusy = true;
            _pauseLiveUiUpdates = true;
            IsLogProgressIndeterminate = true;
            LogStatusMessage = "Resetting controller logs...";

            try
            {
                bool resetOk = await _portService.ResetLogStorageAsync(10000);
                if (!resetOk)
                {
                    throw new InvalidOperationException("Controller log reset failed.");
                }

                _downloadedLogCache.Clear();
                AvailableLogFiles.Clear();
                SelectedLogFile = null;
                ResetParsedLogContent();
                LogDownloadProgress = 0;
                LogStatusMessage = "Controller logs reset.";
                AddLog("Controller log storage reset complete.");

                await RefreshAvailableLogFilesAsync();
            }
            catch (Exception ex)
            {
                LogStatusMessage = "Failed to reset controller logs.";
                AddLog($"Reset logs failed: {ex.Message}");
            }
            finally
            {
                IsLogBusy = false;
                _pauseLiveUiUpdates = false;
                IsLogProgressIndeterminate = false;
                OnPropertyChanged(nameof(CanDownloadSelectedLog));
                OnPropertyChanged(nameof(CanResetLogs));
                OnPropertyChanged(nameof(CanCancelLogDownload));
            }
        }

        private async Task<string> ReadLogFromControllerAsync(CancellationToken token)
        {
            if (_portService == null)
            {
                throw new InvalidOperationException("The PDM connection is not available.");
            }

            AddLog("Downloading log...");
            try
            {
                return await _portService.ReadLogBulkAsync(progress =>
                {
                    IsLogProgressIndeterminate = false;
                    LogDownloadProgress = progress;
                }, token);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidOperationException)
            {
                AddLog($"Stream download failed. Trying alternative download mode...");
                _portService.CancelLogTransfer();
                await Task.Delay(50, token);
                return await ReadLogFromControllerPullAsync(token);
            }
        }

        private async Task<string> ReadLogFromControllerPullAsync(CancellationToken token, StringBuilder? existingBuilder = null)
        {
            var builder = existingBuilder ?? new StringBuilder();
            int consecutiveChunkFailures = 0;
            const int maxConsecutiveChunkFailures = 12;
            const int pullChunkTimeoutMs = 2000;
            int processedChunkCount = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                var chunk = await _portService!.RequestLogChunkAsync(pullChunkTimeoutMs);
                if (chunk == null)
                {
                    consecutiveChunkFailures++;
                    if (consecutiveChunkFailures >= maxConsecutiveChunkFailures)
                    {
                        throw new IOException("Controller log chunk request timed out.");
                    }

                    int retryDelayMs = Math.Min(500, 75 * consecutiveChunkFailures);
                    await Task.Delay(retryDelayMs, token);
                    continue;
                }

                consecutiveChunkFailures = 0;

                if (!string.IsNullOrEmpty(chunk.Text))
                {
                    builder.Append(chunk.Text);
                }

                IsLogProgressIndeterminate = false;
                LogDownloadProgress = chunk.Progress;

                if (chunk.Done)
                {
                    break;
                }

                processedChunkCount++;
                if ((processedChunkCount % 16) == 0)
                {
                    await Task.Yield();
                }
            }

            return builder.ToString();
        }

        private async Task<string> ReadLogFileWithProgressAsync(string filePath, CancellationToken token)
        {
            var fileInfo = new FileInfo(filePath);
            long totalBytes = fileInfo.Exists ? fileInfo.Length : 0;

            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, useAsync: true);
            using var reader = new StreamReader(stream);

            var builder = new StringBuilder();
            int lineCounter = 0;

            while (true)
            {
                token.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(token);
                if (line == null)
                {
                    break;
                }

                builder.AppendLine(line);
                lineCounter++;

                if (lineCounter % 25 == 0)
                {
                    LogDownloadProgress = totalBytes > 0
                        ? Math.Min(100.0, (stream.Position * 100.0) / totalBytes)
                        : 0;
                    await Task.Yield();
                }
            }

            return builder.ToString();
        }
    }
}
