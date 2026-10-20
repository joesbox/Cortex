using Avalonia.Media;

namespace Cortex.Models
{
    public class ConnectorLegendItem
    {
        public required string Text { get; init; }

        public required IBrush Background { get; init; }

        public required IBrush Foreground { get; init; }
    }
}
