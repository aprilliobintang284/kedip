using System.Runtime.InteropServices;
using Kedip.Core;

namespace Kedip.Media;

/// <summary>
/// Current output level of the default playback device, from Windows' own peak meter (the one behind the volume
/// flyout's bar). Nothing is recorded or captured; reading the meter costs next to nothing.
/// </summary>
internal sealed class AudioMeter : IDisposable
{
    private const int ERender = 0;
    private const int EMultimedia = 1;
    private const int ClsctxAll = 23;
    private static readonly TimeSpan RefreshDevice = TimeSpan.FromSeconds(3);

    private IMMDeviceEnumerator? _enumerator;
    private IAudioMeterInformation? _meter;
    private long _deviceCheckedAt = long.MinValue;
    private bool _failedLogged;

    /// <summary>Peak amplitude 0..1 of what is playing right now (0 on any failure). UI thread.</summary>
    public float ReadPeak()
    {
        try
        {
            long now = Environment.TickCount64;
            if (_meter is null || now - _deviceCheckedAt > RefreshDevice.TotalMilliseconds)
            {
                // Re-acquire now and then: the default device changes when headphones are plugged in.
                _deviceCheckedAt = now;
                Acquire();
            }

            if (_meter is not null && _meter.GetPeakValue(out float peak) == 0 && float.IsFinite(peak))
            {
                return Math.Clamp(peak, 0f, 1f);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Release();
            if (!_failedLogged)
            {
                _failedLogged = true;
                Log.Warn($"Audio meter unavailable: {ex.Message}");
            }
        }

        return 0f;
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
        if (_enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device) != 0 || device is null)
        {
            Release();
            return;
        }

        try
        {
            var iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out object instance) == 0)
            {
                Release();
                _meter = (IAudioMeterInformation)instance;
            }
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void Release()
    {
        if (_meter is not null)
        {
            Marshal.ReleaseComObject(_meter);
            _meter = null;
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
    [Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig]
        int GetPeakValue(out float peak);
    }
}
