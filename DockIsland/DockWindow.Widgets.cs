using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DockIsland;

// Widgets do dock (ver DockWidgets.cs). O MainWindow empurra os dados; o dock só desenha.
// O dock de widgets mostra UM widget por vez; rolar o mouse sobre ele troca entre os widgets disponíveis.
public partial class DockWindow
{
    /// Diz se o widget está ligado nos Ajustes (definido pelo App)
    public Func<string, bool> WidgetEnabled;

    /// Comando de música pedido pelo widget: "playpause", "next" ou "prev" (o MainWindow executa)
    public event Action<string> MediaCommand;

    static readonly Geometry PlayG = Frozen("M7,4 L20,12 L7,20 Z");
    static readonly Geometry PauseG = Frozen("M6,4 L10,4 L10,20 L6,20 Z M14,4 L18,4 L18,20 L14,20 Z");

    static Geometry Frozen(string d) { var g = Geometry.Parse(d); g.Freeze(); return g; }

    bool _musicActive;

    // Música: chamado pelo MainWindow sempre que o player, a faixa ou o estado (tocando/pausado) muda
    public void UpdateMusic(bool active, string title, string artist, ImageSource art, bool playing)
    {
        _musicActive = active;
        string t = string.IsNullOrWhiteSpace(title) ? "Sem título" : title;
        string a = artist ?? "";
        bool changed = MusicTitle.Text != t || MusicArtist.Text != a;
        MusicTitle.Text = t;
        MusicArtist.Text = a;
        MusicArtistHost.Visibility = string.IsNullOrWhiteSpace(artist) ? Visibility.Collapsed : Visibility.Visible;
        if (changed) RefreshMarquee();   // texto novo: recalcula se precisa rolar (play/pause não reinicia a rolagem)
        MusicArtBrush.ImageSource = art;
        MusicPlayIcon.Data = playing ? PauseG : PlayG;
        ApplyWidgets();
    }

    // ---- Letreiro: título/artista grandes demais rolam para o lado e voltam ----
    bool _marqueeHooked;

    void RefreshMarquee()
    {
        if (!_marqueeHooked)
        {
            _marqueeHooked = true;
            // o tamanho da área só existe depois do layout (e muda quando o widget aparece): recalcula nessas horas
            // (adiado: não dá para mexer no layout de dentro do próprio ciclo de layout)
            MusicTitleHost.SizeChanged += (_, __) => Dispatcher.BeginInvoke(new Action(() =>
                Marquee.Update(MusicTitleHost, MusicTitle, MusicTitleShift)), System.Windows.Threading.DispatcherPriority.Loaded);
            MusicArtistHost.SizeChanged += (_, __) => Dispatcher.BeginInvoke(new Action(() =>
                Marquee.Update(MusicArtistHost, MusicArtist, MusicArtistShift)), System.Windows.Threading.DispatcherPriority.Loaded);
        }
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Marquee.Update(MusicTitleHost, MusicTitle, MusicTitleShift);
            Marquee.Update(MusicArtistHost, MusicArtist, MusicArtistShift);
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// Reaplica quais widgets aparecem (depois de ligar/desligar nos Ajustes)
    public void RefreshWidgets()
    {
        ApplyWidgets();
        _clockKey = null; UpdateClock();   // relógio ligado agora: já mostra a hora certa
        if (On("weather") && _weather == null) _ = RefreshWeatherAsync(false);   // acabou de ser ligado
    }

    bool On(string id) { try { return WidgetEnabled?.Invoke(id) ?? true; } catch { return true; } }

    // ---- Lista dos widgets que existem no dock, na ordem em que a roda do mouse passa por eles ----
    // Widget novo: acrescente aqui (id, visual) e em Available (quando ele tem o que mostrar).
    List<(string Id, FrameworkElement El)> AllWidgets() => new() { ("music", MusicWidget), ("weather", WeatherWidget),
        ("clock", ClockWidget), ("clip", ClipWidget), ("trash", TrashWidget) };

    bool Available(string id) => id switch
    {
        "music" => _musicActive,
        "weather" => _weather != null,
        "clock" => true,
        "clip" => _clipHas,
        "trash" => _trashKnown,
        _ => false
    };

    string _wCurrent;                 // id do widget na tela
    long _lastWheelAt;
    readonly List<string> _wVisible = new();   // ids que podem aparecer agora (ligados e com conteúdo)

    // Todos os widgets têm a mesma largura fixa (228): ver Width no DockWindow.xaml (música, tempo, relógio, copiado, lixeira)

    void ApplyWidgets(bool animate = false, int dir = 1)
    {
        var all = AllWidgets();
        _wVisible.Clear();
        foreach (var w in all) if (Available(w.Id) && On(w.Id)) _wVisible.Add(w.Id);

        int idx = _wVisible.IndexOf(_wCurrent);
        if (idx < 0) idx = 0;
        _wCurrent = _wVisible.Count > 0 ? _wVisible[idx] : null;

        foreach (var w in all)
        {
            bool show = w.Id == _wCurrent;
            var v = show ? Visibility.Visible : Visibility.Collapsed;
            if (w.El.Visibility != v)
            {
                w.El.Visibility = v;
                if (show && animate) AnimateWidgetIn(w.El, dir);
            }
        }

        // o dock de widgets só aparece com algum widget visível
        var sv = _wVisible.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (WidgetsPill.Visibility != sv) WidgetsPill.Visibility = sv;
    }

    // Troca de widget: o novo entra deslizando na vertical (para cima ao avançar, para baixo ao voltar),
    // com fade e um leve zoom, terminando com um pequeno assentamento
    static void AnimateWidgetIn(FrameworkElement el, int dir)
    {
        var scale = new ScaleTransform(0.94, 0.94);
        var shift = new TranslateTransform(0, 12 * dir);
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(shift);
        el.RenderTransformOrigin = new Point(0.5, 0.5);
        el.RenderTransform = group;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(320);
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12 * dir, 0, dur) { EasingFunction = ease });
        var back = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.6 };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = back });
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, dur) { EasingFunction = back });
    }

    // Rolar o mouse sobre o dock de widgets: próximo / anterior (dá a volta no fim)
    void WidgetsPill_Wheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_wVisible.Count < 2) return;
        long now = Environment.TickCount64;
        if (now - _lastWheelAt < 160) return;   // touchpad / roda livre: um passo por vez
        _lastWheelAt = now;

        int idx = Math.Max(0, _wVisible.IndexOf(_wCurrent));
        idx = (idx + (e.Delta < 0 ? 1 : -1) + _wVisible.Count) % _wVisible.Count;
        _wCurrent = _wVisible[idx];
        ApplyWidgets(animate: true, dir: e.Delta < 0 ? 1 : -1);
    }

    // Atalho Win + W: próximo widget (dá a volta no fim), igual à roda do mouse para baixo
    public void NextWidget()
    {
        if (_wVisible.Count < 2) return;
        int idx = Math.Max(0, _wVisible.IndexOf(_wCurrent));
        _wCurrent = _wVisible[(idx + 1) % _wVisible.Count];
        ApplyWidgets(animate: true);
    }

    void MusicPrev_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; MediaCommand?.Invoke("prev"); }
    void MusicPlay_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; MediaCommand?.Invoke("playpause"); }
    void MusicNext_Click(object sender, MouseButtonEventArgs e) { e.Handled = true; MediaCommand?.Invoke("next"); }
}

