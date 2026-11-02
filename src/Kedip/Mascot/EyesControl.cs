using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace Kedip.Mascot;

/// <summary>
/// Draws the mascot's two eyes, scaled to the control's height.
/// OWNER: B.
/// </summary>
/// <remarks>
/// Everything is drawn in <see cref="OnRender"/>; the only animated state is a private "openness" (lids) and
/// "joy" (happy arcs) value. Blinks, the wake pop and the sleepy "z" are short bursts scheduled by one-shot timers,
/// so nothing renders between them. All timers stop while the control is unloaded, hidden or the mood is Hidden.
/// </remarks>
public sealed class EyesControl : FrameworkElement
{
    private const double DrowsyOpenness = 0.45;
    private const double ClosedThreshold = 0.08;
    private const int WakeHappyMs = 450;
    private const int WakeOpenMs = 280;
    private const int ZFrameMs = 83; // ≈ 12 fps: enough for a slow drift, cheap on a layered window
    private const double ZDurationMs = 1800;

    public static readonly DependencyProperty MoodProperty = DependencyProperty.Register(
        nameof(Mood), typeof(MascotMood), typeof(EyesControl),
        new FrameworkPropertyMetadata(MascotMood.Awake, FrameworkPropertyMetadataOptions.AffectsRender, OnMoodChanged));

    public static readonly DependencyProperty LookXProperty = DependencyProperty.Register(
        nameof(LookX), typeof(double), typeof(EyesControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LookYProperty = DependencyProperty.Register(
        nameof(LookY), typeof(double), typeof(EyesControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>When false, state changes snap instead of animating (blinks still happen, instantly).</summary>
    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(EyesControl),
        new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender, OnAnimateChanged));

    /// <summary>Lid opening: 0 closed, 1 open, slightly above 1 during the wake bounce.</summary>
    private static readonly DependencyProperty OpennessProperty = DependencyProperty.Register(
        "Openness", typeof(double), typeof(EyesControl),
        new FrameworkPropertyMetadata(1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Happy "^ ^" arcs strength (0 = normal eyes).</summary>
    private static readonly DependencyProperty JoyProperty = DependencyProperty.Register(
        "Joy", typeof(double), typeof(EyesControl),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly IEasingFunction EaseIn = Frozen(new QuadraticEase { EasingMode = EasingMode.EaseIn });
    private static readonly IEasingFunction EaseOut = Frozen(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });
    private static readonly IEasingFunction PopOut = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 });
    private static readonly IEasingFunction BounceOut = Frozen(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.9 });

    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _wakeTimer;
    private readonly DispatcherTimer _zTimer;
    private bool _loaded;
    private bool _active;
    private bool _blinkClosed;
    private bool _waking;
    private bool _zWaiting;
    private double _zProgress = -1;
    private long? _wakeRequestedAt;
    private int _opennessGeneration;
    private int _joyGeneration;
    private Pen? _arcPen;
    private Pen? _zPen;

    public EyesControl()
    {
        _blinkTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _blinkTimer.Tick += OnBlinkTick;
        _wakeTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(WakeHappyMs) };
        _wakeTimer.Tick += OnWakeTick;
        _zTimer = new DispatcherTimer(DispatcherPriority.Background);
        _zTimer.Tick += OnZTick;
        UseLayoutRounding = true;

        Loaded += (_, _) =>
        {
            _loaded = true;
            UpdateActive();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            UpdateActive();
        };
        IsVisibleChanged += (_, _) => UpdateActive();
    }

    public bool Animate
    {
        get => (bool)GetValue(AnimateProperty);
        set => SetValue(AnimateProperty, value);
    }

    public MascotMood Mood
    {
        get => (MascotMood)GetValue(MoodProperty);
        set => SetValue(MoodProperty, value);
    }

    public double LookX
    {
        get => (double)GetValue(LookXProperty);
        set => SetValue(LookXProperty, value);
    }

    public double LookY
    {
        get => (double)GetValue(LookYProperty);
        set => SetValue(LookYProperty, value);
    }

    private double Openness => (double)GetValue(OpennessProperty);

    private double Joy => (double)GetValue(JoyProperty);

    private double RestingOpenness => Mood switch
    {
        MascotMood.Drowsy => DrowsyOpenness,
        MascotMood.Asleep => 0.0,
        _ => 1.0,
    };

