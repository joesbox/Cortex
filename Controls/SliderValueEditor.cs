using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace Cortex.Controls
{
    /// <summary>
    /// Shows a slider's formatted value; clicking it opens an inline box for typing an exact value,
    /// validated against the slider's current range and step before it is applied.
    /// </summary>
    public sealed partial class SliderValueEditor : Panel
    {
        public static readonly StyledProperty<string?> TextProperty =
            AvaloniaProperty.Register<SliderValueEditor, string?>(nameof(Text));

        public static readonly StyledProperty<Slider?> SliderProperty =
            AvaloniaProperty.Register<SliderValueEditor, Slider?>(nameof(Slider));

        public static readonly DirectProperty<SliderValueEditor, bool> IsEditingProperty =
            AvaloniaProperty.RegisterDirect<SliderValueEditor, bool>(nameof(IsEditing), editor => editor.IsEditing);

        public static readonly DirectProperty<SliderValueEditor, string?> ValidationMessageProperty =
            AvaloniaProperty.RegisterDirect<SliderValueEditor, string?>(nameof(ValidationMessage), editor => editor.ValidationMessage);

        private const string EditorClass = "sliderValueBox";
        private const string DisplayTip = "Click to type a value";
        private const double MinimumEditorWidth = 36;

        private readonly TextBlock _display;
        private readonly TextBox _editor;
        private bool _isEditing;
        private string? _validationMessage;
        private string? _invalidText;

        public SliderValueEditor()
        {
            Background = Brushes.Transparent;
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, DisplayTip);

            _display = new TextBlock
            {
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            _display.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));

            Styles.Add(CreateEditorStyle([":pointerover"], "#33FFFFFF", "#66C8A7FF"));
            Styles.Add(CreateEditorStyle([":focus"], "#26000000", "#FFC8A7FF"));
            Styles.Add(CreateEditorStyle(["invalid"], "#26000000", "#FFE5484D"));

            _editor = new TextBox
            {
                Classes = { EditorClass },
                IsVisible = false,
                Background = new SolidColorBrush(Color.Parse("#26000000")),
                BorderBrush = new SolidColorBrush(Color.Parse("#66C8A7FF")),
                CornerRadius = new CornerRadius(4),
                MinWidth = 0,
                MinHeight = 0,
                Padding = new Thickness(4, 2),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Cursor = new Cursor(StandardCursorType.Ibeam),
            };
            _editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);
            _editor.LostFocus += OnEditorLostFocus;
            _editor.TextChanged += OnEditorTextChanged;

            Children.Add(_display);
            Children.Add(_editor);
        }

        public string? Text
        {
            get => GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        /// <summary>The slider to edit. Defaults to the first sibling slider in the same panel.</summary>
        public Slider? Slider
        {
            get => GetValue(SliderProperty);
            set => SetValue(SliderProperty, value);
        }

        public bool IsEditing
        {
            get => _isEditing;
            private set => SetAndRaise(IsEditingProperty, ref _isEditing, value);
        }

        public string? ValidationMessage
        {
            get => _validationMessage;
            private set => SetAndRaise(ValidationMessageProperty, ref _validationMessage, value);
        }

        public TextBox EditorBox => _editor;

        protected override void OnPointerEntered(PointerEventArgs e)
        {
            base.OnPointerEntered(e);
            _display.TextDecorations = TextDecorations.Underline;
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            base.OnPointerExited(e);
            _display.TextDecorations = null;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!IsEditing && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && BeginEdit())
            {
                e.Handled = true;
            }
        }

        public bool BeginEdit()
        {
            Slider? slider = ResolveSlider();
            if (IsEditing || slider == null || !slider.IsEffectivelyEnabled)
            {
                return false;
            }

            _editor.Text = ExtractNumber(Text) ?? FormatNumber(slider.Value, GetDecimals(slider));
            if (Bounds.Width > 0)
            {
                _editor.Width = Math.Max(Bounds.Width, MinimumEditorWidth);
            }

            ClearValidation();
            IsEditing = true;
            ToolTip.SetIsOpen(this, false);
            ToolTip.SetTip(this, null);
            _display.IsVisible = false;
            _editor.IsVisible = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (IsEditing)
                {
                    _editor.Focus();
                    _editor.SelectAll();
                }
            }, DispatcherPriority.Loaded);
            return true;
        }

        public bool Commit()
        {
            Slider? slider = ResolveSlider();
            if (!IsEditing || slider == null)
            {
                return false;
            }

            if (!TryParseNumber(_editor.Text, out double value))
            {
                ShowValidation("Enter a number");
                return false;
            }

            double minimum = slider.Minimum;
            double maximum = slider.Maximum;
            int decimals = GetDecimals(slider);
            double tolerance = Math.Pow(10, -decimals) / 2;
            if (value < minimum - tolerance || value > maximum + tolerance)
            {
                ShowValidation($"Enter a value between {FormatNumber(minimum, decimals)} and {FormatNumber(maximum, decimals)}");
                return false;
            }

            if (slider.IsSnapToTickEnabled && slider.TickFrequency > 0)
            {
                value = minimum + Math.Round((value - minimum) / slider.TickFrequency, MidpointRounding.AwayFromZero) * slider.TickFrequency;
                value = Math.Round(value, 6);
            }

            slider.Value = Math.Clamp(value, minimum, maximum);
            EndEdit();
            return true;
        }

        public void Cancel()
        {
            if (IsEditing)
            {
                EndEdit();
            }
        }

        private void EndEdit()
        {
            ClearValidation();
            IsEditing = false;
            ToolTip.SetTip(this, DisplayTip);
            _editor.IsVisible = false;
            _editor.ClearValue(WidthProperty);
            _display.IsVisible = true;
        }

        private void OnEditorKeyDown(object? sender, KeyEventArgs e)
        {
            // Only consume the keys handled here: on Windows a handled KeyDown suppresses its typed character.
            switch (e.Key)
            {
                case Key.Enter:
                    Commit();
                    e.Handled = true;
                    break;
                case Key.Escape:
                    Cancel();
                    e.Handled = true;
                    break;
            }
        }

        private void OnEditorTextChanged(object? sender, TextChangedEventArgs e)
        {
            // TextChanged can arrive after a commit attempt, so only clear once the rejected text is edited.
            if (_editor.Text != _invalidText)
            {
                ClearValidation();
            }
        }

        private void OnEditorLostFocus(object? sender, RoutedEventArgs e)
        {
            if (IsEditing && !Commit())
            {
                Cancel();
            }
        }

        private void ShowValidation(string message)
        {
            ValidationMessage = message;
            _invalidText = _editor.Text;
            _editor.Classes.Add("invalid");
            ToolTip.SetTip(_editor, message);
            ToolTip.SetIsOpen(_editor, true);
        }

        private void ClearValidation()
        {
            ValidationMessage = null;
            _invalidText = null;
            _editor.Classes.Remove("invalid");
            ToolTip.SetIsOpen(_editor, false);
            _editor.ClearValue(ToolTip.TipProperty);
        }

        private static Style CreateEditorStyle(string[] classes, string? background, string borderBrush)
        {
            var style = new Style(selector =>
            {
                Selector textBox = selector.OfType<TextBox>().Class(EditorClass);
                foreach (string cssClass in classes)
                {
                    textBox = textBox.Class(cssClass);
                }

                if (!classes.Contains("invalid"))
                {
                    textBox = textBox.Not(not => not.Class("invalid"));
                }

                return textBox.Template().OfType<Border>().Name("PART_BorderElement");
            });
            if (background != null)
            {
                style.Setters.Add(new Setter(Border.BackgroundProperty, new SolidColorBrush(Color.Parse(background))));
            }

            style.Setters.Add(new Setter(Border.BorderBrushProperty, new SolidColorBrush(Color.Parse(borderBrush))));
            return style;
        }

        private Slider? ResolveSlider()
        {
            return Slider ?? (Parent as Panel)?.Children.OfType<Slider>().FirstOrDefault();
        }

        private int GetDecimals(Slider slider)
        {
            int tickDecimals = slider.TickFrequency is > 0 and < 1
                ? Math.Min(6, (int)Math.Ceiling(-Math.Log10(slider.TickFrequency) - 1e-9))
                : 0;
            string? shown = ExtractNumber(Text);
            int shownDecimals = shown != null && shown.Contains('.') ? shown.Length - shown.IndexOf('.') - 1 : 0;
            return Math.Max(tickDecimals, shownDecimals);
        }

        private static string FormatNumber(double value, int decimals)
        {
            return value.ToString("F" + decimals, CultureInfo.InvariantCulture);
        }

        private static string? ExtractNumber(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            Match match = LeadingNumberRegex().Match(text);
            return match.Success ? match.Value.Replace(',', '.') : null;
        }

        internal static bool TryParseNumber(string? text, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            // Accept an optional trailing unit such as "12.5 A", "40%" or "250 ms".
            Match match = NumberWithOptionalUnitRegex().Match(text);
            return match.Success
                && double.TryParse(match.Groups["number"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)
                && double.IsFinite(value);
        }

        [GeneratedRegex(@"[-+]?\d+(?:[.,]\d+)?")]
        private static partial Regex LeadingNumberRegex();

        [GeneratedRegex(@"^\s*(?<number>[-+]?(?:\d+(?:[.,]\d*)?|[.,]\d+))\s*(?:%|[a-zA-Z]{1,3})?\s*$")]
        private static partial Regex NumberWithOptionalUnitRegex();
    }
}
