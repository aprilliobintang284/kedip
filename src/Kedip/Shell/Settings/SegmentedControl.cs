using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Kedip.Shell.Settings;

/// <summary>
/// iOS-style segmented picker: equal-width segments with a thumb that slides to the selected one.
/// Segments are given as "A|B|C". Styled by the SettingsTheme keys SSegmentHost, SSegmentThumb and SSegment.
/// </summary>
public sealed class SegmentedControl : Border
{
    private readonly UniformGrid _strip = new() { Rows = 1 };
    private readonly Border _thumb;
    private readonly TranslateTransform _thumbShift = new();
    private readonly List<RadioButton> _buttons = [];
    private int _selectedIndex = -1;
    private bool _suppress;

    public SegmentedControl()
    {
        SetResourceReference(StyleProperty, "SSegmentHost");
        HorizontalAlignment = HorizontalAlignment.Right;

        _thumb = new Border
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            IsHitTestVisible = false,
            Opacity = 0,
            RenderTransform = _thumbShift,
        };
        _thumb.SetResourceReference(StyleProperty, "SSegmentThumb");

        KeyboardNavigation.SetTabNavigation(_strip, KeyboardNavigationMode.Once);
        KeyboardNavigation.SetDirectionalNavigation(_strip, KeyboardNavigationMode.None);
        _strip.PreviewKeyDown += OnStripKeyDown;
        _strip.SizeChanged += (_, _) => PlaceThumb(animate: false);

        var root = new Grid();
        root.Children.Add(_thumb);
        root.Children.Add(_strip);
        Child = root;
    }

    /// <summary>Raised when the user picks a segment (not when <see cref="SelectedIndex"/> is set from code).</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Segment labels separated by '|'.</summary>
    public string Segments
    {
        get => string.Join('|', _buttons.Select(b => b.Content as string));
        set => Build(value.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Selected segment, or -1 for none. Setting it from code slides the thumb but raises no event.</summary>
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int index = value >= 0 && value < _buttons.Count ? value : -1;
            if (index == _selectedIndex)
            {
                return;
            }

            _selectedIndex = index;
            _suppress = true;
            for (int i = 0; i < _buttons.Count; i++)
            {
                _buttons[i].IsChecked = i == index;
            }

            _suppress = false;
            PlaceThumb(animate: IsLoaded);
        }
    }

    private void Build(string[] labels)
    {
        _strip.Children.Clear();
        _buttons.Clear();
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var button = new RadioButton { Content = labels[i], IsChecked = i == _selectedIndex };
            button.SetResourceReference(StyleProperty, "SSegment");
            button.Checked += (_, _) => OnUserChecked(index);
            _buttons.Add(button);
            _strip.Children.Add(button);
        }
    }

    private void OnUserChecked(int index)
    {
        if (_suppress || index == _selectedIndex)
        {
            return;
        }

        _selectedIndex = index;
        PlaceThumb(animate: true);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnStripKeyDown(object sender, KeyEventArgs e)
    {
        int delta = e.Key switch { Key.Left => -1, Key.Right => 1, _ => 0 };
        if (delta == 0 || _buttons.Count == 0)
        {
            return;
        }

        int next = Math.Clamp((_selectedIndex < 0 ? 0 : _selectedIndex) + delta, 0, _buttons.Count - 1);
        _buttons[next].Focus();
        _buttons[next].IsChecked = true;
        e.Handled = true;
    }

    private void PlaceThumb(bool animate)
    {
        if (_selectedIndex < 0 || _selectedIndex >= _buttons.Count)
        {
            Motion.To(_thumb, OpacityProperty, 0, 120, animate);
            return;
        }

        var target = _buttons[_selectedIndex];
        if (target.ActualWidth <= 0)
        {
            return; // Not measured yet; SizeChanged places it.
        }

        double x = target.TranslatePoint(default, _strip).X;
        bool wasHidden = _thumb.Opacity < 0.01;
        _thumb.Width = target.ActualWidth;
        if (wasHidden)
        {
            _thumbShift.BeginAnimation(TranslateTransform.XProperty, null);
            _thumbShift.X = x;
            Motion.To(_thumb, OpacityProperty, 1, 160, animate);
            return;
        }

        Motion.To(_thumbShift, TranslateTransform.XProperty, x, 240, animate);
    }
}
