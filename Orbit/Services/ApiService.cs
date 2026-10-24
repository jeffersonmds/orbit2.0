using System;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace Orbit.Services;

/// <summary>
/// Busca dados das APIs públicas (todas grátis e sem chave).
/// </summary>
public class ApiService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly ILogger _log = Log.ForContext<ApiService>();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        // CoinGecko (e outras) rejeitam requisições sem User-Agent.
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Orbit/2.0");
        return http;
    }

    // ---- Localização por IP (ip-api.com, grátis, sem chave) ----
    public async Task<LocationResult?> GetLocationAsync()
    {
        try
        {
            using var doc = JsonDocument.Parse(
                await Http.GetStringAsync("http://ip-api.com/json/?fields=status,lat,lon,city"));
            var root = doc.RootElement;
            if (root.GetProperty("status").GetString() != "success") return null;
            return new LocationResult(
                root.GetProperty("lat").GetDouble(),
                root.GetProperty("lon").GetDouble(),
                root.GetProperty("city").GetString() ?? "");
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Falha ao obter localização");
            return null;
        }
    }

    // ---- Clima (Open-Meteo) ----
    // Default: São Paulo. Dá pra trocar lat/lon depois.
    public async Task<WeatherResult?> GetWeatherAsync(double lat = -23.55, double lon = -46.63)
    {
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}" +
                  $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                  "&current=temperature_2m,apparent_temperature,weather_code,relative_humidity_2m,wind_speed_10m";
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            return new WeatherResult(
                cur.GetProperty("temperature_2m").GetDouble(),
                cur.GetProperty("apparent_temperature").GetDouble(),
                cur.GetProperty("weather_code").GetInt32(),
                cur.GetProperty("relative_humidity_2m").GetInt32(),
                cur.GetProperty("wind_speed_10m").GetDouble());
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Falha ao obter clima");
            return null;
        }
    }

    // Chuva agora e nas próximas 2 h, de 15 em 15 min (Open-Meteo minutely_15), pro aviso "vai chover".
    public async Task<RainForecast?> GetRainAsync(double lat, double lon)
    {
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}" +
                  $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                  "&current=precipitation&minutely_15=precipitation&forecast_minutely_15=8&timezone=auto";
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var root = doc.RootElement;
            var now = root.GetProperty("current").GetProperty("precipitation").GetDouble();
            var next = root.GetProperty("minutely_15").GetProperty("precipitation").EnumerateArray()
                .Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0).ToArray();
            return new RainForecast(now, next);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Falha ao obter previsão de chuva");
            return null;
        }
    }

    // Série de preços 24h pra sparkline (CoinGecko market_chart).
    public async Task<double[]?> GetSparklineAsync(string coinId)
    {
        var url = $"https://api.coingecko.com/api/v3/coins/{coinId}/market_chart?vs_currency=usd&days=1";
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var prices = doc.RootElement.GetProperty("prices");
            var list = new System.Collections.Generic.List<double>(prices.GetArrayLength());
            foreach (var p in prices.EnumerateArray())
                list.Add(p[1].GetDouble()); // [timestamp, price]
            return Sparkline.Subsample(list.ToArray(), 40);
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Falha ao obter sparkline de {Coin}", coinId);
            return null;
        }
    }

    private static double ParseD(string? s) =>
        double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : 0;

    // URLs montadas a partir da seleção (puras/testáveis).
    public static string BuildCryptoUrl(System.Collections.Generic.IEnumerable<string> ids) =>
        "https://api.coingecko.com/api/v3/simple/price?ids=" + string.Join(",", ids) +
        "&vs_currencies=usd&include_24hr_change=true";

    public static string BuildExchangeUrl(System.Collections.Generic.IEnumerable<string> codes) =>
        "https://economia.awesomeapi.com.br/last/" +
        string.Join(",", System.Linq.Enumerable.Select(codes, c => c + "-BRL"));

    // Preços de uma lista de criptos (CoinGecko), id -> (preço USD, variação 24h %).
    public async Task<System.Collections.Generic.Dictionary<string, (double Price, double Change)>?>
        GetCryptoPricesAsync(System.Collections.Generic.IEnumerable<string> ids)
    {
        var list = System.Linq.Enumerable.ToList(ids);
        if (list.Count == 0) return new();
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(BuildCryptoUrl(list)));
            var root = doc.RootElement;
            var result = new System.Collections.Generic.Dictionary<string, (double, double)>();
            foreach (var id in list)
            {
                if (!root.TryGetProperty(id, out var e)) continue;
                double price = e.TryGetProperty("usd", out var pu) ? pu.GetDouble() : 0;
                double chg = e.TryGetProperty("usd_24h_change", out var cu)
                             && cu.ValueKind != JsonValueKind.Null ? cu.GetDouble() : 0;
                result[id] = (price, chg);
            }
            return result;
        }
        catch (Exception ex) { _log.Warning(ex, "Falha ao obter cripto"); return null; }
    }

    // Preços de uma lista de moedas vs BRL (AwesomeAPI), código -> (bid, pctChange).
    public async Task<System.Collections.Generic.Dictionary<string, (double Price, double Change)>?>
        GetExchangePricesAsync(System.Collections.Generic.IEnumerable<string> codes)
    {
        var list = System.Linq.Enumerable.ToList(codes);
        if (list.Count == 0) return new();
        try
        {
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(BuildExchangeUrl(list)));
            var root = doc.RootElement;
            var result = new System.Collections.Generic.Dictionary<string, (double, double)>();
            foreach (var code in list)
            {
                // AwesomeAPI: par "USD-BRL" vira a chave "USDBRL".
                if (!root.TryGetProperty(code + "BRL", out var e)) continue;
                result[code] = (ParseD(e.GetProperty("bid").GetString()),
                                ParseD(e.GetProperty("pctChange").GetString()));
            }
            return result;
        }
        catch (Exception ex) { _log.Warning(ex, "Falha ao obter câmbio"); return null; }
    }

    // Parser puro (testável): extrai os dias do JSON do Open-Meteo.
    public static System.Collections.Generic.List<ForecastResult> ParseForecast(string json)
    {
        var list = new System.Collections.Generic.List<ForecastResult>();
        using var doc = JsonDocument.Parse(json);
        var daily = doc.RootElement.GetProperty("daily");
        var time = daily.GetProperty("time");
        var code = daily.GetProperty("weather_code");
        var max = daily.GetProperty("temperature_2m_max");
        var min = daily.GetProperty("temperature_2m_min");
        var sunrise = daily.GetProperty("sunrise");
        var sunset = daily.GetProperty("sunset");
        var precip = daily.GetProperty("precipitation_probability_max");
        for (int i = 0; i < time.GetArrayLength(); i++)
        {
            var pp = precip[i];
            int prob = pp.ValueKind == System.Text.Json.JsonValueKind.Null ? 0 : pp.GetInt32();
            list.Add(new ForecastResult(
                System.DateTime.Parse(time[i].GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture),
                code[i].GetInt32(), max[i].GetDouble(), min[i].GetDouble(),
                System.DateTime.Parse(sunrise[i].GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture),
                System.DateTime.Parse(sunset[i].GetString()!,
                    System.Globalization.CultureInfo.InvariantCulture),
                prob));
        }
        return list;
    }

    public async Task<System.Collections.Generic.List<ForecastResult>?> GetForecastAsync(double lat, double lon)
    {
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}" +
                  $"&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                  "&daily=weather_code,temperature_2m_max,temperature_2m_min,sunrise,sunset,precipitation_probability_max&forecast_days=4&timezone=auto";
        try
        {
            return ParseForecast(await Http.GetStringAsync(url));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Falha ao obter previsão");
            return null;
        }
    }

    // Parser puro (testável): extrai um item do Hacker News.
    // Item sem `url` (Ask/Show HN) cai pro thread do HN.
    public static NewsItem ParseNewsItem(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int id = root.GetProperty("id").GetInt32();
        string title = root.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        string url = root.TryGetProperty("url", out var u) && u.ValueKind != JsonValueKind.Null
            ? u.GetString()!
            : $"https://news.ycombinator.com/item?id={id}";
        return new NewsItem(id, title, url);
    }

    // Top N manchetes do Hacker News (API Firebase pública, sem chave).
    // ponytail: N+1 fetch (1 lista + 1 por item); ok pra ~8 itens a cada 5 min.
    // Se pesar, cachear item por id.
    public async Task<System.Collections.Generic.List<NewsItem>?> GetTopNewsAsync(int count)
    {
        try
        {
            using var top = JsonDocument.Parse(
                await Http.GetStringAsync("https://hacker-news.firebaseio.com/v0/topstories.json"));
            var ids = top.RootElement.EnumerateArray().Take(count).Select(e => e.GetInt32());
            var list = new System.Collections.Generic.List<NewsItem>();
            foreach (var id in ids)
            {
                try
                {
                    var json = await Http.GetStringAsync(
                        $"https://hacker-news.firebaseio.com/v0/item/{id}.json");
                    list.Add(ParseNewsItem(json));
                }
                catch (Exception ex) { _log.Warning(ex, "Falha ao obter item {Id} do HN", id); }
            }
            return list;
        }
        catch (Exception ex) { _log.Warning(ex, "Falha ao obter notícias"); return null; }
    }
}

public record LocationResult(double Lat, double Lon, string City);
public record WeatherResult(double Temp, double FeelsLike, int Code, int Humidity, double WindKmh);
// NowMm = chuva agora (mm); Next15 = mm em cada bloco de 15 min a partir de agora.
public record RainForecast(double NowMm, double[] Next15);
public record ForecastResult(
    System.DateTime Date, int Code, double Max, double Min,
    System.DateTime Sunrise, System.DateTime Sunset, int PrecipProb);
public record NewsItem(int Id, string Title, string Url);
