using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Cortex.Models;
using System;
using System.Linq;

namespace Cortex.Views
{
    public partial class PinSetupDialogWindow : Window
    {
        private readonly TextBox[] _pinBoxes;
        private readonly TextBox[] _confirmBoxes;
        private readonly TextBlock _headingText;
        private readonly TextBlock _statusText;
        private readonly Button _toggleShowButton;
        private bool _showPinDigits;

        public PinSetupDialogWindow(bool isPinAlreadyEnabled)
        {
            InitializeComponent();

            _headingText = GetRequiredControl<TextBlock>("HeadingText");
            _statusText = GetRequiredControl<TextBlock>("StatusText");
            _toggleShowButton = GetRequiredControl<Button>("ToggleShowButton");
            Button continueButton = GetRequiredControl<Button>("ContinueButton");
            Button cancelButton = GetRequiredControl<Button>("CancelButton");

            _pinBoxes =
            [
                GetRequiredControl<TextBox>("PinBox1"),
                GetRequiredControl<TextBox>("PinBox2"),
                GetRequiredControl<TextBox>("PinBox3"),
                GetRequiredControl<TextBox>("PinBox4")
            ];

            _confirmBoxes =
            [
                GetRequiredControl<TextBox>("ConfirmBox1"),
                GetRequiredControl<TextBox>("ConfirmBox2"),
                GetRequiredControl<TextBox>("ConfirmBox3"),
                GetRequiredControl<TextBox>("ConfirmBox4")
            ];

            Title = isPinAlreadyEnabled ? "Change PIN" : "Set PIN Lock";
            _headingText.Text = isPinAlreadyEnabled ? "Change PIN" : "Set PIN Lock";

            AttachPinBehavior(_pinBoxes);
            AttachPinBehavior(_confirmBoxes);

            SetPinBoxesVisible(_pinBoxes, false);
            SetPinBoxesVisible(_confirmBoxes, false);

            _toggleShowButton.Click += (_, _) => TogglePinVisibility();
            continueButton.Click += (_, _) => ConfirmPin();
            cancelButton.Click += (_, _) => Close(null);
            Opened += (_, _) => _pinBoxes[0].Focus();
        }

        private void InitializeComponent()
        {
            AvaloniaXamlLoader.Load(this);
        }

        private TControl GetRequiredControl<TControl>(string name) where TControl : Control
        {
            return this.FindControl<TControl>(name)
                ?? throw new InvalidOperationException($"Required control '{name}' was not found in {nameof(PinSetupDialogWindow)}.");
        }

        private void TogglePinVisibility()
        {
            _showPinDigits = !_showPinDigits;
            SetPinBoxesVisible(_pinBoxes, _showPinDigits);
            SetPinBoxesVisible(_confirmBoxes, _showPinDigits);
            _toggleShowButton.Content = _showPinDigits ? "Hide PIN" : "Show PIN";
        }

        private void ConfirmPin()
        {
            string pin = GetPinDigits(_pinBoxes);
            string confirmPin = GetPinDigits(_confirmBoxes);
            bool valid = IsFourDigitPin(pin) && IsFourDigitPin(confirmPin) && pin == confirmPin;
            if (!valid)
            {
                _statusText.Text = "Enter and confirm the same 4-digit PIN.";
                return;
            }

            Close(new PinSetupDialogResult(true, pin));
        }

        private static void AttachPinBehavior(TextBox[] boxes)
        {
            for (int index = 0; index < boxes.Length; index++)
            {
                TextBox currentBox = boxes[index];
                int currentIndex = index;

                currentBox.TextChanged += (_, _) =>
                {
                    string text = currentBox.Text ?? string.Empty;
                    string digitsOnly = new(text.Where(char.IsDigit).ToArray());

                    if (digitsOnly.Length > 1)
                    {
                        ApplyPinDigitsFromIndex(boxes, currentIndex, digitsOnly);
                        return;
                    }

                    if (!string.Equals(text, digitsOnly, StringComparison.Ordinal))
                    {
                        currentBox.Text = digitsOnly;
                        return;
                    }

                    if (digitsOnly.Length == 1 && currentIndex < boxes.Length - 1)
                    {
                        boxes[currentIndex + 1].Focus();
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
            return string.Concat(boxes.Select(box => box.Text ?? string.Empty));
        }

        private static bool IsFourDigitPin(string pin)
        {
            return pin.Length == 4 && pin.All(char.IsDigit);
        }

        private static void SetPinBoxesVisible(TextBox[] boxes, bool isVisible)
        {
            char passwordChar = isVisible ? '\0' : '•';
            foreach (TextBox box in boxes)
            {
                box.PasswordChar = passwordChar;
            }
        }
    }
}
