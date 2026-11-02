namespace Kedip.Prayer;

public enum PrayerName { Imsak, Subuh, Terbit, Dzuhur, Ashar, Maghrib, Isya }

public sealed record PrayerTime(PrayerName Name, DateTime Time)
{
    /// <summary>Imsak and Terbit are markers, not prayers.</summary>
    public bool IsPrayer => Name is not (PrayerName.Imsak or PrayerName.Terbit);
}

/// <summary>
/// Astronomical prayer-time calculation with Kemenag RI parameters. See DESIGN.md "Prayer".
/// OWNER: D. Scaffold stub — replace the implementation, keep the public API.
/// </summary>
public static class PrayerCalculator
{
    /// <summary>Times for <paramref name="date"/> in local clock time of the given UTC offset, ordered Imsak..Isya.</summary>
    public static IReadOnlyList<PrayerTime> Compute(DateOnly date, double latitude, double longitude, double timeZoneHours, int ihtiyatMinutes) => [];
}
