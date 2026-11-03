using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Kedip.Core;
using Kedip.Notifications;
using Kedip.Productivity;
using static Kedip.DockBar.WinKeys;

namespace Kedip.DockBar;

/// <summary>
/// Kedip's own clock flyout (instead of Windows' notification centre and calendar): the notifications Windows holds,
/// grouped by app; the month in Indonesian with the Hijri date; and a focus timer on Kedip's pomodoro.
/// </summary>
public partial class ClockPanelWindow : GlassPanel
{
    private const int ShownPerApp = 3;
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");
    private static readonly string[] DayLetters = ["Min", "Sen", "Sel", "Rab", "Kam", "Jum", "Sab"];

    private static readonly string[] HijriMonths =
    [
        "Muharram", "Safar", "Rabiulawal", "Rabiulakhir", "Jumadilawal", "Jumadilakhir",
        "Rajab", "Syakban", "Ramadan", "Syawal", "Zulkaidah", "Zulhijah",
    ];

    private readonly AppServices _services;
    private readonly HashSet<string> _expandedApps = new(StringComparer.OrdinalIgnoreCase);
    private List<NotificationItem> _notifications = [];
    private DateTime _month;
    private DateTime _selected;

    internal ClockPanelWindow(AppServices services)
    {
        _services = services;
        InitializeComponent();

        _selected = DateTime.Today;
        _month = new DateTime(_selected.Year, _selected.Month, 1);
        EmptyGlyph.Text = Glyph(0xEA8F);
        CollapseButton.Content = Glyph(0xE70D);
        PrevMonth.Content = Glyph(0xE70E);
        NextMonth.Content = Glyph(0xE70D);
        LessButton.Content = Glyph(0xE738);
        MoreButton.Content = Glyph(0xE710);
        foreach (string day in DayLetters)
        {
            DayNames.Children.Add(new TextBlock
            {
                Text = day,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 4),
            });
        }

        DndButton.Click += (_, _) =>
        {
            WindowsDoNotDisturb.Set(WindowsDoNotDisturb.IsOn() != true);
            UpdateDnd();
        };
        ClearButton.Click += (_, _) => ClearAll();
        CollapseButton.Click += (_, _) => ToggleCalendar();
        PrevMonth.Click += (_, _) => ShowMonth(_month.AddMonths(-1));
        NextMonth.Click += (_, _) => ShowMonth(_month.AddMonths(1));
        TodayButton.Click += (_, _) => Select(DateTime.Today);
        LessButton.Click += (_, _) => ChangeFocusMinutes(-5);
        MoreButton.Click += (_, _) => ChangeFocusMinutes(5);
        FocusButton.Click += (_, _) => ToggleFocus();
        StopButton.Click += (_, _) => _services.Pomodoro.Reset();
        PreviewMouseWheel += OnWheel;

        _services.Settings.Changed += OnSettingsChanged;
        _services.Pomodoro.Changed += OnPomodoroChanged;
        Closed += (_, _) =>
        {
            _services.Settings.Changed -= OnSettingsChanged;
            _services.Pomodoro.Changed -= OnPomodoroChanged;
        };

        UpdateDnd();
        UpdateFocus();
        Select(_selected);
        Loaded += async (_, _) =>
        {
            _notifications = await _services.Notifications.GetCurrentAsync();
            ShowNotifications();
        };
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => UpdateFocus();

    private void OnPomodoroChanged(object? sender, EventArgs e) => UpdateFocus();

    // ---- Notifications ---------------------------------------------------------------------------------------------

    private void ShowNotifications()
    {
        NotificationList.Children.Clear();
        foreach (var group in _notifications.GroupBy(n => n.AppId, StringComparer.OrdinalIgnoreCase))
        {
            NotificationList.Children.Add(AppGroup(group.Key, group.ToList()));
        }

        bool empty = NotificationList.Children.Count == 0;
        NotificationScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyNotifications.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ClearButton.IsEnabled = !empty;
    }

