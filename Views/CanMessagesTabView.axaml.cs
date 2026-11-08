using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cortex.Controls;
using Cortex.Models;
using Cortex.Services;
using Cortex.ViewModels;
using System.Collections.Generic;
using System.Linq;

namespace Cortex.Views
{
    public partial class CanMessagesTabView : UserControl
    {
        public CanMessagesTabView() : this(new PanelLayoutStore("can-messages-layout.json", 4.0 / 7.0))
        {
        }

        public CanMessagesTabView(PanelLayoutStore layoutStore)
        {
            AvaloniaXamlLoader.Load(this);
            Grid layoutGrid = this.FindControl<Grid>("CanMessagesLayoutGrid")!;
            GridSplitter splitter = this.FindControl<GridSplitter>("CanMessagesSplitter")!;
            VerticalPanelLayout.RestoreAndTrack(layoutGrid, splitter, layoutStore);
            AttachedToVisualTree += (_, _) => NotifyCanMessagesVisibility();
            DetachedFromVisualTree += (_, _) => NotifyCanMessagesVisibility(false);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty)
            {
                NotifyCanMessagesVisibility();
            }
        }

        private void NotifyCanMessagesVisibility(bool? visibilityOverride = null)
        {
            if (DataContext is MainWindowViewModel viewModel)
            {
                viewModel.SetCanMessagesTabVisible(visibilityOverride ?? IsVisible);
            }
        }

        private void ImportedDbcSignalsGrid_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            if (DataContext is not MainWindowViewModel viewModel || sender is not DataGrid grid)
            {
                return;
            }

            IReadOnlyList<DbcCanSignalDefinition> selectedSignals = grid.SelectedItems
                .OfType<DbcCanSignalDefinition>()
                .ToList();
            viewModel.SetSelectedImportedDbcSignals(selectedSignals, grid.SelectedItem as DbcCanSignalDefinition);
        }
    }
}
