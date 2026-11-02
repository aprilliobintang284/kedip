using System.Globalization;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.Json;
using Kedip.Core;

namespace Kedip.DockBar;

internal enum ConnectionKind { None, Wifi, Ethernet }

/// <summary>Network throughput and connection type, sampled on demand (the dock samples every 2 s while visible).</summary>
internal sealed class NetworkInfo
{
    private static readonly string[] VirtualMarkers = ["virtual", "hyper-v", "vmware", "virtualbox", "loopback", "tap-", "wsl", "vpn", "bluetooth"];

    private long _received = -1;
    private long _sent = -1;
    private long _at;

    public double DownBytesPerSecond { get; private set; }
    public double UpBytesPerSecond { get; private set; }
    public ConnectionKind Connection { get; private set; }

    public void Sample()
    {
        long received = 0;
        long sent = 0;
        var kind = ConnectionKind.None;
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up
                    || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel
                    || VirtualMarkers.Any(m => nic.Description.Contains(m, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var stats = nic.GetIPStatistics();
                received += stats.BytesReceived;
                sent += stats.BytesSent;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211)
                {
                    kind = ConnectionKind.Wifi;
                }
                else if (kind == ConnectionKind.None && nic.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet)
                {
                    kind = ConnectionKind.Ethernet;
                }
            }
        }
        catch (NetworkInformationException)
        {
        }

        long now = Environment.TickCount64;
        if (_received >= 0 && now > _at)
        {
            double seconds = (now - _at) / 1000.0;
            DownBytesPerSecond = Math.Max(0, (received - _received) / seconds);
            UpBytesPerSecond = Math.Max(0, (sent - _sent) / seconds);
        }

        _received = received;
        _sent = sent;
        _at = now;
        Connection = kind;
    }

    /// <summary>"5 KB/s", "1,2 MB/s".</summary>
    public static string Format(double bytesPerSecond)
    {
        var id = CultureInfo.GetCultureInfo("id-ID");
        return bytesPerSecond >= 1024 * 1024
            ? (bytesPerSecond / (1024 * 1024)).ToString("0.0", id) + " MB/s"
            : Math.Round(bytesPerSecond / 1024).ToString("0", id) + " KB/s";
    }
}

/// <summary>Master volume of the default playback device (Core Audio). Best-effort; null when unavailable.</summary>
internal sealed class SystemVolume : IDisposable
{
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private long _checkedAt = long.MinValue;

    public (float Level, bool Muted)? Read()
    {
        try
        {
            if (_endpoint is null || Environment.TickCount64 - _checkedAt > 5000)
            {
                _checkedAt = Environment.TickCount64;
                Acquire();
            }

            if (_endpoint is not null && _endpoint.GetMasterVolumeLevelScalar(out float level) == 0 && _endpoint.GetMute(out bool muted) == 0)
            {
                return (level, muted);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Release();
        }

        return null;
    }

    public void SetLevel(float level)
    {
        var context = Guid.Empty;
        _endpoint?.SetMasterVolumeLevelScalar(Math.Clamp(level, 0, 1), ref context);
    }

    public void SetMuted(bool muted)
    {
        var context = Guid.Empty;
        _endpoint?.SetMute(muted, ref context);
    }

    public void Dispose()
    {
        Release();
        if (_enumerator is not null)
        {
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    private void Acquire()
    {
        _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (_enumerator.GetDefaultAudioEndpoint(0, 1, out var device) != 0 || device is null)
        {
            Release();
            return;
        }

        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 23, IntPtr.Zero, out object instance) == 0)
            {
                Release();
                _endpoint = (IAudioEndpointVolume)instance;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void Release()
    {
        if (_endpoint is not null)
        {
            Marshal.ReleaseComObject(_endpoint);
            _endpoint = null;
        }
    }

    [ComImport]
    [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject
    {
    }

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport]
    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}

/// <summary>Current weather from Open-Meteo (free, no key) for the configured city; refreshed every 30 minutes.</summary>
internal sealed class WeatherInfo
{
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kedip/0.1 (personal notch app)");
        return client;
    });

    private DateTime _fetchedAt = DateTime.MinValue;
    private (double Lat, double Lon) _place;
    private bool _busy;

    public double? Temperature { get; private set; }
    public int Code { get; private set; }
    public bool IsDay { get; private set; } = true;

    /// <summary>Raised on the UI thread after a successful update.</summary>
    public event EventHandler? Updated;

    /// <summary>Fetches when stale (30 min) or the place changed. Call on the UI thread.</summary>
    public async void RefreshIfStale(double latitude, double longitude)
    {
        if (_busy || (_place == (latitude, longitude) && DateTime.UtcNow - _fetchedAt < TimeSpan.FromMinutes(30)))
        {
            return;
        }

        _busy = true;
        try
        {
            string url = string.Create(CultureInfo.InvariantCulture,
                $"https://api.open-meteo.com/v1/forecast?latitude={latitude:0.###}&longitude={longitude:0.###}&current=temperature_2m,weather_code,is_day");
            using var response = await Http.Value.GetAsync(url).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync().ConfigureAwait(true));
            var current = doc.RootElement.GetProperty("current");
            Temperature = current.GetProperty("temperature_2m").GetDouble();
            Code = current.GetProperty("weather_code").GetInt32();
            IsDay = current.TryGetProperty("is_day", out var day) && day.GetInt32() == 1;
            _place = (latitude, longitude);
            _fetchedAt = DateTime.UtcNow;
            Updated?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Warn($"Weather update failed: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>A Segoe UI Symbol character for the WMO weather code.</summary>
    public string Glyph => Code switch
    {
        0 => IsDay ? "☀" : "☾",
        1 or 2 => IsDay ? "⛅" : "☁",
        3 or 45 or 48 => "☁",
        >= 51 and <= 67 or >= 80 and <= 82 => "☔",
        >= 71 and <= 77 or 85 or 86 => "❄",
        >= 95 => "⛈",
        _ => "☁",
    };
}
