using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cortex.Models;

namespace Cortex.Views
{
    public partial class ConnectorTabView : UserControl
    {
        private readonly DataGrid? _connectorPinsGrid;
        private ConnectorPinDisplayItem? _highlightedPin;

        public ConnectorTabView()
        {
            AvaloniaXamlLoader.Load(this);

            _connectorPinsGrid = this.FindControl<DataGrid>("ConnectorPinsGrid");
            if (_connectorPinsGrid != null)
            {
                _connectorPinsGrid.PointerMoved += ConnectorPinsGrid_PointerMoved;
                _connectorPinsGrid.PointerExited += ConnectorPinsGrid_PointerExited;
            }

            DetachedFromVisualTree += (_, _) => SetHighlightedPin(null);
        }

        private void ConnectorPinsGrid_PointerMoved(object? sender, PointerEventArgs e)
        {
            if (e.Source is Control sourceControl)
            {
                SetHighlightedPin(sourceControl.DataContext as ConnectorPinDisplayItem);
                return;
            }

            SetHighlightedPin(null);
        }

        private void ConnectorPinsGrid_PointerExited(object? sender, PointerEventArgs e)
        {
            SetHighlightedPin(null);
        }

        private void SetHighlightedPin(ConnectorPinDisplayItem? pin)
        {
            if (ReferenceEquals(_highlightedPin, pin))
            {
                return;
            }

            _highlightedPin?.SetHighlighted(false);
            _highlightedPin = pin;
            _highlightedPin?.SetHighlighted(true);
        }
    }
}
