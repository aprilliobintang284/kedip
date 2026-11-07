# Kedip — design spec (v0.1)

A lightweight Windows notch at the top of the screen, written from scratch in C# / WPF (.NET 10).
All visuals (notch shape, eyes, icons) are drawn by our own code.

Target machine: Windows 11, single 1920×1080 monitor at 125% scale (120 DPI), laptop (battery present).

## Goals

1. **Light**: idle RAM ≲ 60 MB private working set, ~0% CPU when nothing moves. No timers faster than 1 Hz while idle;
   faster timers only while something is visibly animating (eyes following the cursor, lyrics, countdown).
2. **Mascot eyes** with the behaviour the user asked for:
   - Desktop / shell is foreground (`Progman`, `WorkerW`, `Shell_TrayWnd`, `Shell_SecondaryTrayWnd`, no foreground window,
     or one of Kedip's own windows) → eyes appear **immediately** and stay while the mouse moves on the desktop (they look at the cursor).
   - An app is foreground → clock; eyes appear after `EyesIdleSeconds` (default 15) with no mouse/keyboard input.
   - Long idle → drowsy (`DrowsyAfterMinutes`, default 3) → asleep (`SleepAfterMinutes`, default 10).
   - Input returns → short "hello" (wake) animation; then hidden again if an app is foreground.
3. **Adjustable notch size** (default 166×30 DIP). Presets Small 120×28, Medium 166×30, Large 200×32, or Custom.
4. Features: clock, music (Windows media session) + **synced lyrics** (LRCLIB), battery/charger, pomodoro timer,
   water reminder, file shelf, **Islamic prayer times** (Kemenag RI parameters), tray menu, settings window, start with Windows.

## Process / threading model

- One process, one UI (Dispatcher) thread. Every service is constructed and started on the UI thread.
- **All public service events are raised on the UI thread.** Background work (HTTP, WinRT callbacks) must marshal back with
  `Application.Current.Dispatcher.InvokeAsync(...)`.
- Use `DispatcherTimer` for periodic work. Stop timers when they have nothing to do.
- Services never reference views. Views/shell subscribe to service events.

## Solution layout

```
Kedip.sln
src/Kedip/Kedip.csproj           net10.0-windows10.0.19041.0, WPF + WinForms (NotifyIcon only)
src/Kedip/app.manifest           PerMonitorV2 DPI awareness
src/Kedip/App.xaml(.cs)          [core] composition root, single instance, shared resources (brushes, fonts)
src/Kedip/Core/                  [core] AppPaths, AppSettings, SettingsStore, PeekBus, AppServices, Log
src/Kedip/Interop/NativeMethods.cs [core] shared P/Invoke (add new imports here only if shared; private ones may live in the owning file)
src/Kedip/Mascot/                [B] ForegroundWatcher, IdleWatcher, MascotController, EyesControl
src/Kedip/Media/                 [C] MediaService, LyricsService, LrcParser
src/Kedip/Prayer/                [D] PrayerCalculator, PrayerService, Cities
src/Kedip/Power/                 [E] PowerService
src/Kedip/Productivity/          [E] PomodoroService, WaterService, ShelfService, Chime
src/Kedip/Views/Panels/          MusicPanel [C], PrayerPanel [D], TimerPanel [E], ShelfPanel [E]
src/Kedip/Shell/                 [A] NotchWindow, NotchShape  ·  [F] TrayIcon, SettingsWindow, StartupRegistration
src/Kedip/Assets/kedip.ico       [F] generated icon (our own drawing)
```

Letters are the owner of each file for parallel implementation. **Only edit files you own.** Public APIs in the scaffold are a
contract: you may add members, but do not rename/remove existing public members or change their signatures.

## Shared resources (App.xaml)

Brushes: `NotchBrush` (#000), `TextPrimaryBrush` (#FFFFFF), `TextSecondaryBrush` (#99FFFFFF), `SurfaceBrush` (#1AFFFFFF),
`AccentBrush` (#34C759 green), `WarnBrush` (#FF9F0A), `DangerBrush` (#FF453A).
Fonts: `UiFont` = "Segoe UI Variable Text, Segoe UI", `IconFont` = "Segoe Fluent Icons, Segoe MDL2 Assets".
Panels must look good on a black background, compact (expanded panel area is **440×150 DIP**).

## Notch shell (A)

- `NotchWindow`: borderless, `AllowsTransparency`, transparent background, `Topmost`, not in taskbar, `WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE`
  (never steals focus). Fixed window size 600×260 DIP anchored at the top edge of the primary monitor, horizontally per
  `Position` (Left = 24 DIP margin, Center, Right = 24 DIP margin). Fully transparent pixels are click-through (layered window).
- The visible "island": black shape flush with the top edge, bottom corners rounded (radius ≈ 0.45 × height), small inverse
  "ear" curves at the two top corners (radius 8 DIP) so it melts into the screen edge. Size animates smoothly (≈ 250 ms, ease-out; no
  animation when `Animations` = false).
- States (priority high → low): **Hidden** (fullscreen app foreground && `HideInFullscreen`) › **Expanded** (mouse hovering ≥ 150 ms,
  or a file drag enters) › **Peek** (a `PeekRequest` is showing) › **Collapsed**.
- Collapsed content (centre): lyric line (music playing && synced lyrics && `LyricsInNotch`) › eyes (`MascotController.IsVisible`) › clock `HH:mm`.
  Wings: left = album art (music) or charging bolt (charging); right = equalizer bars (playing) or battery % when charging.
  Wings widen the island beyond the base size; base size = settings preset.
- Peek: island widens (≤ 360 DIP) and shows glyph + title (+ subtitle) for `Duration` (default 3 s); queued by priority.
- Expanded: island grows to 460×190 DIP; a tab row (glyph buttons) switches panels: Musik, Sholat, Timer, Shelf (only enabled features).
  Mouse leaves for ≥ 400 ms → collapse. Dropping files anywhere on the island → `ShelfService.Add` and switch to Shelf tab.
- Re-assert topmost when the foreground changes. Hide/show on fullscreen changes.

## Mascot (B)

- `ForegroundWatcher`: `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, …, WINEVENT_OUTOFCONTEXT)` (+ minimize start/end) on the UI thread; keep the
  delegate alive. `IsDesktopForeground` per the class list above; `IsFullscreenAppForeground` = foreground window rect covers its
  monitor rect and it is not desktop/shell/own. Raise `Changed` only when a value changes.
- `IdleWatcher`: `GetLastInputInfo` helpers (`IdleTime`, `LastInputTick`).
- `MascotController`: adaptive `DispatcherTimer` (sleep until the next threshold; ≤ 4 Hz only while it must notice input to wake/hide).
  Exposes `Mood` (Hidden/Awake/Drowsy/Asleep), `IsVisible`, `LookX/LookY` (−1..1, towards cursor, updated ≤ 20 Hz only while visible,
  awake and `EyesFollowCursor`), events `Changed`, `LookChanged`, `Woke`.
- `EyesControl`: `FrameworkElement` that draws two white rounded eyes scaled to its height; dependency properties `Mood`, `LookX`, `LookY`;
  random blink (2.5–6 s) while awake; drowsy = half-closed with slow blinks; asleep = closed lines; `PlayWake()` = quick happy pop.
  No continuous rendering when nothing changes.

## Media & lyrics (C)

- `MediaService` uses `Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager` (current session). Exposes `Current`
  (`MediaInfo`: title, artist, album, source app id, thumbnail `ImageSource?`, duration, `IsPlaying`), live extrapolated `Position`,
  commands (play/pause, next, previous, seek), events `TrackChanged`, `PlaybackChanged`.
- `LyricsService`: on track change query `https://lrclib.net/api/get` (artist, track, album, duration in s; fallback `/api/search`),
  User-Agent `Kedip/0.1 (personal notch app)`, cache per track under `AppPaths.LyricsCacheDir`, parse LRC (`LrcParser`), expose `Lines`,
  `CurrentIndex`, `CurrentLine`, events `LyricsChanged`, `LineChanged`. Line timer is scheduled to the next line time (not polling).
- `MusicPanel`: art, title/artist, prev/play-pause/next, progress bar, current + next lyric lines.

## Prayer (D)

- `PrayerCalculator.Compute(date, lat, lon, tzHours, ihtiyat)` → Imsak, Subuh, Terbit, Dzuhur, Ashar, Maghrib, Isya.
  Kemenag RI: Subuh sun altitude −20°, Isya −18°, Ashar shadow factor 1 (Syafi'i), Maghrib at sunset, ihtiyat (default 2 min) added to
  prayers, Imsak = Subuh − 10 min, Terbit = sunrise − ihtiyat. Accuracy target ±2 min vs. official Kemenag schedule.
- `PrayerService`: today's list, `Next`, `Updated` event, recompute at midnight/settings change; at each of the five prayer times raise a Peek
  (+ chime if `PrayerSound`). `Cities` = small preset list of Indonesian cities (name, lat, lon, tz).
- `PrayerPanel`: next prayer + countdown, today's times (highlight next).

## Power, timer, water, shelf (E)

- `PowerService`: percent, `IsCharging` (AC online), `HasBattery`; `SystemEvents.PowerModeChanged` + 60 s refresh; Peek on plug/unplug and at 20 %/10 % while discharging.
- `PomodoroService`: Focus/ShortBreak/LongBreak phases from settings, start/pause/resume/reset/skip, 1 Hz tick **only while running**, Peek + chime on transitions.
- `WaterService`: today's ml/glasses vs goal, add/undo glass, persisted per day (`water.json`), reminder Peek every `WaterIntervalMinutes` within the active hours while below goal.
- `ShelfService`: references to dropped files (paths, not copies), persisted in `shelf.json`, prune missing files, shell icons; `ShelfPanel` lists items, drag out (FileDrop), remove/clear, open on double-click.
- `Chime`: tiny generated sine "ding" played via `System.Media.SoundPlayer` (no asset files).

## Tray, settings, startup (F)

- `TrayIcon` (WinForms `NotifyIcon`): Pengaturan…, Mata maskot ✓, Ukuran notch ▸ (Kecil/Sedang/Besar), Mulai bersama Windows ✓, Tentang Kedip, Keluar. Double-click = settings.
- `SettingsWindow`: normal dark window, sections Tampilan, Maskot, Musik, Sholat, Minum, Timer, Umum; every change applies live via `SettingsStore.Update`.
- `StartupRegistration`: HKCU `…\Run` value `Kedip` = quoted exe path; kept in sync with `StartWithWindows`.
- `Assets/kedip.ico`: our own icon (black notch with two white eyes), sizes 16–256.

## Persistence

`%APPDATA%\Kedip\settings.json` (System.Text.Json, enums as strings, indented), `water.json`, `shelf.json`, `kedip.log`;
`%LOCALAPPDATA%\Kedip\lyrics\` cache.
