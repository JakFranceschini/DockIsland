using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace DockIsland;

/// Previsão atual: temperatura (°C), código WMO do tempo, se é dia, e o nome do lugar
public sealed record WeatherData(double Temp, int Code, bool IsDay, string City, string Caption = null, string Url = null);

// Busca o tempo atual sem chave de API:
//   - lugar: a cidade digitada nos Ajustes (Open-Meteo Geocoding) ou, se vazia, a localização aproximada pelo IP;
//   - tempo: MSN Weather (a mesma fonte do widget de clima do Windows); se o MSN não responder, Open-Meteo (https://open-meteo.com).
public static class WeatherService
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("DockIsland/1.0");
        return h;
    }

    // o lugar fica guardado: nas atualizações seguintes só o tempo é pedido de novo
    static string _placeKey;
    static double _lat, _lon;
    static string _place, _region, _country, _cc;

    static string Inv(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// Devolve o tempo atual, ou null se não deu (sem internet, serviço fora do ar...)
    public static async Task<WeatherData> FetchAsync(string city)
    {
        try
        {
            city = (city ?? "").Trim();
            if (_placeKey != city || _place == null)
            {
                if (!await ResolvePlaceAsync(city)) return null;
                _placeKey = city;
            }

            string pageUrl = MsnPageUrl();
            var msn = await FetchMsnAsync(pageUrl);
            if (msn != null) return msn;

            string url = "https://api.open-meteo.com/v1/forecast?latitude=" + Inv(_lat) + "&longitude=" + Inv(_lon) +
                         "&current=temperature_2m,weather_code,is_day&timezone=auto";
            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
            var cur = doc.RootElement.GetProperty("current");
            return new WeatherData(
                cur.GetProperty("temperature_2m").GetDouble(),
                cur.GetProperty("weather_code").GetInt32(),
                cur.GetProperty("is_day").GetInt32() == 1,
                _place, null, pageUrl);
        }
        catch { return null; }
    }

    // ---------------------------------------------------------------- MSN Weather
    // Página de previsão do MSN para o lugar atual (aberta ao clicar no widget)
    static string MsnPageUrl()
    {
        string Slug(string t) => Uri.EscapeDataString((t ?? "").Trim().Replace(' ', '-'));
        string loc = "{\"l\":" + JsonSerializer.Serialize(_place ?? "") +
                     ",\"r\":" + JsonSerializer.Serialize(_region ?? "") +
                     ",\"c\":" + JsonSerializer.Serialize(_country ?? "") +
                     ",\"i\":" + JsonSerializer.Serialize(_cc ?? "") +
                     ",\"g\":\"pt-br\",\"x\":\"" + Inv(_lon) + "\",\"y\":\"" + Inv(_lat) + "\"}";
        string path = Slug(_place) + (string.IsNullOrWhiteSpace(_region) ? "" : "," + Slug(_region));
        return "https://www.msn.com/pt-br/clima/forecast/in-" + path +
               "?loc=" + Uri.EscapeDataString(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(loc))) +
               "&weadegreetype=C";
    }

    // Tempo atual pelo serviço do MSN (o mesmo que alimenta o widget do Windows). null = não deu: usa o Open-Meteo.
    static async Task<WeatherData> FetchMsnAsync(string pageUrl)
    {
        string common = "&locale=pt-BR&lat=" + Inv(_lat) + "&lon=" + Inv(_lon) +
                        "&units=C&ocid=msftweather&wrapodata=false&includemapsmetadata=false";
        string[] urls =
        {
            "https://api.msn.com/weather/overview?apikey=j5i4gDqHL6nGYwx5wi5kRhXjtf2c5qgFX9fzfk0TOo" + common,
            "https://api.msn.com/weather/overview?" + common.TrimStart('&'),
        };
        foreach (var u in urls)
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(u));
                var cur = FindCurrent(doc.RootElement);
                if (cur == null) continue;
                var c = cur.Value;
                if (!c.TryGetProperty("temp", out var t) || t.ValueKind != JsonValueKind.Number) continue;
                double temp = t.GetDouble();
                if (temp < -90 || temp > 60) continue;

                string cap = c.TryGetProperty("cap", out var cp) && cp.ValueKind == JsonValueKind.String ? cp.GetString() : null;
                bool day;
                if (c.TryGetProperty("daytime", out var dt) && dt.ValueKind == JsonValueKind.String)
                    day = !string.Equals(dt.GetString(), "n", StringComparison.OrdinalIgnoreCase);
                else { int h = DateTime.Now.Hour; day = h >= 6 && h < 18; }

                return new WeatherData(temp, CaptionToCode(cap), day, _place, cap, pageUrl);
            }
            catch { }
        }
        return null;
    }

    // acha o objeto "current" (que tem "temp") em qualquer lugar da resposta
    static JsonElement? FindCurrent(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("current", out var cur) && cur.ValueKind == JsonValueKind.Object && cur.TryGetProperty("temp", out _))
                return cur;
            foreach (var p in e.EnumerateObject())
            {
                var r = FindCurrent(p.Value);
                if (r != null) return r;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in e.EnumerateArray())
            {
                var r = FindCurrent(i);
                if (r != null) return r;
            }
        }
        return null;
    }

    // texto do MSN ("Parcialmente ensolarado", "Mostly cloudy"...) -> código WMO usado pelo desenho do widget
    static int CaptionToCode(string cap)
    {
        string t = (cap ?? "").ToLowerInvariant();
        bool Has(params string[] k) { foreach (var x in k) if (t.Contains(x)) return true; return false; }
        if (Has("trovoad", "tempest", "thunder", "storm")) return 95;
        if (Has("neve", "nevasca", "granizo", "gelo", "snow", "sleet", "flurr", "ice")) return 71;
        if (Has("chuv", "pancada", "garoa", "aguaceiro", "rain", "shower", "drizzle")) return 61;
        if (Has("neblin", "nevoeiro", "névoa", "bruma", "fog", "mist", "haz")) return 45;
        if (Has("parcial", "pouco", "intermit", "partly", "mostly sunny", "mostly clear", "predominantemente ensolarado", "predominantemente limpo", "sol com nuvens", "sol e nuvens")) return 2;
        if (Has("nublado", "encoberto", "cloud", "overcast", "dreary")) return 3;
        return 0;
    }

    static async Task<bool> ResolvePlaceAsync(string city)
    {
        try
        {
            if (city.Length > 0)
            {
                string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=pt&format=json&name=" +
                             Uri.EscapeDataString(city);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                if (!doc.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return false;
                var r = res[0];
                _lat = r.GetProperty("latitude").GetDouble();
                _lon = r.GetProperty("longitude").GetDouble();
                _place = r.TryGetProperty("name", out var n) ? n.GetString() : city;
                _region = r.TryGetProperty("admin1", out var a1) ? a1.GetString() : "";
                _country = r.TryGetProperty("country", out var co) ? co.GetString() : "";
                _cc = r.TryGetProperty("country_code", out var cc) ? cc.GetString() : "";
                return true;
            }

            // sem cidade escolhida: localização aproximada pelo IP (dois serviços, o segundo é reserva)
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/"));
                var r = doc.RootElement;
                if (r.TryGetProperty("success", out var ok) && ok.GetBoolean())
                {
                    _lat = r.GetProperty("latitude").GetDouble();
                    _lon = r.GetProperty("longitude").GetDouble();
                    _place = r.TryGetProperty("city", out var c) && !string.IsNullOrWhiteSpace(c.GetString()) ? c.GetString() : "Local";
                    _region = r.TryGetProperty("region", out var rg) ? rg.GetString() : "";
                    _country = r.TryGetProperty("country", out var co) ? co.GetString() : "";
                    _cc = r.TryGetProperty("country_code", out var cc) ? cc.GetString() : "";
                    return true;
                }
            }
            catch { }
            using (var doc = JsonDocument.Parse(await Http.GetStringAsync("https://ipapi.co/json/")))
            {
                var r = doc.RootElement;
                _lat = r.GetProperty("latitude").GetDouble();
                _lon = r.GetProperty("longitude").GetDouble();
                _place = r.TryGetProperty("city", out var c) && !string.IsNullOrWhiteSpace(c.GetString()) ? c.GetString() : "Local";
                _region = r.TryGetProperty("region", out var rg) ? rg.GetString() : "";
                _country = r.TryGetProperty("country_name", out var co) ? co.GetString() : "";
                _cc = r.TryGetProperty("country_code", out var cc) ? cc.GetString() : "";
                return true;
            }
        }
        catch { return false; }
    }
}
