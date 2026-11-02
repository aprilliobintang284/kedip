using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Kedip.Core;
using Microsoft.Win32;

namespace Kedip.Power;

/// <summary>
/// Battery level and charger state; peeks on plug/unplug and low battery.
/// Refreshed on <see cref="SystemEvents.PowerModeChanged"/> plus a 60 s safety timer. No peek at startup.
/// </summary>
public sealed class PowerService : IAppService
{
    private const string ChargingAccent = "#34C759";
    private const string UnpluggedAccent = "#FF9F0A";
    private const string LowAccent = "#FF453A";
    private static readonly string BoltGlyph = ((char)0xE945).ToString();
    private const int LowThreshold = 20;
    private const int CriticalThreshold = 10;

    private readonly PeekBus _peeks;
    private readonly DispatcherTimer _refreshTimer;
    private bool _started;
    private bool _warnedLow;
    private bool _warnedCritical;

    public PowerService(PeekBus peeks)
    {
        _peeks = peeks;
        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(60) };
        _refreshTimer.Tick += (_, _) => Refresh();
    }

    public bool HasBattery { get; private set; }

    /// <summary>AC power connected.</summary>
    public bool IsCharging { get; private set; }

    /// <summary>0..100.</summary>
    public int Percent { get; private set; } = 100;

    /// <summary>Raised on the UI thread when any property changes.</summary>
    public event EventHandler? Changed;

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        if (TryRead(out var status))
        {
            Apply(status, initial: true);
        }

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        _refreshTimer.Start();
    }

    /// <summary>Re-reads the power status now (UI thread).</summary>
    public void Refresh()
    {
        if (_started && TryRead(out var status))
        {
            Apply(status, initial: false);
        }
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _refreshTimer.Stop();
    }

    // Raised on the SystemEvents thread.
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            return;
        }

        Application.Current?.Dispatcher.InvokeAsync(Refresh, DispatcherPriority.Background);
    }

    private void Apply(PowerReading reading, bool initial)
    {
        bool wasCharging = IsCharging;
        int previousPercent = Percent;
        bool changed = reading.HasBattery != HasBattery || reading.IsCharging != IsCharging || reading.Percent != Percent;

        HasBattery = reading.HasBattery;
        IsCharging = reading.IsCharging;
        Percent = reading.Percent;

        if (initial)
        {
            // A cycle that is already below a threshold at startup does not warn for it again.
            _warnedLow = !IsCharging && Percent <= LowThreshold;
            _warnedCritical = !IsCharging && Percent <= CriticalThreshold;
        }
        else if (HasBattery)
        {
            RaisePeeks(wasCharging, previousPercent);
        }

        if (changed)
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RaisePeeks(bool wasCharging, int previousPercent)
    {
        if (IsCharging)
        {
            _warnedLow = false;
            _warnedCritical = false;
            if (!wasCharging)
            {
                _peeks.Show(new PeekRequest(PeekKind.Charging, "Mengisi daya", $"{Percent}%", BoltGlyph,
                    Priority: 5, AccentHex: ChargingAccent));
            }

            return;
        }

        if (wasCharging)
        {
            // A new discharge cycle starts here; thresholds already passed are not "crossed".
            _warnedLow = Percent <= LowThreshold;
            _warnedCritical = Percent <= CriticalThreshold;
            _peeks.Show(new PeekRequest(PeekKind.Battery, "Charger dicabut", $"{Percent}%", BatteryGlyph(Percent),
                Priority: 5, AccentHex: Percent <= LowThreshold ? LowAccent : UnpluggedAccent));
            return;
        }

        if (Percent >= previousPercent)
        {
            return;
        }

        bool critical = !_warnedCritical && Percent <= CriticalThreshold;
        bool low = !_warnedLow && Percent <= LowThreshold;
        if (!critical && !low)
        {
            return;
        }

        _warnedLow = true;
        _warnedCritical |= critical;
        string subtitle = critical ? $"Sisa {Percent}%, segera colok charger" : $"Sisa {Percent}%, colok charger ya";
        _peeks.Show(new PeekRequest(PeekKind.Battery, "Baterai lemah", subtitle, BatteryGlyph(Percent),
            TimeSpan.FromSeconds(critical ? 5 : 4), Priority: critical ? 8 : 6, AccentHex: LowAccent));
    }

    /// <summary>Horizontal battery outline with a fill level (Segoe Fluent Icons MobBattery0..10).</summary>
    public static string BatteryGlyph(int percent) =>
        ((char)(0xEBA0 + Math.Clamp((percent + 5) / 10, 0, 10))).ToString();

    private static bool TryRead(out PowerReading reading)
    {
        reading = default;
        try
        {
            if (!PowerNative.GetSystemPowerStatus(out var status))
            {
                Log.Warn($"GetSystemPowerStatus failed ({Marshal.GetLastPInvokeError()})");
                return false;
            }

            reading = PowerReading.From(status);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Could not read power status", ex);
            return false;
        }
    }

    private readonly record struct PowerReading(bool HasBattery, bool IsCharging, int Percent)
    {
        public static PowerReading From(PowerNative.SYSTEM_POWER_STATUS status)
        {
            bool hasBattery = status.BatteryFlag != 128 && status.BatteryFlag != 255;
            int percent = hasBattery && status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 100;
            return new PowerReading(hasBattery, status.ACLineStatus == 1, percent);
        }
    }

    private static class PowerNative
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct SYSTEM_POWER_STATUS
        {
            public byte ACLineStatus;
            public byte BatteryFlag;
            public byte BatteryLifePercent;
            public byte SystemStatusFlag;
            public int BatteryLifeTime;
            public int BatteryFullLifeTime;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    }
}
