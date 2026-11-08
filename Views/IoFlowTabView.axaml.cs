using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Cortex.ViewModels;
using Cortex.ViewModels.IoFlow;
using Nodify.Avalonia;
using Nodify.Avalonia.Connections;
using System;
using System.Linq;
using Avalonia.Threading;

namespace Cortex.Views
{
    public partial class IoFlowTabView : UserControl
    {
        public static FuncMultiValueConverter<object, BoxShadows> NodeSelectionShadowConverter { get; } = new(values =>
        {
            var inputs = values.ToArray();
            if (inputs.Length != 2 || inputs[0] is not double zoom || inputs[1] is not string borderHex)
                return default;
            Color color = Color.Parse(borderHex);
            // Zero spread preserves the rounded silhouette; blur fades the glow in screen space.
            return new BoxShadows(
                new BoxShadow { Blur = 16 / zoom, Color = Color.FromArgb(255, color.R, color.G, color.B) },
                [new BoxShadow { Blur = 36 / zoom, Color = Color.FromArgb(170, color.R, color.G, color.B) }]);
        });

        private Border? _ioFlowSurfaceBorder;
        private ItemContainer? _dragSuppressedContainer;
        private readonly Button? _undoButton;

        public IoFlowTabView()
        {
            AvaloniaXamlLoader.Load(this);

            _undoButton = this.FindControl<Button>("IoFlowUndoButton");
            _ioFlowSurfaceBorder = this.FindControl<Border>("IoFlowSurfaceBorder");
            var editor = this.FindControl<Control>("IoFlowEditor");
            editor?.AddHandler(
                InputElement.PointerPressedEvent,
                OnIoFlowEditorPointerPressed,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            editor?.AddHandler(
                InputElement.PointerReleasedEvent,
                OnIoFlowEditorPointerReleased,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            editor?.AddHandler(
                InputElement.PointerCaptureLostEvent,
                OnIoFlowEditorPointerCaptureLost,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            AttachedToVisualTree += (_, _) => NotifyIoFlowVisibility();
            DetachedFromVisualTree += (_, _) => NotifyIoFlowVisibility(false);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty || change.Property == DataContextProperty)
            {
                NotifyIoFlowVisibility();
            }
        }

        private void NotifyIoFlowVisibility(bool? visibilityOverride = null)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.SetIoFlowTabVisible(visibilityOverride ?? (IsVisible && this.GetVisualRoot() != null));
            }
        }

        private void OnRenameCanvasClick(object? sender, RoutedEventArgs e)
        {
            if (sender is MenuItem { Tag: TextBox editor } && editor.DataContext is IoFlowCanvasViewModel canvas)
            {
                editor.Text = canvas.Name;
                editor.IsVisible = true;
                if (editor.Parent is Grid header)
                    header.Children.OfType<TextBlock>().Single().IsVisible = false;
                Dispatcher.UIThread.Post(() =>
                {
                    if (editor.IsVisible)
                    {
                        editor.Focus();
                        editor.SelectAll();
                    }

                });
            }
        }

        private void OnCanvasNameKeyDown(object? sender, KeyEventArgs e)
        {
            if (sender is TextBox editor && e.Key is Key.Enter or Key.Escape)
            {
                FinishCanvasRename(editor, commit: e.Key == Key.Enter);
                this.FindControl<TabStrip>("IoFlowCanvasTabs")?.Focus();
                e.Handled = true;
            }
        }

        private void OnCanvasNameLostFocus(object? sender, RoutedEventArgs e)
        {
            if (sender is TextBox editor)
                FinishCanvasRename(editor, commit: true);
        }

        private static void FinishCanvasRename(TextBox editor, bool commit)
        {
            if (!editor.IsVisible)
                return;
            if (commit && editor.DataContext is IoFlowCanvasViewModel canvas)
                canvas.Name = string.IsNullOrWhiteSpace(editor.Text) ? canvas.Name : editor.Text.Trim();
            editor.IsVisible = false;
            if (editor.Parent is Grid header)
                header.Children.OfType<TextBlock>().Single().IsVisible = true;
        }

