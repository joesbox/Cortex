using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Cortex.ViewModels;
using Nodify.Avalonia;
using Nodify.Avalonia.Connections;

namespace Cortex.Views
{
    public partial class IoFlowTabView : UserControl
    {
        private Border? _ioFlowSurfaceBorder;
        private ItemContainer? _dragSuppressedContainer;

        public IoFlowTabView()
        {
            AvaloniaXamlLoader.Load(this);

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
        }

        private void OnIoFlowEditorPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            PointerPointProperties properties = e.GetCurrentPoint(this).Properties;

            if (properties.PointerUpdateKind == PointerUpdateKind.LeftButtonPressed)
            {
                SuppressDragForInteractiveControlPress(e.Source as StyledElement);
            }

            if (_ioFlowSurfaceBorder?.ContextMenu == null)
            {
                return;
            }

            if (!properties.IsRightButtonPressed || properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
            {
                return;
            }

            if (_ioFlowSurfaceBorder.ContextMenu.IsOpen)
            {
                return;
            }

            if (DataContext is MainWindowViewModel viewModel && sender is NodifyEditor editor)
            {
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
                if (current is ComboBox or TextBox or ToggleSwitch or Expander)
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
            }
        }

        private void OnIoFlowEditorKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Delete || DataContext is not MainWindowViewModel mainViewModel)
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
                mainViewModel.IoFlowGraph.RemoveSelectedConnectionCommand.Execute(null);
                e.Handled = true;
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
