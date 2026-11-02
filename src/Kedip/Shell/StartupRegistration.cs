using Kedip.Core;
using Microsoft.Win32;

namespace Kedip.Shell;

/// <summary>HKCU\Software\Microsoft\Windows\CurrentVersion\Run value "Kedip".</summary>
public static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Kedip";

    // Settings.Changed fires for every setting; only touch the registry when the wanted state changes.
    private static bool? _lastRequested;

    /// <summary>Adds or removes the Run entry so it matches <paramref name="enabled"/>. Idempotent.</summary>
    public static void Sync(bool enabled)
    {
        if (_lastRequested == enabled)
        {
            return;
        }

        _lastRequested = enabled;
        try
        {
            if (enabled)
            {
                Register();
            }
            else
            {
                Unregister();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not {(enabled ? "add" : "remove")} the startup entry", ex);
        }
    }

    private static void Register()
    {
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Warn("Startup entry not written: the process path is unknown");
            return;
        }

        string command = "\"" + exe + "\"";
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
        if (key.GetValue(ValueName) is string existing && string.Equals(existing, command, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        key.SetValue(ValueName, command, RegistryValueKind.String);
        Log.Info($"Startup entry set to {command}");
    }

    private static void Unregister()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(ValueName) is null)
        {
            return;
        }

        key.DeleteValue(ValueName, throwOnMissingValue: false);
        Log.Info("Startup entry removed");
    }
}
