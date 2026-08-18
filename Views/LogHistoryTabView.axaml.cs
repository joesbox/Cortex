using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Cortex.Models;
using Cortex.ViewModels;
using Mapsui;
using Mapsui.Extensions;
using Mapsui.Layers;
using Mapsui.Limiting;
using Mapsui.Manipulations;
using Mapsui.Nts;
using Mapsui.Projections;
using Mapsui.Styles;
using Mapsui.Tiling;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Cortex.Views;

public partial class LogHistoryTabView : UserControl
{
        private const double MinimumVisibleMarkerSpacingPixels = 18;

        private readonly DispatcherTimer _logMapRefreshTimer;
        private readonly Mapsui.UI.Avalonia.MapControl? _logMap;
        private readonly DataGrid? _logMapDataGrid;
        private MainWindowViewModel? _viewModel;
        private bool _isLogMapInitialized;
        private bool _pendingLogMapFitToRoute;
        private ILayer? _logRouteLayer;
        private MemoryLayer? _logPointLayer;
        private MemoryLayer? _logHighlightLayer;
        private IReadOnlyList<LogMapGridRow> _displayedLogMapRows = [];
        private IReadOnlyList<LogMapParameterColumn> _displayedLogMapColumns = [];
        private ScreenPosition? _lastLogMapPointerPosition;
        private LogMapGridRow? _lockedLogMapRow;
        private bool _isLogMapSelectionLocked;

