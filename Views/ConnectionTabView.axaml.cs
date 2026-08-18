using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Cortex.ViewModels;
using System;
using System.Collections.Specialized;
using System.Linq;

namespace Cortex.Views
{
    public partial class ConnectionTabView : UserControl
    {
        private readonly ScrollViewer? _logScrollViewer;
        private readonly TextBox? _systemLogTextBox;
        private MainWindowViewModel? _viewModel;

        public ConnectionTabView()
        {
            AvaloniaXamlLoader.Load(this);
            _logScrollViewer = this.FindControl<ScrollViewer>("LogScrollViewer");
            _systemLogTextBox = this.FindControl<TextBox>("SystemLogTextBox");
            DataContextChanged += (_, _) => AttachToViewModel();
            AttachedToVisualTree += (_, _) => AttachToViewModel();
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
                UpdateLogText();
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
                UpdateLogText();
                _logScrollViewer?.ScrollToHome();
            });
        }

        private void UpdateLogText()
        {
            if (_systemLogTextBox == null || _viewModel == null)
            {
                return;
            }

            _systemLogTextBox.Text = string.Join(Environment.NewLine, _viewModel.LogEntries.Select(NormaliseLogEntry));
        }

        private static string NormaliseLogEntry(string entry)
        {
            return entry.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
        }
    }
}
