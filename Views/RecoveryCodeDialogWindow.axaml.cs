using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using System;

namespace Cortex.Views
{
    public partial class RecoveryCodeDialogWindow : Window
    {
        private readonly SelectableTextBlock _recoveryCodeText;
        private readonly Button _copyButton;
        private readonly TextBlock _statusText;
        private readonly Button _closeButton;
        private readonly Button _doneButton;
        private readonly CheckBox _acknowledgeCheckBox;

        public RecoveryCodeDialogWindow(string recoveryCode)
        {
            InitializeComponent();

            _recoveryCodeText = GetRequiredControl<SelectableTextBlock>("RecoveryCodeText");
            _copyButton = GetRequiredControl<Button>("CopyButton");
            _statusText = GetRequiredControl<TextBlock>("StatusText");
            _closeButton = GetRequiredControl<Button>("CloseButton");
            _doneButton = GetRequiredControl<Button>("DoneButton");
            _acknowledgeCheckBox = GetRequiredControl<CheckBox>("AcknowledgeCheckBox");

            _recoveryCodeText.Text = recoveryCode;
            _copyButton.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clipboard == null)
                {
                    _statusText.Text = "Clipboard is not available on this platform.";
                    return;
                }

                await clipboard.SetTextAsync(recoveryCode);
                _statusText.Text = "Recovery code copied to clipboard.";
            };

            _closeButton.Click += (_, _) => Close(false);
            _doneButton.Click += (_, _) =>
            {
                if (_acknowledgeCheckBox.IsChecked != true)
                {
                    _statusText.Text = "Please confirm you have saved the recovery code.";
                    return;
                }

                Close(true);
            };
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private TControl GetRequiredControl<TControl>(string name) where TControl : Control
        {
            return this.FindControl<TControl>(name)
                ?? throw new InvalidOperationException($"Required control '{name}' was not found in {nameof(RecoveryCodeDialogWindow)}.");
        }
    }
}
