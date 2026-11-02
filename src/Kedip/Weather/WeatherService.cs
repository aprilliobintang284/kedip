using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;
using Kedip.Core;

namespace Kedip.Weather;

/// <summary>One hour of the forecast.</summary>
public sealed record WeatherHour(DateTime Time, double Temperature, int Code, bool IsDay, int RainChance);

/// <summary>
/// Weather for the configured place (the prayer-time location) from Open-Meteo: free and without a key. Refreshed
/// every 30 minutes and whenever the place changes; nothing is fetched while weather is switched off everywhere.
/// </summary>
public sealed class WeatherService : IAppService
{
    private static readonly TimeSpan Stale = TimeSpan.FromMinutes(30);
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Kedip/0.1 (personal notch app)");
        return client;
    });

    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _timer;
    private DateTime _fetchedAt = DateTime.MinValue;
    private (double Lat, double Lon) _place;
    private bool _busy;

    public WeatherService(SettingsStore settings)
    {
        _settings = settings;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMinutes(5) };
        _timer.Tick += (_, _) => RefreshIfStale();
    }

    /// <summary>Raised on the UI thread after each successful update.</summary>
    public event EventHandler? Updated;

    public bool HasData => Temperature is not null;
    public double? Temperature { get; private set; }
    public double FeelsLike { get; private set; }
    public int Humidity { get; private set; }
    public double Wind { get; private set; }
    public int Code { get; private set; }
    public bool IsDay { get; private set; } = true;
    public double High { get; private set; }
    public double Low { get; private set; }
    public int RainChance { get; private set; }
    public DateTime? Sunrise { get; private set; }
    public DateTime? Sunset { get; private set; }
    public IReadOnlyList<WeatherHour> Hours { get; private set; } = [];
    public DateTime UpdatedAt { get; private set; }

    /// <summary>Wanted by the notch tab or the dock widget.</summary>
    private bool Wanted => _settings.Current.WeatherEnabled || (_settings.Current.Show != ShowMode.NotchOnly && _settings.Current.DockWeather);

    public void Start()
    {
        _settings.Changed += (_, _) => RefreshIfStale();
        _timer.Start();
        RefreshIfStale();
    }

    public void Dispose() => _timer.Stop();

    /// <summary>Fetches when the data is older than 30 minutes or the place changed. UI thread.</summary>
    public async void RefreshIfStale()
    {
        var s = _settings.Current;
        if (!Wanted || _busy || (_place == (s.Latitude, s.Longitude) && DateTime.UtcNow - _fetchedAt < Stale))
        {
            return;
        }

        _busy = true;
        try
        {
            string url = FormattableString.Invariant(
                    $"https://api.open-meteo.com/v1/forecast?latitude={s.Latitude:0.###}&longitude={s.Longitude:0.###}")
                + "&current=temperature_2m,apparent_temperature,relative_humidity_2m,wind_speed_10m,weather_code,is_day"
                + "&hourly=temperature_2m,weather_code,is_day,precipitation_probability"
                + "&daily=temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max"
                + "&timezone=auto&forecast_days=2";
            using var response = await Http.Value.GetAsync(url).ConfigureAwait(true);
            if (!response.IsSuccessStatusCode)
            {
                Log.Warn($"Weather: HTTP {(int)response.StatusCode}");
                return;
            }

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStreamAsync().ConfigureAwait(true));
            Read(doc.RootElement);
            _place = (s.Latitude, s.Longitude);
            _fetchedAt = DateTime.UtcNow;
            UpdatedAt = DateTime.Now;
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

    private void Read(JsonElement root)
    {
        var current = root.GetProperty("current");
        Temperature = current.GetProperty("temperature_2m").GetDouble();
        FeelsLike = current.GetProperty("apparent_temperature").GetDouble();
        Humidity = (int)Math.Round(current.GetProperty("relative_humidity_2m").GetDouble());
        Wind = current.GetProperty("wind_speed_10m").GetDouble();
        Code = current.GetProperty("weather_code").GetInt32();
        IsDay = current.TryGetProperty("is_day", out var day) && day.GetInt32() == 1;

        var daily = root.GetProperty("daily");
        High = daily.GetProperty("temperature_2m_max")[0].GetDouble();
        Low = daily.GetProperty("temperature_2m_min")[0].GetDouble();
        RainChance = Number(daily.GetProperty("precipitation_probability_max")[0]);
        Sunrise = Time(daily.GetProperty("sunrise")[0]);
        Sunset = Time(daily.GetProperty("sunset")[0]);

        // The coming hours, starting with the one now under way (times are local to the place).
        var hourly = root.GetProperty("hourly");
        var times = hourly.GetProperty("time");
        var temps = hourly.GetProperty("temperature_2m");
        var codes = hourly.GetProperty("weather_code");
        var days = hourly.GetProperty("is_day");
        var rain = hourly.GetProperty("precipitation_probability");
        int offset = root.TryGetProperty("utc_offset_seconds", out var o) ? o.GetInt32() : 0;
        var placeNow = DateTime.UtcNow.AddSeconds(offset);
        var hours = new List<WeatherHour>();
        for (int i = 0; i < times.GetArrayLength() && hours.Count < 6; i++)
        {
            if (Time(times[i]) is { } time && time > placeNow.AddMinutes(-60))
            {
                hours.Add(new WeatherHour(time, temps[i].GetDouble(), codes[i].GetInt32(), days[i].GetInt32() == 1, Number(rain[i])));
            }
        }

        Hours = hours;
    }

    private static int Number(JsonElement value) => value.ValueKind == JsonValueKind.Number ? (int)Math.Round(value.GetDouble()) : 0;

    private static DateTime? Time(JsonElement value) =>
        DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time : null;

    // ---- Presentation helpers ------------------------------------------------------------------------------------

    /// <summary>A Segoe UI Symbol character for a WMO weather code.</summary>
    public static string Glyph(int code, bool isDay) => code switch
    {
        0 => isDay ? "☀" : "☾",
        1 or 2 => isDay ? "⛅" : "☁",
        3 or 45 or 48 => "☁",
        >= 51 and <= 67 or >= 80 and <= 82 => "☔",
        >= 71 and <= 77 or 85 or 86 => "❄",
        >= 95 => "⛈",
        _ => "☁",
    };

    public string Glyph() => Glyph(Code, IsDay);

    /// <summary>The WMO weather code in plain Indonesian.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "Cerah",
        1 => "Cerah berawan",
        2 => "Berawan sebagian",
        3 => "Mendung",
        45 or 48 => "Berkabut",
        51 or 53 => "Gerimis",
        55 => "Gerimis lebat",
        56 or 57 => "Gerimis beku",
        61 => "Hujan ringan",
        63 => "Hujan",
        65 => "Hujan lebat",
        66 or 67 => "Hujan beku",
        71 or 73 or 75 or 77 => "Salju",
        80 => "Hujan lokal",
        81 => "Hujan deras",
        82 => "Hujan sangat deras",
        85 or 86 => "Hujan salju",
        95 => "Badai petir",
        96 or 99 => "Badai petir & es",
        _ => "Berawan",
    };
}