// Texto em letreiro: cabendo, fica parado; senão rola sempre para a esquerda e recomeça quando termina.
static class Marquee
{
    const double Speed = 32;      // pixels por segundo
    const double HoldStart = 1.6; // pausa no começo (segundos)

    public static void Update(FrameworkElement host, TextBlock tb, TranslateTransform shift)
    {
        // O texto fica num Canvas (sem corte de layout), então mede-se o tamanho natural dele e compara com a área.
        double hostW = host.ActualWidth;
        if (hostW <= 0 || string.IsNullOrEmpty(tb.Text)) return;   // widget escondido: refaz quando aparecer

        tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textW = Math.Ceiling(tb.DesiredSize.Width) + 2;
        string key = tb.Text + "|" + hostW + "|" + textW;
        bool same = (tb.Tag as string) == key;
        tb.Tag = key;

        if (textW - hostW <= 1)   // cabe inteiro: parado
        {
            shift.BeginAnimation(TranslateTransform.XProperty, null);
            shift.X = 0;
            host.OpacityMask = null;
            return;
        }

        if (same && host.OpacityMask != null) return;   // já está rolando do mesmo jeito: não reinicia

        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = 0;

        // borda direita esmaecida para o texto "sumir" suavemente em vez de cortar seco
        double fade = Math.Min(12, hostW / 3) / hostW;
        host.OpacityMask = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Colors.Black, 0),
                new GradientStop(Colors.Black, 1 - fade),
                new GradientStop(Colors.Transparent, 1)
            }, new Point(0, 0), new Point(1, 0));

        // Só para a esquerda: o texto sai inteiro pela esquerda, reaparece pela direita e volta ao lugar; repete
        double tOut = Math.Max(0.8, (textW + 2) / Speed);   // sai pela esquerda
        double tIn = Math.Max(0.6, hostW / Speed);          // entra pela direita até a posição inicial
        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        KeyTime K(double sec) => KeyTime.FromTimeSpan(TimeSpan.FromSeconds(sec));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, K(HoldStart)));                       // parado no começo
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(-textW - 2, K(HoldStart + tOut)));       // rola para a esquerda
        anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(hostW, K(HoldStart + tOut + 0.001)));   // (já invisível) vai para a direita
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, K(HoldStart + tOut + 0.001 + tIn)));   // entra pela direita
        shift.BeginAnimation(TranslateTransform.XProperty, anim);
    }
}
