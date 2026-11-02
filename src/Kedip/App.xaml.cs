using System.Windows;
using Kedip.Core;
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

        _notch = new NotchWindow(_services);
        _notch.Show();

        _tray = new TrayIcon(_services, OpenSettings, () => Shutdown());

        StartupRegistration.Sync(settings.Current.StartWithWindows);
        settings.Changed += (_, _) => StartupRegistration.Sync(settings.Current.StartWithWindows);

        // Start-up allocates a lot that is never needed again.
        MemoryTrimmer.Request(TimeSpan.FromSeconds(20));
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
