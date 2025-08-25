using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Xaml.Interactivity;
using System;
using System.Text.RegularExpressions;


namespace Cortex.Behaviours
{
    public class Hex11BitInputBehavior : Behavior<TextBox>
    {
        private static readonly Regex Hex11BitRegex = new Regex("^[0-7]?[0-9A-F]{0,2}$", RegexOptions.IgnoreCase);

        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject!.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Tunnel);
            AssociatedObject!.LostFocus += OnLostFocus;
        }

        protected override void OnDetaching()
        {
            base.OnDetaching();
            AssociatedObject!.RemoveHandler(InputElement.TextInputEvent, OnTextInput);
            AssociatedObject!.LostFocus -= OnLostFocus;
        }

        private void OnTextInput(object? sender, TextInputEventArgs e)
        {
            TextBox? textBox = sender as TextBox;
            string? proposed = textBox.Text?.Remove(textBox.SelectionStart, textBox.SelectionEnd - textBox.SelectionStart);
            proposed = proposed?.Insert(textBox.CaretIndex, e.Text);

            if (!IsValidHex11Bit(proposed))
            {
                e.Handled = true;
            }
        }

        private void OnLostFocus(object? sender, RoutedEventArgs e)
        {
            var textBox = (TextBox)sender!;
            if (!IsValidHex11Bit(textBox.Text))
            {
                textBox.Text = string.Empty;
            }
        }

        private bool IsValidHex11Bit(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return true;

            if (!Hex11BitRegex.IsMatch(text)) return false;

            // Parse and validate numeric range
            try
            {
                var val = Convert.ToInt32(text, 16);
                return val >= 0 && val <= 0x7FF;
            }
            catch
            {
                return false;
            }
        }
    }
}
