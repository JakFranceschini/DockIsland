using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DockIsland;

// Volume do sistema na pílula (no lugar do popup do Windows)
public partial class MainWindow
{
    const double VolW = 240;        // largura da pílula mostrando o volume
    const double VolTrackW = 130;   // largura da barra (igual ao XAML)

    // 0 = pílula normal, 1 = pílula mostrando o volume
    public static readonly DependencyProperty VolPeekProperty =
        DependencyProperty.Register(nameof(VolPeek), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, (d, e) => { var w = (MainWindow)d; w.ApplyProgress(w.Progress); }));

    public double VolPeek
    {
        get => (double)GetValue(VolPeekProperty);
        set => SetValue(VolPeekProperty, value);
    }

    readonly DispatcherTimer _volTimer = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    readonly DispatcherTimer _volWatch = new() { Interval = TimeSpan.FromMilliseconds(100) };
    VolumeKeyHook _volHook;
    double _watchLevel = -1;
    bool _watchMuted;
    bool _volFsPeek;    // pílula do volume aparecendo por cima de um programa em tela cheia
    int _volFsGen;      // invalida fades antigos quando chega uma nova tecla
    bool _volFsRaised;  // a janela já subiu por cima do programa em tela cheia
    DispatcherTimer _volRaise;   // espera o WPF desenhar a pílula do volume antes de a janela subir

    void InitVolumePill()
    {
        _volTimer.Tick += (_, __) => HideVolumePill();

        // detecta mudanças de volume vindas de qualquer origem (bandeja, outros apps, troca de dispositivo)
        _volWatch.Tick += (_, __) =>
        {
            if (!AudioVolume.TryGet(out var level, out var muted)) return;
            if (_watchLevel < 0) { _watchLevel = level; _watchMuted = muted; return; }
            if (Math.Abs(level - _watchLevel) < 0.0005 && muted == _watchMuted) return;
            _watchLevel = level; _watchMuted = muted;
            OnSystemVolumeChanged(level, muted);
        };
        _volWatch.Start();

        // teclas de volume: a ilha trata e o Windows não mostra o popup
        _volHook = new VolumeKeyHook(OnVolumeKey);
    }

    void ShutdownVolumePill()
    {
        _volWatch.Stop();
        _volTimer.Stop();
        _volHook?.Dispose();
        _volHook = null;
    }

    // Tecla de volume (mudo / diminuir / aumentar), em passos de 2% como o Windows
    void OnVolumeKey(int vk)
    {
        if (!AudioVolume.TryGet(out var level, out var muted)) return;
        double lv = level;

        if (vk == VolumeKeyHook.VK_VOLUME_MUTE)
        {
            muted = !muted;
            AudioVolume.SetMute(muted);
        }
        else
        {
            double pct = Math.Round(lv * 100) + (vk == VolumeKeyHook.VK_VOLUME_UP ? 2 : -2);
            lv = Math.Clamp(pct, 0, 100) / 100.0;
            AudioVolume.SetLevel((float)lv);   // mexer no volume tira do mudo
            if (lv > 0) muted = false;
        }

        _watchLevel = lv;
        _watchMuted = muted;
        OnSystemVolumeChanged(lv, muted);
    }

    void OnSystemVolumeChanged(double level, bool muted)
    {
        if (Progress > 0.05)
        {
            // ilha expandida: só mantém a barra da aba Sistema em dia
            if (!_volDrag) ShowVolume(level, muted);
            return;
        }
        ShowVolumePill(level, muted);
    }

    void ShowVolumePill(double level, bool muted)
    {
        if (Progress > 0.05) return;

        VolPeekIcon.Text = (muted || level <= 0.001) ? "\uE74F" : "\uE767";
        VolPeekFill.Width = VolTrackW * Math.Clamp(level, 0, 1);
        VolPeekFill.Background = muted ? SysDim : SysWhite;
        VolPeekText.Text = Math.Round(level * 100).ToString();

        HideTrackPill();   // a faixa nova dá lugar ao volume
        _volTimer.Stop();
        _volFsGen++;

        if (TopmostGuard.Fullscreen)
        {
            // Tela cheia: nada de pílula normal. A pílula do volume já nasce pronta (sem expandir a partir da
            // pílula normal) e entra/sai só com fade. A janela só sobe por cima do programa DEPOIS de o WPF
            // ter desenhado a pílula do volume (senão aparece um instante o último quadro: a pílula normal).
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            BeginAnimation(VolPeekProperty, null);
            VolPeek = 1;                          // já na forma de volume
            if (!_volFsPeek)
            {
                _volFsPeek = true;
                _volFsRaised = false;
                BeginAnimation(OpacityProperty, null);
                Opacity = 0;                      // invisível até estar desenhada
                UpdateLayout();
                _volRaise ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
                _volRaise.Stop();
                _volRaise.Tick -= VolRaiseTick;
                _volRaise.Tick += VolRaiseTick;
                _volRaise.Start();
            }
            else if (_volFsRaised)
            {
                BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
            }
            _volTimer.Start();
            return;
        }

        if (VolPeek < 0.999)
        {
            BeginAnimation(VolPeekProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 }
            });
        }
        _volTimer.Start();
    }

    void VolRaiseTick(object sender, EventArgs e)
    {
        _volRaise.Stop();
        if (!_volFsPeek || _volFsRaised) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (!TopmostGuard.Fullscreen)
        {
            // a tela cheia acabou enquanto esperava: segue o caminho normal
            _volFsPeek = false;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            return;
        }
        _volFsRaised = true;
        TopmostGuard.PeekShow(hwnd);
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
    }

    void HideVolumePill()
    {
        _volTimer.Stop();
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        _volRaise?.Stop();
        if (_volFsPeek)
        {
            if (TopmostGuard.Fullscreen && !_volFsRaised)
            {
                // nunca chegou a subir: só desfaz
                _volFsGen++;
                FinishVolumeFsPeek(hwnd);
                return;
            }
            if (TopmostGuard.Fullscreen)
            {
                // tela cheia: só some com fade (sem encolher para a pílula normal) e volta para trás do programa
                int gen = ++_volFsGen;
                var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };
                fade.Completed += (_, __) =>
                {
                    if (gen != _volFsGen) return;   // chegou outra tecla de volume no meio do fade
                    FinishVolumeFsPeek(hwnd);
                };
                BeginAnimation(OpacityProperty, fade);
                return;
            }

            // a tela cheia terminou durante a aparição: volta ao normal (a ilha já está visível)
            _volFsPeek = false;
            _volFsRaised = false;
            _volFsGen++;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            BeginAnimation(VolPeekProperty, null);
            VolPeek = 1;
        }

        if (VolPeek <= 0) { TopmostGuard.PeekHide(hwnd); return; }
        var hide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        hide.Completed += (_, __) =>
        {
            if (VolPeek <= 0.001) TopmostGuard.PeekHide(hwnd);
        };
        BeginAnimation(VolPeekProperty, hide);
    }

    // Fim da aparição em tela cheia: volta atrás do programa e só então restaura a pílula normal (já invisível)
    void FinishVolumeFsPeek(IntPtr hwnd)
    {
        _volFsPeek = false;
        _volFsRaised = false;
        TopmostGuard.PeekHide(hwnd);
        BeginAnimation(VolPeekProperty, null);
        VolPeek = 0;
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }
}
