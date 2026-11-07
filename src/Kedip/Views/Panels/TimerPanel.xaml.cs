using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Kedip.Core;
using Kedip.Productivity;

namespace Kedip.Views.Panels;

/// <summary>
/// Pomodoro controls + water intake. Updates only from service Changed events, and skips the work
/// while the panel is hidden (it catches up when shown again).
/// </summary>
public partial class TimerPanel : UserControl
{
    private const int MaxDots = 6;
    private const double DotSize = 6.4;
    private const double DotGap = 4.8;
    private const int WaterCapMl = 5000;

    private static readonly Duration BarDuration = new(TimeSpan.FromMilliseconds(420));
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly IEasingFunction Ease = CreateEase();
    private static readonly string Dot = " " + (char)0xB7 + " ";

    private readonly AppServices _services;
    private readonly Brush _focusBrush;
    private readonly Brush _focusTintBrush;
    private readonly Brush _breakBrush;
    private readonly Brush _breakTintBrush;
    private readonly Brush _waterBrush;
    private readonly Brush _secondaryBrush;
    private readonly Brush _dotEmptyBrush;
    private bool _subscribed;
    private bool _pomodoroDirty = true;
    private bool _waterDirty = true;
    private double _timerFraction;
    private double _waterFraction;
    private bool? _timeDimmed;

    public TimerPanel(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _focusBrush = (Brush)FindResource("FocusBrush");
        _focusTintBrush = (Brush)FindResource("FocusTintBrush");
        _breakBrush = (Brush)FindResource("BreakBrush");
        _breakTintBrush = (Brush)FindResource("BreakTintBrush");
        _waterBrush = (Brush)FindResource("WaterBrush");
        _secondaryBrush = (Brush)FindResource("DockText2Brush");
        _dotEmptyBrush = (Brush)FindResource("TrackBrush");

        TimerTrack.SizeChanged += (_, _) => SetBarNow(TimerFill, TimerTrack, _timerFraction);
        WaterTrack.SizeChanged += (_, _) => SetBarNow(WaterFill, WaterTrack, _waterFraction);
        Loaded += (_, _) => Subscribe();
        Unloaded += (_, _) => Unsubscribe();
        IsVisibleChanged += (_, _) => CatchUp();
    }

    private PomodoroService Pomodoro => _services.Pomodoro;

    private WaterService Water => _services.Water;

