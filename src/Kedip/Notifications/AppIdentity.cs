using System.Text.RegularExpressions;
using System.Windows.Media;
using Kedip.Interop;

namespace Kedip.Notifications;

/// <summary>Friendly name and icon of an app by its AppUserModelID, via the shell's AppsFolder (cached).</summary>
internal static partial class AppIdentity
{
    private static readonly Dictionary<string, (string Name, ImageSource? Icon)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();

    /// <summary>Call on an STA thread (the UI thread is fine; lookups are cached after the first one).</summary>
    public static (string Name, ImageSource? Icon) Get(string appId)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(appId, out var hit))
            {
                return hit;
            }
        }

        string parsing = @"shell:AppsFolder\" + appId;
        string? display = ShellItems.DisplayName(parsing);
        // Unregistered ids come back as the raw id; keep the derived name then.
        string name = display is not null && !display.Contains('!') && !display.Contains('_') ? display : FallbackName(appId);
        ImageSource? icon = ShellItems.Image(parsing, 64);

        lock (Gate)
        {
            Cache[appId] = (name, icon);
        }

        return (name, icon);
    }

    /// <summary>"5319275A.WhatsAppDesktop_cv1g1gvanyjgm!App" → "WhatsApp Desktop"; "Windows.SystemToast.LowDisk" → "Low Disk".</summary>
    internal static string FallbackName(string appId)
    {
        string id = appId;
        int bang = id.IndexOf('!');
        if (bang > 0)
        {
            id = id[..bang];
        }

        int underscore = id.LastIndexOf('_');
        if (underscore > 0)
        {
            id = id[..underscore];
        }

        int dot = id.LastIndexOf('.');
        string last = dot >= 0 && dot < id.Length - 1 ? id[(dot + 1)..] : id;
        last = last.Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        string spaced = CamelBoundary().Replace(last, " ");
        return spaced.Length > 0 ? char.ToUpperInvariant(spaced[0]) + spaced[1..] : appId;
    }

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z])")]
    private static partial Regex CamelBoundary();
}
