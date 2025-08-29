using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactivity;
using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Cortex.Behaviours
{
    public class Hex11BitInputBehavior : Behavior<TextBox>
    {
        private static readonly Regex HexCharRegex = new Regex("^[0-9A-F]{1,3}$", RegexOptions.IgnoreCase);
        private bool _isFormattingValue = false;

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject!.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
            AssociatedObject!.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
            AssociatedObject!.LostFocus += OnLostFocus;
            AssociatedObject!.PropertyChanged += OnPropertyChanged;

            // Format initial bound value
            FormatInitialValue();
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            AssociatedObject!.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
            AssociatedObject!.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            AssociatedObject!.LostFocus -= OnLostFocus;
            AssociatedObject!.PropertyChanged -= OnPropertyChanged;
        }

        private void OnPropertyChanged(object? sender, Avalonia.AvaloniaPropertyChangedEventArgs e)
        {
            // Only format when Text property changes from binding (not user input)
            if (e.Property == TextBox.TextProperty && !_isFormattingValue && !AssociatedObject!.IsFocused)
            {
                FormatInitialValue();
            }
        }

        private void FormatInitialValue()
        {
            var textBox = AssociatedObject;
            if (textBox?.Text == null) return;

            string text = textBox.Text.Trim();

            // Try to parse as integer (most common case from view model)
            if (int.TryParse(text, out int intValue))
            {
                UpdateTextWithFormattedValue(intValue);
            }
        }

        private void UpdateTextWithFormattedValue(int value)
        {
            // Mask to 11 bits and format as uppercase hex (no 0x prefix)
            int maskedValue = value & 0x7FF;
            string formattedText = maskedValue.ToString("X3");

            _isFormattingValue = true;
            try
            {
                AssociatedObject!.Text = formattedText;
            }
            finally
            {
                _isFormattingValue = false;
            }
        }

        private void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (_isFormattingValue) return;

            TextBox? textBox = sender as TextBox;
            if (textBox?.Text == null) return;

            // Handle Delete and Backspace that might result in empty text
            if (e.Key == Key.Delete || e.Key == Key.Back)
            {
                string currentText = textBox.Text;
                int selectionStart = textBox.SelectionStart;
                int selectionLength = textBox.SelectionEnd - textBox.SelectionStart;

                string proposed;
                if (selectionLength > 0)
                {
                    // Text is selected, will be deleted
                    proposed = currentText.Remove(selectionStart, selectionLength);
                }
                else if (e.Key == Key.Delete && selectionStart < currentText.Length)
                {
                    // Delete key - remove character at cursor
                    proposed = currentText.Remove(selectionStart, 1);
                }
                else if (e.Key == Key.Back && selectionStart > 0)
                {
                    // Backspace - remove character before cursor
                    proposed = currentText.Remove(selectionStart - 1, 1);
                }
                else
                {
                    return; // No change would occur
                }

                // If the proposed text would be empty, set to "0" to prevent binding errors
                if (string.IsNullOrWhiteSpace(proposed))
                {
                    _isFormattingValue = true;
                    try
                    {
                        textBox.Text = "0";
                        textBox.CaretIndex = 1; // Position cursor after the 0
                    }
                    finally
                    {
                        _isFormattingValue = false;
                    }
                    e.Handled = true;
                    return;
                }

                // Validate the proposed text
                if (!IsValidHex11Bit(proposed))
                {
                    e.Handled = true;
                }
            }
        }

        private void OnTextInput(object? sender, TextInputEventArgs e)
        {
            if (_isFormattingValue) return;

            TextBox? textBox = sender as TextBox;
            if (textBox?.Text == null) return;

            // Calculate the proposed text after the input
            string currentText = textBox.Text;
            int selectionStart = textBox.SelectionStart;
            int selectionLength = textBox.SelectionEnd - textBox.SelectionStart;

            // Remove selected text
            string textAfterRemoval = selectionLength > 0
                ? currentText.Remove(selectionStart, selectionLength)
                : currentText;

            // Insert new text at caret position
            string proposed = textAfterRemoval.Insert(selectionStart, e.Text ?? "");

            if (!IsValidHex11Bit(proposed))
            {
                e.Handled = true;
            }
        }

        private void OnLostFocus(object? sender, RoutedEventArgs e)
        {
            if (_isFormattingValue) return;

            var textBox = (TextBox)sender!;
            string text = textBox.Text?.Trim() ?? "";

            if (string.IsNullOrWhiteSpace(text))
            {
                // Set to 000 and update the bound property to 0
                _isFormattingValue = true;
                try
                {
                    textBox.Text = "000";
                }
                finally
                {
                    _isFormattingValue = false;
                }
                return;
            }

            if (!IsValidHex11Bit(text))
            {
                _isFormattingValue = true;
                try
                {
                    textBox.Text = "000";
                }
                finally
                {
                    _isFormattingValue = false;
                }
            }
            else
            {
                // Format to uppercase and ensure 3 digits
                if (int.TryParse(text, NumberStyles.HexNumber, null, out int value))
                {
                    _isFormattingValue = true;
                    try
                    {
                        textBox.Text = (value & 0x7FF).ToString("X3");
                    }
                    finally
                    {
                        _isFormattingValue = false;
                    }
                }
            }
        }

        private bool IsValidHex11Bit(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            // Check if it contains only hex characters and is 1-3 characters long
            if (!HexCharRegex.IsMatch(text)) return false;

            // Parse and validate numeric range (0 to 2047 = 0x7FF)
            try
            {
                var val = Convert.ToInt32(text, 16);
                return val >= 0 && val <= 0x7FF; // 0x7FF = 2047 = 2^11 - 1
            }
            catch
            {
                return false;
            }
        }
    }
}