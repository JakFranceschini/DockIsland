using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using WPath = System.Windows.Shapes.Path;

namespace DockIsland;

// Widget de previsão do tempo do dock: cartão com degradê (azul = sol, roxo = noite nublada, verde = chuva...),
// condição, temperatura grande, data, lugar e uma ilustração desenhada em vetor à direita.
public partial class DockWindow
{
    /// Cidade escolhida nos Ajustes (vazia = automática pelo IP); definido pelo App
    public Func<string> WeatherCity;

    static readonly CultureInfo PtBr = new("pt-BR");
    WeatherData _weather;
    bool _weatherBusy;
    long _weatherAt;
    DispatcherTimer _weatherTimer;

    /// Começa a atualizar o tempo (de tempos em tempos, só enquanto o widget estiver ligado)
    public void StartWeather()
    {
        _weatherTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _weatherTimer.Tick += (_, __) =>
        {
            // sem dados ainda: tenta de novo a cada minuto; com dados: a cada 15 minutos
            long age = Environment.TickCount64 - _weatherAt;
            if (On("weather") && (_weather == null || age > 15 * 60 * 1000)) _ = RefreshWeatherAsync(true);
        };
        _weatherTimer.Start();
        WeatherRoot.SizeChanged += (_, e) =>
            WeatherRoot.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 16, 16);
        if (On("weather")) _ = RefreshWeatherAsync(true);
    }

    /// Pede o tempo de novo agora (cidade trocada nos Ajustes, widget ligado, clique no cartão)
    public void WeatherRefresh() => _ = RefreshWeatherAsync(true);

    async System.Threading.Tasks.Task RefreshWeatherAsync(bool force)
    {
        if (_weatherBusy) return;
        if (!force && _weather != null && Environment.TickCount64 - _weatherAt < 60_000) return;
        _weatherBusy = true;
        try
        {
            var w = await WeatherService.FetchAsync(WeatherCity?.Invoke());
            if (w != null) { _weather = w; _weatherAt = Environment.TickCount64; ApplyWeather(); ApplyWidgets(animate: false); }
        }
        finally { _weatherBusy = false; }
    }

    // clicar no cartão abre a previsão do tempo no MSN (a mesma do widget do Windows)
    void Weather_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        string url = _weather?.Url ?? "https://www.msn.com/pt-br/clima";
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        _ = RefreshWeatherAsync(false);   // aproveita o clique para atualizar se os dados estiverem velhos
    }

    // ---------------------------------------------------------------- aparência
    enum Art { Sun, SunCloud, Cloud, Moon, MoonCloud, Rain, Storm, Snow, Fog }

    // código WMO (Open-Meteo) -> texto, ilustração e cores do degradê (esquerda -> direita)
    static (string Text, Art Art, string A, string B) Look(int code, bool day)
    {
        switch (code)
        {
            case 0:
            case 1: return day ? ("Ensolarado", Art.Sun, "#5B8EF5", "#A9D0FB") : ("Céu limpo", Art.Moon, "#3440C8", "#8A66D0");
            case 2: return day ? ("Pouco nublado", Art.SunCloud, "#5B8EF5", "#A9C9F2") : ("Noite nublada", Art.MoonCloud, "#4B5DF0", "#A07FDD");
            case 3: return day ? ("Nublado", Art.Cloud, "#6D8DB9", "#B4C4DA") : ("Noite nublada", Art.MoonCloud, "#4B5DF0", "#A07FDD");
            case 45:
            case 48: return ("Neblina", Art.Fog, "#7F93A3", "#C9D3DA");
            case 51: case 53: case 55: case 56: case 57:
            case 61: case 63: case 65: case 66: case 67:
            case 80: case 81: case 82: return ("Chuva", Art.Rain, "#46709B", "#9EC4E0");
            case 71: case 73: case 75: case 77: case 85: case 86: return ("Neve", Art.Snow, "#7BA3C8", "#D2E5F4");
            case 95: case 96: case 99: return ("Tempestade", Art.Storm, "#4F5F8C", "#8E83C8");
            default: return day ? ("Ensolarado", Art.Sun, "#5B8EF5", "#A9D0FB") : ("Céu limpo", Art.Moon, "#3440C8", "#8A66D0");
        }
    }

    static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    void ApplyWeather()
    {
        var w = _weather;
        if (w == null) return;
        var (text, art, a, b) = Look(w.Code, w.IsDay);

        WeatherCond.Text = string.IsNullOrWhiteSpace(w.Caption) ? text : w.Caption;
        WeatherTemp.Text = Math.Round(w.Temp).ToString("0", CultureInfo.InvariantCulture);
        var now = DateTime.Now;
        var wd = now.ToString("ddd", PtBr).TrimEnd('.');
        WeatherDate.Text = $"{char.ToUpper(wd[0], PtBr)}{wd.Substring(1)}, {now.Day} de {now.ToString("MMM", PtBr).TrimEnd('.')}";
        WeatherPlace.Text = w.City;
        WeatherPlace.ToolTip = w.City;

        // degradê do cartão, levemente inclinado, como na referência
        var bg = new LinearGradientBrush(C(a), C(b), new Point(0, 0.35), new Point(1, 0.65));
        bg.Freeze();
        WeatherWidget.Background = bg;
        DrawArt(art);
    }

    // ---------------------------------------------------------------- ilustrações (vetor, sem imagens)
    void DrawArt(Art art)
    {
        var c = WeatherArt;
        c.Children.Clear();
        switch (art)
        {
            case Art.Sun:
                Halo(c, 92, 14, 116, .16); Halo(c, 92, 14, 88, .22); SunBall(c, 92, 14, 60);
                break;
            case Art.SunCloud:
                Halo(c, 96, 10, 96, .16); Halo(c, 96, 10, 72, .22); SunBall(c, 96, 10, 46);
                CloudShape(c, 38, 24, .95, "#FFFFFF", "#D5E1F2");
                break;
            case Art.Cloud:
                CloudShape(c, 36, 14, 1.15, "#FFFFFF", "#D3DDEB");
                break;
            case Art.Moon:
                Bokeh(c);
                Crescent(c, 90, 26, 24);
                break;
            case Art.MoonCloud:
                Bokeh(c);
                Crescent(c, 98, 14, 15);
                CloudShape(c, 38, 26, 1.0, "#FFFFFF", "#D9C8F0");
                break;
            case Art.Rain:
            case Art.Storm:
                Drops(c);
                CloudShape(c, 40, -2, 1.0, "#FFFFFF", "#D5E4F2");
                if (art == Art.Storm) Bolt(c, 78, 34);
                break;
            case Art.Snow:
                CloudShape(c, 40, -2, 1.0, "#FFFFFF", "#DCEBF7");
                foreach (var (x, y) in new[] { (50, 46), (66, 54), (82, 44), (96, 56), (108, 46), (58, 60), (90, 62) })
                    Dot(c, x, y, 3.2, "#FFFFFF", .85);
                break;
            case Art.Fog:
                CloudShape(c, 38, 4, 1.05, "#FFFFFF", "#DDE5EA");
                Line(c, 44, 48, 70); Line(c, 60, 56, 54);
                break;
        }
    }

    static void Put(Canvas c, UIElement e, double x, double y)
    {
        Canvas.SetLeft(e, x); Canvas.SetTop(e, y); c.Children.Add(e);
    }

    static void Halo(Canvas c, double cx, double cy, double d, double op) =>
        Put(c, new Ellipse { Width = d, Height = d, Fill = new SolidColorBrush(Colors.White) { Opacity = op } }, cx - d / 2, cy - d / 2);

    static void SunBall(Canvas c, double cx, double cy, double d)
    {
        var br = new RadialGradientBrush { GradientOrigin = new Point(.35, .3), Center = new Point(.5, .5), RadiusX = .6, RadiusY = .6 };
        br.GradientStops.Add(new GradientStop(C("#FFFFBD"), 0));
        br.GradientStops.Add(new GradientStop(C("#FADB5F"), .55));
        br.GradientStops.Add(new GradientStop(C("#F0AC12"), 1));
        Put(c, new Ellipse { Width = d, Height = d, Fill = br }, cx - d / 2, cy - d / 2);
    }

    static void Crescent(Canvas c, double cx, double cy, double r)
    {
        var geo = new CombinedGeometry(GeometryCombineMode.Exclude,
            new EllipseGeometry(new Point(cx, cy), r, r),
            new EllipseGeometry(new Point(cx + r * .42, cy - r * .3), r * .86, r * .86));
        var br = new LinearGradientBrush(C("#FFEB8C"), C("#F2AE1A"), 60);
        c.Children.Add(new WPath { Data = geo, Fill = br });
    }

    // nuvem = união de um retângulo arredondado e dois círculos (medidas para escala 1: 70 x 40)
    static void CloudShape(Canvas c, double x, double y, double s, string top, string bottom)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new RectangleGeometry(new Rect(0, 16, 70, 24), 12, 12));
        g.Children.Add(new EllipseGeometry(new Point(26, 19), 17, 17));
        g.Children.Add(new EllipseGeometry(new Point(47, 21), 13, 13));
        var br = new LinearGradientBrush(C(top), C(bottom), 90);
        var p = new WPath { Data = g, Fill = br, Opacity = .96 };
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(s, s));
        tg.Children.Add(new TranslateTransform(x, y));
        p.RenderTransform = tg;
        c.Children.Add(p);
    }

    static void Dot(Canvas c, double cx, double cy, double r, string color, double op) =>
        Put(c, new Ellipse { Width = 2 * r, Height = 2 * r, Fill = new SolidColorBrush(C(color)) { Opacity = op } }, cx - r, cy - r);

    static void Bokeh(Canvas c)
    {
        foreach (var (x, y, r, o) in new[] { (14, 12, 4.0, .22), (34, 30, 2.5, .3), (52, 8, 3.0, .2), (24, 50, 3.0, .18), (60, 46, 2.0, .28), (8, 36, 2.0, .25) })
            Dot(c, x, y, r, "#FFFFFF", o);
    }

    static void Drops(Canvas c)
    {
        foreach (var (x, y) in new[] { (44, 38), (58, 46), (72, 36), (86, 48), (100, 38), (112, 50), (52, 56), (80, 58), (94, 60) })
        {
            var r = new Rectangle { Width = 3, Height = 11, RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(Colors.White) { Opacity = .55 },
                                    RenderTransformOrigin = new Point(.5, .5), RenderTransform = new RotateTransform(20) };
            Put(c, r, x, y);
        }
    }

    static void Bolt(Canvas c, double x, double y)
    {
        var p = new WPath
        {
            Data = Geometry.Parse("M8,0 L0,13 L6,13 L3,26 L15,9 L9,9 L12,0 Z"),
            Fill = new LinearGradientBrush(C("#FFF08A"), C("#F5B800"), 90)
        };
        Put(c, p, x, y);
    }

    static void Line(Canvas c, double x, double y, double w) =>
        Put(c, new Rectangle { Width = w, Height = 3, RadiusX = 1.5, RadiusY = 1.5, Fill = new SolidColorBrush(Colors.White) { Opacity = .45 } }, x, y);
}
