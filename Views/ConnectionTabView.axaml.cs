using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Cortex.ViewModels;
using System.Collections.Specialized;

namespace Cortex.Views
{
    public partial class ConnectionTabView : UserControl
    {
        private readonly ScrollViewer? _logScrollViewer;
        private MainWindowViewModel? _viewModel;

        public ConnectionTabView()
        {
            AvaloniaXamlLoader.Load(this);
            _logScrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");
            DataContextChanged += (_, _) => AttachToViewModel();
            DetachedFromVisualTree += (_, _) => DetachFromViewModel();
        }

        private void AttachToViewModel()
        {
            if (ReferenceEquals(_viewModel, DataContext))
            {
                return;
            }

            DetachFromViewModel();

            _viewModel = DataContext as MainWindowViewModel;
            if (_viewModel != null)
            {
                _viewModel.LogEntries.CollectionChanged += LogEntries_CollectionChanged;
            }
        }

        private void DetachFromViewModel()
        {
            if (_viewModel != null)
            {
                _viewModel.LogEntries.CollectionChanged -= LogEntries_CollectionChanged;
                _viewModel = null;
            }
        }

        private void LogEntries_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _logScrollViewer?.ScrollToHome();
            });
        }
    }
}
