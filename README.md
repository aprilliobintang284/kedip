# Kedip

A lightweight notch and glass dock for Windows 11, written from scratch in C# / WPF (.NET 10).
*Notch ringan dan dock kaca untuk Windows 11.*

- **Notch** at the top of the screen: clock, mascot eyes that follow the cursor, music with synced lyrics (LRCLIB),
  battery/charger, pomodoro timer, water reminder, file shelf, Islamic prayer times and weather.
- **Dock** at the bottom: pinned and running apps with live window previews, widgets (weather, network speed),
  Wi-Fi / volume / battery status, a calendar + notification panel, quick settings and an all-apps panel.
  It can replace the Windows taskbar.
- **Themes** (Settings > Dock > Warna): dark glass, light glass, solid dark, solid light. Switching cross-fades.
- Smooth motion: panels unfold out of the dock, buttons ease in and out, notifications slide away when cleared.
- Light on resources: no fast timers while idle, live data only sampled while the dock is visible.

Kedip is *inspired by* Nusa and Intip; no code, artwork or text is copied from either. See [DESIGN.md](DESIGN.md).

## Build and run

Requires Windows 11 and the .NET 10 SDK.

```powershell
dotnet build src/Kedip/Kedip.csproj -c Release
```

To install a copy under `%LOCALAPPDATA%\Programs\Kedip` (Start Menu shortcut, restarts a running copy):

```powershell
powershell -ExecutionPolicy Bypass -File scripts/install.ps1
```

Settings live in `%APPDATA%\Kedip\settings.json`. If you use "Gantikan taskbar" (replace the taskbar) and Kedip is
ever stopped abnormally, the next start brings the Windows taskbar back.

## Notes

- Builds are unsigned, so Windows Smart App Control may refuse a freshly built copy for a few minutes; retry later.
- The UI text is mostly Indonesian.

## License

[MIT](LICENSE)
