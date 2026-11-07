using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Kedip.Core;
using Kedip.Weather;

namespace Kedip.Views.Panels;

/// <summary>Weather tab of the island: conditions now, today's range, and the next few hours.</summary>
public partial class WeatherPanel : UserControl
{
    private static readonly CultureInfo Indonesian = CultureInfo.GetCultureInfo("id-ID");
    private readonly AppServices _services;

    internal WeatherPanel(AppServices services)
    {
        _services = services;
        InitializeComponent();
        PlaceGlyph.Text = char.ConvertFromUtf32(0xE7B7); // map pin, as on the prayer tab
        _services.Weather.Updated += OnUpdated;
        _services.Settings.Changed += OnUpdated;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                _services.Weather.RefreshIfStale();
            }
        };
        Show();
    }

    private void OnUpdated(object? sender, EventArgs e) => Show();

    private void Show()
    {
        var weather = _services.Weather;
        bool ready = weather.Temperature is not null;
        ContentRoot.Visibility = ready ? Visibility.Visible : Visibility.Hidden;
        LoadingText.Visibility = ready ? Visibility.Collapsed : Visibility.Visible;
        if (!ready)
        {
            return;
        }

        PlaceText.Text = _services.Settings.Current.PrayerCity;
        NowGlyph.Text = weather.Glyph();
        NowTemp.Text = Degrees(weather.Temperature!.Value);
        SkyText.Text = WeatherService.Describe(weather.Code);
        RangeText.Text = $"Terasa {Degrees(weather.FeelsLike)}  ·  ↑{Degrees(weather.High)} ↓{Degrees(weather.Low)}";

        HoursGrid.Children.Clear();
        for (int i = 0; i < weather.Hours.Count; i++)
        {
            HoursGrid.Children.Add(HourCell(weather.Hours[i], now: i == 0));
        }

        string sun = weather.IsDay && weather.Sunset is { } set ? $"Terbenam {set:HH.mm}"
            : weather.Sunrise is { } rise ? $"Terbit {rise:HH.mm}" : "";
        // Rain chances sit under the hours; this line keeps to what fits.
        DetailText.Text = string.Join("  ·  ", new[] { $"Lembap {weather.Humidity}%", $"Angin {Math.Round(weather.Wind)} km/j", sun }
            .Where(p => p.Length > 0));
        ToolTip = $"Diperbarui {weather.UpdatedAt.ToString("HH.mm", Indonesian)} · Open-Meteo";
    }

    private Border HourCell(WeatherHour hour, bool now)
    {
        var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(new TextBlock
        {
            Text = now ? "Kini" : hour.Time.ToString("HH.mm", Indonesian),
            FontSize = 11,
            FontWeight = now ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = (Brush)FindResource(now ? "DockTextBrush" : "DockText2Brush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = WeatherService.Glyph(hour.Code, hour.IsDay),
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 17,
            Margin = new Thickness(0, 4, 0, 3),
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = Degrees(hour.Temperature),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        stack.Children.Add(new TextBlock
        {
            Text = hour.RainChance >= 20 ? $"{hour.RainChance}%" : " ",
            FontSize = 10,
            Foreground = (Brush)FindResource("AccentBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        return new Border
        {
            Child = stack,
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(2, 0, 2, 0),
            Background = now ? (Brush)FindResource("NowBrush") : Brushes.Transparent,
            ToolTip = $"{hour.Time.ToString("dddd HH.mm", Indonesian)} · {WeatherService.Describe(hour.Code)}",
        };
    }

    private static string Degrees(double value) => string.Create(CultureInfo.InvariantCulture, $"{Math.Round(value)}°");
}
