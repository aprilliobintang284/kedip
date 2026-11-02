namespace Kedip.Prayer;

public sealed record City(string Name, double Latitude, double Longitude, double TimeZoneHours);

/// <summary>
/// Preset Indonesian cities for the settings window.
/// OWNER: D.
/// </summary>
public static class Cities
{
    public static IReadOnlyList<City> All { get; } =
    [
        new("Kab. Kendal", -7.026, 110.169, 7),
    ];
}
