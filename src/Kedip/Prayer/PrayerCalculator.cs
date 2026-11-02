namespace Kedip.Prayer;

public enum PrayerName { Imsak, Subuh, Terbit, Dzuhur, Ashar, Maghrib, Isya }

public sealed record PrayerTime(PrayerName Name, DateTime Time)
{
    /// <summary>Imsak and Terbit are markers, not prayers.</summary>
    public bool IsPrayer => Name is not (PrayerName.Imsak or PrayerName.Terbit);
}

/// <summary>
/// Astronomical prayer-time calculation with Kemenag RI parameters. See DESIGN.md "Prayer".
/// Sun position uses the NOAA / Meeus low-precision series (accurate to well under a minute of time).
/// </summary>
/// <remarks>
/// Two conventions were fitted against official Kemenag schedules (myquran / bimasislam, 2026, six cities):
/// Kemenag's sunrise/sunset altitude is −1° (refraction 34′ + semi-diameter 16′ + ≈10′ horizon dip), and Dzuhur
/// starts one minute after transit, once the sun's disc has cleared the meridian (zawal), before ihtiyat.
/// With these, ≈85–99 % of published minutes match exactly and none differ by more than one minute
/// (Kab. Kendal all of 2026; all 41 preset cities for March, June and November 2026).
/// </remarks>
public static class PrayerCalculator
{
    public const double FajrAngle = -20.0;
    public const double IshaAngle = -18.0;

    /// <summary>Sun altitude at Terbit and Maghrib as used by Kemenag (see remarks).</summary>
    public const double HorizonAngle = -1.0;

    /// <summary>Time for the sun's disc to clear the meridian, added to transit for Dzuhur.</summary>
    public const double ZawalMinutes = 1.0;

    public const int ImsakOffsetMinutes = 10;

    private const double HighLatitudeFallback = 45.0;
    private const int Iterations = 3;
    private const double Deg = Math.PI / 180.0;

    /// <summary>Times for <paramref name="date"/> in local clock time of the given UTC offset, ordered Imsak..Isya.</summary>
    public static IReadOnlyList<PrayerTime> Compute(DateOnly date, double latitude, double longitude, double timeZoneHours, int ihtiyatMinutes) =>
        Compute(date, latitude, longitude, timeZoneHours, ihtiyatMinutes, HorizonAngle);

