using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Media;

namespace Kedip.Views.Panels;

/// <summary>
/// Now playing + controls + lyric lines. OWNER: C.
/// Updates only while visible (a stale flag triggers a full refresh when shown); the progress clock ticks once per
/// second of playback, only while visible and playing; all motion is one-shot and eased.
/// </summary>
public partial class MusicPanel : UserControl
{
    private const string PlayGlyph = "\uF5B0";
    private const string PauseGlyph = "\uF8AE";
    private const string GapText = "•  •  •";
    private const double LyricPitch = 20;
    private const double LyricMinFontSize = 11;
    private const double PausedArtScale = 0.94;
    private const double SeekRestHeight = 4;
    private const double SeekHoverHeight = 6;
    private const double KnobRadius = 5;

    private static readonly TimeSpan ArtGrace = TimeSpan.FromMilliseconds(450);
    private static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction EaseInOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseInOut });

    private readonly AppServices _services;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _artGraceTimer;

    private bool _attached;
    private bool _stale = true;
    private bool? _showingTrack;
    private ImageSource? _shownArt;
    private bool? _shownPlaying;
    private IReadOnlyList<LyricLine>? _shownLines;
    private int _shownIndex = int.MinValue;
    private bool _dragging;
    private double _dragFraction;
    private double _lyricFontSize = 13;

    public MusicPanel(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _progressTimer = new DispatcherTimer(DispatcherPriority.Normal);
        _progressTimer.Tick += OnProgressTick;
        _artGraceTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = ArtGrace };
        _artGraceTimer.Tick += OnArtGraceTick;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;

        SeekArea.SizeChanged += (_, _) => UpdateProgress();
        SeekArea.MouseEnter += (_, _) => SetSeekHover(true);
        SeekArea.MouseLeave += (_, _) => SetSeekHover(_dragging);
        SeekArea.MouseLeftButtonDown += OnSeekDown;
        SeekArea.MouseMove += OnSeekMove;
        SeekArea.MouseLeftButtonUp += OnSeekUp;
        SeekArea.LostMouseCapture += OnSeekLostCapture;
        LyricsBox.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged)
            {
                FitLyrics();
            }
        };
    }

    private MediaService Media => _services.Media;

    private LyricsService Lyrics => _services.Lyrics;

    private bool CanAnimate => IsVisible && _services.Settings.Current.Animations;

    // ---- Lifetime ----

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_attached)
        {
            return;
        }

        _attached = true;
        Media.TrackChanged += OnTrackChanged;
        Media.PlaybackChanged += OnPlaybackChanged;
        Lyrics.LyricsChanged += OnLyricsChanged;
        Lyrics.LineChanged += OnLyricsChanged;
        _services.Settings.Changed += OnSettingsChanged;
        _stale = true;
        RefreshIfVisible();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_attached)
        {
            return;
        }

        _attached = false;
        Media.TrackChanged -= OnTrackChanged;
        Media.PlaybackChanged -= OnPlaybackChanged;
        Lyrics.LyricsChanged -= OnLyricsChanged;
        Lyrics.LineChanged -= OnLyricsChanged;
        _services.Settings.Changed -= OnSettingsChanged;
        _progressTimer.Stop();
        _artGraceTimer.Stop();
        EndDrag(commit: false);
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            RefreshIfVisible();
        }
        else
        {
            _progressTimer.Stop();
            EndDrag(commit: false);
        }
    }

    private void RefreshIfVisible()
    {
        if (!_attached || !IsVisible)
        {
            return;
        }

        if (_stale)
        {
            _stale = false;
            UpdateTrack(animate: false);
            UpdatePlayback(animate: false);
            UpdateLyrics(animate: false);
        }

        UpdateProgress();
        UpdateProgressTimer();
    }

    // ---- Service events (UI thread) ----

    private void OnTrackChanged(object? sender, EventArgs e) => Run(() =>
    {
        UpdateTrack(CanAnimate);
        UpdatePlayback(CanAnimate);
        UpdateLyrics(animate: false);
        UpdateProgress();
        UpdateProgressTimer();
    });

    private void OnPlaybackChanged(object? sender, EventArgs e) => Run(() =>
    {
        UpdatePlayback(CanAnimate);
        UpdateProgress();
        UpdateProgressTimer();
    });

    private void OnLyricsChanged(object? sender, EventArgs e) => Run(() => UpdateLyrics(CanAnimate));

    private void OnSettingsChanged(object? sender, EventArgs e) => Run(() =>
    {
        UpdateTrack(CanAnimate);
        UpdatePlayback(animate: false);
        UpdateLyrics(animate: false);
        UpdateProgress();
        UpdateProgressTimer();
    });

    /// <summary>Runs a view update now when visible, otherwise remembers to refresh on show.</summary>
    private void Run(Action update)
    {
        if (!_attached)
        {
            return;
        }

        if (!IsVisible)
        {
            _stale = true;
            return;
        }

        try
        {
            update();
        }
        catch (Exception ex)
        {
            Log.Error("Music panel update failed", ex);
        }
    }

    // ---- Track ----

    private void UpdateTrack(bool animate)
    {
        var info = Media.Current;
        bool enabled = _services.Settings.Current.MusicEnabled;
        bool show = enabled && info is not null;
        SetLayer(show, animate);

        if (!show || info is null)
        {
            EmptyGlyph.Text = enabled ? "\uE8D6" : "\uE74F";
            EmptyTitle.Text = enabled ? "Tidak ada musik yang diputar" : "Fitur musik sedang dimatikan";
            EmptyHint.Text = enabled
                ? "Putar lagu di Spotify, YouTube, atau aplikasi musik lain"
                : "Nyalakan lagi lewat Pengaturan › Musik";
            return;
        }

        string app = info.SourceAppName;
        TitleText.Text = info.Title.Length > 0 ? info.Title : "Tanpa judul";
        ArtistText.Text = info.Artist.Length > 0 ? info.Artist : app.Length > 0 ? app : "Artis tidak diketahui";
        SetArt(info.Thumbnail, animate);
    }

    private void SetLayer(bool showTrack, bool animate)
    {
        if (_showingTrack == showTrack)
        {
            return;
        }

        bool first = _showingTrack is null;
        _showingTrack = showTrack;
        FrameworkElement shown = showTrack ? NowPlaying : EmptyState;
        FrameworkElement hidden = showTrack ? EmptyState : NowPlaying;
        shown.Visibility = Visibility.Visible;
        shown.IsHitTestVisible = true;
        hidden.IsHitTestVisible = false;

        if (!animate || first)
        {
            StopAnimation(shown, OpacityProperty, 1);
            StopAnimation(hidden, OpacityProperty, 0);
            hidden.Visibility = Visibility.Collapsed;
            return;
        }

        AnimateTo(shown, OpacityProperty, 1, 220, EaseOut);
        AnimateTo(hidden, OpacityProperty, 0, 140, EaseOut, onCompleted: () =>
        {
            if (_showingTrack == showTrack)
            {
                hidden.Visibility = Visibility.Collapsed;
            }
        });
    }

    private void SetArt(ImageSource? art, bool animate)
    {
        if (ReferenceEquals(art, _shownArt))
        {
            _artGraceTimer.Stop();
            return;
        }

        if (art is null && _shownArt is not null && animate)
        {
            // New track whose art has not arrived yet: keep the old cover briefly, then cross-fade straight to the new one.
            if (!_artGraceTimer.IsEnabled)
            {
                _artGraceTimer.Start();
            }

            return;
        }

        _artGraceTimer.Stop();
        ShowArt(art, animate);
    }

    private void OnArtGraceTick(object? sender, EventArgs e)
    {
        _artGraceTimer.Stop();
        if (Media.Current?.Thumbnail is null)
        {
            ShowArt(null, CanAnimate);
        }
    }

    private void ShowArt(ImageSource? art, bool animate)
    {
        _shownArt = art;
        var previous = ArtFront.Background;
        var next = art is null ? null : CreateArtBrush(art);

        StopAnimation(ArtFront, OpacityProperty, 1);
        StopAnimation(ArtBack, OpacityProperty, 1);
        if (!animate || previous is null)
        {
            ArtBack.Background = null;
            ArtFront.Background = next;
            if (animate && next is not null)
            {
                AnimateTo(ArtFront, OpacityProperty, 1, 240, EaseOut, from: 0);
            }

            return;
        }

        ArtBack.Background = previous;
        ArtFront.Background = next;
        if (next is not null)
        {
            // New cover fades in over the old one (no dip to the placeholder in between).
            AnimateTo(ArtFront, OpacityProperty, 1, 260, EaseOut, from: 0, onCompleted: () =>
            {
                if (ReferenceEquals(ArtFront.Background, next))
                {
                    ArtBack.Background = null;
                }
            });
        }
        else
        {
            AnimateTo(ArtBack, OpacityProperty, 0, 220, EaseOut, onCompleted: () =>
            {
                if (ArtFront.Background is null)
                {
                    ArtBack.Background = null;
                    StopAnimation(ArtBack, OpacityProperty, 1);
                }
            });
        }
    }

    private static ImageBrush CreateArtBrush(ImageSource art)
    {
        var brush = new ImageBrush(art) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();
        return brush;
    }

    // ---- Playback ----

    private void UpdatePlayback(bool animate)
    {
        if (Media.Current is not { } info)
        {
            _shownPlaying = null;
            return;
        }

        bool playing = info.IsPlaying;
        PlayButton.Content = playing ? PauseGlyph : PlayGlyph;
        // The play triangle's optical centre sits left of its box; nudge it so it looks centred in the circle.
        PlayButton.Padding = playing ? new Thickness(0) : new Thickness(2, 0, 0, 0);
        AutomationProperties.SetName(PlayButton, playing ? "Jeda" : "Putar");
        PlayButton.IsEnabled = info.CanPlayPause;
        PrevButton.IsEnabled = info.CanGoPrevious;
        NextButton.IsEnabled = info.CanGoNext;

        SeekArea.IsHitTestVisible = info.CanSeek;
        SeekArea.Cursor = info.CanSeek ? Cursors.Hand : null;
        if (!info.CanSeek)
        {
            EndDrag(commit: false);
            SetSeekHover(false);
        }

        if (_shownPlaying == playing)
        {
            return;
        }

        _shownPlaying = playing;
        double scale = playing ? 1 : PausedArtScale;
        if (animate)
        {
            AnimateTo(ArtScale, ScaleTransform.ScaleXProperty, scale, 320, EaseInOut);
            AnimateTo(ArtScale, ScaleTransform.ScaleYProperty, scale, 320, EaseInOut);
        }
        else
        {
            StopAnimation(ArtScale, ScaleTransform.ScaleXProperty, scale);
            StopAnimation(ArtScale, ScaleTransform.ScaleYProperty, scale);
        }
    }

    private void OnPlayPauseClick(object sender, RoutedEventArgs e) => _ = Media.PlayPauseAsync();

    private void OnPreviousClick(object sender, RoutedEventArgs e) => _ = Media.PreviousAsync();

    private void OnNextClick(object sender, RoutedEventArgs e) => _ = Media.NextAsync();

    // ---- Progress ----

    private void UpdateProgress()
    {
        if (Media.Current is not { } info || !IsVisible)
        {
            return;
        }

        var duration = info.Duration;
        bool known = duration > TimeSpan.Zero;
        var position = _dragging && known ? duration * _dragFraction : Media.Position;
        ElapsedText.Text = FormatTime(position, duration);
        TotalText.Text = known ? FormatTime(duration, duration) : "--:--";
        SeekArea.Opacity = known ? 1 : 0.5;

        double fraction = known ? Math.Clamp(position / duration, 0, 1) : 0;
        double width = Math.Round(SeekArea.ActualWidth * fraction);
        SeekFill.Width = width;
        KnobShift.X = Math.Clamp(width - KnobRadius, -KnobRadius, Math.Max(0, SeekArea.ActualWidth - KnobRadius));
    }

    /// <summary>Ticks just after each whole second of playback so the elapsed clock advances evenly.</summary>
    private void UpdateProgressTimer()
    {
        bool run = _attached && IsVisible && !_dragging && Media.Current is { IsPlaying: true };
        if (!run)
        {
            _progressTimer.Stop();
            return;
        }

        double rate = Media.PlaybackRate > 0 ? Media.PlaybackRate : 1;
        double msToNextSecond = 1000 - (Media.Position.TotalMilliseconds % 1000);
        double wait = Math.Clamp(msToNextSecond / rate + 15, 50, 1015);
        _progressTimer.Interval = TimeSpan.FromMilliseconds(wait);
        if (!_progressTimer.IsEnabled)
        {
            _progressTimer.Start();
        }
    }

    private void OnProgressTick(object? sender, EventArgs e)
    {
        try
        {
            UpdateProgress();
            UpdateProgressTimer();
        }
        catch (Exception ex)
        {
            _progressTimer.Stop();
            Log.Error("Music progress update failed", ex);
        }
    }

    private static string FormatTime(TimeSpan value, TimeSpan duration)
    {
        if (value < TimeSpan.Zero)
        {
            value = TimeSpan.Zero;
        }

        return duration.TotalHours >= 1 || value.TotalHours >= 1
            ? $"{(int)value.TotalHours}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{(int)value.TotalMinutes}:{value.Seconds:00}";
    }

    // ---- Seeking ----

    private void SetSeekHover(bool on)
    {
        double height = on ? SeekHoverHeight : SeekRestHeight;
        if (CanAnimate)
        {
            AnimateTo(SeekTrack, HeightProperty, height, 120, EaseOut);
            AnimateTo(SeekFill, HeightProperty, height, 120, EaseOut);
            AnimateTo(SeekKnob, OpacityProperty, on ? 1 : 0, 120, EaseOut);
        }
        else
        {
            StopAnimation(SeekTrack, HeightProperty, height);
            StopAnimation(SeekFill, HeightProperty, height);
            StopAnimation(SeekKnob, OpacityProperty, on ? 1 : 0);
        }
    }

    private void OnSeekDown(object sender, MouseButtonEventArgs e)
    {
        if (Media.Current is not { CanSeek: true } || SeekArea.ActualWidth <= 0)
        {
            return;
        }

        if (SeekArea.CaptureMouse())
        {
            _dragging = true;
            _dragFraction = FractionAt(e);
            _progressTimer.Stop();
            SetSeekHover(true);
            UpdateProgress();
            e.Handled = true;
        }
    }

    private void OnSeekMove(object sender, MouseEventArgs e)
    {
        if (_dragging)
        {
            _dragFraction = FractionAt(e);
            UpdateProgress();
        }
    }

    private void OnSeekUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragFraction = FractionAt(e);
            EndDrag(commit: true);
            e.Handled = true;
        }
    }

    private void OnSeekLostCapture(object sender, MouseEventArgs e) => EndDrag(commit: false);

    private void EndDrag(bool commit)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (SeekArea.IsMouseCaptured)
        {
            SeekArea.ReleaseMouseCapture();
        }

        SetSeekHover(SeekArea.IsMouseOver);
        if (commit && Media.Current is { CanSeek: true } info && info.Duration > TimeSpan.Zero)
        {
            _ = Media.SeekAsync(info.Duration * _dragFraction);
        }

        UpdateProgress();
        UpdateProgressTimer();
    }

    private double FractionAt(MouseEventArgs e) =>
        SeekArea.ActualWidth > 0 ? Math.Clamp(e.GetPosition(SeekArea).X / SeekArea.ActualWidth, 0, 1) : 0;

    // ---- Lyrics ----

    private void UpdateLyrics(bool animate)
    {
        var lines = Lyrics.Lines;
        if (lines is null || Media.Current is null)
        {
            ShowLyricStatus();
            return;
        }

        LyricStatus.Visibility = Visibility.Collapsed;
        LyricStack.Visibility = Visibility.Visible;

        int index = Lyrics.CurrentIndex;
        if (ReferenceEquals(lines, _shownLines) && index == _shownIndex)
        {
            return;
        }

        bool step = animate && ReferenceEquals(lines, _shownLines) && index == _shownIndex + 1;
        string leaving = LyricCurrent.Text;
        bool leavingIsGap = ReferenceEquals(LyricCurrent.Foreground, TertiaryBrush);
        _shownLines = lines;
        _shownIndex = index;

        SetLyricText(LyricCurrent, LineText(lines, index), primary: true);
        SetLyricText(LyricNext, index + 1 < lines.Count ? LineText(lines, index + 1) : "", primary: false);
        FitLyrics();

        StopAnimation(LyricShift, TranslateTransform.YProperty, 0);
        StopAnimation(LyricPrev, OpacityProperty, 0);
        StopAnimation(LyricCurrent, OpacityProperty, 1);
        StopAnimation(LyricNext, OpacityProperty, 1);
        StopAnimation(LyricStack, OpacityProperty, 1);

        if (step)
        {
            // Scroll up one line: the old line leaves through the top while the next one rises into place.
            LyricPrev.Text = leaving;
            LyricPrev.Foreground = leavingIsGap ? TertiaryBrush : LyricBrush;
            LyricPrev.FontSize = _lyricFontSize;
            AnimateTo(LyricShift, TranslateTransform.YProperty, 0, 340, EaseOut, from: LyricPitch);
            AnimateTo(LyricPrev, OpacityProperty, 0, 260, EaseOut, from: 1);
            AnimateTo(LyricCurrent, OpacityProperty, 1, 340, EaseOut, from: 0.4);
            AnimateTo(LyricNext, OpacityProperty, 1, 300, EaseOut, from: 0, delay: 80);
        }
        else
        {
            LyricPrev.Text = "";
            if (animate)
            {
                AnimateTo(LyricStack, OpacityProperty, 1, 200, EaseOut, from: 0);
            }
        }
    }

    private Brush LyricBrush => (Brush)Resources["LyricBrush"];

    private Brush TertiaryBrush => (Brush)Resources["TertiaryBrush"];

    private static string LineText(IReadOnlyList<LyricLine> lines, int index) =>
        index < 0 || index >= lines.Count || lines[index].Text.Length == 0 ? GapText : lines[index].Text;

    private void SetLyricText(TextBlock block, string text, bool primary)
    {
        block.Text = text;
        block.Foreground = primary && text != GapText ? LyricBrush : TertiaryBrush;
    }

    /// <summary>Shrinks a long line a little (13 → 11 DIP) before falling back to an ellipsis.</summary>
    private void FitLyrics()
    {
        _lyricFontSize = Fit(LyricCurrent, 13);
        Fit(LyricNext, 12);
    }

    private double Fit(TextBlock block, double baseSize)
    {
        double available = LyricsBox.ActualWidth;
        block.FontSize = baseSize;
        if (available <= 0 || string.IsNullOrEmpty(block.Text))
        {
            return baseSize;
        }

        var text = new FormattedText(
            block.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            baseSize,
            block.Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (text.WidthIncludingTrailingWhitespace <= available)
        {
            return baseSize;
        }

        double size = Math.Floor(baseSize * available / text.WidthIncludingTrailingWhitespace * 2) / 2;
        block.FontSize = Math.Clamp(size, LyricMinFontSize, baseSize);
        return block.FontSize;
    }

    private void ShowLyricStatus()
    {
        _shownLines = null;
        _shownIndex = int.MinValue;
        StopAnimation(LyricShift, TranslateTransform.YProperty, 0);
        LyricStack.Visibility = Visibility.Collapsed;
        LyricPrev.Text = LyricCurrent.Text = LyricNext.Text = "";

        if (Media.Current is not { } info)
        {
            LyricStatus.Visibility = Visibility.Collapsed;
            return;
        }

        string app = info.SourceAppName;
        string playedOn = app.Length > 0 ? $"Diputar di {app}" : "";
        string album = info.Album.Length > 0 && !string.Equals(info.Album, info.Title, StringComparison.OrdinalIgnoreCase)
            ? info.Album
            : "";
        string context = album.Length > 0 && playedOn.Length > 0 ? $"{album}  ·  {app}" : album.Length > 0 ? album : playedOn;

        (string primary, string secondary) = Lyrics.Status switch
        {
            LyricsStatus.Searching => ("Mencari lirik…", context),
            LyricsStatus.NotFound => ("Lirik belum tersedia untuk lagu ini", context),
            _ => album.Length > 0 ? (album, playedOn) : (playedOn, ""),
        };

        StatusPrimary.Text = primary;
        StatusSecondary.Text = secondary;
        StatusSecondary.Visibility = secondary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        LyricStatus.Visibility = primary.Length > 0 || secondary.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Animation helpers ----

    /// <summary>
    /// Animates from the current (or given) value to <paramref name="to"/>. The base value is set to the target first and the
    /// animation does not hold its end value, so later direct assignments are never overridden by a finished clock.
    /// </summary>
    private static void AnimateTo(
        IAnimatable target,
        DependencyProperty property,
        double to,
        double milliseconds,
        IEasingFunction? easing = null,
        double? from = null,
        double delay = 0,
        Action? onCompleted = null)
    {
        var element = (DependencyObject)target;
        double start = from ?? (double)element.GetValue(property);
        element.SetValue(property, to);
        if (Math.Abs(start - to) < 0.001 && delay <= 0)
        {
            target.BeginAnimation(property, null);
            onCompleted?.Invoke();
            return;
        }

        // A delay is folded into the easing curve so the start value stays visible while waiting.
        double total = delay + milliseconds;
        var animation = new DoubleAnimation(start, to, TimeSpan.FromMilliseconds(total))
        {
            EasingFunction = delay > 0 ? new DelayedEase(delay / total, easing) : easing,
            FillBehavior = FillBehavior.Stop,
        };
        if (onCompleted is not null)
        {
            animation.Completed += (_, _) => onCompleted();
        }

        target.BeginAnimation(property, animation);
    }

    private static void StopAnimation(IAnimatable target, DependencyProperty property, double value)
    {
        target.BeginAnimation(property, null);
        ((DependencyObject)target).SetValue(property, value);
    }

    private static IEasingFunction Frozen(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }

    /// <summary>Stays at 0 for the first <c>delayFraction</c> of the timeline, then applies the inner easing.</summary>
    private sealed class DelayedEase(double delayFraction, IEasingFunction? inner) : IEasingFunction
    {
        public double Ease(double normalizedTime)
        {
            if (normalizedTime <= delayFraction)
            {
                return 0;
            }

            double t = (normalizedTime - delayFraction) / (1 - delayFraction);
            return inner?.Ease(t) ?? t;
        }
    }
}
