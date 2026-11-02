using System.Windows;
using System.Windows.Controls;
using Kedip.Core;
using static Kedip.DockBar.WinKeys;

namespace Kedip.DockBar;

/// <summary>Kedip's dark menus: the quick menu on the Start button (Win+X style), power options, and app actions.</summary>
internal static class QuickMenu
{
    /// <summary>Right-click menu of the dock's Start button.</summary>
    public static ContextMenu Start(FrameworkElement owner, Action opened, Action closed)
    {
        var menu = Create(owner);
        menu.Opened += (_, _) => opened();
        menu.Closed += (_, _) => closed();

        Add(menu, owner, "Aplikasi terinstal", 0xE71D, () => SystemActions.Open("ms-settings:appsfeatures"));
        Add(menu, owner, "Opsi daya", 0xE7E8, () => SystemActions.Open("ms-settings:powersleep"));
        Add(menu, owner, "Event Viewer", 0xE7BA, () => SystemActions.Open("eventvwr.msc"));
        Add(menu, owner, "Sistem", 0xE770, () => SystemActions.Open("ms-settings:about"));
        Add(menu, owner, "Device Manager", 0xE772, () => SystemActions.Open("devmgmt.msc"));
        Add(menu, owner, "Koneksi jaringan", 0xE839, () => SystemActions.Open("ms-settings:network"));
        Add(menu, owner, "Manajemen disk", 0xEDA2, () => SystemActions.Open("diskmgmt.msc"));
        Add(menu, owner, "Manajemen komputer", 0xE7F4, () => SystemActions.Open("compmgmt.msc"));
        Separate(menu, owner);
        Add(menu, owner, "Terminal", 0xE756, () => SystemActions.Open("wt.exe"));
        Add(menu, owner, "Terminal (Admin)", 0xE7EF, () => SystemActions.Open("wt.exe", admin: true));
        Add(menu, owner, "Task Manager", 0xE9D9, () => SystemActions.Open("taskmgr.exe"));
        Add(menu, owner, "Pengaturan", 0xE713, () => SystemActions.Open("ms-settings:"));
        Add(menu, owner, "File Explorer", 0xEC50, () => SystemActions.Open("explorer.exe"));
        Add(menu, owner, "Cari", 0xE721, () => Win(S));
        Add(menu, owner, "Jalankan", 0xE8A7, () => Win(R));
        Separate(menu, owner);
        AddPower(menu, owner);
        Separate(menu, owner);
        Add(menu, owner, "Desktop", 0xE80F, () => Win(D));
        return menu;
    }

    /// <summary>Lock / sleep / sign out / restart / shut down.</summary>
    public static ContextMenu Power(FrameworkElement owner)
    {
        var menu = Create(owner);
        AddPower(menu, owner);
        return menu;
    }

    /// <summary>Open, and pin to / unpin from the dock, for an AppsFolder entry.</summary>
    public static ContextMenu ForApp(FrameworkElement owner, AppServices services, string appId, Action open)
    {
        // The apps panel is rebuilt every time it opens, so the pinned state read here is current.
        var menu = Create(owner);
        string key = "aumid:" + appId;
        bool pinned = services.Settings.Current.DockPinned.Contains(key, StringComparer.OrdinalIgnoreCase);
        Add(menu, owner, "Buka", 0xE8A7, open);
        Add(menu, owner, pinned ? "Lepas dari dock" : "Sematkan di dock", pinned ? 0xE77A : 0xE718, () =>
            services.Settings.Update(s =>
            {
                s.DockPinned.RemoveAll(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                if (!pinned)
                {
                    s.DockPinned.Add(key);
                }
            }));
        return menu;
    }

    private static void AddPower(ContextMenu menu, FrameworkElement owner)
    {
        Add(menu, owner, "Kunci", 0xE72E, SystemActions.Lock);
        Add(menu, owner, "Tidur", 0xE708, SystemActions.Sleep);
        Add(menu, owner, "Keluar", 0xE748, SystemActions.SignOut);
        Add(menu, owner, "Mulai ulang", 0xE72C, SystemActions.Restart);
        Add(menu, owner, "Matikan", 0xE7E8, SystemActions.ShutDown);
    }

    private static ContextMenu Create(FrameworkElement owner) => new() { Style = (Style)owner.FindResource(typeof(ContextMenu)) };

    private static void Add(ContextMenu menu, FrameworkElement owner, string text, int glyph, Action action)
    {
        var item = new MenuItem { Header = text, Tag = Glyph(glyph), Style = (Style)owner.FindResource(typeof(MenuItem)) };
        item.Click += (_, _) => action();
        menu.Items.Add(item);
    }

    private static void Separate(ContextMenu menu, FrameworkElement owner) =>
        menu.Items.Add(new Separator { Style = (Style)owner.FindResource(typeof(Separator)) });
}