    private bool Animate => _services.Settings.Current.Animations && IsVisible;

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _subscribed = true;
        Pomodoro.Changed += OnPomodoroChanged;
        Water.Changed += OnWaterChanged;
        _pomodoroDirty = true;
        _waterDirty = true;
        CatchUp();
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _subscribed = false;
        Pomodoro.Changed -= OnPomodoroChanged;
        Water.Changed -= OnWaterChanged;
    }

    private void OnPomodoroChanged(object? sender, EventArgs e)
    {
        _pomodoroDirty = true;
        if (IsVisible)
        {
            UpdatePomodoro();
        }
    }

    private void OnWaterChanged(object? sender, EventArgs e)
    {
        _waterDirty = true;
        if (IsVisible)
        {
            UpdateWater();
        }
    }

    private void CatchUp()
    {
        if (!IsVisible)
        {
            return;
        }

        if (_pomodoroDirty)
        {
            UpdatePomodoro();
        }

        if (_waterDirty)
        {
            UpdateWater();
        }
    }

    private void UpdatePomodoro()
    {
        _pomodoroDirty = false;
        var p = Pomodoro;
        bool idle = p.Phase == PomodoroPhase.Idle;
        bool isBreak = p.Phase is PomodoroPhase.ShortBreak or PomodoroPhase.LongBreak;
        bool fresh = !p.IsRunning && p.Remaining >= p.PhaseLength;
        bool paused = !idle && !p.IsRunning && !fresh;
        Brush accent = isBreak ? _breakBrush : _focusBrush;

        PhaseText.Text = p.Phase switch
        {
            PomodoroPhase.Focus => "Fokus",
            PomodoroPhase.ShortBreak => "Istirahat",
            PomodoroPhase.LongBreak => "Istirahat panjang",
            _ => "Siap",
        };
        PhaseGlyph.Text = isBreak ? Glyphs.Cup : Glyphs.Stopwatch;
        PhaseGlyph.Foreground = idle ? _secondaryBrush : accent;

        TimeText.Text = IdFormat.Clock(p.Remaining);
        SetTimeDimmed(paused);
        TimerSubText.Text = PomodoroSubtitle(p, idle, fresh, paused);

        TimerFill.Background = accent;
        double fraction = p.Progress;
        SetBar(TimerFill, TimerTrack, ref _timerFraction, fraction, animate: Math.Abs(fraction - _timerFraction) > 0.02);

        StartPauseGlyph.Text = p.IsRunning ? Glyphs.PauseSolid : Glyphs.PlaySolid;
        StartPauseLabel.Text = p.IsRunning ? "Jeda" : paused ? "Lanjut" : "Mulai";
        StartPauseButton.Background = isBreak ? _breakTintBrush : _focusTintBrush;
        StartPauseButton.Foreground = accent;
        StartPauseButton.ToolTip = p.IsRunning ? "Jeda dulu"
            : paused ? "Lanjutkan timer"
            : isBreak ? "Mulai istirahat"
            : "Mulai sesi fokus";

        ResetButton.IsEnabled = !idle || p.CompletedFocusSessions > 0;
        SkipButton.IsEnabled = !idle;
        SkipButton.ToolTip = p.Phase == PomodoroPhase.Focus ? "Lewati ke istirahat" : "Lewati ke fokus";

        UpdateSessions(p);
    }

    private string PomodoroSubtitle(PomodoroService p, bool idle, bool fresh, bool paused)
    {
        var s = _services.Settings.Current;
        string session = $"Sesi {p.CurrentSessionInCycle} dari {p.SessionsPerCycle}";
        string? endsAt = p.EndsAt?.ToString("HH:mm", CultureInfo.InvariantCulture);

        if (idle)
        {
            return $"Fokus {Math.Max(1, s.FocusMinutes)} mnt" + Dot + $"rehat {Math.Max(1, s.ShortBreakMinutes)} mnt";
        }

        return p.Phase switch
        {
            PomodoroPhase.Focus when p.IsRunning => session + Dot + "selesai " + endsAt,
            PomodoroPhase.Focus when fresh => "Siap untuk sesi " + p.CurrentSessionInCycle,
            PomodoroPhase.Focus => "Dijeda" + Dot + session.ToLowerInvariant(),
            PomodoroPhase.LongBreak when p.IsRunning => "Rehat panjang sampai " + endsAt,
            PomodoroPhase.ShortBreak when p.IsRunning => "Rehat sampai " + endsAt,
            _ when paused => "Istirahat dijeda",
            _ => "Siap istirahat",
        };
    }

    private void UpdateSessions(PomodoroService p)
    {
        int perCycle = p.SessionsPerCycle;
        int done = p.CompletedFocusSessions % perCycle;
        if (done == 0 && p.CompletedFocusSessions > 0 && p.Phase == PomodoroPhase.LongBreak)
        {
            done = perCycle;
        }

        string tip = p.CompletedFocusSessions == 0
            ? $"Belum ada sesi fokus selesai (istirahat panjang tiap {perCycle} sesi)"
            : $"{p.CompletedFocusSessions} sesi fokus selesai";

        if (perCycle > MaxDots)
        {
            SessionDots.Visibility = Visibility.Collapsed;
            SessionCountText.Visibility = Visibility.Visible;
            SessionCountText.Text = $"{done}/{perCycle}";
            SessionCountText.ToolTip = tip;
            return;
        }

        SessionCountText.Visibility = Visibility.Collapsed;
        SessionDots.Visibility = Visibility.Visible;
        SessionDots.ToolTip = tip;
        if (SessionDots.Children.Count != perCycle)
        {
            SessionDots.Children.Clear();
            for (int i = 0; i < perCycle; i++)
            {
                SessionDots.Children.Add(new Ellipse
                {
                    Width = DotSize,
                    Height = DotSize,
                    Margin = new Thickness(i == 0 ? 0 : DotGap, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
        }

        bool inFocus = p.Phase == PomodoroPhase.Focus;
        for (int i = 0; i < perCycle; i++)
        {
            var dot = (Ellipse)SessionDots.Children[i];
            bool current = inFocus && i == done;
            dot.Fill = i < done || current ? _focusBrush : _dotEmptyBrush;
            dot.Opacity = current ? (p.IsRunning ? 0.55 : 0.35) : 1;
        }
    }

    private void SetTimeDimmed(bool dimmed)
    {
        if (_timeDimmed == dimmed)
        {
            return;
        }

        bool first = _timeDimmed is null;
        _timeDimmed = dimmed;
        double to = dimmed ? 0.5 : 1;
        if (first || !Animate)
        {
            TimeText.BeginAnimation(OpacityProperty, null);
            TimeText.Opacity = to;
            return;
        }

        TimeText.BeginAnimation(OpacityProperty, new DoubleAnimation(to, FadeDuration) { EasingFunction = Ease });
    }

    private void UpdateWater()
    {
        _waterDirty = false;
        var w = Water;
        var s = _services.Settings.Current;
        int goal = Math.Max(0, w.GoalMl);
        bool reached = goal > 0 && w.TodayMl >= goal;
        int percent = goal > 0 ? (int)Math.Round(100.0 * w.TodayMl / goal) : 100;

        WaterAmountRun.Text = IdFormat.Int(w.TodayMl);
        WaterGoalRun.Text = $" / {IdFormat.Int(goal)} ml";
        GlassesText.Text = $"{w.GlassesToday} gelas" + Dot + $"{percent}%";

        WaterFill.Background = reached ? _breakBrush : _waterBrush;
        SetBar(WaterFill, WaterTrack, ref _waterFraction, w.Progress, animate: true);

        if (reached)
        {
            WaterSubText.Text = "Target hari ini tercapai, mantap!";
            WaterSubText.Foreground = _breakBrush;
        }
        else
        {
            string status = $"Kurang {IdFormat.Int(goal - w.TodayMl)} ml";
            if (!s.WaterEnabled)
            {
                status += Dot + "pengingat mati";
            }
            else if (w.NextReminder is { } next && next.Date == DateTime.Today)
            {
                status += Dot + "pengingat " + next.ToString("HH:mm", CultureInfo.InvariantCulture);
            }

            WaterSubText.Text = status;
            WaterSubText.Foreground = _secondaryBrush;
        }

        AddGlassText.Text = $"+1 gelas ({IdFormat.Int(w.GlassMl)} ml)";
        AddGlassButton.IsEnabled = w.TodayMl < WaterCapMl;
        UndoButton.IsEnabled = w.TodayMl > 0 || w.GlassesToday > 0;
    }

    /// <summary>Sets a progress fill (Width animated, ease-out) relative to its track.</summary>
    private void SetBar(Border fill, FrameworkElement track, ref double current, double fraction, bool animate)
    {
        fraction = double.IsFinite(fraction) ? Math.Clamp(fraction, 0, 1) : 0;
        bool changed = Math.Abs(fraction - current) > 0.0005;
        current = fraction;
        if (!changed && fill.HasAnimatedProperties)
        {
            return;
        }

        double width = FillWidth(track, fraction);
        if (!animate || !Animate || track.ActualWidth <= 0 || !changed)
        {
            fill.BeginAnimation(WidthProperty, null);
            fill.Width = width;
            return;
        }

        var animation = new DoubleAnimation(fill.ActualWidth, width, BarDuration) { EasingFunction = Ease };
        fill.BeginAnimation(WidthProperty, animation);
    }

    private static void SetBarNow(Border fill, FrameworkElement track, double fraction)
    {
        fill.BeginAnimation(WidthProperty, null);
        fill.Width = FillWidth(track, fraction);
    }

    /// <summary>Any started progress shows at least as a round dot.</summary>
    private static double FillWidth(FrameworkElement track, double fraction) =>
        fraction <= 0 || track.ActualWidth <= 0 ? 0 : Math.Max(4, track.ActualWidth * fraction);

    private void StartPause_Click(object sender, RoutedEventArgs e)
    {
        if (Pomodoro.IsRunning)
        {
            Pomodoro.Pause();
        }
        else
        {
            Pomodoro.StartOrResume();
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => Pomodoro.Reset();

    private void Skip_Click(object sender, RoutedEventArgs e) => Pomodoro.Skip();

    private void AddGlass_Click(object sender, RoutedEventArgs e) => Water.AddGlass();

    private void UndoGlass_Click(object sender, RoutedEventArgs e) => Water.UndoGlass();

    private static IEasingFunction CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }
}
