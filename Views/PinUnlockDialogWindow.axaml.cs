using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cortex.Models;
using System;
using System.Linq;

namespace Cortex.Views
{
    public partial class PinUnlockDialogWindow : Window
    {
        private readonly TextBox[] _pinBoxes;
        private readonly TextBox[] _newPinBoxes;
        private readonly Border _loginSection;
        private readonly Border _recoverySection;
        private readonly TextBox _recoveryKeyBox;
        private readonly TextBlock _instructionText;
        private readonly TextBlock _statusText;
        private readonly TextBlock _retryText;
        private readonly Button _toggleModeButton;
        private readonly Button _unlockButton;
        private readonly bool _allowRecovery;
        private bool _isRecoveryMode;

        public PinUnlockDialogWindow(bool allowRecovery, uint retryAfterMs, string? statusMessage)
        {
            InitializeComponent();

            _allowRecovery = allowRecovery;
            _pinBoxes =
            [
                GetRequiredControl<TextBox>("PinBox1"),
                GetRequiredControl<TextBox>("PinBox2"),
                GetRequiredControl<TextBox>("PinBox3"),
                GetRequiredControl<TextBox>("PinBox4")
            ];

            _newPinBoxes =
            [
                GetRequiredControl<TextBox>("NewPinBox1"),
                GetRequiredControl<TextBox>("NewPinBox2"),
                GetRequiredControl<TextBox>("NewPinBox3"),
                GetRequiredControl<TextBox>("NewPinBox4")
            ];

            _loginSection = GetRequiredControl<Border>("LoginSection");
            _recoverySection = GetRequiredControl<Border>("RecoverySection");
            _recoveryKeyBox = GetRequiredControl<TextBox>("RecoveryKeyBox");
            _instructionText = GetRequiredControl<TextBlock>("InstructionText");
            _statusText = GetRequiredControl<TextBlock>("StatusText");
            _retryText = GetRequiredControl<TextBlock>("RetryText");
            _toggleModeButton = GetRequiredControl<Button>("ToggleModeButton");
            _unlockButton = GetRequiredControl<Button>("UnlockButton");
            Button cancelButton = GetRequiredControl<Button>("CancelButton");

            AttachPinBehavior(_pinBoxes, () =>
            {
                if (!_isRecoveryMode)
                {
                    _unlockButton.Focus();
                }
            });
            AttachPinBehavior(_newPinBoxes);

            SetPinBoxesVisible(_pinBoxes, false);
            SetPinBoxesVisible(_newPinBoxes, false);

            SetRetryText(retryAfterMs > 0 ? $"Retry after {Math.Ceiling(retryAfterMs / 1000d)} seconds." : string.Empty);
            SetStatusText(statusMessage ?? string.Empty);

            _toggleModeButton.IsVisible = _allowRecovery;
            _unlockButton.Click += (_, _) => Submit();
            _toggleModeButton.Click += (_, _) => ToggleMode();
            cancelButton.Click += (_, _) => Close(null);
            Opened += (_, _) => FocusActiveEntry();

            ApplyMode(isRecoveryMode: false);
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private TControl GetRequiredControl<TControl>(string name) where TControl : Control
        {
            return this.FindControl<TControl>(name)
                ?? throw new InvalidOperationException($"Required control '{name}' was not found in {nameof(PinUnlockDialogWindow)}.");
        }

        private void Submit()
        {
            if (_isRecoveryMode)
            {
                RecoverWithKey();
            }
            else
            {
                UnlockWithPin();
            }
        }

        private void ToggleMode()
        {
            if (!_allowRecovery)
            {
                return;
            }

            ApplyMode(!_isRecoveryMode);
        }

        private void ApplyMode(bool isRecoveryMode)
        {
            _isRecoveryMode = isRecoveryMode && _allowRecovery;
            _loginSection.IsVisible = !_isRecoveryMode;
            _recoverySection.IsVisible = _isRecoveryMode;
            _instructionText.Text = _isRecoveryMode
                ? "Recover your PIN with your recovery key and set a new PIN."
                : "Enter your PIN to unlock this session.";
            _toggleModeButton.Content = _isRecoveryMode ? "LOGIN" : "RECOVER PIN";
            _unlockButton.Content = _isRecoveryMode ? "RECOVER" : "UNLOCK";
            FocusActiveEntry();
        }

        private void FocusActiveEntry()
        {
            if (_isRecoveryMode)
            {
                _recoveryKeyBox.Focus();
            }
            else
            {
                _pinBoxes[0].Focus();
            }
        }

        private void SetStatusText(string text)
        {
            _statusText.Text = text;
            _statusText.IsVisible = !string.IsNullOrWhiteSpace(text);
        }

        private void SetRetryText(string text)
        {
            _retryText.Text = text;
            _retryText.IsVisible = !string.IsNullOrWhiteSpace(text);
        }

        private void UnlockWithPin()
        {
            string pin = GetPinDigits(_pinBoxes);
            if (!IsFourDigitPin(pin))
            {
                SetStatusText("Enter a 4-digit PIN.");
                return;
            }

            Close(new PinUnlockDialogResult(true, pin, null, null));
        }

        private void RecoverWithKey()
        {
            string recoveryKey = (_recoveryKeyBox.Text ?? string.Empty).Trim();
            string newPin = GetPinDigits(_newPinBoxes);

            if (string.IsNullOrWhiteSpace(recoveryKey))
            {
                SetStatusText("Enter your recovery key.");
                return;
            }

            if (!IsFourDigitPin(newPin))
            {
                SetStatusText("Enter a valid 4-digit new PIN for recovery.");
                return;
            }

            Close(new PinUnlockDialogResult(true, string.Empty, recoveryKey, newPin));
        }

        private static void AttachPinBehavior(TextBox[] boxes, Action? onPinComplete = null)
        {
            ArgumentNullException.ThrowIfNull(boxes);

            for (int index = 0; index < boxes.Length; index++)
            {
                TextBox currentBox = boxes[index]
                    ?? throw new InvalidOperationException($"PIN box at index {index} was null.");
                int currentIndex = index;

                currentBox.TextChanged += (_, _) =>
                {
                    string text = currentBox.Text ?? string.Empty;
                    string digitsOnly = new(text.Where(char.IsDigit).ToArray());

                    if (digitsOnly.Length > 1)
                    {
                        ApplyPinDigitsFromIndex(boxes, currentIndex, digitsOnly);
                        if (IsPinComplete(boxes))
                        {
                            onPinComplete?.Invoke();
                        }
                        return;
                    }

                    if (!string.Equals(text, digitsOnly, StringComparison.Ordinal))
                    {
                        currentBox.Text = digitsOnly;
                        return;
                    }

                    if (digitsOnly.Length == 1)
                    {
                        if (currentIndex < boxes.Length - 1)
                        {
                            boxes[currentIndex + 1].Focus();
                        }
                        else if (IsPinComplete(boxes))
                        {
                            onPinComplete?.Invoke();
                        }
                    }
                };

                currentBox.KeyDown += (_, args) =>
                {
                    if (args.Key == Key.Back && string.IsNullOrEmpty(currentBox.Text) && currentIndex > 0)
                    {
                        TextBox previousBox = boxes[currentIndex - 1];
                        previousBox.Text = string.Empty;
                        previousBox.Focus();
                        args.Handled = true;
                    }
                    else if (args.Key == Key.Left && currentIndex > 0)
                    {
                        boxes[currentIndex - 1].Focus();
                        args.Handled = true;
                    }
                    else if (args.Key == Key.Right && currentIndex < boxes.Length - 1)
                    {
                        boxes[currentIndex + 1].Focus();
                        args.Handled = true;
                    }
                };
            }
        }

        private static void ApplyPinDigitsFromIndex(TextBox[] boxes, int startIndex, string digits)
        {
            ArgumentNullException.ThrowIfNull(boxes);
            ArgumentNullException.ThrowIfNull(digits);

            int targetIndex = startIndex;
            foreach (char digit in digits.Where(char.IsDigit))
            {
                if (targetIndex >= boxes.Length)
                {
                    break;
                }

                boxes[targetIndex].Text = digit.ToString();
                targetIndex++;
            }

            if (targetIndex < boxes.Length)
            {
                boxes[targetIndex].Focus();
            }
        }

        private static string GetPinDigits(TextBox[] boxes)
        {
            ArgumentNullException.ThrowIfNull(boxes);
            return string.Concat(boxes.Select(box => box?.Text ?? string.Empty));
        }

        private static bool IsFourDigitPin(string pin)
        {
            return pin.Length == 4 && pin.All(char.IsDigit);
        }

        private static bool IsPinComplete(TextBox[] boxes)
        {
            return IsFourDigitPin(GetPinDigits(boxes));
        }

        private static void SetPinBoxesVisible(TextBox[] boxes, bool isVisible)
        {
            ArgumentNullException.ThrowIfNull(boxes);

            char passwordChar = isVisible ? '\0' : '•';
            foreach (TextBox box in boxes)
            {
                if (box is not null)
                {
                    box.PasswordChar = passwordChar;
                }
            }
        }
    }
}
