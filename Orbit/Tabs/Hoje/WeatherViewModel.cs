using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Orbit.Services;

namespace Orbit.Tabs.Hoje;

public sealed record ForecastDay(string Day, string Icon, string MaxMin);

// Portado do WeatherTileViewModel do 1.0. Diferenças: não busca nada no construtor; só
// atualiza com a aba Hoje visível; ao reabrir, só busca de novo se o dado tiver >= 10 min.
public sealed partial class WeatherViewModel : ObservableObject
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private const int RainAlertThreshold = 60; // ponytail: const; vira setting se pedirem

    private readonly ApiService _api;
    private readonly DispatcherTimer _timer = new() { Interval = Interval };
    private double _lat = -23.55, _lon = -46.63; // São Paulo até geolocalizar
    private bool _located;
    private DateTime? _lastOk;

    [ObservableProperty] private string _icon = "…";
    [ObservableProperty] private string _temp = "--°";
    [ObservableProperty] private string _condition = "Carregando";
    [ObservableProperty] private string _city = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _sun = "";
    [ObservableProperty] private string _rainChance = "";
    [ObservableProperty] private bool _rainAlert;

    public ObservableCollection<ForecastDay> Forecast { get; } = new();

    public WeatherViewModel(ApiService api)
    {
        _api = api;
        _timer.Tick += async (_, _) => await RefreshAsync();
    }

    public void SetActive(bool active)
    {
        if (!active)
        {
            _timer.Stop();
            return;
        }
        if (Freshness.IsStale(_lastOk, Interval, DateTime.Now)) _ = RefreshAsync();
        _timer.Start();
    }

    private async Task RefreshAsync()
    {
        if (!_located)
        {
            var loc = await _api.GetLocationAsync();
            if (loc is not null)
            {
                _lat = loc.Lat;
                _lon = loc.Lon;
                City = loc.City;
                _located = true; // achou: não geolocaliza de novo
            }
        }

        var w = await _api.GetWeatherAsync(_lat, _lon);
        if (w is null)
        {
            Condition = "indisponível";
            _timer.Interval = TimeSpan.FromSeconds(20); // falhou: tenta de novo logo
            return;
        }
        _timer.Interval = Interval;
        _lastOk = DateTime.Now;
        var (icon, desc) = Describe(w.Code);
        Icon = icon;
        Temp = $"{Math.Round(w.Temp)}°";
        Condition = desc;
        Details = $"Sensação {Math.Round(w.FeelsLike)}° · Umidade {w.Humidity}% · Vento {Math.Round(w.WindKmh)} km/h";

        var fc = await _api.GetForecastAsync(_lat, _lon);
        if (fc is { Count: > 0 })
        {
            var today = fc[0];
            Sun = $"{today.Sunrise:HH:mm} – {today.Sunset:HH:mm}";
            RainChance = $"Chuva {today.PrecipProb}%";
            RainAlert = IsRainAlert(today.PrecipProb);
            Forecast.Clear();
            foreach (var day in BuildForecast(fc)) Forecast.Add(day);
        }
    }

    public static bool IsRainAlert(int precipProb) => precipProb > RainAlertThreshold;

    // Os 3 próximos dias (pula hoje = índice 0).
    public static List<ForecastDay> BuildForecast(IReadOnlyList<ForecastResult> days)
    {
        var pt = new CultureInfo("pt-BR");
        var result = new List<ForecastDay>();
        for (int i = 1; i < days.Count && result.Count < 3; i++)
        {
            var d = days[i];
            var (icon, _) = Describe(d.Code);
            var label = pt.TextInfo.ToTitleCase(d.Date.ToString("ddd", pt)).Replace(".", "");
            result.Add(new ForecastDay(label, icon, $"{Math.Round(d.Max)}°/{Math.Round(d.Min)}°"));
        }
        return result;
    }

    // WMO weather codes → dingbat BMP mono + descrição. ponytail: emoji colorido (Segoe UI
    // Emoji) renderiza invisível no texto do WPF; dingbats BMP saem como glifo mono.
    private static (string Icon, string Description) Describe(int code) => code switch
    {
        0 => ("☀", "Céu limpo"),
        1 or 2 => ("⛅", "Parcialmente nublado"),
        3 => ("☁", "Nublado"),
        45 or 48 => ("☁", "Neblina"),
        51 or 53 or 55 => ("☂", "Garoa"),
        61 or 63 or 65 => ("☔", "Chuva"),
        66 or 67 => ("☔", "Chuva congelante"),
        71 or 73 or 75 or 77 => ("❄", "Neve"),
        80 or 81 or 82 => ("☔", "Pancadas de chuva"),
        85 or 86 => ("❄", "Pancadas de neve"),
        95 => ("⚡", "Tempestade"),
        96 or 99 => ("⚡", "Tempestade c/ granizo"),
        _ => ("○", "—"),
    };
}