        private void OnIoFlowContainerPrepared(object? sender, ContainerPreparedEventArgs e)
        {
            if (e.Container is ItemContainer container)
            {
                // Nodify writes local Location values during drags, which override theme bindings.
                container.Bind(ItemContainer.LocationProperty, new Binding(nameof(IoFlowNodeViewModel.Location))
                {
                    Mode = BindingMode.TwoWay,
                });
                container.SizeChanged += OnIoFlowContainerSizeChanged;
            }
        }

        private void OnIoFlowContainerClearing(object? sender, ContainerClearingEventArgs e)
        {
            if (e.Container is ItemContainer container)
            {
                container.SizeChanged -= OnIoFlowContainerSizeChanged;
                BindingOperations.GetBindingExpressionBase(container, ItemContainer.LocationProperty)?.Dispose();
                container.ClearValue(ItemContainer.LocationProperty);
            }
        }

        private static void OnIoFlowContainerSizeChanged(object? sender, SizeChangedEventArgs e)
        {
            if (sender is ItemContainer { DataContext: IoFlowNodeViewModel node })
            {
                node.MeasuredSize = e.NewSize;
            }
        }

        private void OnIoFlowEditorPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            PointerPointProperties properties = e.GetCurrentPoint(this).Properties;

            if (properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
            {
                SuppressDragForInteractiveControlPress(e.Source as StyledElement);
                if (e.KeyModifiers == KeyModifiers.None
                    && FindAncestor<ItemContainer>(e.Source as StyledElement) == null
                    && FindAncestor<Connection>(e.Source as StyledElement) == null
                    && FindAncestor<Connector>(e.Source as StyledElement) == null
                    && DataContext is MainWindowViewModel selectionViewModel)
                {
                    selectionViewModel.IoFlowGraph.ClearSelection();
                }
            }

            if (_ioFlowSurfaceBorder?.ContextMenu == null)
            {
                return;
            }

            if (!properties.IsRightButtonPressed || properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
            {
                return;
            }

            if (FindAncestor<Connection>(e.Source as StyledElement) != null)
            {
                return;
            }

            if (_ioFlowSurfaceBorder.ContextMenu.IsOpen)
            {
                return;
            }

            if (DataContext is MainWindowViewModel viewModel && sender is NodifyEditor editor)
            {
                var graph = viewModel.IoFlowGraph;
                if (FindAncestor<ItemContainer>(e.Source as StyledElement) is { DataContext: IoFlowNodeViewModel node }
                    && !graph.SelectedNodes.Contains(node))
                {
                    graph.ClearSelection();
                    graph.SelectedNodes.Add(node);
                }
                if (this.FindControl<MenuItem>("IoFlowMoveToCanvasMenu") is { } moveMenu)
                {
                    moveMenu.Items.Clear();
                    foreach (var canvas in graph.Canvases)
                        moveMenu.Items.Add(new MenuItem
                        {
                            Header = canvas.Name,
                            IsEnabled = graph.SelectedNodes.Count > 0 && canvas != graph.SelectedCanvas,
                            Command = graph.MoveSelectedNodesToCanvasCommand,
                            CommandParameter = canvas,
                        });
                    moveMenu.IsEnabled = graph.SelectedNodes.Count > 0 && graph.Canvases.Count > 1;
                }
                double viewportZoom = editor.ViewportZoom > 0 ? editor.ViewportZoom : 1;
                Point spawnPoint = editor.MouseLocation;
                Point viewportOrigin = editor.ViewportLocation;
                Size viewportSize = new(editor.Bounds.Width / viewportZoom, editor.Bounds.Height / viewportZoom);
                viewModel.IoFlowGraph.SetNextNodeSpawnLocation(spawnPoint, viewportOrigin, viewportSize);
            }

            _ioFlowSurfaceBorder.ContextMenu.Open(_ioFlowSurfaceBorder);
            e.Handled = true;
        }

        private void OnIoFlowEditorPointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            RestoreSuppressedDrag();
        }