    /// <summary>One card per app: its name and icon, its newest notifications, and "+N lagi" for the rest.</summary>
    private Border AppGroup(string appId, List<NotificationItem> items)
    {
        var (name, icon) = AppIdentity.Get(appId);
        var card = new StackPanel();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 2) };
        if (icon is not null)
        {
            var image = new Image { Source = icon, Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            DockPanel.SetDock(image, Dock.Left);
            head.Children.Add(image);
        }

        var close = CloseButton($"Tutup notifikasi {name}", () => Dismiss(items));
        DockPanel.SetDock(close, Dock.Right);
        head.Children.Add(close);
        head.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        card.Children.Add(head);

        bool expanded = _expandedApps.Contains(appId);
        var shown = expanded ? items : items.Take(ShownPerApp).ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            card.Children.Add(Entry(shown[i], first: i == 0));
        }

        if (items.Count > shown.Count)
        {
            var more = new Button
            {
                Style = (Style)FindResource("PanelButton"),
                MinWidth = 0,
                Content = $"+{items.Count - shown.Count} notifikasi lagi",
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 8, 0, 0),
            };
            more.Click += (_, _) =>
            {
                _expandedApps.Add(appId);
                ShowNotifications();
            };
            card.Children.Add(more);
        }

        return new Border
        {
            Child = card,
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(0x10, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x0D, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 8, 12),
            Margin = new Thickness(0, 0, 0, 8),
        };
    }

    /// <summary>A notification: time, title, text. Clicking it opens its app; the cross dismisses it here.</summary>
    private Button Entry(NotificationItem item, bool first)
    {
        bool showContent = _services.Settings.Current.NotificationShowContent;
        var body = new StackPanel();
        var titleRow = new DockPanel();
        var time = new TextBlock
        {
            Text = When(item.Time),
            FontSize = 11,
            Foreground = (Brush)FindResource("TextSecondaryBrush"),
            Margin = new Thickness(8, 1, 0, 0),
        };
        DockPanel.SetDock(time, Dock.Right);
        titleRow.Children.Add(time);
        titleRow.Children.Add(new TextBlock
        {
            Text = showContent ? item.Title : "Notifikasi baru",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 36,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        body.Children.Add(titleRow);
        if (showContent && item.Body.Length > 0)
        {
            body.Children.Add(new TextBlock
            {
                Text = item.Body,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxHeight = 48,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = (Brush)FindResource("TextSecondaryBrush"),
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        var row = new Button
        {
            Style = (Style)FindResource("PanelRow"),
            Content = body,
            Margin = new Thickness(-10, first ? 2 : 0, 0, 0),
            ToolTip = "Buka app-nya",
        };
        AutomationProperties.SetName(row, item.Title);
        row.Click += (_, _) =>
        {
            CloseSmooth();
            SystemActions.Open("explorer.exe", @"shell:AppsFolder\" + item.AppId);
        };
        return row;
    }

    private Button CloseButton(string name, Action click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("RoundIconButton"),
            Width = 26,
            Height = 26,
            FontSize = 10,
            Content = Glyph(0xE711),
            ToolTip = "Tutup",
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        button.Click += (_, _) => click();
        return button;
    }

    private static string When(DateTime time)
    {
        var today = DateTime.Today;
        return time.Date == today ? time.ToString("HH.mm", Indonesian)
            : time.Date == today.AddDays(-1) ? "Kemarin"
            : time > today.AddDays(-6) ? time.ToString("dddd", Indonesian)
            : time.ToString("d MMM", Indonesian);
    }

    /// <summary>Hides them from Kedip's list (Windows' own notification centre is left as it is).</summary>
    private void Dismiss(List<NotificationItem> items)
    {
        _services.Settings.Update(s =>
        {
            s.NotificationsDismissed.AddRange(items.Select(i => i.Key));
            if (s.NotificationsDismissed.Count > 300)
            {
                s.NotificationsDismissed.RemoveRange(0, s.NotificationsDismissed.Count - 300);
            }
        });
        _notifications.RemoveAll(n => items.Contains(n));
        ShowNotifications();
    }

    private void ClearAll()
    {
        _services.Settings.Update(s =>
        {
            s.NotificationsClearedBefore = DateTime.UtcNow.ToFileTimeUtc();
            s.NotificationsDismissed.Clear();
        });
        _notifications.Clear();
        ShowNotifications();
    }

    /// <summary>
    /// Windows' Do Not Disturb (the bell with a "z", as in Windows): notifications keep arriving in this list, silently and
    /// without banners. Kedip's own "Jangan ganggu" (the moon in the control panel) is a separate switch.
    /// </summary>
    private void UpdateDnd()
    {
        bool? state = WindowsDoNotDisturb.IsOn();
        bool on = state == true;
        DndButton.IsEnabled = state is not null;
        if (DndButton.Content is not Grid)
        {
            var bell = new Grid { Width = 18, Height = 18 };
            bell.Children.Add(new TextBlock
            {
                Text = Glyph(0xEA8F),
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            bell.Children.Add(new TextBlock
            {
                Text = "z",
                FontFamily = (FontFamily)FindResource("UiFont"),
                FontSize = 8.5,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -3, -3, 0),
            });
            DndButton.Content = bell;
        }

        DndButton.SetResourceReference(ForegroundProperty, on ? "DockAccentBrush" : "TextPrimaryBrush");
        DndButton.ToolTip = on
            ? "Do not disturb nyala: notifikasi masuk tanpa banner dan suara (klik untuk mematikan)"
            : "Do not disturb: bisukan notifikasi (tetap masuk ke daftar ini)";
        AutomationProperties.SetName(DndButton, on ? "Do not disturb nyala" : "Do not disturb mati");
        EmptyText.Text = on ? "Do not disturb nyala, notifikasi masuk tanpa suara" : "Tidak ada notifikasi baru";
    }

    // ---- Calendar --------------------------------------------------------------------------------------------------

    private void ToggleCalendar()
    {
        bool open = CalendarBody.Visibility != Visibility.Visible;
        CalendarBody.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        CollapseButton.Content = Glyph(open ? 0xE70D : 0xE70E);
        AutomationProperties.SetName(CollapseButton, open ? "Ciutkan kalender" : "Tampilkan kalender");
    }

    private void Select(DateTime day)
    {
        _selected = day.Date;
        DateTitle.Text = Capitalize(_selected.ToString("dddd, d MMMM yyyy", Indonesian));
        HijriText.Text = Hijri(_selected);
        ShowMonth(new DateTime(_selected.Year, _selected.Month, 1));
    }

    private void ShowMonth(DateTime month)
    {
        _month = month;
        MonthTitle.Text = Capitalize(month.ToString("MMMM yyyy", Indonesian));
        TodayButton.Visibility = _selected == DateTime.Today && month == new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1)
            ? Visibility.Collapsed : Visibility.Visible;

        DaysGrid.Children.Clear();
        var start = month.AddDays(-(int)month.DayOfWeek);
        for (int i = 0; i < 42; i++)
        {
            DaysGrid.Children.Add(DayCell(start.AddDays(i)));
        }
    }

    /// <summary>Today: filled with the accent. Picked day: an accent ring. Days of other months: faint.</summary>
    private Button DayCell(DateTime day)
    {
        bool today = day == DateTime.Today;
        bool picked = day == _selected && !today;
        var mark = new Ellipse { Width = 32, Height = 32 };
        if (today)
        {
            mark.SetResourceReference(Shape.FillProperty, "DockAccentBrush");
        }
        else if (picked)
        {
            mark.SetResourceReference(Shape.StrokeProperty, "DockAccentBrush");
            mark.StrokeThickness = 1.5;
        }

        var number = new TextBlock
        {
            Text = day.Day.ToString(CultureInfo.InvariantCulture),
            FontSize = 13,
            FontWeight = today ? FontWeights.SemiBold : FontWeights.Normal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = day.Month == _month.Month ? 1 : 0.32,
        };
        number.SetResourceReference(TextBlock.ForegroundProperty, today ? "DockOnAccentBrush" : "TextPrimaryBrush");
        if (!today && day.DayOfWeek == DayOfWeek.Sunday)
        {
            number.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x80)); // Sundays in red, as on Indonesian calendars
        }

        var face = new Grid { Width = 40, Height = 36 };
        face.Children.Add(mark);
        face.Children.Add(number);
        var cell = new Button { Style = (Style)FindResource("CalendarDay"), Content = face, ToolTip = Hijri(day) };
        AutomationProperties.SetName(cell, day.ToString("d MMMM yyyy", Indonesian));
        cell.Click += (_, _) => Select(day);
        return cell;
    }

    private void OnWheel(object sender, System.Windows.Input.MouseWheelEventArgs e)
    {
        if (CalendarBody.IsMouseOver)
        {
            ShowMonth(_month.AddMonths(e.Delta > 0 ? -1 : 1));
            e.Handled = true;
        }
    }

    /// <summary>The Hijri (Umm al-Qura) date in Indonesian; it may differ by a day from Kemenag's sighting.</summary>
    private static string Hijri(DateTime day)
    {
        try
        {
            var calendar = new UmAlQuraCalendar();
            return $"{calendar.GetDayOfMonth(day)} {HijriMonths[calendar.GetMonth(day) - 1]} {calendar.GetYear(day)} H";
        }
        catch (ArgumentOutOfRangeException)
        {
            return "";
        }
    }

    private static string Capitalize(string text) => text.Length > 0 ? char.ToUpper(text[0], Indonesian) + text[1..] : text;

    // ---- Focus -------------------------------------------------------------------------------------------------------

    private void ChangeFocusMinutes(int delta) =>
        _services.Settings.Update(s => s.FocusMinutes = Math.Clamp(s.FocusMinutes + delta, 5, 180));

    private void ToggleFocus()
    {
        var pomodoro = _services.Pomodoro;
        if (pomodoro.IsRunning)
        {
            pomodoro.Pause();
        }
        else
        {
            pomodoro.StartOrResume();
        }
    }

    private void UpdateFocus()
    {
        var pomodoro = _services.Pomodoro;
        bool active = pomodoro.Phase != PomodoroPhase.Idle;
        FocusSetup.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        FocusRunning.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        StopButton.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        FocusMinutesText.Text = $"{_services.Settings.Current.FocusMinutes} mnt";
        FocusPhaseText.Text = pomodoro.Phase switch
        {
            PomodoroPhase.Focus => pomodoro.IsRunning ? "Fokus" : "Fokus · dijeda",
            PomodoroPhase.ShortBreak or PomodoroPhase.LongBreak => pomodoro.IsRunning ? "Istirahat" : "Istirahat · dijeda",
            _ => "",
        };
        var left = pomodoro.Remaining;
        FocusTimeText.Text = left.TotalHours >= 1 ? left.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : left.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
        if (active)
        {
            FocusButton.Content = pomodoro.IsRunning ? "Jeda" : "Lanjut";
        }
        else if (FocusButton.Content is not StackPanel)
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new TextBlock
            {
                Text = Glyph(0xE768),
                FontFamily = (FontFamily)FindResource("IconFont"),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 7, 0),
            });
            label.Children.Add(new TextBlock { Text = "Fokus", VerticalAlignment = VerticalAlignment.Center });
            FocusButton.Content = label;
        }
    }
}
