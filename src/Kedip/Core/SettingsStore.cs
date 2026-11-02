using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace Kedip.Core;

/// <summary>Owns the live <see cref="AppSettings"/> instance and persists it (debounced).</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly DispatcherTimer _saveTimer;

    private SettingsStore(AppSettings current)
    {
        Current = current;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => SaveNow();
    }

    public AppSettings Current { get; }

    /// <summary>Raised on the UI thread after every <see cref="Update"/>.</summary>
    public event EventHandler? Changed;

    public static SettingsStore Load()
    {
        AppSettings? settings = null;
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), JsonOptions);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Settings file is unreadable; using defaults", ex);
            TryBackupCorruptFile();
        }

        var store = new SettingsStore(settings ?? new AppSettings());
        if (settings is null)
        {
            store.SaveNow();
        }

        return store;
    }

    /// <summary>Mutates settings, notifies listeners and schedules a save.</summary>
    public void Update(Action<AppSettings> mutate)
    {
        mutate(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            string tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Could not save settings", ex);
        }
    }

    private static void TryBackupCorruptFile()
    {
        try
        {
            File.Copy(AppPaths.SettingsFile, Path.ChangeExtension(AppPaths.SettingsFile, ".bad.json"), overwrite: true);
        }
        catch (IOException)
        {
        }
    }
}
