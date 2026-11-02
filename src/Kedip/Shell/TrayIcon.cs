using System.Windows.Forms;
using Kedip.Core;

namespace Kedip.Shell;

/// <summary>
/// Notification-area icon and menu. See DESIGN.md "Tray".
/// OWNER: F. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(AppServices services, Action openSettings, Action exit)
    {
        _icon = new NotifyIcon
        {
            Text = "Kedip",
            Icon = System.Drawing.SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _icon.ContextMenuStrip.Items.Add("Pengaturan…", null, (_, _) => openSettings());
        _icon.ContextMenuStrip.Items.Add("Keluar", null, (_, _) => exit());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
