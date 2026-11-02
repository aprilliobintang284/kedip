using System.Windows;
using Kedip.Core;
using Kedip.DockBar;
using Kedip.Mascot;
using Kedip.Media;
using Kedip.Notifications;
using Kedip.Power;
using Kedip.Prayer;
using Kedip.Productivity;
using Kedip.Shell;

namespace Kedip;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private AppServices? _services;
    private NotchWindow? _notch;
    private TrayIcon? _tray;
    private DockWindow? _dock;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Local\Kedip.SingleInstance", out bool created);
        if (!created)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
            Shutdown();
            return;
        }

        Log.Info($"Kedip {typeof(App).Assembly.GetName().Version} starting");
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Error("Unhandled exception", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        var settings = SettingsStore.Load();
        var peeks = new PeekBus();
        var foreground = new ForegroundWatcher();
        var idle = new IdleWatcher();
        var media = new MediaService();

        _services = new AppServices
        {
            Settings = settings,
            Peeks = peeks,
            Foreground = foreground,
            Idle = idle,
            Mascot = new MascotController(settings, foreground, idle),
            Media = media,
            Lyrics = new LyricsService(settings, media),
            Prayer = new PrayerService(settings, peeks),
            Power = new PowerService(peeks),
            Pomodoro = new PomodoroService(settings, peeks),
            Water = new WaterService(settings, peeks),
            Shelf = new ShelfService(),
            Notifications = new NotificationService(settings, peeks),
        };
        _services.StartAll();

        // Do Not Disturb: no notification/water peeks and no sounds; prayer and power peeks still show (silently).
        peeks.Filter = request => !settings.Current.DoNotDisturb || request.Kind is not (PeekKind.Notification or PeekKind.Water);
        Chime.Muted = settings.Current.DoNotDisturb;
        settings.Changed += (_, _) => Chime.Muted = settings.Current.DoNotDisturb;

        _notch = new NotchWindow(_services);
        _notch.Show();
        ApplyShowMode();
        settings.Changed += (_, _) => ApplyShowMode();

        _tray = new TrayIcon(_services, OpenSettings, () => Shutdown());

        StartupRegistration.Sync(settings.Current.StartWithWindows);
        settings.Changed += (_, _) => StartupRegistration.Sync(settings.Current.StartWithWindows);

        // Start-up allocates a lot that is never needed again.
        MemoryTrimmer.Request(TimeSpan.FromSeconds(20));
    }

    /// <summary>Creates or closes the dock to match "Notch saja / Dock saja / Keduanya" (the notch hides itself).</summary>
    private void ApplyShowMode()
    {
        if (_services is null)
        {
            return;
        }

        bool wantDock = _services.Settings.Current.Show != ShowMode.NotchOnly;
        if (wantDock && _dock is null)
        {
            _dock = new DockWindow(_services);
            _dock.Show();
        }
        else if (!wantDock && _dock is not null)
        {
            _dock.Close();
            _dock = null;
            MemoryTrimmer.Request(TimeSpan.FromSeconds(2));
        }
    }

    private void OpenSettings()
    {
        if (_services is null)
        {
            return;
        }

        if (_settingsWindow is not null)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_services);
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            // The settings window is by far the heaviest thing Kedip shows; hand its memory back once it is gone.
            MemoryTrimmer.Request(TimeSpan.FromSeconds(2));
        };
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _dock?.Close();
        _settingsWindow?.Close();
        _notch?.Close();
        if (_services is not null)
        {
            _services.Settings.SaveNow();
            _services.Dispose();
        }

        if (_singleInstance is not null)
        {
            _singleInstance.ReleaseMutex();
            _singleInstance.Dispose();
        }

        Log.Info("Kedip exited");
        base.OnExit(e);
    }
}
