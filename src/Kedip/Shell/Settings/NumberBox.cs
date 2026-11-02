using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace Kedip.Shell.Settings;

/// <summary>
/// Compact numeric input: text field plus −/+ step buttons, clamped to [<see cref="Minimum"/>, <see cref="Maximum"/>].
/// Accepts "." or "," as the decimal separator. Styled by the SettingsTheme resource keys.
/// </summary>
public sealed class NumberBox : UserControl
{
    private readonly Border _frame;
    private readonly TextBox _text;
    private readonly TextBlock _suffix;
    private double _value;

    public NumberBox()
    {
        Focusable = false;
        IsTabStop = false;
        Width = 140;
        Height = 32;

        _text = new TextBox { HorizontalContentAlignment = HorizontalAlignment.Left };
        _text.SetResourceReference(StyleProperty, "SBareTextBox");
        _text.PreviewTextInput += OnPreviewTextInput;
        _text.PreviewKeyDown += OnPreviewKeyDown;
        _text.LostKeyboardFocus += (_, _) => CommitText();
        _text.GotKeyboardFocus += (_, _) => _text.SelectAll();

        _suffix = new TextBlock { Margin = new Thickness(2, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        _suffix.SetResourceReference(TextBlock.ForegroundProperty, "STextSecondaryBrush");

        var minus = CreateStepButton("", "Kurangi", -1);
        var plus = CreateStepButton("", "Tambah", +1);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(_text);
        Grid.SetColumn(_suffix, 1);
        grid.Children.Add(_suffix);
        Grid.SetColumn(minus, 2);
        grid.Children.Add(minus);
        Grid.SetColumn(plus, 3);
        grid.Children.Add(plus);

        _frame = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 0, 2, 0),
        };
        _frame.SetResourceReference(Border.BackgroundProperty, "SControlBrush");
        UpdateFrameBorder();
        IsKeyboardFocusWithinChanged += (_, _) => UpdateFrameBorder();

        Content = _frame;
    }

    public double Minimum { get; set; }

    public double Maximum { get; set; } = 100;

    public double Step { get; set; } = 1;

    /// <summary>Number of decimals kept (0 = integers).</summary>
    public int Decimals { get; set; }

    /// <summary>Unit shown after the number, e.g. "menit".</summary>
    public string? Suffix
    {
        get => _suffix.Text;
        set => _suffix.Text = value ?? string.Empty;
    }

    /// <summary>
    /// Optional hook for committed text that is not a single number (e.g. a pasted "lat, lon" pair).
    /// Return true when handled; the box then shows <see cref="Value"/> again.
    /// </summary>
    public Func<string, bool>? CustomTextHandler { get; set; }

    /// <summary>Current value. Setting it from code never raises <see cref="ValueCommitted"/>.</summary>
    public double Value
    {
        get => _value;
        set
        {
            double v = Normalize(value);
            string text = Format(v);
            if (v.Equals(_value) && _text.Text == text)
            {
                return;
            }

            _value = v;
            _text.Text = text;
        }
    }

    /// <summary>Raised when the user changes the value (typing + Enter/focus loss, step buttons, arrow keys).</summary>
    public event EventHandler? ValueCommitted;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        string? name = AutomationProperties.GetName(this);
        if (!string.IsNullOrEmpty(name))
        {
            AutomationProperties.SetName(_text, name);
        }
    }

    /// <summary>Parses user text: "." or "," decimal separator, optional minus sign (incl. U+2212).</summary>
    public static bool TryParse(string text, out double value)
    {
        string t = text.Trim().Replace('−', '-').Replace(',', '.');
        return double.TryParse(t, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingWhite |
            NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private RepeatButton CreateStepButton(string glyph, string name, int direction)
    {
        var button = new RepeatButton { Content = glyph };
        button.SetResourceReference(StyleProperty, "SStepButton");
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) =>
        {
            CommitText();
            StepBy(direction);
        };
        return button;
    }

    private void StepBy(int direction)
    {
        // Snap to the step grid first so 7.3 + 1 → 8 rather than 8.3 for integer-step boxes.
        double snapped = Math.Round(_value / Step) * Step;
        double next = snapped.Equals(_value) || (direction > 0 ? snapped < _value : snapped > _value)
            ? snapped + direction * Step
            : snapped;
        SetFromUser(next);
    }

    private void CommitText()
    {
        string raw = _text.Text;
        if (raw == Format(_value))
        {
            return;
        }

        if (TryParse(raw, out double parsed))
        {
            SetFromUser(parsed);
            return;
        }

        if (CustomTextHandler?.Invoke(raw) != true)
        {
            // Invalid input: show the last good value again.
        }

        _text.Text = Format(_value);
    }

    private void SetFromUser(double value)
    {
        double v = Normalize(value);
        _text.Text = Format(v);
        if (v.Equals(_value))
        {
            return;
        }

        _value = v;
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    private double Normalize(double value)
    {
        if (!double.IsFinite(value))
        {
            value = Minimum;
        }

        double max = Math.Max(Minimum, Maximum);
        return Math.Round(Math.Clamp(value, Minimum, max), Math.Clamp(Decimals, 0, 6), MidpointRounding.AwayFromZero);
    }

    private string Format(double value) =>
        value.ToString(Decimals > 0 ? "0." + new string('#', Decimals) : "0", CultureInfo.InvariantCulture);

    private void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        foreach (char c in e.Text)
        {
            if (!char.IsDigit(c) && c is not ('-' or '−' or '.' or ',' or ' '))
            {
                e.Handled = true;
                return;
            }
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitText();
                _text.SelectAll();
                e.Handled = true;
                break;
            case Key.Escape when _text.Text != Format(_value):
                _text.Text = Format(_value);
                _text.SelectAll();
                e.Handled = true;
                break;
            case Key.Up:
                CommitText();
                StepBy(+1);
                _text.SelectAll();
                e.Handled = true;
                break;
            case Key.Down:
                CommitText();
                StepBy(-1);
                _text.SelectAll();
                e.Handled = true;
                break;
        }
    }

    private void UpdateFrameBorder() =>
        _frame.SetResourceReference(Border.BorderBrushProperty, IsKeyboardFocusWithin ? "SAccentBrush" : "SControlBorderBrush");
}
