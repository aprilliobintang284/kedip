using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Kedip.Media;

namespace Kedip.Shell.Island;

/// <summary>
/// The tucked notch while music plays: a small wobbly line that swells with the beat, driven by the playback
/// device's peak meter. Renders at ~25 fps only while running; nothing ticks when stopped.
/// </summary>
internal sealed class BeatWave : FrameworkElement, IDisposable
{
    private const int Points = 44;
    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(40);

    private readonly DispatcherTimer _timer;
    private readonly AudioMeter _meter = new();
    private Pen _main = null!;
    private Pen _echo = null!;
    private Color _color;
    private double _level;   // smoothed loudness 0..1 (fast attack, slow release)
    private double _average; // slow running average, to notice hits above it
    private double _kick;    // extra swell right after a beat, decays quickly
    private double _phase;

    public BeatWave()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = Frame };
        _timer.Tick += OnFrame;
        SetColor(Colors.White);
    }

    public bool IsRunning => _timer.IsEnabled;

    /// <summary>Largest swing from the centre line in DIPs (the wave also never leaves the control).</summary>
    public double MaxAmplitude { get; set; } = double.PositiveInfinity;

    public void SetColor(Color color)
    {
        if (_main is not null && color == _color)
        {
            return;
        }

        _color = color;
        _main = MakePen(color, 2.1, 1.0);
        _echo = MakePen(color, 1.4, 0.45);
        InvalidateVisual();
    }

    public void Start()
    {
        if (!_timer.IsEnabled)
        {
            _timer.Start();
        }
    }

    public void Stop()
    {
        _timer.Stop();
        _level = 0;
        _kick = 0;
        InvalidateVisual();
    }

    public void Dispose()
    {
        _timer.Stop();
        _meter.Dispose();
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        double loud = Math.Min(1, Math.Sqrt(_meter.ReadPeak()) * 1.15);
        _level = loud > _level ? _level + (loud - _level) * 0.65 : _level * 0.86;
        _average += (loud - _average) * 0.04;
        if (loud > _average * 1.35 + 0.06)
        {
            _kick = Math.Max(_kick, Math.Min(1, (loud - _average) * 2.2));
        }

        _kick *= 0.82;
        _phase += 0.16 + 0.42 * _level;
        if (_phase > Math.PI * 400)
        {
            _phase -= Math.PI * 400;
        }

        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth;
        double h = ActualHeight;
        if (w < 8 || h < 4)
        {
            return;
        }

        double pad = Math.Min(10, w * 0.08);
        double mid = h / 2;
        double amp = Math.Min(h / 2 - 1.4, MaxAmplitude) * Math.Clamp(0.18 + 0.78 * _level + 0.65 * _kick, 0, 1);
        dc.DrawGeometry(null, _echo, Wave(pad, w - pad, mid, amp * 0.65, 1.6, _phase * 0.7 + 1.9));
        dc.DrawGeometry(null, _main, Wave(pad, w - pad, mid, amp, 2.3, _phase));
    }

    private static StreamGeometry Wave(double x0, double x1, double mid, double amp, double cycles, double phase)
    {
        var points = new Point[Points];
        for (int i = 1; i <= Points; i++)
        {
            points[i - 1] = WavePoint(i);
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(WavePoint(0), false, false);
            ctx.PolyLineTo(points, true, true);
        }

        geometry.Freeze();
        return geometry;

        Point WavePoint(int i)
        {
            double t = (double)i / Points;
            double envelope = Math.Pow(Math.Sin(Math.PI * t), 1.4); // calm ends, lively middle
            return new Point(x0 + (x1 - x0) * t, mid + amp * envelope * Math.Sin(2 * Math.PI * cycles * t - phase));
        }
    }

    private static Pen MakePen(Color color, double thickness, double opacity)
    {
        var brush = new SolidColorBrush(Color.FromArgb((byte)(color.A * opacity), color.R, color.G, color.B));
        brush.Freeze();
        var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }
}
