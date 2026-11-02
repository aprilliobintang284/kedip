using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace Kedip.Shell.Island;

/// <summary>
/// Three small rounded bars that bounce while music plays. Ticks at 20 Hz only while <see cref="IsActive"/> and visible;
/// otherwise the bars rest and nothing runs.
/// </summary>
internal sealed class EqualizerBars : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(EqualizerBars),
        new FrameworkPropertyMetadata(false, (d, _) => ((EqualizerBars)d).UpdateTimer()));

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush), typeof(Brush), typeof(EqualizerBars),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly double[] Rest = [0.35, 0.55, 0.3];

    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly double[] _level = [.. Rest];
    private readonly double[] _target = [0.8, 0.4, 0.95];
    private readonly double[] _speed = [0.32, 0.4, 0.28];
    private readonly Random _random = new();

    public EqualizerBars()
    {
        _timer.Tick += OnTick;
        IsVisibleChanged += (_, _) => UpdateTimer();
    }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth;
        double height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double bar = Motion.Snap(Math.Max(2, width / 5.4), scale);
        double gap = (width - 3 * bar) / 2;
        double min = bar;
        for (int i = 0; i < 3; i++)
        {
            double h = Math.Max(min, min + (height - min) * Math.Clamp(_level[i], 0, 1));
            double x = Motion.Snap(i * (bar + gap), scale);
            var rect = new Rect(x, (height - h) / 2, bar, h);
            dc.DrawRoundedRectangle(BarBrush, null, rect, bar / 2, bar / 2);
        }
    }

    private void UpdateTimer()
    {
        bool run = IsActive && IsVisible;
        if (run == _timer.IsEnabled)
        {
            return;
        }

        if (run)
        {
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            Array.Copy(Rest, _level, Rest.Length);
            InvalidateVisual();
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        for (int i = 0; i < 3; i++)
        {
            _level[i] += (_target[i] - _level[i]) * _speed[i];
            if (Math.Abs(_target[i] - _level[i]) < 0.06)
            {
                _target[i] = 0.12 + _random.NextDouble() * 0.88;
                _speed[i] = 0.22 + _random.NextDouble() * 0.28;
            }
        }

        InvalidateVisual();
    }
}
