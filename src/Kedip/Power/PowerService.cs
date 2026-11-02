using Kedip.Core;

namespace Kedip.Power;

/// <summary>
/// Battery level and charger state; peeks on plug/unplug and low battery.
/// OWNER: E. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public sealed class PowerService : IAppService
{
    private readonly PeekBus _peeks;

    public PowerService(PeekBus peeks)
    {
        _peeks = peeks;
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
    }

    public void Dispose()
    {
    }
}
