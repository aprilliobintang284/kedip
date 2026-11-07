<div align="center">

# Kedip

**Notch ringan dan dock kaca untuk Windows 11.**
A lightweight notch and glass dock for Windows 11, built from scratch in C# / WPF on .NET 10.

![Windows 11](https://img.shields.io/badge/Windows-11-0078D4?logo=windows11&logoColor=white)
![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![License: MIT](https://img.shields.io/badge/license-MIT-green)

</div>

Kedip is a personal project made to stay small and quiet: it sits at the edges of the screen, does a handful of
useful things well, and uses almost nothing while idle.

## Features

### Notch (top of the screen)

- Clock, with **mascot eyes** that follow the cursor and get sleepy when you are away
- **Music** with synced lyrics (via LRCLIB)
- Battery and charger status
- Pomodoro timer and water reminder
- File shelf
- Islamic prayer times and weather

### Dock (bottom of the screen)

- Pinned and running apps, with live window previews
- Widgets: weather and network speed
- Live Wi-Fi, volume and battery status
- Calendar and notification panel, quick settings, all-apps panel
- Can replace the Windows taskbar

### Look and feel

- Four colour modes: **dark glass**, **light glass**, **solid dark**, **solid light**. They apply to the notch,
  dock, panels and menus, and switching cross-fades.
- Smooth motion: panels unfold out of the dock, buttons ease in and out, and notifications slide away when cleared.
- Light on resources: no fast timers while idle, and live data is only sampled while the dock is visible.

## Getting started

Requires Windows 11 and the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet build src/Kedip/Kedip.csproj -c Release
```

To install a copy under `%LOCALAPPDATA%\Programs\Kedip` (adds a Start Menu shortcut and restarts a running copy):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install.ps1
```

## Settings

Everything is in the settings window, and is stored in `%APPDATA%\Kedip\settings.json`. The colour mode is under
**Tampilan > Warna**.

If you use **Gantikan taskbar** (replace the taskbar) and Kedip is ever stopped abnormally, the next start brings the
Windows taskbar back.

## Notes

- Builds are unsigned, so Windows Smart App Control may refuse a freshly built copy for a few minutes. Try again later.
- The interface text is mostly Indonesian.
- Design notes live in [DESIGN.md](DESIGN.md).

## License

[MIT](LICENSE) © Aprillio Bintang Perdana
