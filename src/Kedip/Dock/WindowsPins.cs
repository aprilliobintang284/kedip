using System.IO;
using Microsoft.Win32;
using Kedip.Interop;

namespace Kedip.DockBar;

/// <summary>The apps pinned to the Windows taskbar, in its order (Explorer keeps them as shell item ids in the registry).</summary>
internal static class WindowsPins
{
    private const string AppsFolderPrefix = @"::{4234D49B-0245-4DF3-B780-3893943456E1}\";

    /// <summary>Dock keys ("aumid:…" / "path:…") of the taskbar pins; empty when they cannot be read.</summary>
    public static List<string> Read()
    {
        var keys = new List<string>();
        byte[]? blob;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Taskband");
            blob = key?.GetValue("Favorites") as byte[];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            blob = null;
        }

        if (blob is null)
        {
            return keys;
        }

        // Records of [0x00][int32 size][item id list]; a 0xFF byte ends the list.
        int i = 0;
        while (i + 5 <= blob.Length && blob[i] == 0)
        {
            int size = BitConverter.ToInt32(blob, i + 1);
            if (size <= 0 || i + 5 + size > blob.Length)
            {
                break;
            }

            if (KeyFor(blob.AsSpan(i + 5, size)) is { } dockKey && !keys.Contains(dockKey, StringComparer.OrdinalIgnoreCase))
            {
                keys.Add(dockKey);
            }

            i += 5 + size;
        }

        return keys;
    }

    private static string? KeyFor(ReadOnlySpan<byte> idList)
    {
        if (ShellItems.ParsingName(idList) is not { } parsing)
        {
            return null;
        }

        if (parsing.StartsWith(AppsFolderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return "aumid:" + parsing[AppsFolderPrefix.Length..];
        }

        // Store apps are pinned as their AppsFolder item, whose parsing name is the bare app id.
        if (!parsing.Contains(Path.DirectorySeparatorChar) && ShellItems.DisplayName(@"shell:AppsFolder\" + parsing) is not null)
        {
            return "aumid:" + parsing;
        }

        // A shortcut in "User Pinned\TaskBar": the app id it carries when Start knows it, else its target exe.
        if (ShellItems.AppId(parsing) is { } aumid && ShellItems.DisplayName(@"shell:AppsFolder\" + aumid) is not null)
        {
            return "aumid:" + aumid;
        }

        return ShellItems.TargetPath(parsing) is { } target ? "path:" + target : null;
    }
}
