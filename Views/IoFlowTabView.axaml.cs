using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace Cortex.Views
{
    public partial class IoFlowTabView : UserControl
    {
        private Border? _ioFlowSurfaceBorder;

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
        }

        private void OnIoFlowEditorPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if (_ioFlowSurfaceBorder?.ContextMenu == null)
            {
                return;
            }

            PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
            if (!properties.IsRightButtonPressed || properties.PointerUpdateKind != PointerUpdateKind.RightButtonPressed)
            {
                return;
            }

            if (_ioFlowSurfaceBorder.ContextMenu.IsOpen)
            {
                return;
            }

            _ioFlowSurfaceBorder.ContextMenu.Open(_ioFlowSurfaceBorder);
            e.Handled = true;
        }
    }
}
