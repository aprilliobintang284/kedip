using System.Runtime.InteropServices;
using Kedip.Core;

namespace Kedip.DockBar;

/// <summary>A playback device (speakers, headphones, HDMI …).</summary>
internal sealed record AudioOutput(string Id, string Name, bool IsDefault);

/// <summary>
/// Playback devices (Core Audio): list them, make one the default, and connect/disconnect Bluetooth audio devices
/// through their driver's one-shot reconnect property (the way the Windows sound flyout does it).
/// </summary>
internal static class AudioDevices
{
    private static readonly PROPERTYKEY FriendlyName = new(new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), 14);
    private static readonly PROPERTYKEY ContainerId = new(new Guid("8C7ED206-3F8A-4827-B3AB-AE9E1FAEFC6C"), 2);
    private static readonly Guid BtAudioPropertySet = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    public static List<AudioOutput> Outputs()
    {
        var outputs = new List<AudioOutput>();
        if (TryEnumerator() is not { } enumerator)
        {
            return outputs;
        }

        try
        {
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out IMMDevice? fallback) == 0 && fallback is not null)
            {
                defaultId = Id(fallback);
                Marshal.ReleaseComObject(fallback);
            }

            foreach (var device in Devices(enumerator, ERender, DEVICE_STATE_ACTIVE))
            {
                try
                {
                    if (Id(device) is { } id)
                    {
                        outputs.Add(new AudioOutput(id, ReadString(device, FriendlyName) ?? "Perangkat suara", id == defaultId));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(device);
                }
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Warn($"Audio outputs unavailable: {ex.Message}");
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return outputs;
    }

    /// <summary>Makes <paramref name="id"/> the default for every role (console, multimedia, communications).</summary>
    public static bool SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(PolicyConfigClsid)!)!;
            try
            {
                for (int role = 0; role < 3; role++)
                {
                    Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(id, role));
                }

                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(policy);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Warn($"Could not switch the audio output: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Asks the Bluetooth audio driver of the device in <paramref name="container"/> (or named like
    /// <paramref name="name"/>) to connect or disconnect. False when it has no audio endpoint. Blocking: call off the UI thread.
    /// </summary>
    public static bool SetBluetoothConnected(Guid? container, string name, bool connect)
    {
        if (TryEnumerator() is not { } enumerator)
        {
            return false;
        }

        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var endpoint in Devices(enumerator, EAll, DEVICE_STATEMASK_ALL))
            {
                try
                {
                    bool mine = container is { } c && ReadGuid(endpoint, ContainerId) == c
                        || (ReadString(endpoint, FriendlyName)?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false);
                    if (mine && FilterOf(endpoint) is { } filterId && done.Add(filterId))
                    {
                        SendOneShot(enumerator, filterId, connect);
                    }
                }
                catch (Exception ex) when (ex is COMException or InvalidCastException)
                {
                }
                finally
                {
                    Marshal.ReleaseComObject(endpoint);
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return done.Count > 0;
    }

    /// <summary>Id of the KS filter (the driver) an endpoint hangs off, for Bluetooth audio endpoints.</summary>
    private static string? FilterOf(IMMDevice endpoint)
    {
        var iid = typeof(IDeviceTopology).GUID;
        if (endpoint.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object instance) != 0)
        {
            return null;
        }

        var topology = (IDeviceTopology)instance;
        try
        {
            if (topology.GetConnector(0, out IConnector? connector) != 0 || connector is null)
            {
                return null;
            }

            try
            {
                if (connector.GetDeviceIdConnectedTo(out IntPtr id) != 0 || id == IntPtr.Zero)
                {
                    return null;
                }

                string filter = Marshal.PtrToStringUni(id) ?? "";
                Marshal.FreeCoTaskMem(id);
                return filter.Contains("BTH", StringComparison.OrdinalIgnoreCase) ? filter : null;
            }
            finally
            {
                Marshal.ReleaseComObject(connector);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(topology);
        }
    }

    private static void SendOneShot(IMMDeviceEnumerator enumerator, string filterId, bool connect)
    {
        if (enumerator.GetDevice(filterId, out IMMDevice? filter) != 0 || filter is null)
        {
            return;
        }

        try
        {
            var iid = typeof(IKsControl).GUID;
            if (filter.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object instance) != 0)
            {
                return;
            }

            var control = (IKsControl)instance;
            try
            {
                var property = new KSPROPERTY { Set = BtAudioPropertySet, Id = connect ? 0u : 1u, Flags = KSPROPERTY_TYPE_GET };
                int hr = control.KsProperty(ref property, Marshal.SizeOf<KSPROPERTY>(), IntPtr.Zero, 0, out _);
                Log.Info($"Bluetooth audio {(connect ? "reconnect" : "disconnect")}: 0x{hr:X8}");
            }
            finally
            {
                Marshal.ReleaseComObject(control);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(filter);
        }
    }

    private static IEnumerable<IMMDevice> Devices(IMMDeviceEnumerator enumerator, int flow, int states)
    {
        if (enumerator.EnumAudioEndpoints(flow, states, out IMMDeviceCollection? collection) != 0 || collection is null)
        {
            yield break;
        }

        try
        {
            collection.GetCount(out uint count);
            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out IMMDevice? device) == 0 && device is not null)
                {
                    yield return device;
                }
            }
        }
        finally
        {
            Marshal.ReleaseComObject(collection);
        }
    }

    private static string? Id(IMMDevice device)
    {
        if (device.GetId(out IntPtr id) != 0 || id == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(id);
        }
        finally
        {
            Marshal.FreeCoTaskMem(id);
        }
    }

    private static string? ReadString(IMMDevice device, PROPERTYKEY key) =>
        Read(device, key, v => v.vt == VT_LPWSTR && v.pointer != IntPtr.Zero ? Marshal.PtrToStringUni(v.pointer) : null);

    private static Guid? ReadGuid(IMMDevice device, PROPERTYKEY key) =>
        Read<Guid?>(device, key, v => v.vt == VT_CLSID && v.pointer != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(v.pointer) : null);

    private static T? Read<T>(IMMDevice device, PROPERTYKEY key, Func<PROPVARIANT, T?> convert)
    {
        if (device.OpenPropertyStore(STGM_READ, out IPropertyStore? store) != 0 || store is null)
        {
            return default;
        }

        try
        {
            if (store.GetValue(ref key, out PROPVARIANT value) != 0)
            {
                return default;
            }

            try
            {
                return convert(value);
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    /// <summary>
    /// The device enumerator, created by class id: other parts of Kedip wrap the same (shared) COM object in their own
    /// class, and casting that wrapper to a second class fails.
    /// </summary>
    private static IMMDeviceEnumerator? TryEnumerator()
    {
        try
        {
            return (IMMDeviceEnumerator)Activator.CreateInstance(Type.GetTypeFromCLSID(EnumeratorClsid)!)!;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Log.Warn($"Audio devices unavailable: {ex.Message}");
            return null;
        }
    }

    // ---- Interop -------------------------------------------------------------------------------------------------

    private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid PolicyConfigClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");

    private const int ERender = 0;
    private const int EAll = 2;
    private const int EMultimedia = 1;
    private const int DEVICE_STATE_ACTIVE = 0x1;
    private const int DEVICE_STATEMASK_ALL = 0xF;
    private const int CLSCTX_ALL = 23;
    private const int STGM_READ = 0;
    private const ushort VT_LPWSTR = 31;
    private const ushort VT_CLSID = 72;
    private const uint KSPROPERTY_TYPE_GET = 1;

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PROPERTYKEY(Guid fmtid, uint pid)
    {
        public readonly Guid fmtid = fmtid;
        public readonly uint pid = pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort reserved1;
        public ushort reserved2;
        public ushort reserved3;
        public IntPtr pointer;
        public IntPtr extra;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KSPROPERTY
    {
        public Guid Set;
        public uint Id;
        public uint Flags;
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT value);

    [ComImport]
    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection? devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice? endpoint);

        [PreserveSig]
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice? device);
    }

    [ComImport]
    [Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int Item(uint index, out IMMDevice? device);
    }

    [ComImport]
    [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);

        [PreserveSig]
        int OpenPropertyStore(int access, out IPropertyStore? store);

        [PreserveSig]
        int GetId(out IntPtr id);
    }

    [ComImport]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig]
        int GetCount(out uint count);

        [PreserveSig]
        int GetAt(uint index, out PROPERTYKEY key);

        [PreserveSig]
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
    }

    [ComImport]
    [Guid("2A07407E-6497-4A18-9787-32F79BD0D98F")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig]
        int GetConnectorCount(out uint count);

        [PreserveSig]
        int GetConnector(uint index, out IConnector? connector);
    }

    [ComImport]
    [Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig]
        int GetType(out int type);

        [PreserveSig]
        int GetDataFlow(out int flow);

        [PreserveSig]
        int ConnectTo(IntPtr other);

        [PreserveSig]
        int Disconnect();

        [PreserveSig]
        int IsConnected(out int connected);

        [PreserveSig]
        int GetConnectedTo(out IntPtr other);

        [PreserveSig]
        int GetConnectorIdConnectedTo(out IntPtr id);

        [PreserveSig]
        int GetDeviceIdConnectedTo(out IntPtr id);
    }

    [ComImport]
    [Guid("28F54685-06FD-11D2-B27A-00A0C9223196")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig]
        int KsProperty(ref KSPROPERTY property, int propertyLength, IntPtr data, int dataLength, out int returned);
    }

    /// <summary>Undocumented but long-stable interface the Sound control panel uses to pick the default device.</summary>
    [ComImport]
    [Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);

        [PreserveSig]
        int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
    }
}