    /// <summary>
    /// As <see cref="Compute(DateOnly, double, double, double, int)"/> with an explicit sun altitude (degrees) for Terbit and
    /// Maghrib; Kemenag uses a lower horizon for some highland regions (see <see cref="City.HorizonAltitude"/>).
    /// </summary>
    public static IReadOnlyList<PrayerTime> Compute(
        DateOnly date, double latitude, double longitude, double timeZoneHours, int ihtiyatMinutes, double horizonAltitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) || !double.IsFinite(timeZoneHours))
        {
            return [];
        }

        double horizon = double.IsFinite(horizonAltitude) ? Math.Clamp(horizonAltitude, -5.0, 0.0) : HorizonAngle;

        // Keep away from the poles so tan/cos stay finite; wrap longitude into [-180, 180).
        double lat = Math.Clamp(latitude, -89.5, 89.5);
        double lon = ((longitude + 180.0) % 360.0 + 360.0) % 360.0 - 180.0;
        double tz = Math.Clamp(timeZoneHours, -14, 14);
        int ihtiyat = Math.Clamp(ihtiyatMinutes, 0, 30);

        double jd0 = JulianDayAtMidnightUtc(date);
        var day = new Day(jd0, lat, lon, tz);

        // High latitudes: an event the sun never reaches (polar day/night, endless twilight) is taken from the
        // nearest latitude where it always exists (±45°, "aqrab al-bilad"); never happens in Indonesia.
        Day? fallback = null;
        Day Fallback() => fallback ??= new Day(jd0, Math.CopySign(HighLatitudeFallback, lat), lon, tz);

        double transit = day.Transit(12);
        double sunrise = OrElse(day.Event(horizon, rising: true, guess: 6), () => Fallback().Event(horizon, rising: true, guess: 6));
        double sunset = OrElse(day.Event(horizon, rising: false, guess: 18), () => Fallback().Event(horizon, rising: false, guess: 18));
        double fajr = OrElse(day.Event(FajrAngle, rising: true, guess: 5), () => Fallback().Event(FajrAngle, rising: true, guess: 5));
        double isha = OrElse(day.Event(IshaAngle, rising: false, guess: 19), () => Fallback().Event(IshaAngle, rising: false, guess: 19));
        double asr = OrElse(day.AsrEvent(guess: 15), () => Fallback().AsrEvent(guess: 15));
        if (!double.IsFinite(sunrise + sunset + fajr + isha + asr + transit))
        {
            return [];
        }

        // Kemenag rounding: prayers get + ihtiyat and are rounded up; sunrise gets − ihtiyat and is rounded down.
        int subuhMin = CeilMinutes(fajr, ihtiyat);
        int dzuhurMin = CeilMinutes(transit + ZawalMinutes / 60.0, ihtiyat);
        int asharMin = CeilMinutes(asr, ihtiyat);
        int maghribMin = CeilMinutes(sunset, ihtiyat);
        int isyaMin = CeilMinutes(isha, ihtiyat);
        int terbitMin = FloorMinutes(sunrise, -ihtiyat);
        int imsakMin = subuhMin - ImsakOffsetMinutes;

        // Guarantee non-decreasing order even in degenerate (polar) geometry.
        int[] minutes = [imsakMin, subuhMin, terbitMin, dzuhurMin, asharMin, maghribMin, isyaMin];
        for (int i = 1; i < minutes.Length; i++)
        {
            minutes[i] = Math.Max(minutes[i], minutes[i - 1]);
        }

        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var result = new PrayerTime[minutes.Length];
        for (int i = 0; i < minutes.Length; i++)
        {
            result[i] = new PrayerTime((PrayerName)i, midnight.AddMinutes(minutes[i]));
        }

        return result;
    }

    private static double OrElse(double value, Func<double> fallback) => double.IsNaN(value) ? fallback() : value;

    private static int CeilMinutes(double hours, int offsetMinutes) =>
        (int)Math.Ceiling(Math.Round(hours * 60.0 + offsetMinutes, 6));

    private static int FloorMinutes(double hours, int offsetMinutes) =>
        (int)Math.Floor(Math.Round(hours * 60.0 + offsetMinutes, 6));

    /// <summary>Julian day number at 0h UT of the given civil date (Gregorian).</summary>
    internal static double JulianDayAtMidnightUtc(DateOnly date)
    {
        int y = date.Year;
        int m = date.Month;
        if (m <= 2)
        {
            y--;
            m += 12;
        }

        int a = y / 100;
        int b = 2 - a + a / 4;
        return Math.Floor(365.25 * (y + 4716)) + Math.Floor(30.6001 * (m + 1)) + date.Day + b - 1524.5;
    }

    /// <summary>Solar declination (degrees) and equation of time (minutes) at the given Julian day.</summary>
    internal static (double Declination, double EquationOfTime) SunPosition(double jd)
    {
        double t = (jd - 2451545.0) / 36525.0;

        double l0 = Normalize(280.46646 + t * (36000.76983 + t * 0.0003032));
        double m = 357.52911 + t * (35999.05029 - t * 0.0001537);
        double e = 0.016708634 - t * (0.000042037 + t * 0.0000001267);

        double mRad = m * Deg;
        double center = Math.Sin(mRad) * (1.914602 - t * (0.004817 + t * 0.000014))
            + Math.Sin(2 * mRad) * (0.019993 - t * 0.000101)
            + Math.Sin(3 * mRad) * 0.000289;
        double trueLong = l0 + center;

        double omega = (125.04 - 1934.136 * t) * Deg;
        double apparentLong = trueLong - 0.00569 - 0.00478 * Math.Sin(omega);

        double meanObliquity = 23.0 + (26.0 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60.0) / 60.0;
        double obliquity = meanObliquity + 0.00256 * Math.Cos(omega);

        double declination = Math.Asin(Math.Sin(obliquity * Deg) * Math.Sin(apparentLong * Deg)) / Deg;

        double y = Math.Tan(obliquity * Deg / 2);
        y *= y;
        double l0Rad = l0 * Deg;
        double eot = y * Math.Sin(2 * l0Rad)
            - 2 * e * Math.Sin(mRad)
            + 4 * e * y * Math.Sin(mRad) * Math.Cos(2 * l0Rad)
            - 0.5 * y * y * Math.Sin(4 * l0Rad)
            - 1.25 * e * e * Math.Sin(2 * mRad);

        return (declination, 4.0 * eot / Deg);
    }

    private static double Normalize(double degrees) => ((degrees % 360.0) + 360.0) % 360.0;

    /// <summary>One location on one civil date. Times are local clock hours from that date's midnight.</summary>
    private sealed class Day(double jd0, double latitude, double longitude, double timeZone)
    {
        private (double Declination, double EquationOfTime) SunAt(double localHours) =>
            SunPosition(jd0 + (localHours - timeZone) / 24.0);

        private double TransitFor(double equationOfTimeMinutes) =>
            12.0 + timeZone - longitude / 15.0 - equationOfTimeMinutes / 60.0;

        public double Transit(double guess)
        {
            double t = guess;
            for (int i = 0; i < Iterations; i++)
            {
                t = TransitFor(SunAt(t).EquationOfTime);
            }

            return t;
        }

        /// <summary>Time the sun's centre is at <paramref name="altitude"/>; NaN when it never gets there.</summary>
        public double Event(double altitude, bool rising, double guess) =>
            Iterate(guess, rising, _ => altitude);

        /// <summary>Ashar (Syafi'i): shadow length = object length + noon shadow.</summary>
        public double AsrEvent(double guess) =>
            Iterate(guess, rising: false, declination =>
                Math.Atan(1.0 / (1.0 + Math.Tan(Math.Abs(latitude - declination) * Deg))) / Deg);

        private double Iterate(double guess, bool rising, Func<double, double> altitudeFor)
        {
            double t = guess;
            for (int i = 0; i < Iterations; i++)
            {
                var (declination, eot) = SunAt(t);
                double hourAngle = HourAngle(altitudeFor(declination), declination);
                if (double.IsNaN(hourAngle))
                {
                    return double.NaN;
                }

                double transit = TransitFor(eot);
                t = rising ? transit - hourAngle : transit + hourAngle;
            }

            return t;
        }

        /// <summary>Hour angle in hours for the given altitude, or NaN if unreachable.</summary>
        private double HourAngle(double altitude, double declination)
        {
            double phi = latitude * Deg;
            double delta = declination * Deg;
            double cosH = (Math.Sin(altitude * Deg) - Math.Sin(phi) * Math.Sin(delta)) / (Math.Cos(phi) * Math.Cos(delta));
            if (cosH is < -1.0 or > 1.0 || double.IsNaN(cosH))
            {
                return double.NaN;
            }

            return Math.Acos(cosH) / Deg / 15.0;
        }
    }
}