        private void OnIoFlowEditorPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
        {
            RestoreSuppressedDrag();
        }

        private void SuppressDragForInteractiveControlPress(StyledElement? source)
        {
            if (!IsInteractiveNodeControl(source))
            {
                return;
            }

            if (FindAncestor<ItemContainer>(source) is not ItemContainer itemContainer)
            {
                return;
            }

            if (_dragSuppressedContainer != null && !ReferenceEquals(_dragSuppressedContainer, itemContainer))
            {
                _dragSuppressedContainer.ClearValue(ItemContainer.IsDraggableProperty);
            }

            _dragSuppressedContainer = itemContainer;
            _dragSuppressedContainer.IsDraggable = false;
        }

        private void RestoreSuppressedDrag()
        {
            if (_dragSuppressedContainer == null)
            {
                return;
            }

            _dragSuppressedContainer.ClearValue(ItemContainer.IsDraggableProperty);
            _dragSuppressedContainer = null;
        }

        private static bool IsInteractiveNodeControl(StyledElement? source)
        {
            for (StyledElement? current = source; current != null; current = current.Parent)
            {
                if (current is ComboBox or TextBox or ToggleSwitch or Expander or Cortex.Controls.SliderValueEditor)
                {
                    return true;
                }

                if (current is ItemContainer)
                {
                    return false;
                }
            }

            return false;
        }

        private void OnConnectionPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (sender is not Connection connection)
            {
                return;
            }

            if (DataContext is MainWindowViewModel mainViewModel)
            {
                mainViewModel.IoFlowGraph.SelectConnectionCommand.Execute(connection.DataContext);
                PointerPointProperties properties = e.GetCurrentPoint(connection).Properties;
                if (properties.IsLeftButtonPressed)
                {
                    this.FindControl<NodifyEditor>("IoFlowEditor")?.Focus();
                    e.Handled = true;
                }
                else if (properties.IsRightButtonPressed && connection.ContextMenu is { } menu)
                {
                    menu.Open(connection);
                    e.Handled = true;
                }
            }
        }

        private static void OnConnectionPointerEntered(object? sender, PointerEventArgs e)
        {
            if (sender is Connection { DataContext: IoFlowConnectionViewModel connection })
            {
                connection.IsHovered = true;
            }
        }

        private static void OnConnectionPointerExited(object? sender, PointerEventArgs e)
        {
            if (sender is Connection { DataContext: IoFlowConnectionViewModel connection })
            {
                connection.IsHovered = false;
            }
        }

        private async void OnIoFlowUndoKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Z || e.KeyModifiers != KeyModifiers.Control ||
                FindAncestor<TextBox>(e.Source as StyledElement) != null)
            {
                return;
            }

            if (_undoButton?.Command is IAsyncRelayCommand command && command.CanExecute(null))
            {
                e.Handled = true;
                await command.ExecuteAsync(null);
            }
        }

        private async void OnIoFlowEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete || DataContext is not MainWindowViewModel mainViewModel ||
                FindAncestor<TextBox>(e.Source as StyledElement) != null)
            {
                return;
            }

            if (mainViewModel.IoFlowGraph.SelectedNodes.Count > 0 && mainViewModel.IoFlowGraph.RemoveSelectedNodesCommand.CanExecute(null))
            {
                mainViewModel.IoFlowGraph.RemoveSelectedNodesCommand.Execute(null);
                e.Handled = true;
                return;
            }

            if (mainViewModel.IoFlowGraph.SelectedConnection != null && mainViewModel.IoFlowGraph.RemoveSelectedConnectionCommand.CanExecute(null))
            {
                e.Handled = true;
                await mainViewModel.IoFlowGraph.RemoveSelectedConnectionCommand.ExecuteAsync(null);
            }
        }

        private static T? FindAncestor<T>(StyledElement? source) where T : StyledElement
        {
            for (StyledElement? current = source; current != null; current = current.Parent)
            {
                if (current is T typed)
                {
                    return typed;
                }
            }

            return null;
        }
    }
}
