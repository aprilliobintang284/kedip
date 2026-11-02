namespace Kedip.Media;

/// <summary>Maps media-session app ids (AUMIDs / exe names) to short display names.</summary>
internal static class MediaAppNames
{
    private static readonly (string Hint, string Name)[] Known =
    [
        ("spotify", "Spotify"),
        ("cinhimbnkkaeohfgghhklpknlkffjgod", "YouTube Music"),
        ("youtubemusic", "YouTube Music"),
        ("youtube", "YouTube"),
        ("msedge", "Edge"),
        ("microsoftedge", "Edge"),
        ("chrome", "Chrome"),
        ("firefox", "Firefox"),
        ("opera", "Opera"),
        ("brave", "Brave"),
        ("vivaldi", "Vivaldi"),
        ("zunemusic", "Media Player"),
        ("zunevideo", "Film & TV"),
        ("applemusic", "Apple Music"),
        ("itunes", "iTunes"),
        ("tidal", "TIDAL"),
        ("deezer", "Deezer"),
        ("soundcloud", "SoundCloud"),
        ("amazonmusic", "Amazon Music"),
        ("joox", "JOOX"),
        ("resso", "Resso"),
        ("foobar2000", "foobar2000"),
        ("musicbee", "MusicBee"),
        ("aimp", "AIMP"),
        ("vlc", "VLC"),
        ("potplayer", "PotPlayer"),
        ("winamp", "Winamp"),
    ];

    public static string Get(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "";
        }

        foreach (var (hint, name) in Known)
        {
            if (appId.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }

        // "Publisher.App_hash!Entry" / "C:\...\app.exe" / "app.exe" -> "App".
        string name2 = appId;
        int bang = name2.IndexOf('!');
        if (bang > 0)
        {
            name2 = name2[..bang];
        }

        int underscore = name2.IndexOf('_');
        if (underscore > 0)
        {
            name2 = name2[..underscore];
        }

        int slash = name2.LastIndexOfAny(['\\', '/']);
        if (slash >= 0)
        {
            name2 = name2[(slash + 1)..];
        }

        if (name2.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name2 = name2[..^4];
        }

        int dot = name2.LastIndexOf('.');
        if (dot >= 0 && dot < name2.Length - 1)
        {
            name2 = name2[(dot + 1)..];
        }

        name2 = name2.Trim();
        if (name2.Length == 0)
        {
            return "";
        }

        return char.IsLower(name2[0]) ? char.ToUpperInvariant(name2[0]) + name2[1..] : name2;
    }
}
