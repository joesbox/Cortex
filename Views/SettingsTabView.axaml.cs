using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Cortex.ViewModels;

namespace Cortex.Views
{
    public partial class SettingsTabView : UserControl
    {
        private MainWindowViewModel? _subscribedViewModel;

        public SettingsTabView()
        {
            AvaloniaXamlLoader.Load(this);
            this.FindControl<TabControl>("SettingsTabs")!.SelectedItem = this.FindControl<TabItem>("IoFlowTab");
            DataContextChanged += (_, _) => UpdateViewModelSubscription();
        }

        private void UpdateViewModelSubscription()
        {
            if (_subscribedViewModel != null)
            {
                _subscribedViewModel.CanMessagesNavigationRequested -= ViewModel_CanMessagesNavigationRequested;
            }

            _subscribedViewModel = DataContext as MainWindowViewModel;
            if (_subscribedViewModel != null)
            {
                _subscribedViewModel.CanMessagesNavigationRequested += ViewModel_CanMessagesNavigationRequested;
            }
        }

        private void ViewModel_CanMessagesNavigationRequested(object? sender, System.EventArgs e)
        {
            var tabs = this.FindControl<TabControl>("SettingsTabs");
            var canMessagesTab = this.FindControl<TabItem>("CanMessagesTab");
            if (tabs != null && canMessagesTab != null)
            {
                tabs.SelectedItem = canMessagesTab;
            }
        }
    }
}
