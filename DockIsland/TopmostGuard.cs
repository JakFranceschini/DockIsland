using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace DockIsland;

// Mantém a ilha, o dock e o lançador SEMPRE acima de todas as outras janelas (inclusive as "sempre na frente"
// e as maximizadas). A única exceção é um programa em TELA CHEIA de verdade (jogo, vídeo, F11): aí as nossas
// janelas ficam atrás dele e voltam sozinhas quando a tela cheia termina.
//
// Como evita que a ilha / o dock "sumam":
//  1) Confere a cada 120 ms (e na hora, a cada evento do Windows: janela da frente mudou, janela restaurada,
//     mostrada, "descoberta", fim de mover/redimensionar, troca de área de trabalho/UAC) se algo cobriu as
//     nossas janelas, se elas perderam a camada "sempre na frente" ou se foram escondidas/minimizadas.
//  2) Rede de segurança: se a ilha/dock ficarem presos invisíveis (opacidade 0), sem aceitar clique ou fora da
//     tela, ou se a visão geral (Win+Tab) ficar "aberta" sem existir, o estado é corrigido sozinho.
//  3) Ao voltar da suspensão, desbloquear a sessão ou mudar o monitor/resolução, tudo é recentrado, redesenhado
//     e trazido para a frente (várias vezes, enquanto o Windows termina de se reorganizar).
static class TopmostGuard
{
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RC r);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint evMin, uint evMax, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)] struct RC { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int Size; public RC Monitor, Work; public uint Flags; }

    static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;
    const uint GW_HWNDFIRST = 0, GW_HWNDNEXT = 2, GW_HWNDPREV = 3;
    const uint GA_ROOT = 2;
    const uint WINEVENT_SKIPOWNPROCESS = 0x0002;   // não ouve eventos das nossas próprias janelas (evita laço)
    const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    const int WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_EX_TOPMOST = 0x00000008;
    const int WS_EX_TRANSPARENT = 0x00000020, WS_EX_TOOLWINDOW = 0x00000080;
    const int DWMWA_CLOAKED = 14;

    // Eventos do Windows que podem mudar quem está na frente
    const uint EV_FOREGROUND = 0x0003, EV_MOVESIZEEND = 0x000B, EV_MINIMIZEEND = 0x0017, EV_DESKTOPSWITCH = 0x0020;
    const uint EV_OBJ_SHOW = 0x8002, EV_OBJ_UNCLOAKED = 0x8018;

    static readonly uint Pid = (uint)Environment.ProcessId;
    static DispatcherTimer _timer;
    static bool _fullscreen;
    static IntPtr _fsHwnd;   // janela em tela cheia que está na frente (as nossas ficam logo atrás dela)
    static int _fullTicks;   // quantos ciclos seguidos a tela cheia foi detectada (evita falso positivo passageiro)

    static WinEventProc _evProc;        // referência fixa: o Windows chama de volta enquanto o programa roda
    static DispatcherTimer _burst;
    static int _burstTicks;
    static int _scrW, _scrH;

    static long _lastRaise, _lastSanity, _ghostSince, _lastTickAt;
    static DispatcherTimer _recover;
    static int _recoverLeft;

    // janelas nossas que estão "espiando" por cima de um programa em tela cheia (pílula de volume)
    static readonly HashSet<IntPtr> _peeking = new();

    // desde quando cada janela está com opacidade baixa / "transparente ao clique" sem motivo
    sealed class Stuck { public long Opacity, Click; }
    static readonly Dictionary<IntPtr, Stuck> _stuck = new();

    // ---- aparição rápida em tela cheia (ex.: pílula de volume) ----
    // Em tela cheia a ilha fica atrás do programa. Para mostrar SÓ a pílula do volume, a janela sobe por cima
    // por alguns segundos, sem pegar o foco e sem reagir ao mouse (cliques passam direto), e depois volta para trás.
    public static bool Fullscreen => _fullscreen;

    public static void PeekShow(IntPtr h)
    {
        try
        {
            if (!_fullscreen || h == IntPtr.Zero) return;
            _peeking.Add(h);
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT);
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    public static void PeekHide(IntPtr h)
    {
        try
        {
            if (h == IntPtr.Zero) return;
            _peeking.Remove(h);
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) & ~WS_EX_TRANSPARENT);
            if (_fullscreen) PushBehind(h);
        }
        catch { }
    }

    public static void Start()
    {
        if (_timer != null) return;
        _scrW = GetSystemMetrics(0); _scrH = GetSystemMetrics(1);
        _lastTickAt = Environment.TickCount64;
        _timer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(120) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // Assim que algo muda na pilha de janelas, confere na hora (e algumas vezes logo depois, enquanto o
        // Windows termina de reorganizar as janelas) em vez de esperar o próximo ciclo.
        _burst = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromMilliseconds(60) };
        _burst.Tick += (_, _) => { EnsureOnTop(false); if (++_burstTicks >= 8) _burst.Stop(); };
        try
        {
            _evProc = OnWinEvent;
            foreach (var ev in new[] { EV_FOREGROUND, EV_MOVESIZEEND, EV_MINIMIZEEND, EV_DESKTOPSWITCH, EV_OBJ_SHOW, EV_OBJ_UNCLOAKED })
                SetWinEventHook(ev, ev, IntPtr.Zero, _evProc, 0, 0, WINEVENT_SKIPOWNPROCESS);   // fora do processo, na thread da interface
        }
        catch { }

        // Suspensão, bloqueio/desbloqueio e troca de monitor/resolução: o Windows pode deixar janelas
        // transparentes em branco, fora da tela ou sem a camada "sempre na frente".
        try
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (_, _) => ScheduleRecover();
            Microsoft.Win32.SystemEvents.SessionSwitch += (_, e) =>
            {
                if (e.Reason == Microsoft.Win32.SessionSwitchReason.SessionUnlock ||
                    e.Reason == Microsoft.Win32.SessionSwitchReason.SessionLogon ||
                    e.Reason == Microsoft.Win32.SessionSwitchReason.ConsoleConnect ||
                    e.Reason == Microsoft.Win32.SessionSwitchReason.RemoteConnect) ScheduleRecover();
            };
            Microsoft.Win32.SystemEvents.PowerModeChanged += (_, e) =>
            {
                if (e.Mode == Microsoft.Win32.PowerModes.Resume) ScheduleRecover();
            };
        }
        catch { }
    }

    static void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (ev == EV_OBJ_SHOW || ev == EV_OBJ_UNCLOAKED)
            {
                // só janelas de nível superior (controles internos disparam milhares de eventos)
                if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || GetAncestor(hwnd, GA_ROOT) != hwnd) return;
            }
            _burstTicks = 0;
            _burst.Stop(); _burst.Start();
            if (ev != EV_OBJ_SHOW && ev != EV_OBJ_UNCLOAKED && ev != EV_MOVESIZEEND) EnsureOnTop(true);
        }
        catch { }
    }

    // As nossas janelas visíveis, de baixo para cima (a ilha fica por último = por cima de todas)
    static IntPtr[] OurWindows()
    {
        try
        {
            var app = Application.Current;
            if (app == null) return Array.Empty<IntPtr>();
            int Rank(Window w) => w is MainWindow ? 3 : w is ConfirmWindow ? 2 : w is LauncherWindow ? 1 : 0;
            return app.Windows.Cast<Window>()
                .Where(w => (w is MainWindow || w is DockWindow || w is LauncherWindow || w is ConfirmWindow) && w.IsVisible)
                .OrderBy(Rank)
                .Select(w => new WindowInteropHelper(w).Handle)
                .Where(h => h != IntPtr.Zero)
                .ToArray();
        }
        catch { return Array.Empty<IntPtr>(); }
    }

    // Só a ilha e o dock (as janelas que podem "sumir")
    static List<Window> Chrome()
    {
        try
        {
            var app = Application.Current;
            if (app == null) return new List<Window>();
            return app.Windows.Cast<Window>().Where(w => (w is MainWindow || w is DockWindow) && w.IsVisible).ToList();
        }
        catch { return new List<Window>(); }
    }

    static void Tick()
    {
        try
        {
            long now = Environment.TickCount64;

            // o relógio "pulou": o computador estava suspenso (ou o programa ficou congelado). Refaz tudo.
            if (now - _lastTickAt > 4000) ScheduleRecover();
            _lastTickAt = now;

            CheckScreenChange();
            CheckOverviewGhost(now);
            if (SwitcherWindow.OverviewOpen) return;   // Win+Tab aberto: ilha e dock ficam escondidos, não sobe nada
            var ours = OurWindows();
            if (ours.Length == 0) return;

            HealChrome(now);
            CheckOnScreen(now);

            // tela cheia só vale depois de 2 conferências seguidas (~0,25 s): trocas rápidas de janela,
            // Alt+Tab e animações não fazem a ilha e o dock sumirem
            bool seen = IsFullscreenAppActive(ours[ours.Length - 1]);
            _fullTicks = seen ? Math.Min(_fullTicks + 1, 10) : 0;
            bool full = _fullscreen ? _fullTicks > 0 : _fullTicks >= 2;

            if (full)
            {
                // programa em tela cheia: as nossas janelas ficam ATRÁS dele (e continuam atrás enquanto durar)
                _fullscreen = true;
                if (_fsHwnd != IntPtr.Zero)
                    foreach (var h in ours)
                        if (!_peeking.Contains(h) && !IsBelow(h, _fsHwnd)) PushBehind(h);
                return;
            }

            if (_fullscreen)
            {
                // a tela cheia terminou: volta para a frente de tudo
                _fullscreen = false;
                _fsHwnd = IntPtr.Zero;
                _peeking.Clear();
                Raise(ours);
                return;
            }

            EnsureOnTop(ours, false);
        }
        catch { }
    }

    // ---- ilha/dock presos ----------------------------------------------------------------------------------

    // A visão geral (Win+Tab) esconde a ilha e o dock (opacidade 0 + clique atravessando). Se ela fechar de um
    // jeito inesperado e o aviso de "fechou" não chegar, a ilha e o dock ficariam invisíveis para sempre e o
    // guard parado. Se OverviewOpen está ligado mas nenhuma visão geral existe na tela por 2 s, desfaz.
    static void CheckOverviewGhost(long now)
    {
        if (!SwitcherWindow.OverviewOpen) { _ghostSince = 0; return; }
        bool alive = Application.Current.Windows.OfType<SwitcherWindow>().Any(w => w.IsVisible);
        if (alive) { _ghostSince = 0; return; }
        if (_ghostSince == 0) { _ghostSince = now; return; }
        if (now - _ghostSince > 2000)
        {
            _ghostSince = 0;
            SwitcherWindow.RestoreChromeNow();
        }
    }

    // Fora de tela cheia e sem visão geral, a ilha e o dock nunca ficam "meio apagados" por mais que um fade
    // (≤ 0,3 s) nem ignoram o mouse. Se ficarem, algum fade/estado ficou preso: volta ao normal.
    static void HealChrome(long now)
    {
        if (_fullscreen || SwitcherWindow.OverviewOpen) { _stuck.Clear(); return; }
        foreach (var w in Chrome())
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) continue;
                if (!_stuck.TryGetValue(h, out var st)) _stuck[h] = st = new Stuck();

                if (w.Opacity < 0.99)
                {
                    if (st.Opacity == 0) st.Opacity = now;
                    else if (now - st.Opacity > 2500)
                    {
                        w.BeginAnimation(UIElement.OpacityProperty, null);
                        w.Opacity = 1;
                        st.Opacity = 0;
                    }
                }
                else st.Opacity = 0;

                if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0)
                {
                    if (st.Click == 0) st.Click = now;
                    else if (now - st.Click > 1000)
                    {
                        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) & ~WS_EX_TRANSPARENT);
                        st.Click = 0;
                    }
                }
                else st.Click = 0;
            }
            catch { }
        }
    }

    // A ilha e o dock têm posição fixa (centro do topo / centro da base). Se por qualquer motivo saírem da
    // tela principal (monitor desconectado, RDP, bug do Windows), voltam para o lugar.
    static void CheckOnScreen(long now)
    {
        if (now - _lastSanity < 1000) return;
        _lastSanity = now;
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        bool bad = false;
        foreach (var w in Chrome())
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero || !GetWindowRect(h, out var r)) continue;
            if (r.Left <= -9000) continue;   // fora da tela de propósito (aquecimento)
            if (!(r.Right > 0 && r.Left < sw && r.Bottom > 0 && r.Top < sh)) bad = true;
        }
        if (bad) RecenterAll(false);
    }

    // ---- recuperação (suspensão, bloqueio, monitor) --------------------------------------------------------

    static void ScheduleRecover()
    {
        try
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                _recoverLeft = 4;
                if (_recover == null)
                {
                    _recover = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
                    _recover.Tick += (_, _) => { Recover(); if (--_recoverLeft <= 0) _recover.Stop(); };
                }
                Recover();
                _recover.Stop(); _recover.Start();   // repete mais 4 vezes: o Windows leva um tempo para se reorganizar
            }));
        }
        catch { }
    }

    static void Recover()
    {
        try
        {
            if (Application.Current == null) return;
            RecenterAll(false);
            if (SwitcherWindow.OverviewOpen) return;

            foreach (var w in Chrome())
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) continue;
                if (!IsWindowVisible(h)) ShowWindow(h, 4);   // SW_SHOWNOACTIVATE
                w.InvalidateVisual();                         // janela transparente que ficou em branco: redesenha
            }
            if (!_fullscreen && !FullscreenLikely())
                foreach (var h in OurWindows())
                    SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Glass.Refresh();   // o fundo borrado atrás da ilha/dock pode estar desatualizado
        }
        catch { }
    }

    // ---- ficar por cima -----------------------------------------------------------------------------------

    // Fora de tela cheia: as nossas janelas voltam para a frente se algo as cobriu, se perderam a camada
    // "sempre na frente" ou se o Windows as escondeu/minimizou (Win+D, Win+M, "mostrar área de trabalho").
    static void EnsureOnTop(bool force)
    {
        try
        {
            if (_fullscreen || SwitcherWindow.OverviewOpen) return;
            var ours = OurWindows();
            if (ours.Length == 0) return;
            if (FullscreenLikely()) return;   // um jogo/vídeo está abrindo em tela cheia: não sobe por cima dele (o ciclo confirma em ~0,25 s)
            EnsureOnTop(ours, force);
        }
        catch { }
    }

    static void EnsureOnTop(IntPtr[] ours, bool force)
    {
        bool covered = false;
        foreach (var h in ours)
        {
            if (!IsWindowVisible(h) || IsIconic(h))
            {
                ShowWindow(h, 4);                          // SW_SHOWNOACTIVATE: volta sem roubar o foco
                if (IsIconic(h)) ShowWindow(h, 9);         // SW_RESTORE, se ainda estiver minimizada
                covered = true; force = true;
            }
            if ((GetWindowLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) == 0) { covered = true; force = true; }   // perdeu a camada: urgente
            else if (IsCoveredByForeignWindow(h)) covered = true;
        }
        if (!covered) return;

        // Se um programa fica se re-elevando sem parar, não brigamos a cada milissegundo (evita piscar)
        long now = Environment.TickCount64;
        if (!force && now - _lastRaise < 100) return;
        _lastRaise = now;
        Raise(ours);
    }

    static void Raise(IntPtr[] ours)
    {
        foreach (var h in ours)
            SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // Em tela cheia: coloca a janela logo ATRÁS do programa. (HWND_NOTOPMOST sozinho deixaria a janela por cima
    // de todas as janelas normais, inclusive do programa em tela cheia.)
    static void PushBehind(IntPtr h)
    {
        var fg = _fsHwnd;
        SetWindowPos(h, (fg != IntPtr.Zero && IsWindow(fg)) ? fg : HWND_NOTOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // true se a janela h está abaixo de other na pilha de janelas
    static bool IsBelow(IntPtr h, IntPtr other)
    {
        int guard = 0;
        for (var p = GetWindow(h, GW_HWNDPREV); p != IntPtr.Zero && guard < 800; p = GetWindow(p, GW_HWNDPREV), guard++)
            if (p == other) return true;
        return false;
    }

    // Resolução/monitor principal mudou (ou voltou do repouso): recentra a ilha e o dock para não ficarem fora da tela
    static void CheckScreenChange()
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        if (w == _scrW && h == _scrH) return;
        RecenterAll(true);
        if (!_fullscreen && !SwitcherWindow.OverviewOpen && !FullscreenLikely()) Raise(OurWindows());
    }

    // resize = true: também refaz a largura do dock para a nova resolução
    static void RecenterAll(bool resize)
    {
        int w = GetSystemMetrics(0), h = GetSystemMetrics(1);
        _scrW = w; _scrH = h;
        var app = Application.Current;
        if (app == null) return;
        foreach (var win in app.Windows.Cast<Window>().ToList())
        {
            if (win is not (MainWindow or DockWindow)) continue;
            double k = 1.0;
            try { k = (PresentationSource.FromVisual(win) as HwndSource)?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0; } catch { }
            if (k <= 0) k = 1.0;
            double sw = w * k, sh = h * k;
            if (win is MainWindow m) { m.Left = (sw - m.Width) / 2; m.Top = 0; }
            else if (win is DockWindow d)
            {
                if (resize) d.Width = Math.Min(sw, 1800);
                d.Left = (sw - d.Width) / 2; d.Top = sh - d.Height;
            }
        }
    }

    // Percorre as janelas que estão acima da nossa na pilha; se alguma é de outro programa, está visível
    // e se sobrepõe à nossa, estamos cobertos.
    static bool IsCoveredByForeignWindow(IntPtr h)
    {
        if (!GetWindowRect(h, out var mine) || mine.Left <= -10000) return false;   // fora da tela (aquecimento)
        int guard = 0;
        for (var p = GetWindow(h, GW_HWNDPREV); p != IntPtr.Zero && guard < 400; p = GetWindow(p, GW_HWNDPREV), guard++)
        {
            if (!IsWindowVisible(p) || IsIconic(p)) continue;
            GetWindowThreadProcessId(p, out uint pid);
            if (pid == Pid) continue;                                   // nossas janelas e menus
            if (DwmGetWindowAttribute(p, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) continue;   // janelas "fantasma" do Windows
            if ((GetWindowLong(p, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) continue;   // camada que deixa o clique passar (overlay): não "cobre"
            if (!GetWindowRect(p, out var r)) continue;
            if (r.Right - r.Left <= 1 || r.Bottom - r.Top <= 1) continue;
            bool overlap = r.Left < mine.Right && r.Right > mine.Left && r.Top < mine.Bottom && r.Bottom > mine.Top;
            if (overlap) return true;
        }
        return false;
    }

    // ---- detecção de tela cheia ---------------------------------------------------------------------------
    // Programa em tela cheia de verdade (jogo, vídeo, F11): uma janela de outro programa cobre o monitor da ilha
    // e não é uma janela comum maximizada. Janelas maximizadas (inclusive as sem barra de título do Chrome,
    // Spotify, VS Code, Discord...) NÃO contam: a ilha e o dock continuam por cima delas.
    //
    // Sinais usados (qualquer um basta):
    //  1) o Windows avisa que um jogo Direct3D está em tela cheia exclusiva (SHQueryUserNotificationState = 3);
    //  2) a janela da frente cobre o monitor (com 2 px de tolerância) e não é maximizada comum;
    //  3) se a janela da frente é só um overlay (Steam, Discord, Game Bar, ferramenta...), olha a pilha de
    //     janelas de cima para baixo e decide pela primeira janela que cobre o monitor.
    // (Não usa o estado "ocupado" do SHQueryUserNotificationState: com a barra de tarefas oculta ele acusa
    // "tela cheia" em qualquer janela maximizada, e era isso que fazia a ilha e o dock sumirem.)
    static bool IsFullscreenAppActive(IntPtr island)
    {
        try
        {
            var islandMon = MonitorFromWindow(island, 2);   // MONITOR_DEFAULTTONEAREST
            var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(islandMon, ref mi)) return false;
            var fg = GetForegroundWindow();

            // 1) jogo Direct3D em tela cheia exclusiva
            if (fg != IntPtr.Zero && D3DFullscreenState() && IsPlainForeignWindow(fg))
            {
                _fsHwnd = fg;
                GetWindowThreadProcessId(fg, out uint dpid);
                Note("d3d:" + fg, "TELA CHEIA (Windows informou jogo D3D em tela cheia) " + ClassOf(fg), dpid);
                return true;
            }

            // 2) janela da frente
            if (fg != IntPtr.Zero)
            {
                int r = Judge(fg, true, islandMon, mi, out var info, out uint pid);
                if (info.Length > 0) Note("fg:" + fg + ":" + r, (r == 1 ? "TELA CHEIA " : "ignorada ") + info, pid);
                if (r == 1) { _fsHwnd = fg; return true; }
                if (r != -1) return false;   // janela comum, shell, outro monitor ou nossa: não é tela cheia
            }

            // 3) a da frente é um overlay/ferramenta (ou não há): a primeira janela que cobre o monitor decide
            int guard = 0;
            for (var p = GetWindow(island, GW_HWNDFIRST); p != IntPtr.Zero && guard < 400; p = GetWindow(p, GW_HWNDNEXT), guard++)
            {
                if (p == fg) continue;
                int r2 = Judge(p, false, islandMon, mi, out var info2, out uint pid2);
                if (info2.Length > 0) Note("z:" + p + ":" + r2, (r2 == 1 ? "TELA CHEIA (sob overlay) " : "ignorada (sob overlay) ") + info2, pid2);
                if (r2 == 1) { _fsHwnd = p; return true; }
                if (r2 == 0) return false;
            }
            return false;
        }
        catch { return false; }
    }

    // 1 = tela cheia | 0 = janela comum ou do shell (não é tela cheia) | -1 = ignorar (overlay, ferramenta,
    // invisível) | -2 = não cobre este monitor | -3 = janela nossa
    static int Judge(IntPtr w, bool isFg, IntPtr islandMon, MONITORINFO mi, out string info, out uint pid)
    {
        info = ""; pid = 0;
        if (!IsWindowVisible(w) || IsIconic(w)) return -1;
        GetWindowThreadProcessId(w, out pid);
        if (pid == Pid) return -3;
        if (DwmGetWindowAttribute(w, DWMWA_CLOAKED, out int cloaked, 4) == 0 && cloaked != 0) return -1;   // janela "fantasma" do Windows
        string cls = ClassOf(w);
        if (IsShellClass(cls)) return isFg ? 0 : -1;   // área de trabalho / barra de tarefas / menu Iniciar / visão de tarefas
        int ex = GetWindowLong(w, GWL_EXSTYLE);
        // camadas transparentes e janelas de ferramenta (overlays de captura, jogo, gravação...) não são um programa em tela cheia
        if ((ex & (WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW)) != 0) return -1;
        if (!GetWindowRect(w, out var r)) return -1;
        if (MonitorFromWindow(w, 2) != islandMon) return -2;   // tela cheia em outro monitor não esconde a ilha

        const int tol = 2;
        bool covers = r.Left <= mi.Monitor.Left + tol && r.Top <= mi.Monitor.Top + tol &&
                      r.Right >= mi.Monitor.Right - tol && r.Bottom >= mi.Monitor.Bottom - tol;
        if (!covers) return -2;

        int style = GetWindowLong(w, GWL_STYLE);
        bool hasCaption = (style & WS_CAPTION) == WS_CAPTION;
        bool hasThick = (style & WS_THICKFRAME) != 0;
        bool zoomed = IsZoomed(w);
        bool exact = Math.Abs(r.Left - mi.Monitor.Left) <= tol && Math.Abs(r.Top - mi.Monitor.Top) <= tol &&
                     Math.Abs(r.Right - mi.Monitor.Right) <= tol && Math.Abs(r.Bottom - mi.Monitor.Bottom) <= tol;

        int res;
        if (hasCaption) res = (!zoomed && exact) ? 1 : 0;   // com barra de título: só conta se NÃO é maximizada e tem exatamente o tamanho do monitor (jogo "sem borda" que manteve o estilo)
        else if (hasThick && zoomed) res = 0;               // maximizada sem barra de título (app com visual próprio)
        else res = 1;

        info = $"{cls} style=0x{style:X8} ex=0x{ex:X8} janela=({r.Left},{r.Top},{r.Right},{r.Bottom}) " +
               $"monitor=({mi.Monitor.Left},{mi.Monitor.Top},{mi.Monitor.Right},{mi.Monitor.Bottom}) maximizada={zoomed}";
        return res;
    }

    static bool IsShellClass(string cls) =>
        cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "XamlExplorerHostIslandWindow"
            or "MultitaskingViewFrame" or "TaskSwitcherWnd" or "ForegroundStaging" or "Shell_LightDismissOverlay"
            or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland";

    static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    // janela de outro programa, visível, que não é do shell (candidata a ser o jogo)
    static bool IsPlainForeignWindow(IntPtr w)
    {
        if (!IsWindowVisible(w) || IsIconic(w)) return false;
        GetWindowThreadProcessId(w, out uint pid);
        if (pid == Pid) return false;
        return !IsShellClass(ClassOf(w));
    }

    // QUNS_RUNNING_D3D_FULL_SCREEN (3): só jogos Direct3D em tela cheia exclusiva; não confunde com janela maximizada
    static bool D3DFullscreenState()
    {
        try { return SHQueryUserNotificationState(out int st) == 0 && st == 3; }
        catch { return false; }
    }

    // true se (neste instante) há um programa em tela cheia — sem esperar a confirmação do ciclo
    static bool FullscreenLikely()
    {
        var ours = OurWindows();
        return ours.Length > 0 && IsFullscreenAppActive(ours[ours.Length - 1]);
    }

    // ---- diagnóstico ---------------------------------------------------------------------------------------
    // Registra em %LOCALAPPDATA%\DockIsland\fullscreen.log, só quando muda, qual janela cobriu o monitor e por que
    // foi (ou não) considerada tela cheia. Serve para descobrir por que um jogo específico não é detectado.
    static string _noteKey;
    static void Note(string key, string text, uint pid)
    {
        if (key == _noteKey) return;
        _noteKey = key;
        try
        {
            string name = "?";
            try { name = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { }
            string dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DockIsland");
            System.IO.Directory.CreateDirectory(dir);
            string file = System.IO.Path.Combine(dir, "fullscreen.log");
            try { var fi = new System.IO.FileInfo(file); if (fi.Exists && fi.Length > 200_000) System.IO.File.Delete(file); } catch { }
            System.IO.File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  [{name}] {text}{Environment.NewLine}");
        }
        catch { }
    }
}