    /// <summary>
    /// Short happy "hello" animation when the user comes back: "^ ^" arcs pop in, then the eyes open with a small
    /// bounce (≈ 730 ms in total). Safe to call just before the control becomes visible; it then starts on appearance.
    /// </summary>
    public void PlayWake()
    {
        if (!_active)
        {
            // Usually called in the same breath as the eyes being shown: play once we become visible.
            _wakeRequestedAt = Environment.TickCount64;
            return;
        }

        _blinkTimer.Stop();
        _zTimer.Stop();
        _blinkClosed = false;
        _zProgress = -1;
        _waking = true;
        StopOpenness();
        SetValue(OpennessProperty, 1.0);

        if (Animate)
        {
            int generation = ++_joyGeneration;
            var pop = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160)) { EasingFunction = PopOut };
            pop.Completed += (_, _) =>
            {
                if (generation == _joyGeneration)
                {
                    SetValue(JoyProperty, 1.0);
                    BeginAnimation(JoyProperty, null);
                }
            };
            BeginAnimation(JoyProperty, pop, HandoffBehavior.SnapshotAndReplace);
        }
        else
        {
            StopJoy();
            SetValue(JoyProperty, 1.0);
        }

        _wakeTimer.Stop();
        _wakeTimer.Start();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double height = double.IsInfinity(availableSize.Height) ? 30 : availableSize.Height;
        double width = height * 1.6;
        if (!double.IsInfinity(availableSize.Width))
        {
            width = Math.Min(width, availableSize.Width);
        }

        return new Size(width, height);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        InvalidateVisual(); // the pixel snapping depends on the scale
    }

    protected override void OnRender(DrawingContext dc)
    {
        double height = ActualHeight;
        double width = ActualWidth;
        if (height <= 0 || width <= 0)
        {
            return;
        }

        // Resting edges sit on the device pixel grid so the straight sides stay crisp; only the gaze offset is sub-pixel.
        double px = 1 / Math.Max(0.5, VisualTreeHelper.GetDpi(this).DpiScaleX);
        double eyeH = Math.Max(4 * px, Snap(height * 0.45, px));
        double eyeW = Math.Max(3 * px, Snap(eyeH * 0.62, px));
        double gap = Snap(eyeW * 1.1, px);
        double lookX = Clamp1(LookX);
        double lookY = Clamp1(LookY);
        double offsetX = lookX * eyeH * 0.18;
        double offsetY = lookY * eyeH * 0.12;
        double leftX = Snap(width / 2 - gap / 2 - eyeW, px) + eyeW / 2;
        double cy = Snap((height - eyeH) / 2, px) + eyeH / 2;
        var left = new Point(leftX + offsetX, cy + offsetY);
        var right = new Point(leftX + eyeW + gap + offsetX, cy + offsetY);
        double stroke = Math.Max(1.25, eyeH * 0.16);

        double joy = Joy;
        if (joy > 0.001)
        {
            var pen = GetPen(ref _arcPen, stroke);
            dc.DrawGeometry(null, pen, ArcGeometry(left, eyeW * 1.1, eyeH, joy, happy: true));
            dc.DrawGeometry(null, pen, ArcGeometry(right, eyeW * 1.1, eyeH, joy, happy: true));
            return;
        }

        double openness = Openness;
        if (Mood == MascotMood.Asleep && openness <= ClosedThreshold)
        {
            var pen = GetPen(ref _arcPen, stroke);
            dc.DrawGeometry(null, pen, ArcGeometry(left, eyeW * 1.1, eyeH, 1, happy: false));
            dc.DrawGeometry(null, pen, ArcGeometry(right, eyeW * 1.1, eyeH, 1, happy: false));
            double z = Animate ? _zProgress : 0.4;
            if (z >= 0)
            {
                DrawZ(dc, right, eyeW, eyeH, height, z);
            }

            return;
        }

        // The eye on the side we look towards turns slightly away (narrower): a hint of depth.
        double squeezeLeft = 1 - 0.08 * Math.Max(0, -lookX);
        double squeezeRight = 1 - 0.08 * Math.Max(0, lookX);
        dc.DrawGeometry(Brushes.White, null, EyeGeometry(left, eyeW * squeezeLeft, eyeH, openness));
        dc.DrawGeometry(Brushes.White, null, EyeGeometry(right, eyeW * squeezeRight, eyeH, openness));
    }

    // ---- Activity / mood ----

    private static void OnMoodChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var eyes = (EyesControl)d;
        bool wasActive = eyes._active;
        eyes.UpdateActive();
        if (wasActive && eyes._active)
        {
            eyes.EnterMood(snap: false);
        }
    }

    private static void OnAnimateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var eyes = (EyesControl)d;
        if (eyes._active && !eyes._waking)
        {
            eyes.EnterMood(snap: true);
        }
    }

    private void UpdateActive()
    {
        bool active = _loaded && IsVisible && Mood != MascotMood.Hidden;
        if (active == _active)
        {
            return;
        }

        _active = active;
        if (active)
        {
            EnterMood(snap: true);
            if (_wakeRequestedAt is long requested && Environment.TickCount64 - requested < 600)
            {
                PlayWake();
            }
        }
        else
        {
            Halt();
        }

        _wakeRequestedAt = null;
    }

    /// <summary>Stops every timer and animation and leaves the eyes at rest.</summary>
    private void Halt()
    {
        _blinkTimer.Stop();
        _wakeTimer.Stop();
        _zTimer.Stop();
        _blinkClosed = false;
        _waking = false;
        _zProgress = -1;
        StopJoy();
        SetValue(JoyProperty, 0.0);
        StopOpenness();
        SetValue(OpennessProperty, RestingOpenness);
    }

    private void EnterMood(bool snap)
    {
        _blinkTimer.Stop();
        _zTimer.Stop();
        _blinkClosed = false;
        if (_zProgress >= 0)
        {
            _zProgress = -1;
            InvalidateVisual();
        }

        if (_waking)
        {
            return; // the wake sequence settles on the new resting openness by itself
        }

        double target = RestingOpenness;
        if (snap || !Animate)
        {
            StopOpenness();
            SetValue(OpennessProperty, target);
        }
        else
        {
            int ms = Mood switch
            {
                MascotMood.Asleep => 600,
                MascotMood.Drowsy => 450,
                _ => 220,
            };
            var anim = new DoubleAnimation(target, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseInOut };
            Timeline.SetDesiredFrameRate(anim, 30);
            RunOpenness(anim, target, null);
        }

        ScheduleIdle(first: true);
    }

    private void ScheduleIdle(bool first = false)
    {
        if (!_active || _waking)
        {
            return;
        }

        switch (Mood)
        {
            case MascotMood.Awake:
                StartOneShot(_blinkTimer, Random.Shared.Next(2500, 6001));
                break;
            case MascotMood.Drowsy:
                StartOneShot(_blinkTimer, Random.Shared.Next(4000, 8001));
                break;
            case MascotMood.Asleep when Animate:
                _zWaiting = true;
                StartOneShot(_zTimer, first ? Random.Shared.Next(1500, 3001) : Random.Shared.Next(4000, 7001));
                break;
        }
    }

    // ---- Blinks ----

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        _blinkTimer.Stop();
        if (!_active || _waking || Mood is not (MascotMood.Awake or MascotMood.Drowsy))
        {
            return;
        }

        double rest = RestingOpenness;
        if (_blinkClosed)
        {
            _blinkClosed = false;
            SetValue(OpennessProperty, rest);
            ScheduleIdle();
            return;
        }

        if (!Animate)
        {
            StopOpenness();
            SetValue(OpennessProperty, 0.0);
            _blinkClosed = true;
            StartOneShot(_blinkTimer, Mood == MascotMood.Drowsy ? 260 : 110);
            return;
        }

        var blink = Mood == MascotMood.Awake
            ? BuildBlink(rest, twice: Random.Shared.NextDouble() < 0.22)
            : BuildSlowBlink(rest);
        RunOpenness(blink, rest, () => ScheduleIdle());
    }

    private static DoubleAnimationUsingKeyFrames BuildBlink(double rest, bool twice)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        double t = 0;
        Add(anim, ref t, 70, 0, EaseIn);
        Add(anim, ref t, 110, rest, EaseOut);
        if (twice)
        {
            Add(anim, ref t, 90, rest, null);
            Add(anim, ref t, 70, 0, EaseIn);
            Add(anim, ref t, 110, rest, EaseOut);
        }

        return anim;
    }

    private static DoubleAnimationUsingKeyFrames BuildSlowBlink(double rest)
    {
        var anim = new DoubleAnimationUsingKeyFrames();
        double t = 0;
        Add(anim, ref t, 300, 0, EaseInOut);
        Add(anim, ref t, 180, 0, null);
        Add(anim, ref t, 480, rest, EaseOut);
        Timeline.SetDesiredFrameRate(anim, 30);
        return anim;
    }

    private static void Add(DoubleAnimationUsingKeyFrames anim, ref double t, double ms, double value, IEasingFunction? ease)
    {
        t += ms;
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(value, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(t)), ease));
    }

    // ---- Wake ----

    private void OnWakeTick(object? sender, EventArgs e)
    {
        _wakeTimer.Stop();
        _waking = false;
        StopJoy();
        SetValue(JoyProperty, 0.0);
        if (!_active)
        {
            return;
        }

        double rest = RestingOpenness;
        StopOpenness();
        if (Animate && rest > 0)
        {
            SetValue(OpennessProperty, 0.2);
            var open = new DoubleAnimation(0.2, rest, TimeSpan.FromMilliseconds(WakeOpenMs)) { EasingFunction = BounceOut };
            RunOpenness(open, rest, null);
        }
        else
        {
            SetValue(OpennessProperty, rest);
        }

        ScheduleIdle(first: true);
    }

    // ---- Sleepy "z" ----

    private void OnZTick(object? sender, EventArgs e)
    {
        if (!_active || _waking || Mood != MascotMood.Asleep || !Animate)
        {
            _zTimer.Stop();
            _zProgress = -1;
            return;
        }

        if (_zWaiting)
        {
            _zWaiting = false;
            _zProgress = 0;
            StartOneShot(_zTimer, ZFrameMs);
            InvalidateVisual();
            return;
        }

        _zProgress += ZFrameMs / ZDurationMs;
        if (_zProgress >= 1)
        {
            _zProgress = -1;
            _zTimer.Stop();
            ScheduleIdle();
        }
        else if (!_zTimer.IsEnabled)
        {
            StartOneShot(_zTimer, ZFrameMs);
        }

        InvalidateVisual();
    }

    private void DrawZ(DrawingContext dc, Point rightEye, double eyeW, double eyeH, double height, double p)
    {
        double size = height * 0.2 * (0.65 + 0.35 * p);
        double x = rightEye.X + eyeW * 0.85 + eyeH * 0.9 * p;
        double y = height / 2 - eyeH * 0.1 - eyeH * 0.7 * p;
        double alpha = (p < 0.2 ? p / 0.2 : (1 - p) / 0.8) * 0.85;
        if (alpha <= 0.01)
        {
            return;
        }

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(new Point(x - size / 2, y - size / 2), false, false);
            ctx.LineTo(new Point(x + size / 2, y - size / 2), true, true);
            ctx.LineTo(new Point(x - size / 2, y + size / 2), true, true);
            ctx.LineTo(new Point(x + size / 2, y + size / 2), true, true);
        }

        geometry.Freeze();
        dc.PushOpacity(alpha);
        dc.DrawGeometry(null, GetPen(ref _zPen, Math.Max(1, height * 0.045)), geometry);
        dc.Pop();
    }

    // ---- Animation plumbing ----

    private void RunOpenness(AnimationTimeline anim, double end, Action? then)
    {
        int generation = ++_opennessGeneration;
        anim.Completed += (_, _) =>
        {
            if (generation != _opennessGeneration)
            {
                return;
            }

            // Land on the base value and drop the clock so nothing keeps ticking.
            SetValue(OpennessProperty, end);
            BeginAnimation(OpennessProperty, null);
            then?.Invoke();
        };
        BeginAnimation(OpennessProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopOpenness()
    {
        _opennessGeneration++;
        BeginAnimation(OpennessProperty, null);
    }

    private void StopJoy()
    {
        _joyGeneration++;
        BeginAnimation(JoyProperty, null);
    }

    private static void StartOneShot(DispatcherTimer timer, int ms)
    {
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(ms);
        timer.Start();
    }

    // ---- Geometry ----

    /// <summary>
    /// A vertical rounded eye. Closing lowers the top lid towards a line slightly below the centre and flattens it,
    /// so half-open reads as sleepy; above 1 the eye grows a little (wake bounce).
    /// </summary>
    private static StreamGeometry EyeGeometry(Point c, double w, double h, double openness)
    {
        double o = double.IsNaN(openness) ? 1 : Math.Clamp(openness, 0, 1.3);

        // Squash and stretch: a closing lid widens the eye a touch, the wake overshoot makes it a bit fuller.
        w *= o > 1 ? 1 + (o - 1) * 0.5 : 1 + (1 - o) * 0.1;

        double rx = w / 2;
        double ry = Math.Min(h / 2, w * 0.62);
        double top;
        double bottom;
        double ryTop;
        double ryBottom;
        if (o >= 1)
        {
            double hv = h * o;
            top = c.Y - hv / 2;
            bottom = c.Y + hv / 2;
            ryTop = ryBottom = Math.Min(ry, hv / 2);
        }
        else
        {
            double minH = Math.Max(1.2, h * 0.14);
            double pivot = c.Y + h * 0.25;
            bottom = pivot + (c.Y + h / 2 - pivot) * o;
            double hv = Math.Max(minH, h * o);
            top = bottom - hv;

            // The upper lid flattens as it drops, so a half-open eye reads as a heavy lid rather than a smaller eye.
            ryTop = ry * (0.15 + 0.85 * o * o * o);
            ryBottom = Math.Min(ry, hv - Math.Min(ryTop, hv / 2));
            ryTop = Math.Min(ryTop, hv - ryBottom);
        }

        double l = c.X - w / 2;
        double r = c.X + w / 2;
        const double k = 0.5523; // cubic Bézier quarter-ellipse constant
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(c.X, top), true, true);
            ctx.BezierTo(new Point(r - rx + rx * k, top), new Point(r, top + ryTop - ryTop * k), new Point(r, top + ryTop), true, true);
            ctx.LineTo(new Point(r, bottom - ryBottom), true, true);
            ctx.BezierTo(new Point(r, bottom - ryBottom + ryBottom * k), new Point(r - rx + rx * k, bottom), new Point(c.X, bottom), true, true);
            ctx.BezierTo(new Point(l + rx - rx * k, bottom), new Point(l, bottom - ryBottom + ryBottom * k), new Point(l, bottom - ryBottom), true, true);
            ctx.LineTo(new Point(l, top + ryTop), true, true);
            ctx.BezierTo(new Point(l, top + ryTop - ryTop * k), new Point(l + rx - rx * k, top), new Point(c.X, top), true, true);
        }

        g.Freeze();
        return g;
    }

    /// <summary>Happy arcs bulge up ("^ ^"); sleeping arcs sag down (closed, content eyes).</summary>
    private static StreamGeometry ArcGeometry(Point c, double w, double h, double strength, bool happy)
    {
        double baseY;
        double peak;
        double inset;
        if (happy)
        {
            w *= 1.05;
            baseY = c.Y + h * 0.16;
            peak = -h * 0.36 * Math.Clamp(strength, 0.2, 1.3);
            inset = 0.16;
        }
        else
        {
            // Sits where a closing lid ends (EyeGeometry pivot), so falling asleep does not jump.
            baseY = c.Y + h * 0.06;
            peak = h * 0.2;
            inset = 0.12;
        }

        double x0 = c.X - w / 2;
        double x1 = c.X + w / 2;

        // A cubic whose two control points sit at 4/3 of the peak reaches exactly the peak at its middle.
        double cy = baseY + peak * 4 / 3;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(new Point(x0, baseY), false, false);
            ctx.BezierTo(new Point(x0 + w * inset, cy), new Point(x1 - w * inset, cy), new Point(x1, baseY), true, true);
        }

        g.Freeze();
        return g;
    }

    private static double Snap(double value, double px) => Math.Round(value / px) * px;

    private static double Clamp1(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, -1, 1);

    private static Pen GetPen(ref Pen? cache, double thickness)
    {
        if (cache is null || Math.Abs(cache.Thickness - thickness) > 0.01)
        {
            var pen = new Pen(Brushes.White, thickness)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
            pen.Freeze();
            cache = pen;
        }

        return cache;
    }

    private static IEasingFunction Frozen(EasingFunctionBase ease)
    {
        ease.Freeze();
        return ease;
    }
}
