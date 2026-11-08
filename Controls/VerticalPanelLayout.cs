using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Cortex.Services;

namespace Cortex.Controls;

public static class VerticalPanelLayout
{
    public static void RestoreAndTrack(Grid grid, GridSplitter splitter, PanelLayoutStore store)
    {
        double fraction = store.LoadTopFraction();
        grid.RowDefinitions[0].Height = new GridLength(fraction, GridUnitType.Star);
        grid.RowDefinitions[2].Height = new GridLength(1 - fraction, GridUnitType.Star);

        splitter.DragCompleted += (_, _) => SaveProportions();
        splitter.KeyUp += (_, e) =>
        {
            if (e.Key is Key.Up or Key.Down)
            {
                SaveProportions();
            }
        };

        void SaveProportions()
        {
            double topWeight = grid.RowDefinitions[0].Height.Value;
            double bottomWeight = grid.RowDefinitions[2].Height.Value;
            store.SaveTopFraction(topWeight / (topWeight + bottomWeight));
        }
    }
}
