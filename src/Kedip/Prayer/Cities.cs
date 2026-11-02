namespace Kedip.Prayer;

public sealed record City(string Name, double Latitude, double Longitude, double TimeZoneHours)
{
    /// <summary>Sun altitude (degrees) at Terbit/Maghrib in Kemenag's schedule for this place.</summary>
    public double HorizonAltitude { get; init; } = PrayerCalculator.HorizonAngle;
}

/// <summary>
/// Preset Indonesian cities for the settings window: Kab. Kendal (the default) first, then alphabetical.
/// Coordinates are city centres (alun-alun / civic centre); WIB = 7, WITA = 8, WIT = 9.
/// </summary>
public static class Cities
{
    // Kemenag's published schedules for these highland cities put Terbit ≈ 4 min earlier and Maghrib ≈ 4 min later
    // than for nearby lowland ones (a site-elevation horizon dip); −1.9° reproduces them to the minute.
    // Other highland places (Malang, Magelang) are published with the normal horizon.
    private const double Highland = -1.9;

    public static IReadOnlyList<City> All { get; } =
    [
        new("Kab. Kendal", -7.026, 110.169, 7),
        new("Ambon", -3.695, 128.181, 9),
        new("Balikpapan", -1.238, 116.853, 8),
        new("Banda Aceh", 5.548, 95.324, 7),
        new("Bandar Lampung", -5.429, 105.262, 7),
        new("Bandung", -6.917, 107.619, 7) { HorizonAltitude = Highland },
        new("Banjarmasin", -3.319, 114.594, 8),
        new("Batam", 1.117, 104.050, 7),
        new("Batang", -6.909, 109.730, 7),
        new("Bekasi", -6.238, 106.976, 7),
        new("Bengkulu", -3.800, 102.266, 7),
        new("Bogor", -6.597, 106.806, 7) { HorizonAltitude = Highland },
        new("Cirebon", -6.732, 108.552, 7),
        new("Denpasar", -8.670, 115.213, 8),
        new("Depok", -6.402, 106.794, 7),
        new("Jakarta", -6.175, 106.827, 7),
        new("Jambi", -1.610, 103.613, 7),
        new("Jayapura", -2.534, 140.718, 9),
        new("Kudus", -6.805, 110.841, 7),
        new("Kupang", -10.177, 123.607, 8),
        new("Magelang", -7.477, 110.218, 7),
        new("Makassar", -5.148, 119.433, 8),
        new("Malang", -7.983, 112.631, 7),
        new("Manado", 1.475, 124.842, 8),
        new("Mataram", -8.583, 116.117, 8),
        new("Medan", 3.595, 98.672, 7),
        new("Padang", -0.947, 100.417, 7),
        new("Palembang", -2.976, 104.775, 7),
        new("Pekalongan", -6.889, 109.675, 7),
        new("Pekanbaru", 0.507, 101.448, 7),
        new("Pontianak", -0.026, 109.343, 7),
        new("Purwokerto", -7.424, 109.231, 7),
        new("Salatiga", -7.331, 110.508, 7) { HorizonAltitude = Highland },
        new("Samarinda", -0.502, 117.154, 8),
        new("Semarang", -6.990, 110.423, 7),
        new("Serang", -6.120, 106.150, 7),
        new("Surabaya", -7.258, 112.752, 7),
        new("Surakarta", -7.567, 110.817, 7),
        new("Tangerang", -6.178, 106.632, 7),
        new("Tegal", -6.869, 109.140, 7),
        new("Yogyakarta", -7.797, 110.370, 7),
    ];

    /// <summary>Preset with this name (case-insensitive), or null.</summary>
    public static City? Find(string? name) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : All.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}
