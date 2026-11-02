namespace Kedip.Core;

public enum NotchSizePreset { Small, Medium, Large, Custom }

public enum NotchPosition { Left, Center, Right }

public enum IdleStyle { Eyes, Clock }

/// <summary>When the notch tucks itself into a thin line so the window under it (e.g. browser tabs) can be used.</summary>
public enum TuckMode { Off, Browser, AnyWindow }

/// <summary>What a tucked notch shows while a song plays.</summary>
public enum TuckMusicView { Line, Wave, Mini, MiniWave }

/// <summary>Which parts of Kedip are on screen.</summary>
public enum ShowMode { NotchOnly, DockOnly, Both }

/// <summary>Smart: the dock slides away while a window covers it. Never: always visible.</summary>
public enum DockHide { Smart, Never }

/// <summary>User settings, persisted as JSON. Sizes are in DIPs.</summary>
public sealed class AppSettings
{
    // Appearance
    public NotchSizePreset SizePreset { get; set; } = NotchSizePreset.Medium;
    public double CustomWidth { get; set; } = 166;
    public double CustomHeight { get; set; } = 30;
    public NotchPosition Position { get; set; } = NotchPosition.Center;
    public bool Animations { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;

    /// <summary>Tuck into a thin line over a maximized browser (or any window) whose top sits under the notch.</summary>
    public TuckMode Tuck { get; set; } = TuckMode.Browser;

    /// <summary>While tucked and a song plays: plain line, beat wave, the mini notch, or the mini notch with the wave in its centre.</summary>
    public TuckMusicView TuckMusic { get; set; } = TuckMusicView.Wave;

    /// <summary>While tucked, hovering only reveals the mini notch; a click opens the full panel.</summary>
    public bool TuckClickToOpen { get; set; } = true;

    /// <summary>Don't tuck while a synced lyric line is showing.</summary>
    public bool TuckKeepsLyrics { get; set; } = true;
    // Off by default so development builds never register themselves; enabled when installed.
    public bool StartWithWindows { get; set; }

    // Mascot
    public IdleStyle IdleStyle { get; set; } = IdleStyle.Eyes;
    public bool EyesOnDesktop { get; set; } = true;
    public int EyesIdleSeconds { get; set; } = 15;
    public int DrowsyAfterMinutes { get; set; } = 3;
    public int SleepAfterMinutes { get; set; } = 10;
    public bool EyesFollowCursor { get; set; } = true;

    // Music
    public bool MusicEnabled { get; set; } = true;
    public bool LyricsEnabled { get; set; } = true;
    public bool LyricsInNotch { get; set; } = true;

    /// <summary>When LRCLIB has nothing, also search a second lyrics source.</summary>
    public bool LyricsExtraSources { get; set; } = true;

    // Dock
    public ShowMode Show { get; set; } = ShowMode.NotchOnly;
    public DockHide DockAutoHide { get; set; } = DockHide.Smart;

    /// <summary>"aumid:&lt;AppUserModelID&gt;" or "path:&lt;exe&gt;"; empty list = seed the defaults on first run.</summary>
    public List<string> DockPinned { get; set; } = [];

    public bool DockPinnedSeeded { get; set; }
    public bool DockTimer { get; set; } = true;
    public bool DockWeather { get; set; } = true;
    public bool DockNetSpeed { get; set; } = true;

    /// <summary>Do Not Disturb: no notification/water peeks and no sounds.</summary>
    public bool DoNotDisturb { get; set; }

    // Notifications mirrored from Windows
    public bool NotificationsEnabled { get; set; } = true;
    public bool NotificationShowContent { get; set; } = true;

    /// <summary>App user model ids whose notifications are not shown.</summary>
    public List<string> NotificationMutedApps { get; set; } = [];

    /// <summary>App user model ids that have sent a notification (for the settings list).</summary>
    public List<string> NotificationSeenApps { get; set; } = [];

    // Prayer times (defaults: Kab. Kendal, Jawa Tengah)
    public bool PrayerEnabled { get; set; } = true;
    public string PrayerCity { get; set; } = "Kab. Kendal";
    public double Latitude { get; set; } = -7.026;
    public double Longitude { get; set; } = 110.169;
    public double TimeZoneHours { get; set; } = 7;
    public int IhtiyatMinutes { get; set; } = 2;
    public bool PrayerReminder { get; set; } = true;
    public bool PrayerSound { get; set; } = true;

    // Water
    public bool WaterEnabled { get; set; } = true;
    public int WaterGoalMl { get; set; } = 2000;
    public int GlassMl { get; set; } = 250;
    public int WaterIntervalMinutes { get; set; } = 60;
    public int WaterStartHour { get; set; } = 8;
    public int WaterEndHour { get; set; } = 21;

    // Pomodoro
    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int SessionsBeforeLongBreak { get; set; } = 4;
    public bool TimerSound { get; set; } = true;

    /// <summary>Base (collapsed) notch size in DIPs for the current preset.</summary>
    public (double Width, double Height) GetNotchSize() => SizePreset switch
    {
        NotchSizePreset.Small => (120, 28),
        NotchSizePreset.Large => (200, 32),
        NotchSizePreset.Custom => (Math.Clamp(CustomWidth, 80, 320), Math.Clamp(CustomHeight, 22, 44)),
        _ => (166, 30),
    };
}
