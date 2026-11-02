using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Prayer;

namespace Kedip.Views.Panels;

/// <summary>Next prayer countdown + today's schedule. OWNER: D.</summary>
public partial class PrayerPanel : UserControl
{
    private const double PassedOpacity = 0.38;
    private const int RowsPerColumn = 4;
    private const double TrackFallbackWidth = 144;
    private const double MinFillWidth = 4;

    // IconFont "Warning" (written as a code point to keep the source ASCII).
    private static readonly string WarningGlyph = char.ConvertFromUtf32(0xE7BA);

    private static readonly TimeSpan CountdownInterval = TimeSpan.FromSeconds(30);
    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(180));
    private static readonly Duration SwapDuration = new(TimeSpan.FromMilliseconds(260));
    private static readonly Duration FillDuration = new(TimeSpan.FromMilliseconds(520));
    private static readonly IEasingFunction EaseOut = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });
    private static readonly Brush HighlightBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0x34, 0xC7, 0x59)));
    private static readonly Brush TooltipBackground = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E)));
    private static readonly Brush TooltipBorder = Frozen(new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)));

    private static readonly PrayerName[] Order =
    [
        PrayerName.Imsak, PrayerName.Subuh, PrayerName.Terbit, PrayerName.Dzuhur,
        PrayerName.Ashar, PrayerName.Maghrib, PrayerName.Isya,
    ];

    private readonly AppServices _services;
    private readonly DispatcherTimer _countdownTimer;
    private readonly List<Row> _rows = [];
    private readonly Brush _accent;
    private readonly Brush _primary;
    private readonly Brush _secondary;
    private readonly TextBlock _cityTipText;
    private PrayerTime? _shownNext;
    private bool _subscribed;

    public PrayerPanel(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _accent = BrushResource("AccentBrush", Brushes.LimeGreen);
        _primary = BrushResource("TextPrimaryBrush", Brushes.White);
        _secondary = BrushResource("TextSecondaryBrush", Brushes.Gray);

        _cityTipText = new TextBlock { FontSize = 11, LineHeight = 16, Foreground = _primary };
        CityChip.ToolTip = CreateToolTip(_cityTipText);

        _countdownTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = CountdownInterval };
        _countdownTimer.Tick += (_, _) => UpdateTimeDependent(animate: AnimationsOn);

        BuildRows();

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private bool AnimationsOn => IsVisible && _services.Settings.Current.Animations && SystemParameters.ClientAreaAnimation;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed)
        {
            _services.Prayer.Updated += OnPrayerUpdated;
            _subscribed = true;
        }

        Refresh(appearing: true);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            _services.Prayer.Updated -= OnPrayerUpdated;
            _subscribed = false;
        }

        _countdownTimer.Stop();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible)
        {
            Refresh(appearing: true);
        }
        else
        {
            _countdownTimer.Stop();
        }
    }

    private void OnPrayerUpdated(object? sender, EventArgs e) => Refresh(appearing: false);

    private void Refresh(bool appearing)
    {
        var prayer = _services.Prayer;
        if (!prayer.IsEnabled)
        {
            _shownNext = null;
            ShowMessage(PrayerService.GlyphFor(PrayerName.Isya), "Waktu sholat dimatikan di Pengaturan", null);
            return;
        }

        if (prayer.Today.Count == 0)
        {
            _shownNext = null;
            ShowMessage(WarningGlyph, "Jadwal belum bisa dihitung", "Cek lokasi di Pengaturan, ya");
            return;
        }

        MessagePanel.Visibility = Visibility.Collapsed;
        ContentRoot.Visibility = Visibility.Visible;

        var schedule = DisplayedSchedule(prayer);
        foreach (var row in _rows)
        {
            row.TimeText.Text = TimeOf(schedule, row.Name) is { } time ? FormatTime(time) : "--:--";
        }

        var settings = _services.Settings.Current;
        CityText.Text = prayer.CityName;
        _cityTipText.Text = string.Create(CultureInfo.InvariantCulture,
            $"{prayer.CityName}\n{settings.Latitude:0.###}, {settings.Longitude:0.###} · {ZoneLabel(settings.TimeZoneHours)}\n" +
            $"Metode Kemenag RI · ihtiyat {Math.Max(0, settings.IhtiyatMinutes)} menit");

        var next = prayer.Next;
        if (next is not null)
        {
            NextGlyph.Text = PrayerService.GlyphFor(next.Name);
            NextNameText.Text = PrayerService.DisplayName(next.Name);
            NextTimeRun.Text = FormatTime(next.Time);
            NextDayRun.Text = prayer.IsNextTomorrow ? "  ·  besok" : "";
        }
        else
        {
            NextGlyph.Text = PrayerService.GlyphFor(PrayerName.Isya);
            NextNameText.Text = "Selesai";
            NextTimeRun.Text = "";
            NextDayRun.Text = "Tidak ada jadwal lagi";
        }

        // Soft slide-up when the next prayer changes while the panel is on screen.
        if (!appearing && _shownNext is not null && next != _shownNext && AnimationsOn)
        {
            Animate(NextBlock, OpacityProperty, 1, SwapDuration, animate: true, from: 0);
            Animate(NextBlockShift, TranslateTransform.YProperty, 0, SwapDuration, animate: true, from: 6);
        }
        else if (appearing)
        {
            Animate(NextBlock, OpacityProperty, 1, SwapDuration, animate: false);
            Animate(NextBlockShift, TranslateTransform.YProperty, 0, SwapDuration, animate: false);
        }

        _shownNext = next;
        UpdateTimeDependent(animate: AnimationsOn, fillIn: appearing);
    }

    /// <summary>Countdown, progress and passed/next row styling: the only parts that change with the clock.</summary>
    private void UpdateTimeDependent(bool animate, bool fillIn = false)
    {
        var prayer = _services.Prayer;
        if (!prayer.IsEnabled || prayer.Today.Count == 0)
        {
            _countdownTimer.Stop();
            return;
        }

        var now = prayer.Now;
        var next = prayer.Next;
        CountdownText.Text = next is null ? "" : FormatCountdown(next.Time - now);

        double trackWidth = ProgressTrack.ActualWidth > 0 ? ProgressTrack.ActualWidth : TrackFallbackWidth;
        double fraction = ProgressFraction(prayer.Today, next, now);
        double fill = fraction <= 0 ? 0 : Math.Max(MinFillWidth, Math.Round(fraction * trackWidth));
        Animate(ProgressFill, WidthProperty, fill, FillDuration, animate && fillIn, from: fillIn ? 0 : null);

        // After Isya the list already shows tomorrow, so nothing in it has passed yet.
        var schedule = DisplayedSchedule(prayer);
        foreach (var row in _rows)
        {
            var time = TimeOf(schedule, row.Name);
            bool isNext = next is not null && next.Name == row.Name && time == next.Time;
            bool passed = !isNext && time is { } t && t <= now;
            Animate(row.Highlight, OpacityProperty, isNext ? 1 : 0, FadeDuration, animate && !fillIn);
            Animate(row.Root, OpacityProperty, passed ? PassedOpacity : 1, FadeDuration, animate && !fillIn);
            row.NameText.Foreground = isNext ? _accent : _secondary;
            row.TimeText.Foreground = isNext ? _accent : _primary;
        }

        if (IsVisible && next is not null)
        {
            if (!_countdownTimer.IsEnabled)
            {
                _countdownTimer.Start();
            }
        }
        else
        {
            _countdownTimer.Stop();
        }
    }

    private void ShowMessage(string glyph, string title, string? hint)
    {
        _countdownTimer.Stop();
        MessageGlyph.Text = glyph;
        MessageText.Text = title;
        MessageHint.Text = hint ?? "";
        MessageHint.Visibility = hint is null ? Visibility.Collapsed : Visibility.Visible;
        ContentRoot.Visibility = Visibility.Collapsed;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void BuildRows()
    {
        for (int i = 0; i < Order.Length; i++)
        {
            var name = Order[i];
            bool marker = name is PrayerName.Imsak or PrayerName.Terbit;

            var nameText = new TextBlock
            {
                Text = PrayerService.DisplayName(name),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = _secondary,
            };
            var timeText = new TextBlock
            {
                Text = "--:--",
                FontSize = 13,
                FontWeight = marker ? FontWeights.Normal : FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Foreground = _primary,
            };
            DockPanel.SetDock(timeText, Dock.Right);

            var content = new DockPanel { Margin = new Thickness(8, 0, 8, 0), LastChildFill = true };
            content.Children.Add(timeText);
            content.Children.Add(nameText);

            var highlight = new Border { CornerRadius = new CornerRadius(8), Background = HighlightBrush, Opacity = 0 };
            var root = new Grid { Height = 26, VerticalAlignment = VerticalAlignment.Center };
            root.Children.Add(highlight);
            root.Children.Add(content);

            Grid.SetRow(root, i % RowsPerColumn);
            Grid.SetColumn(root, i < RowsPerColumn ? 0 : 2);
            ListGrid.Children.Add(root);
            _rows.Add(new Row(name, root, highlight, nameText, timeText));
        }
    }

    private ToolTip CreateToolTip(object content)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BackgroundProperty, TooltipBackground);
        border.SetValue(Border.BorderBrushProperty, TooltipBorder);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetValue(Border.PaddingProperty, new Thickness(10, 6, 10, 7));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        return new ToolTip
        {
            Content = content,
            Template = new ControlTemplate(typeof(ToolTip)) { VisualTree = border },
            FontFamily = TryFindResource("UiFont") as FontFamily ?? FontFamily,
            HasDropShadow = false,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            VerticalOffset = 4,
        };
    }

    private Brush BrushResource(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    /// <summary>Sets or eases a double property; clears any running animation first so local values always win.</summary>
    private static void Animate(IAnimatable target, DependencyProperty property, double to, Duration duration, bool animate, double? from = null)
    {
        var element = (DependencyObject)target;
        if (!animate)
        {
            target.BeginAnimation(property, null);
            element.SetValue(property, to);
            return;
        }

        if (from is null && Math.Abs((double)element.GetValue(property) - to) < 0.01)
        {
            return;
        }

        var animation = new DoubleAnimation(to, duration) { EasingFunction = EaseOut };
        if (from is { } start)
        {
            animation.From = start;
        }

        animation.Freeze();
        target.BeginAnimation(property, animation, HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>How far we are from the previous prayer (yesterday's Isya before Subuh) to the next one.</summary>
    private static double ProgressFraction(IReadOnlyList<PrayerTime> today, PrayerTime? next, DateTime now)
    {
        if (next is null)
        {
            return 0;
        }

        DateTime? previous = null;
        foreach (var entry in today)
        {
            if (entry.IsPrayer && entry.Time <= now && entry.Time < next.Time)
            {
                previous = entry.Time;
            }
        }

        previous ??= TimeOf(today, PrayerName.Isya)?.AddDays(-1);
        if (previous is not { } start || next.Time <= start)
        {
            return 0;
        }

        return Math.Clamp((now - start) / (next.Time - start), 0, 1);
    }

    /// <summary>Today's times, or tomorrow's once today's Isya has passed.</summary>
    private static IReadOnlyList<PrayerTime> DisplayedSchedule(PrayerService prayer) =>
        prayer.IsNextTomorrow && prayer.Tomorrow is { Count: > 0 } tomorrow ? tomorrow : prayer.Today;

    private static DateTime? TimeOf(IReadOnlyList<PrayerTime> schedule, PrayerName name)
    {
        foreach (var entry in schedule)
        {
            if (entry.Name == name)
            {
                return entry.Time;
            }
        }

        return null;
    }

    private static string FormatTime(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string ZoneLabel(double hours) => hours switch
    {
        7 => "WIB",
        8 => "WITA",
        9 => "WIT",
        _ => string.Create(CultureInfo.InvariantCulture, $"UTC{(hours < 0 ? "−" : "+")}{Math.Abs(hours):0.##}"),
    };

    /// <summary>"dalam 1 j 23 m" / "dalam 12 m"; minutes are rounded up so the last minute never reads "0 m".</summary>
    internal static string FormatCountdown(TimeSpan remaining)
    {
        int minutes = (int)Math.Ceiling(remaining.TotalMinutes);
        if (minutes <= 0)
        {
            return "sekarang";
        }

        if (minutes < 60)
        {
            return $"dalam {minutes} m";
        }

        int hours = minutes / 60;
        int rest = minutes % 60;
        return rest == 0 ? $"dalam {hours} j" : $"dalam {hours} j {rest} m";
    }

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    private sealed record Row(PrayerName Name, Grid Root, Border Highlight, TextBlock NameText, TextBlock TimeText);
}
