using Avalonia.Controls;
using Avalonia.Xaml.Interactivity;
using System;
using System.Linq;

namespace Cortex.Behaviours;

public class Hex11BitInputBehavior : Behavior<TextBox>
{
        protected override void OnAttached()
        {
            base.OnAttached();
            AssociatedObject!.TextChanging += OnTextChanging;
        }

        protected override void OnDetaching()
        {
            AssociatedObject!.TextChanging -= OnTextChanging;
            base.OnDetaching();
        }

        private void OnTextChanging(object? sender, TextChangingEventArgs e)
        {
            var tb = AssociatedObject!;
            var text = tb.Text ?? string.Empty;

            var filtered = new string(
                text
                    .ToUpperInvariant()
                    .Where(Uri.IsHexDigit)
                    .Take(3)
                    .ToArray());

            if (filtered != text)
            {
                tb.Text = filtered;
                tb.CaretIndex = filtered.Length;
            }
        }
    }