        public LogHistoryTabView()
        {
            AvaloniaXamlLoader.Load(this);
            _logMap = this.FindControl<Mapsui.UI.Avalonia.MapControl>("LogMap");
            _logMapDataGrid = this.FindControl<DataGrid>("LogMapDataGrid");

            _logMapRefreshTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150),
            };
            _logMapRefreshTimer.Tick += LogMapRefreshTimer_Tick;

            InitializeLogMap();

            DataContextChanged += (_, _) => AttachToViewModel();
            DetachedFromVisualTree += (_, _) => DetachFromViewModel();
        }

        private void InitializeLogMap()
        {
            if (_isLogMapInitialized || _logMap == null)
            {
                return;
            }

            Mapsui.Widgets.InfoWidgets.LoggingWidget.ShowLoggingInMap = Mapsui.Widgets.ActiveMode.No;

            _logMap.Map ??= new Mapsui.Map();
            _logMap.Map.CRS = "EPSG:3857";

            var baseTileLayer = OpenStreetMap.CreateTileLayer();
            _logMap.Map.Layers.Add(baseTileLayer);
            _logMap.Map.Navigator.Limiter = new ViewportLimiterKeepWithinExtent();
            _logMap.Map.Navigator.OverridePanBounds = baseTileLayer.Extent;
            _logMap.Map.Navigator.OverrideZoomBounds = new MMinMax(baseTileLayer.Resolutions.Min(), baseTileLayer.Resolutions.Max());
            _logMap.Map.Navigator.ViewportChanged += (_, _) => QueueLogMapRefresh();
            _logMap.PointerMoved += LogMap_PointerMoved;
            _logMap.PointerPressed += LogMap_PointerPressed;
            _logMap.PointerExited += LogMap_PointerExited;

            _isLogMapInitialized = true;
        }

        private void AttachToViewModel()
        {
            if (ReferenceEquals(_viewModel, DataContext))
            {
                return;
            }

            DetachFromViewModel();

            _viewModel = DataContext as MainWindowViewModel;
            if (_viewModel == null)
            {
                return;
            }

            _viewModel.PropertyChanged += ViewModel_PropertyChanged;
            _pendingLogMapFitToRoute = true;
            QueueLogMapRefresh(immediate: _viewModel.SelectedLogDetailTabIndex == 1);
        }

        private void DetachFromViewModel()
        {
            _logMapRefreshTimer.Stop();

            if (_viewModel != null)
            {
                _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _viewModel = null;
            }
        }

        private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (_viewModel == null)
            {
                return;
            }

            if (e.PropertyName == nameof(MainWindowViewModel.LogMapRouteVersion))
            {
                _pendingLogMapFitToRoute = true;
                QueueLogMapRefresh(immediate: _viewModel.SelectedLogDetailTabIndex == 1);
            }

            if (e.PropertyName == nameof(MainWindowViewModel.LogMapDataVersion))
            {
                QueueLogMapRefresh(immediate: _viewModel.SelectedLogDetailTabIndex == 1);
            }

            if (e.PropertyName == nameof(MainWindowViewModel.SelectedLogDetailTabIndex) && _viewModel.SelectedLogDetailTabIndex == 1)
            {
                QueueLogMapRefresh(immediate: true);
            }
        }

        private void QueueLogMapRefresh(bool immediate = false)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => QueueLogMapRefresh(immediate));
                return;
            }

            if (_viewModel == null || _viewModel.SelectedLogDetailTabIndex != 1)
            {
                return;
            }

            _logMapRefreshTimer.Stop();
            if (immediate)
            {
                RefreshLogMapDisplay();
                return;
            }

            _logMapRefreshTimer.Start();
        }

        private void LogMapRefreshTimer_Tick(object? sender, EventArgs e)
        {
            _logMapRefreshTimer.Stop();
            RefreshLogMapDisplay();
        }

        private void RefreshLogMapDisplay()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(RefreshLogMapDisplay);
                return;
            }

            if (_viewModel == null || _viewModel.SelectedLogDetailTabIndex != 1)
            {
                return;
            }

            int maxPointCount = GetLogMapPointBudget();
            LogMapViewport? viewport = TryGetLogMapViewport();
            IReadOnlyList<LogMapGridRow> routeRows = _viewModel.BuildLogMapRows(int.MaxValue, viewport);
            IReadOnlyList<LogMapGridRow> markerRows = BuildDisplayedLogMapMarkerRows(routeRows, maxPointCount);
            IReadOnlyList<LogMapParameterColumn> columns = _viewModel.GetSelectedLogMapParameterColumns();
            bool shouldCenterLogMap = _pendingLogMapFitToRoute && routeRows.Count > 0;

            MRect? routeFocusExtent = shouldCenterLogMap
                ? TryGetLogMapRouteFocusExtent(_viewModel)
                : null;

            _displayedLogMapRows = markerRows;
            _displayedLogMapColumns = columns;
            SyncLockedLogMapRow();
            EnsureLogMapGridColumns();
            UpdateLogMapInspectionRow();

            UpdateLogMapLayers(routeRows, markerRows, routeFocusExtent);
        }

        private int GetLogMapPointBudget()
        {
            double viewportWidth = _logMap?.Bounds.Width ?? 0;
            if (viewportWidth <= 0 && _logMap?.Map != null)
            {
                viewportWidth = _logMap.Map.Navigator.Viewport.Width;
            }

            return Math.Clamp((int)Math.Ceiling(Math.Max(320, viewportWidth) / 12.0), 80, 1400);
        }

        private LogMapViewport? TryGetLogMapViewport()
        {
            if (_logMap?.Map == null || !_logMap.Map.Navigator.Viewport.HasSize())
            {
                return null;
            }

            MRect extent = _logMap.Map.Navigator.Viewport.ToExtent();
            var (minLon, minLat) = SphericalMercator.ToLonLat(extent.MinX, extent.MinY);
            var (maxLon, maxLat) = SphericalMercator.ToLonLat(extent.MaxX, extent.MaxY);

            return new LogMapViewport
            {
                MinLongitude = Math.Min(minLon, maxLon),
                MinLatitude = Math.Min(minLat, maxLat),
                MaxLongitude = Math.Max(minLon, maxLon),
                MaxLatitude = Math.Max(minLat, maxLat),
            };
        }

        private IReadOnlyList<LogMapGridRow> BuildDisplayedLogMapMarkerRows(IReadOnlyList<LogMapGridRow> routeRows, int fallbackMaxPointCount)
        {
            if (routeRows.Count <= 1)
            {
                return routeRows;
            }

            if (_logMap?.Map == null || !_logMap.Map.Navigator.Viewport.HasSize())
            {
                return DownsampleLogMapRows(routeRows, fallbackMaxPointCount);
            }

            Viewport viewport = _logMap.Map.Navigator.Viewport;
            double mapWidth = _logMap.Bounds.Width > 0 ? _logMap.Bounds.Width : viewport.Width;
            double mapHeight = _logMap.Bounds.Height > 0 ? _logMap.Bounds.Height : viewport.Height;
            double screenMargin = MinimumVisibleMarkerSpacingPixels;
            double cellSize = MinimumVisibleMarkerSpacingPixels;

            var acceptedRows = new List<LogMapGridRow>();
            var acceptedPositions = new Dictionary<(int X, int Y), List<ScreenPosition>>();

            foreach (LogMapGridRow row in routeRows)
            {
                var (worldX, worldY) = SphericalMercator.FromLonLat(row.Longitude, row.Latitude);
                ScreenPosition screenPosition = viewport.WorldToScreen(worldX, worldY);

                if (screenPosition.X < -screenMargin || screenPosition.X > mapWidth + screenMargin ||
                    screenPosition.Y < -screenMargin || screenPosition.Y > mapHeight + screenMargin)
                {
                    continue;
                }

                int cellX = (int)Math.Floor(screenPosition.X / cellSize);
                int cellY = (int)Math.Floor(screenPosition.Y / cellSize);
                bool overlapsExistingMarker = false;

                for (int offsetX = -1; offsetX <= 1 && !overlapsExistingMarker; offsetX++)
                {
                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        if (!acceptedPositions.TryGetValue((cellX + offsetX, cellY + offsetY), out List<ScreenPosition>? existingPositions))
                        {
                            continue;
                        }

                        if (existingPositions.Any(existingPosition => existingPosition.Distance(screenPosition) < MinimumVisibleMarkerSpacingPixels))
                        {
                            overlapsExistingMarker = true;
                            break;
                        }
                    }
                }

                if (overlapsExistingMarker)
                {
                    continue;
                }

                acceptedRows.Add(row);
                if (!acceptedPositions.TryGetValue((cellX, cellY), out List<ScreenPosition>? cellPositions))
                {
                    cellPositions = [];
                    acceptedPositions[(cellX, cellY)] = cellPositions;
                }

                cellPositions.Add(screenPosition);
            }

            if (acceptedRows.Count > 0)
            {
                return acceptedRows;
            }

            return DownsampleLogMapRows(routeRows, fallbackMaxPointCount);
        }

        private static IReadOnlyList<LogMapGridRow> DownsampleLogMapRows(IReadOnlyList<LogMapGridRow> rows, int maxPointCount)
        {
            if (rows.Count <= 1 || maxPointCount <= 0 || rows.Count <= maxPointCount)
            {
                return rows;
            }

            if (maxPointCount == 1)
            {
                return [rows[0]];
            }

            var sampledRows = new List<LogMapGridRow>(maxPointCount);
            double step = (rows.Count - 1d) / (maxPointCount - 1d);
            for (int index = 0; index < maxPointCount; index++)
            {
                int sourceIndex = (int)Math.Round(index * step, MidpointRounding.AwayFromZero);
                sourceIndex = Math.Clamp(sourceIndex, 0, rows.Count - 1);

                LogMapGridRow row = rows[sourceIndex];
                if (sampledRows.Count == 0 || !ReferenceEquals(sampledRows[^1], row))
                {
                    sampledRows.Add(row);
                }
            }

            return sampledRows;
        }

        private void UpdateLogMapLayers(IReadOnlyList<LogMapGridRow> routeRows, IReadOnlyList<LogMapGridRow> markerRows, MRect? routeFocusExtent)
        {
            if (_logMap?.Map == null)
            {
                return;
            }

            if (_logRouteLayer != null)
            {
                _logMap.Map.Layers.Remove(_logRouteLayer);
                _logRouteLayer = null;
            }

            if (_logPointLayer != null)
            {
                _logMap.Map.Layers.Remove(_logPointLayer);
                _logPointLayer = null;
            }

            if (_logHighlightLayer != null)
            {
                _logMap.Map.Layers.Remove(_logHighlightLayer);
                _logHighlightLayer = null;
            }

            if (routeRows.Count == 0)
            {
                _logMap.Refresh();
                return;
            }

            var routeCoordinates = new List<Coordinate>(routeRows.Count);
            foreach (LogMapGridRow row in routeRows)
            {
                var (x, y) = SphericalMercator.FromLonLat(row.Longitude, row.Latitude);
                routeCoordinates.Add(new Coordinate(x, y));
            }

            var pointFeatures = new List<IFeature>(markerRows.Count);
            foreach (LogMapGridRow row in markerRows)
            {
                var (x, y) = SphericalMercator.FromLonLat(row.Longitude, row.Latitude);
                var pointFeature = new PointFeature(new MPoint(x, y));
                pointFeatures.Add(pointFeature);
            }

            GeometryFeature routeFeature = new()
            {
                Geometry = new LineString(routeCoordinates.ToArray()),
            };
            routeFeature.Styles.Add(new VectorStyle
            {
                Line = new Pen(Color.White, 7f),
                Outline = null,
            });
            routeFeature.Styles.Add(new VectorStyle
            {
                Line = new Pen(Color.FromArgb(255, 33, 150, 243), 3f),
                Outline = null,
            });

            _logRouteLayer = new MemoryLayer("Historical Route")
            {
                Features = [routeFeature],
            };

            _logPointLayer = new MemoryLayer("Historical Route Points")
            {
                Features = pointFeatures,
                Style = new SymbolStyle
                {
                    SymbolScale = 0.45,
                    Fill = new Brush(Color.FromArgb(255, 244, 67, 54)),
                    Outline = new Pen(Color.White, 1.5f),
                },
            };

            _logHighlightLayer = new MemoryLayer("Historical Route Highlight")
            {
                Features = [],
                Style = null,
            };

            _logMap.Map.Layers.Add(_logRouteLayer);
            _logMap.Map.Layers.Add(_logPointLayer);
            _logMap.Map.Layers.Add(_logHighlightLayer);

            if (routeFocusExtent != null)
            {
                _logMap.Map.Navigator.ZoomToBox(routeFocusExtent, MBoxFit.Fit);
                _pendingLogMapFitToRoute = false;
            }

            _logMap.Refresh();
        }

        private MRect? TryGetLogMapRouteFocusExtent(MainWindowViewModel vm)
        {
            IReadOnlyList<LogMapGridRow> allRows = vm.BuildLogMapRows(int.MaxValue);
            if (allRows.Count == 0)
            {
                return null;
            }

            var worldPoints = allRows
                .Select(row =>
                {
                    var (worldX, worldY) = SphericalMercator.FromLonLat(row.Longitude, row.Latitude);
                    return new MPoint(worldX, worldY);
                })
                .ToList();

            double minX = worldPoints.Min(point => point.X);
            double maxX = worldPoints.Max(point => point.X);
            double minY = worldPoints.Min(point => point.Y);
            double maxY = worldPoints.Max(point => point.Y);

            var extent = new MRect(minX, minY, maxX, maxY);
            if (worldPoints.Count >= 20)
            {
                MRect? trimmedExtent = TryBuildTrimmedLogMapExtent(worldPoints, 0.02);
                if (trimmedExtent != null)
                {
                    extent = trimmedExtent;
                }
            }

            if (extent.Width <= 0 || extent.Height <= 0)
            {
                const double defaultSinglePointSpan = 250;
                return new MRect(
                    extent.Centroid.X - defaultSinglePointSpan,
                    extent.Centroid.Y - defaultSinglePointSpan,
                    extent.Centroid.X + defaultSinglePointSpan,
                    extent.Centroid.Y + defaultSinglePointSpan);
            }

            double paddingX = extent.Width * 0.1;
            double paddingY = extent.Height * 0.1;
            return extent.Grow(paddingX, paddingY);
        }

        private static MRect? TryBuildTrimmedLogMapExtent(List<MPoint> worldPoints, double trimFraction)
        {
            if (worldPoints.Count == 0)
            {
                return null;
            }

            int trimCount = (int)Math.Floor(worldPoints.Count * trimFraction);
            if (trimCount <= 0 || (trimCount * 2) >= worldPoints.Count)
            {
                return null;
            }

            List<double> sortedX = worldPoints.Select(point => point.X).OrderBy(value => value).ToList();
            List<double> sortedY = worldPoints.Select(point => point.Y).OrderBy(value => value).ToList();

            double minX = sortedX[trimCount];
            double maxX = sortedX[^(trimCount + 1)];
            double minY = sortedY[trimCount];
            double maxY = sortedY[^(trimCount + 1)];

            if (maxX <= minX || maxY <= minY)
            {
                return null;
            }

            return new MRect(minX, minY, maxX, maxY);
        }

        private void EnsureLogMapGridColumns()
        {
            var expectedHeaders = new List<string>(_displayedLogMapColumns.Count + 2)
            {
                "Date",
                "Time",
            };
            expectedHeaders.AddRange(_displayedLogMapColumns.Select(column => column.Header));

            if (_logMapDataGrid == null)
            {
                return;
            }

            if (_logMapDataGrid.Columns.Count == expectedHeaders.Count)
            {
                bool matches = true;
                for (int index = 0; index < expectedHeaders.Count; index++)
                {
                    if (!string.Equals(_logMapDataGrid.Columns[index].Header?.ToString(), expectedHeaders[index], StringComparison.Ordinal))
                    {
                        matches = false;
                        break;
                    }
                }

                if (matches)
                {
                    return;
                }
            }

            _logMapDataGrid.Columns.Clear();
            _logMapDataGrid.Columns.Add(CreateInspectionColumn("Date", "Date", 110));
            _logMapDataGrid.Columns.Add(CreateInspectionColumn("Time", "Time", 125));

            foreach (LogMapParameterColumn column in _displayedLogMapColumns)
            {
                _logMapDataGrid.Columns.Add(CreateInspectionColumn(column.ColumnId, column.Header, double.NaN));
            }
        }

        private static DataGridTextColumn CreateInspectionColumn(string columnId, string header, double width)
        {
            var column = new DataGridTextColumn
            {
                Header = header,
                Binding = new Binding($"[{columnId}]"),
            };

            column.Width = double.IsNaN(width)
                ? new DataGridLength(1, DataGridLengthUnitType.Star)
                : new DataGridLength(width);
            return column;
        }

        private void LogMap_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => LogMap_PointerMoved(sender, e));
                return;
            }

            if (_logMap == null)
            {
                return;
            }

            var pointerPosition = e.GetPosition(_logMap);
            _lastLogMapPointerPosition = new ScreenPosition(pointerPosition.X, pointerPosition.Y);
            if (!_isLogMapSelectionLocked)
            {
                UpdateLogMapInspectionRow();
            }
        }

        private void LogMap_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => LogMap_PointerPressed(sender, e));
                return;
            }

            if (_logMap == null)
            {
                return;
            }

            var pointerPosition = e.GetPosition(_logMap);
            _lastLogMapPointerPosition = new ScreenPosition(pointerPosition.X, pointerPosition.Y);

            LogMapGridRow? nearestRow = FindNearestDisplayedLogMapRow(_lastLogMapPointerPosition.Value, out double nearestDistance);
            if (nearestRow == null || nearestDistance > 18)
            {
                _isLogMapSelectionLocked = false;
                _lockedLogMapRow = null;
                ClearLogMapInspectionRow();
                return;
            }

            if (_isLogMapSelectionLocked && IsSameLogMapRow(_lockedLogMapRow, nearestRow))
            {
                _isLogMapSelectionLocked = false;
                _lockedLogMapRow = null;
                UpdateLogMapInspectionRow();
                return;
            }

            _isLogMapSelectionLocked = true;
            _lockedLogMapRow = nearestRow;
            SetLogMapInspectionRow(nearestRow);
        }

        private void LogMap_PointerExited(object? sender, PointerEventArgs e)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(() => LogMap_PointerExited(sender, e));
                return;
            }

            _lastLogMapPointerPosition = null;
            if (!_isLogMapSelectionLocked)
            {
                ClearLogMapInspectionRow();
            }
        }

        private void UpdateLogMapInspectionRow()
        {
            if (_viewModel == null)
            {
                return;
            }

            if (_isLogMapSelectionLocked)
            {
                if (_lockedLogMapRow == null)
                {
                    ClearLogMapInspectionRow();
                    return;
                }

                SetLogMapInspectionRow(_lockedLogMapRow);
                return;
            }

            if (_lastLogMapPointerPosition == null || _logMap?.Map == null || !_logMap.Map.Navigator.Viewport.HasSize() || _displayedLogMapRows.Count == 0)
            {
                ClearLogMapInspectionRow();
                return;
            }

            LogMapGridRow? nearestRow = FindNearestDisplayedLogMapRow(_lastLogMapPointerPosition.Value, out double nearestDistance);

            if (nearestRow == null || nearestDistance > 18)
            {
                ClearLogMapInspectionRow();
                return;
            }

            SetLogMapInspectionRow(nearestRow);
        }

        private LogMapGridRow? FindNearestDisplayedLogMapRow(ScreenPosition pointerPosition, out double nearestDistance)
        {
            nearestDistance = double.MaxValue;
            if (_logMap?.Map == null || !_logMap.Map.Navigator.Viewport.HasSize() || _displayedLogMapRows.Count == 0)
            {
                return null;
            }

            Viewport viewport = _logMap.Map.Navigator.Viewport;
            LogMapGridRow? nearestRow = null;
            foreach (LogMapGridRow row in _displayedLogMapRows)
            {
                var (worldX, worldY) = SphericalMercator.FromLonLat(row.Longitude, row.Latitude);
                var screenPosition = viewport.WorldToScreen(worldX, worldY);
                double distance = screenPosition.Distance(pointerPosition);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestRow = row;
                }
            }

            return nearestRow;
        }

        private void SetLogMapInspectionRow(LogMapGridRow row)
        {
            if (_viewModel == null)
            {
                return;
            }

            UpdateLogMapHighlight(row, _isLogMapSelectionLocked && IsSameLogMapRow(row, _lockedLogMapRow));

            var newRows = new ObservableCollection<LogMapInspectionRow>();
            foreach (var parsedRow in row.AssociatedRows)
            {
                var values = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["Date"] = parsedRow.Timestamp.LocalDateTime.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                    ["Time"] = parsedRow.Timestamp.LocalDateTime.ToString("HH:mm:ss.fff", System.Globalization.CultureInfo.InvariantCulture),
                };

                foreach (LogMapParameterColumn column in _displayedLogMapColumns)
                {
                    values[column.ColumnId] = parsedRow.NumericValues.TryGetValue(column.Key, out double numericValue)
                        ? FormatInspectionValue(numericValue, column.Key, _viewModel)
                        : string.Empty;
                }

                newRows.Add(new LogMapInspectionRow(values));
            }

            _viewModel.LogMapInspectionRows = newRows;
        }

        private static string FormatInspectionValue(double value, string key, MainWindowViewModel vm)
        {
            string text = Math.Abs(value - Math.Round(value)) < 0.0001
                ? Math.Round(value).ToString(System.Globalization.CultureInfo.InvariantCulture)
                : value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

            if (vm.TryGetLogSeriesUnit(key, out string? unit) && !string.IsNullOrWhiteSpace(unit))
            {
                text = $"{text} {unit}";
            }

            return text;
        }

        private void SyncLockedLogMapRow()
        {
            if (!_isLogMapSelectionLocked)
            {
                return;
            }

            _lockedLogMapRow = _displayedLogMapRows.FirstOrDefault(row => IsSameLogMapRow(row, _lockedLogMapRow));
            if (_lockedLogMapRow == null)
            {
                _isLogMapSelectionLocked = false;
            }
        }

        private static bool IsSameLogMapRow(LogMapGridRow? left, LogMapGridRow? right)
        {
            if (left == null || right == null)
            {
                return false;
            }

            return left.Timestamp == right.Timestamp
                && Math.Abs(left.Latitude - right.Latitude) < 0.000001
                && Math.Abs(left.Longitude - right.Longitude) < 0.000001;
        }

        private void ClearLogMapInspectionRow()
        {
            if (_viewModel != null)
            {
                _viewModel.LogMapInspectionRows = new ObservableCollection<LogMapInspectionRow>();
            }

            UpdateLogMapHighlight(null, false);
        }

        private void UpdateLogMapHighlight(LogMapGridRow? row, bool isLocked)
        {
            if (_logHighlightLayer == null)
            {
                return;
            }

            if (row == null)
            {
                _logHighlightLayer.Features = [];
                _logMap?.Refresh();
                return;
            }

            var (worldX, worldY) = SphericalMercator.FromLonLat(row.Longitude, row.Latitude);
            var highlightFeature = new PointFeature(new MPoint(worldX, worldY));
            if (isLocked)
            {
                highlightFeature.Styles.Add(new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse,
                    SymbolScale = 1.2,
                    Fill = new Brush(Color.FromArgb(70, 0, 188, 212)),
                    Outline = new Pen(Color.FromArgb(255, 0, 188, 212), 2.5f),
                });
                highlightFeature.Styles.Add(new SymbolStyle
                {
                    SymbolType = SymbolType.Rectangle,
                    SymbolScale = 0.62,
                    Fill = new Brush(Color.FromArgb(255, 0, 188, 212)),
                    Outline = new Pen(Color.White, 2f),
                });
                highlightFeature.Styles.Add(new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse,
                    SymbolScale = 0.18,
                    Fill = new Brush(Color.White),
                    Outline = null,
                });
            }
            else
            {
                highlightFeature.Styles.Add(new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse,
                    SymbolScale = 1.35,
                    Fill = new Brush(Color.FromArgb(90, 255, 235, 59)),
                    Outline = new Pen(Color.White, 3f),
                });
                highlightFeature.Styles.Add(new SymbolStyle
                {
                    SymbolType = SymbolType.Ellipse,
                    SymbolScale = 0.8,
                    Fill = new Brush(Color.FromArgb(255, 255, 193, 7)),
                    Outline = new Pen(Color.FromArgb(255, 33, 33, 33), 2f),
                });
            }

            _logHighlightLayer.Features = [highlightFeature];
            _logMap?.Refresh();
        }
    }
