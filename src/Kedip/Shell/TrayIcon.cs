using System.Windows;
using System.Windows.Threading;
using Kedip.Core;
using Kedip.Shell.Settings;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace Kedip.Shell;

/// <summary>
/// Notification-area icon and its dark context menu. See DESIGN.md "Tray".
/// Menu events arrive on the UI thread (WinForms shares the WPF STA message loop).
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private static readonly Uri IconUri = new("pack://application:,,,/Assets/kedip.ico");

    private readonly SettingsStore _settings;
    private readonly Action _openSettings;
    private readonly Action _exit;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Forms.ToolStripMenuItem _eyesItem;
    private readonly Forms.ToolStripMenuItem _startupItem;
    private readonly (Forms.ToolStripMenuItem Item, NotchSizePreset Preset)[] _sizeItems;
    private readonly Drawing.Icon? _ownedIcon;
    private readonly Drawing.Font _font;
    private readonly Drawing.Font _boldFont;
    private AboutWindow? _about;
    private bool _disposed;

    public TrayIcon(AppServices services, Action openSettings, Action exit)
    {
        _settings = services.Settings;
        _openSettings = openSettings;
        _exit = exit;

        _font = CreateMenuFont();
        _boldFont = new Drawing.Font(_font, Drawing.FontStyle.Bold);
        var renderer = new DarkMenuRenderer();

        _menu = new Forms.ContextMenuStrip
        {
            Renderer = renderer,
            Font = _font,
            ShowImageMargin = true,
            ShowCheckMargin = false,
            Padding = new Forms.Padding(0, 4, 0, 4),
            BackColor = DarkMenuRenderer.Background,
            ForeColor = DarkMenuRenderer.Text,
        };
        _menu.HandleCreated += (_, _) => DwmStyling.RoundPopup(_menu.Handle);
        _menu.Opening += (_, _) => SyncChecks();

        var settingsItem = CreateItem("Pengaturan…", () => Defer(_openSettings));
        settingsItem.Font = _boldFont;
        _eyesItem = CreateItem("Mata maskot", ToggleEyes);
        _startupItem = CreateItem("Mulai bersama Windows", ToggleStartup);

        _sizeItems =
        [
            (CreateItem("Kecil", () => SetSize(NotchSizePreset.Small)), NotchSizePreset.Small),
            (CreateItem("Sedang", () => SetSize(NotchSizePreset.Medium)), NotchSizePreset.Medium),
            (CreateItem("Besar", () => SetSize(NotchSizePreset.Large)), NotchSizePreset.Large),
        ];
        var sizeMenu = CreateItem("Ukuran notch", null);
        foreach (var (item, _) in _sizeItems)
        {
            item.Tag = DarkMenuRenderer.RadioTag;
            sizeMenu.DropDownItems.Add(item);
        }

        if (sizeMenu.DropDown is Forms.ToolStripDropDownMenu sizeDropDown)
        {
            sizeDropDown.Renderer = renderer;
            sizeDropDown.Font = _font;
            sizeDropDown.ShowImageMargin = true;
            sizeDropDown.ShowCheckMargin = false;
            sizeDropDown.Padding = new Forms.Padding(0, 4, 0, 4);
            sizeDropDown.BackColor = DarkMenuRenderer.Background;
            sizeDropDown.HandleCreated += (_, _) => DwmStyling.RoundPopup(sizeDropDown.Handle);
        }

        _menu.Items.AddRange(
        [
            settingsItem,
            CreateSeparator(),
            _eyesItem,
            sizeMenu,
            _startupItem,
            CreateSeparator(),
            CreateItem("Tentang Kedip", () => Defer(ShowAbout)),
            CreateItem("Keluar", () => Defer(_exit)),
        ]);

        _ownedIcon = LoadIcon();
        _icon = new Forms.NotifyIcon
        {
            Text = "Kedip",
            Icon = _ownedIcon ?? Drawing.SystemIcons.Application,
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                Defer(_openSettings);
            }
        };

        SyncChecks();
        _settings.Changed += OnSettingsChanged;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _settings.Changed -= OnSettingsChanged;
        _about?.Close();
        _icon.Visible = false;
        _icon.ContextMenuStrip = null;
        _icon.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
        _boldFont.Dispose();
        _font.Dispose();
    }

    private void OnSettingsChanged(object? sender, EventArgs e) => SyncChecks();

    private void SyncChecks()
    {
        if (_disposed)
        {
            return;
        }

        var s = _settings.Current;
        _eyesItem.Checked = s.IdleStyle == IdleStyle.Eyes;
        _startupItem.Checked = s.StartWithWindows;
        foreach (var (item, preset) in _sizeItems)
        {
            item.Checked = s.SizePreset == preset;
        }
    }

    private void ToggleEyes() => Update(s => s.IdleStyle = s.IdleStyle == IdleStyle.Eyes ? IdleStyle.Clock : IdleStyle.Eyes);

    private void ToggleStartup() => Update(s => s.StartWithWindows = !s.StartWithWindows);

    private void SetSize(NotchSizePreset preset) => Update(s => s.SizePreset = preset);

    private void Update(Action<AppSettings> mutate)
    {
        try
        {
            _settings.Update(mutate);
        }
        catch (Exception ex)
        {
            Log.Error("Tray: could not apply a setting", ex);
        }
    }

    private void ShowAbout()
    {
        if (_disposed)
        {
            return;
        }

        if (_about is not null)
        {
            if (_about.WindowState == WindowState.Minimized)
            {
                _about.WindowState = WindowState.Normal;
            }

            _about.Activate();
            return;
        }

        _about = new AboutWindow();
        _about.Closed += (_, _) => _about = null;
        _about.Show();
        _about.Activate();
    }

    /// <summary>Runs <paramref name="action"/> after the menu has closed, so windows get focus and disposal is safe.</summary>
    private void Defer(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.InvokeAsync(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Error("Tray action failed", ex);
            }
        }, DispatcherPriority.Background);
    }

    private Forms.ToolStripMenuItem CreateItem(string text, Action? onClick)
    {
        var item = new Forms.ToolStripMenuItem(text)
        {
            Font = _font,
            ForeColor = DarkMenuRenderer.Text,
            Padding = new Forms.Padding(2, 4, 12, 4),
        };
        if (onClick is not null)
        {
            item.Click += (_, _) => onClick();
        }

        return item;
    }

    private static Forms.ToolStripSeparator CreateSeparator() => new() { Margin = new Forms.Padding(0, 2, 0, 2) };

    private static Drawing.Font CreateMenuFont()
    {
        foreach (string family in new[] { "Segoe UI Variable Text", "Segoe UI" })
        {
            var font = new Drawing.Font(family, 9f, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);
            if (string.Equals(font.Name, family, StringComparison.OrdinalIgnoreCase))
            {
                return font;
            }

            font.Dispose();
        }

        return new Drawing.Font(Drawing.SystemFonts.MenuFont ?? Drawing.SystemFonts.DefaultFont, Drawing.FontStyle.Regular);
    }

    private static Drawing.Icon? LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(IconUri);
            if (info is null)
            {
                return null;
            }

            using var stream = info.Stream;
            return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            Log.Error("Could not load the tray icon", ex);
            return null;
        }
    }
}
