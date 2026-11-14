using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DockIsland;

// Programa aberto encontrado na leitura das janelas
class RunInfo
{
    public string Key;
    public string ExePath;
    public IntPtr Hwnd;
    public string Title;
    public string Name;
    public string Aumid;   // AppUserModelID da janela (liga ao app do lançador)
    public bool IsPwa;     // janela de app web instalado (PWA): é um app à parte, não faz parte do navegador
    public bool IsFrame;   // app da Loja (janela hospedada pelo ApplicationFrameHost)
    public List<WinRef> Windows = new();   // todas as janelas deste app (a primeira é a de cima)
}

// =========================================================
//  Dock: pílula embaixo da tela com apps fixados, programas abertos e botão do lançador.
//  Recolhe e reaparece ao encostar o mouse na borda inferior.
// =========================================================
public partial class DockWindow : Window
{
    // ---------- Win32 ----------
    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WS_EX_APPWINDOW = 0x00040000;

    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out DockPoint p);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
    [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);
    [DllImport("user32.dll")] static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] static extern IntPtr GetClassLongPtr(IntPtr hWnd, int index);
    [StructLayout(LayoutKind.Sequential)] struct DockRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out DockRect r);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder buf, ref int size);

    [StructLayout(LayoutKind.Sequential)] struct DockPoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct AbRect { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public AbRect rc;
        public IntPtr lParam;
    }

    const uint ABM_GETSTATE = 0x4, ABM_SETSTATE = 0xA;
    const int ABS_AUTOHIDE = 0x1;

    // ---------- Estado ----------
    static readonly double[] Scales = { 0.85, 1.0, 1.2, 1.4 };
    double _scale = 1.0;
    double HiddenY => Height + 4;   // pílula totalmente fora da tela
    readonly ObservableCollection<AppEntry> _pinned = new();
    readonly ObservableCollection<AppEntry> _running = new();
    readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(40) };
    readonly DispatcherTimer _scanTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    static readonly Dictionary<string, string> ExeNames = new(StringComparer.OrdinalIgnoreCase);
    readonly LauncherWindow _launcher = new();

    IntPtr _hwnd;
    bool _shown, _menuOpen, _modal, _dragging, _scanning, _taskbarChanged, _enabled;
    bool _internalDrag;                 // um item do próprio dock está sendo arrastado
    AppEntry _dragItem;                 // item arrastado
    AppEntry _dragCandidate;            // botão esquerdo apertado sobre este item (ainda pode virar arrasto)
    Point _dragStart;
    const string DragFormat = "DI_App", DockFormat = "DI_DockItem";
    bool _keepTaskbarHidden, _taskbarWasHidden;
    string _launcherIcon;            // ícone personalizado do botão do lançador
    bool _launcherDivider = true;    // divisor entre o lançador e os apps
    bool _runDivider = true;         // divisor entre fixados e abertos
    int _sizeIdx = 1;
    public event Action<int> SizeRequested;   // pedido para trocar o tamanho (o MainWindow salva)
    DateTime _lastInside = DateTime.UtcNow;
    bool _desktopClear;                 // área de trabalho livre (nenhuma janela na tela): o dock fica sempre visível
    DateTime _lastDesktopCheck = DateTime.MinValue;
    bool _edgeLatch;   // escondeu com o mouse ainda na borda (fora do dock): só reabre depois de sair da borda

    static string DockPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "dock.json");

    static readonly Brush RedBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));

    public DockWindow()
    {
        InitializeComponent();
        Pill.SizeChanged += (_, _) => PositionTag();
        PinnedList.ItemsSource = _pinned;
        _pinned.CollectionChanged += (_, _) => UpdateRunSep();
        _running.CollectionChanged += (_, _) => UpdateRunSep();
        RunningList.ItemsSource = _running;

        Width = Math.Min(SystemParameters.PrimaryScreenWidth, 1800);
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        ApplySize(1);

        // o ícone do app acompanha o mouse durante o arrasto
        GiveFeedback += (_, __) => DragGhost.Move();

        LoadPinned();
        _ = UpgradeIconsAsync();
        _launcher.PinRequested += (name, target) => Pin(name, target);
        _launcher.Prewarm();   // carrega a lista de apps e os ícones em segundo plano, para o lançador abrir direto
        IconStore.Changed += key =>
        {
            foreach (var a in _pinned.Concat(_running))
                if (!a.IsDivider && a.IconKeys().Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))) a.RefreshIcon();
        };
        NotifBadges.Changed += () => { foreach (var a in _pinned.Concat(_running)) a.RefreshNotif(); };
        _poll.Tick += (_, __) => PollMouse();
        _scanTimer.Tick += (_, __) => { if (_shown) _ = ScanAsync(); };
    }

    // =====================================================
    //  Janela
    // =====================================================
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        SetWindowLong(_hwnd, GWL_EXSTYLE, GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    // Tecla Windows: abre/fecha o lançador de aplicativos (funciona mesmo com o dock desligado)
    public void ToggleLauncher() => _launcher.Toggle();

    // Ligado/desligado pelos Ajustes
    public void ApplySettings(bool enabled, bool hideTaskbar, int size = 1)
    {
        _enabled = enabled;
        ApplySize(size);
        if (enabled)
        {
            if (!IsVisible) Show();
            _poll.Start();
            _scanTimer.Start();
            _ = ScanAsync();
        }
        else
        {
            _poll.Stop();
            _scanTimer.Stop();
            if (_launcher.IsVisible) _launcher.Hide();
            if (IsVisible) Hide();
            _shown = false;
            PillShift.BeginAnimation(TranslateTransform.YProperty, null);
            PillShift.Y = HiddenY;
            Indicator.BeginAnimation(OpacityProperty, null);
            Indicator.Opacity = 1;
        }
        SetTaskbarAutoHide(enabled && hideTaskbar);
    }

    // Tamanho do dock (0=P 1=M 2=G 3=GG): escala a pílula e ajusta a altura da janela
    void ApplySize(int idx)
    {
        _sizeIdx = Math.Max(0, Math.Min(idx, Scales.Length - 1));
        _scale = Scales[_sizeIdx];
        Pill.LayoutTransform = new ScaleTransform(_scale, _scale);

        // pílula + margem de baixo (5 px) + folga acima para a pílula com o nome do app
        Height = Math.Ceiling(66 * _scale) + 5 + 38;
        Top = SystemParameters.PrimaryScreenHeight - Height;

        PillShift.BeginAnimation(TranslateTransform.YProperty, null);
        PillShift.Y = _shown ? 0 : HiddenY;
    }

    // ---------- Barra de tarefas do Windows ----------
    // Ocultar automático (libera a área de trabalho) + esconder a janela da barra de vez,
    // para ela não aparecer quando o mouse encosta na borda de baixo.
    static IEnumerable<IntPtr> TaskbarWindows()
    {
        var main = FindWindow("Shell_TrayWnd", null);
        if (main != IntPtr.Zero) yield return main;
        IntPtr sec = IntPtr.Zero;
        while ((sec = FindWindowEx(IntPtr.Zero, sec, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
            yield return sec;
    }

    void KeepTaskbarHidden()
    {
        foreach (var h in TaskbarWindows())
            if (IsWindowVisible(h))
            {
                ShowWindow(h, 0);   // SW_HIDE
                _taskbarWasHidden = true;
            }
    }

    void SetTaskbarAutoHide(bool hide)
    {
        try
        {
            var d = new APPBARDATA { cbSize = Marshal.SizeOf<APPBARDATA>(), hWnd = FindWindow("Shell_TrayWnd", null) };
            int state = (int)(uint)SHAppBarMessage(ABM_GETSTATE, ref d);

            if (hide && (state & ABS_AUTOHIDE) == 0)
            {
                d.lParam = (IntPtr)(state | ABS_AUTOHIDE);
                SHAppBarMessage(ABM_SETSTATE, ref d);
                _taskbarChanged = true;
            }
            else if (!hide && _taskbarChanged)
            {
                d.lParam = (IntPtr)(state & ~ABS_AUTOHIDE);
                SHAppBarMessage(ABM_SETSTATE, ref d);
                _taskbarChanged = false;
            }

            _keepTaskbarHidden = hide;
            if (hide) KeepTaskbarHidden();
            else if (_taskbarWasHidden)
            {
                foreach (var h in TaskbarWindows()) ShowWindow(h, 4);   // SW_SHOWNOACTIVATE
                _taskbarWasHidden = false;
            }
        }
        catch { }
    }

    // Ao sair do programa, devolve a barra do Windows como estava
    public void RestoreTaskbar() => SetTaskbarAutoHide(false);

    // =====================================================
    //  Mostrar / esconder ao encostar na borda inferior
    // =====================================================
    void PollMouse()
    {
        if (!_enabled || !IsVisible || _hwnd == IntPtr.Zero) return;
        try
        {
            if (_keepTaskbarHidden) KeepTaskbarHidden();   // o Explorer tenta mostrar a barra ao encostar na borda
            if (_tagOwner != null)
            {
                if (!_tagOwner.IsMouseOver) HideTag();   // item saiu da lista ou o mouse foi embora
                else PositionTag();                      // o dock pode ter mudado de largura/posição
            }
            GetCursorPos(out var pt);
            int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);

            // Sem nenhuma janela na tela (só a área de trabalho): o dock fica visível o tempo todo.
            if ((DateTime.UtcNow - _lastDesktopCheck).TotalMilliseconds >= 250)
            {
                _lastDesktopCheck = DateTime.UtcNow;
                _desktopClear = IsDesktopClear(sw, sh);
            }
            if (_desktopClear)
            {
                _lastInside = DateTime.UtcNow;
                _edgeLatch = false;
                ShowPill();
                return;
            }
            double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            double pillW = Math.Max(120, Pill.ActualWidth * _scale * dpi);

            // Borda inferior da tela inteira (qualquer posição horizontal) mostra o dock
            bool onBottom = pt.Y >= sh - 6 && pt.X >= 0 && pt.X < sw;
            bool nearPill = Math.Abs(pt.X - sw / 2.0) <= pillW / 2 + 140;
            if (!onBottom || nearPill) _edgeLatch = false;
            // A barrinha indicadora (dock oculto) também abre o dock ao encostar nela, mesmo que o mouse
            // não esteja exatamente na última linha de pixels da tela
            bool onIndicator = false;
            if (!_shown && Indicator.Opacity > 0.5)
            {
                var it = Indicator.PointToScreen(new Point(0, 0));
                double iw = Indicator.ActualWidth * dpi, ih = Indicator.ActualHeight * dpi;
                onIndicator = pt.X >= it.X - 10 && pt.X <= it.X + iw + 10 && pt.Y >= it.Y - 8 && pt.Y <= it.Y + ih + 8;
            }
            bool atEdge = (onBottom && (_shown ? nearPill : !_edgeLatch)) || (onIndicator && !_edgeLatch);
            bool inside = false;

            if (_shown)
            {
                var tl = Pill.PointToScreen(new Point(0, 0));
                // Faixa inferior inteira: da altura do topo do dock até a borda da tela, em qualquer posição horizontal
                inside = pt.X >= 0 && pt.X < sw && pt.Y >= tl.Y - 14;
                if (_launcher.IsVisible || _menuOpen || _modal || _dragging || _internalDrag) inside = true;
            }

            if (atEdge)
            {
                _lastInside = DateTime.UtcNow;
                ShowPill();
            }
            else if (_shown)
            {
                if (inside) _lastInside = DateTime.UtcNow;
                else if ((DateTime.UtcNow - _lastInside).TotalMilliseconds > 180)
                {
                    if (onBottom) _edgeLatch = true;
                    HidePill();
                }
            }
        }
        catch { }
    }

    // true quando não há nenhuma janela de programa visível (não minimizada) sobre a área de trabalho
    bool IsDesktopClear(int sw, int sh)
    {
        // menus/lançador/arrasto do próprio dock não contam como "janela"
        uint ownPid = (uint)Environment.ProcessId;
        bool any = false;
        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;

                int ex = GetWindowLong(h, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;
                if (GetWindowTextLength(h) == 0) return true;

                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                var c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return true;

                GetWindowThreadProcessId(h, out uint pid);
                if (pid == ownPid) return true;

                // precisa estar realmente sobre a tela principal
                if (!GetWindowRect(h, out var r)) return true;
                if (r.Right <= 0 || r.Bottom <= 0 || r.Left >= sw || r.Top >= sh) return true;
                if (r.Right - r.Left < 40 || r.Bottom - r.Top < 40) return true;

                any = true;
                return false;   // achou uma janela: para de procurar
            }
            catch { return true; }
        }, IntPtr.Zero);
        return !any;
    }

    void AnimatePill(double to, int ms, IEasingFunction ease) =>
        PillShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = ease });

    void ShowPill()
    {
        if (_shown) return;
        _shown = true;
        _ = ScanAsync();   // mostra os programas abertos já atualizados
        Glass.Refresh();   // o fundo atrás do dock pode ter mudado (ex.: janelas minimizadas)
        Indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(150)));
        AnimatePill(0, 300, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });
    }

    void HidePill()
    {
        if (!_shown) return;
        _shown = false;
        Indicator.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(300)));
        AnimatePill(HiddenY, 140, new CubicEase { EasingMode = EasingMode.EaseIn });
    }

    // =====================================================
    //  Apps fixados
    // =====================================================
    void LoadPinned()
    {
        DockFile f = null;
        try
        {
            if (File.Exists(DockPath))
            {
                var txt = File.ReadAllText(DockPath);
                if (txt.TrimStart().StartsWith("["))   // formato antigo: só a lista
                    f = new DockFile { Items = JsonSerializer.Deserialize<List<DockDto>>(txt) };
                else
                    f = JsonSerializer.Deserialize<DockFile>(txt);
            }
        }
        catch { }

        bool first = f?.Items == null;
        if (first)
        {
            // primeira execução: começa com o Explorador de Arquivos
            f = new DockFile
            {
                Items = new List<DockDto>
                {
                    new DockDto
                    {
                        Name = "Explorador de Arquivos",
                        Target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")
                    }
                }
            };
        }

        _launcherIcon = f.LauncherIcon;
        _launcherDivider = f.LauncherDivider;
        _runDivider = f.RunDivider;

        foreach (var d in f.Items)
        {
            if (d.Divider) _pinned.Add(new AppEntry { IsDivider = true, IsPinned = true });
            else if (!string.IsNullOrEmpty(d.Target))
            {
                // versões antigas guardavam o ícone dentro do dock.json
                if (!string.IsNullOrEmpty(d.IconPath) && !IconStore.Has(d.Target)) IconStore.Set(d.Target, d.IconPath);
                _pinned.Add(MakePinned(d.Name, d.Target));
            }
        }

        ApplyLauncherLook();
        if (first) SavePinned();
    }

    // Usa no dock os mesmos ícones do lançador de apps
    async Task UpgradeIconsAsync()
    {
        try
        {
            await StartAppIcons.EnsureAsync();
            foreach (var a in _pinned.ToList()) StartAppIcons.Apply(a);
            foreach (var a in _running.ToList()) StartAppIcons.Apply(a);
        }
        catch { }
    }

    static AppEntry MakePinned(string name, string target)
    {
        var e = new AppEntry
        {
            Name = name,
            Target = target,
            ResolvedExe = ResolveExe(target),
            Aumid = target.StartsWith(StartAppIcons.AppsFolderPrefix, StringComparison.OrdinalIgnoreCase)
                    ? target.Substring(StartAppIcons.AppsFolderPrefix.Length) : null,
            IsPinned = true,
            DefaultIcon = ShellIcon.GetCached(target)
        };
        e.RefreshIcon();
        StartAppIcons.Apply(e);   // mesmo ícone do lançador (se a lista já carregou)
        return e;
    }

    // .exe direto, ou destino de um atalho .lnk
    internal static string ResolveExe(string target)
    {
        try
        {
            if (string.IsNullOrEmpty(target)) return null;
            if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return target;
            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
                dynamic lnk = sh.CreateShortcut(target);
                string t = lnk.TargetPath;
                return string.IsNullOrWhiteSpace(t) ? null : t;
            }
        }
        catch { }
        return null;
    }

    void SavePinned()
    {
        try
        {
            var f = new DockFile
            {
                LauncherIcon = _launcherIcon,
                LauncherDivider = _launcherDivider,
                RunDivider = _runDivider,
                Items = _pinned.Select(p => p.IsDivider
                    ? new DockDto { Divider = true }
                    : new DockDto { Name = p.Name, Target = p.Target }).ToList()
            };
            Directory.CreateDirectory(Path.GetDirectoryName(DockPath)!);
            AtomicFile.WriteAllText(DockPath, JsonSerializer.Serialize(f, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    void Pin(string name, string target)
    {
        if (string.IsNullOrEmpty(target)) return;
        if (_pinned.Any(p => string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase))) return;
        _pinned.Add(MakePinned(name, target));
        SavePinned();
        _ = ScanAsync();
    }

    // Abriu o app: as notificações dele já foram vistas, então o selo some
    static void ClearNotifsOf(AppEntry a) =>
        (Application.Current?.MainWindow as MainWindow)?.ClearNotifsFor(a);

    static void Launch(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
    }

    // Traz a janela para frente (restaura se estiver minimizada)
    static void ActivateWindow(IntPtr h)
    {
        if (h == IntPtr.Zero) return;
        try
        {
            if (IsIconic(h)) ShowWindow(h, 9);   // SW_RESTORE
            SwitchToThisWindow(h, true);
            SetForegroundWindow(h);
        }
        catch { }
    }

    // Clique: janela aberta e na frente -> minimiza; aberta mas atrás/minimizada -> traz para frente;
    // não está aberto -> abre
    void Pinned_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AppEntry a) return;
        HideTag();
        if (a.IsRunning && a.Hwnd != IntPtr.Zero)
        {
            if (IsActiveWindow(a.Hwnd)) MinimizeWindow(a.Hwnd);
            else { ActivateWindow(a.Hwnd); ClearNotifsOf(a); }
        }
        else { Launch(a.Target); ClearNotifsOf(a); }
    }

    // A janela está visível e é a que está em primeiro plano? (o dock não rouba o foco)
    static bool IsActiveWindow(IntPtr h)
    {
        try
        {
            if (IsIconic(h)) return false;
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            return fg == h || GetAncestor(fg, 3 /* GA_ROOTOWNER */) == GetAncestor(h, 3);
        }
        catch { return false; }
    }

    static void MinimizeWindow(IntPtr h)
    {
        try { ShowWindow(h, 6); }   // SW_MINIMIZE
        catch { }
    }

    // ---------- Pílula com o nome do app ----------
    FrameworkElement _tagOwner;

    void Item_Enter(object sender, MouseEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AppEntry a || a.IsDivider) return;

        ShowTag(fe, a.Name);
    }

    void Item_Leave(object sender, MouseEventArgs e)
    {
        SetPressed(sender as FrameworkElement, false);
        if (ReferenceEquals(sender, _tagOwner)) HideTag();
    }

    // Feedback de clique: escurece levemente o quadrado do item enquanto o botão está pressionado
    void SetPressed(FrameworkElement hit, bool pressed)
    {
        if (hit is not Border b || b.Child is not Grid g) return;
        foreach (var c in g.Children)
            if (c is Border p && p.Name == "Press")
            {
                p.BeginAnimation(OpacityProperty, new DoubleAnimation(pressed ? 1 : 0, TimeSpan.FromMilliseconds(pressed ? 70 : 160)));
                break;
            }
    }

    void ShowTag(FrameworkElement owner, string text)
    {
        if (string.IsNullOrEmpty(text) || _menuOpen || _dragging) return;
        _tagOwner = owner;
        NameTagText.Text = text;
        NameTagBox.Visibility = Visibility.Visible;
        PositionTag();
        NameTag.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(140)));
        TagShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
    }

    // Centraliza a pílula sobre o ícone (centro do ícone, topo da pílula do dock).
    // Chamado ao mostrar e de novo sempre que o dock muda de tamanho/posição.
    void PositionTag()
    {
        var owner = _tagOwner;
        if (owner == null || !owner.IsLoaded) return;
        try
        {
            var c = owner.TransformToVisual(Root).Transform(new Point(owner.ActualWidth / 2, 0));
            var top = Pill.TransformToVisual(Root).Transform(new Point(0, 0)).Y;
            Canvas.SetLeft(NameTagBox, c.X - NameTagBox.Width / 2);
            Canvas.SetTop(NameTagBox, top - 5 - NameTagBox.Height);   // pílula encostada embaixo da caixa
        }
        catch { }
    }

    void HideTag()
    {
        if (_tagOwner == null && NameTagBox.Visibility != Visibility.Visible) return;
        _tagOwner = null;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(100));
        anim.Completed += (_, _) => { if (_tagOwner == null) NameTagBox.Visibility = Visibility.Collapsed; };
        NameTag.BeginAnimation(OpacityProperty, anim);
        TagShift.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(3, TimeSpan.FromMilliseconds(120)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    void Pinned_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AppEntry a) return;
        e.Handled = true;
        HideTag();

        var items = new List<(string, Action, Brush)>();
        if (a.Windows.Count > 1)
        {
            // várias janelas do mesmo app: escolha para qual ir
            foreach (var w in a.Windows)
            {
                var h = w.Hwnd;
                var t = string.IsNullOrWhiteSpace(w.Title) ? a.Name : w.Title.Trim();
                if (t.Length > 40) t = t.Substring(0, 40) + "…";
                items.Add((t, () => ActivateWindow(h), null));
            }
            items.Add(("-", () => { }, null));
        }
        else if (a.IsRunning) items.Add(("Mostrar janela", () => ActivateWindow(a.Hwnd), null));
        else items.Add(("Abrir", () => Launch(a.Target), null));

        if (!a.IsFrameWindow && !string.IsNullOrEmpty(a.Target))
            items.Add(("Abrir local do app", () => AppLocation.Open(a.Target, a.Name), null));
        items.Add(("Alterar ícone…", () => ChangeIcon(a), null));
        if (IconStore.HasFor(a))
            items.Add(("Restaurar ícone padrão", () => IconStore.RemoveFor(a), null));

        if (a.IsPinned)
        {
            items.Add(("Adicionar divisor depois", () => AddDivider(a), null));
            items.Add(("Remover do dock", () => { _pinned.Remove(a); SavePinned(); _ = ScanAsync(); }, null));
        }
        else if (!a.IsFrameWindow)
            items.Add(("Fixar no dock", () => Pin(a.Name, a.Target), null));

        if (a.IsRunning)
            items.Add(("Fechar janela", () => PostMessage(a.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero), RedBrush));

        MenuKit.Show(fe, a.Name, a.Icon, items, open => { _menuOpen = open; _lastInside = DateTime.UtcNow; });
    }

    // Clique direito num divisor
    void Divider_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AppEntry a) return;
        e.Handled = true;
        var items = new List<(string, Action, Brush)>
        {
            ("Remover divisor", () => { _pinned.Remove(a); SavePinned(); }, RedBrush)
        };
        MenuKit.Show(fe, "Divisor", null, items, open => { _menuOpen = open; _lastInside = DateTime.UtcNow; });
    }

    // Clique direito na pílula (fora de um ícone)
    void Pill_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var items = new List<(string, Action, Brush)>
        {
            ("Adicionar aplicativo…", AddFromDialog, null),
            ("Adicionar divisor", () => AddDivider(null), null),
            ("Todos os aplicativos", () => _launcher.Open(), null)
        };
        if (!_launcherDivider)
            items.Add(("Mostrar divisor do lançador", () => { _launcherDivider = true; ApplyLauncherLook(); SavePinned(); }, null));
        if (!_runDivider)
            items.Add(("Mostrar divisor dos abertos", () => { _runDivider = true; UpdateRunSep(); SavePinned(); }, null));
        string[] sizeNames = { "Pequeno", "Médio", "Grande", "Extra grande" };
        for (int i = 0; i < sizeNames.Length; i++)
        {
            int idx = i;
            items.Add(((i == _sizeIdx ? "✓ " : "    ") + "Tamanho: " + sizeNames[i], () => SizeRequested?.Invoke(idx), null));
        }
        MenuKit.Show(Pill, "Dock", null, items, open => { _menuOpen = open; _lastInside = DateTime.UtcNow; });
    }

    // Sem nenhum app no dock (fixado ou aberto): nenhum divisor aparece
    void UpdateRunSep()
    {
        bool pinnedApps = _pinned.Any(p => !p.IsDivider);
        bool anyApps = pinnedApps || _running.Count > 0;
        Sep.Visibility = _launcherDivider && anyApps ? Visibility.Visible : Visibility.Collapsed;
        PinnedList.Visibility = pinnedApps ? Visibility.Visible : Visibility.Collapsed;   // esconde divisores soltos
        RunSep.Visibility = _running.Count > 0 && pinnedApps && _runDivider ? Visibility.Visible : Visibility.Collapsed;
    }

    // Divisor entre o lançador e os apps
    void Sep_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var items = new List<(string, Action, Brush)>
        {
            ("Remover divisor", () => { _launcherDivider = false; ApplyLauncherLook(); SavePinned(); }, RedBrush)
        };
        MenuKit.Show(Sep, "Divisor", null, items, open => { _menuOpen = open; _lastInside = DateTime.UtcNow; });
    }

    // Divisor entre os fixados e os programas abertos
    void RunSep_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var items = new List<(string, Action, Brush)>
        {
            ("Remover divisor", () => { _runDivider = false; UpdateRunSep(); SavePinned(); }, RedBrush)
        };
        MenuKit.Show(RunSep, "Divisor", null, items, open => { _menuOpen = open; _lastInside = DateTime.UtcNow; });
    }

    // Divisor depois de um app (ou no fim dos fixados)
    void AddDivider(AppEntry after)
    {
        int idx = after == null ? _pinned.Count : _pinned.IndexOf(after) + 1;
        _pinned.Insert(Math.Max(0, Math.Min(idx, _pinned.Count)), new AppEntry { IsDivider = true, IsPinned = true });
        SavePinned();
    }

    // =====================================================
    //  Ícones personalizados
    // =====================================================
    // Escolhe um arquivo de ícone (a janela do dock não recolhe enquanto o diálogo está aberto)
    string PickIconFile()
    {
        _modal = true;
        try { return IconStore.Pick(); }
        finally { _modal = false; _lastInside = DateTime.UtcNow; }
    }

    // O ícone fica salvo por aplicativo: vale no dock e no lançador
    void ChangeIcon(AppEntry a)
    {
        var path = PickIconFile();
        if (path != null) IconStore.SetFor(a, path);
    }

    // Botão do lançador: ícone personalizado e divisor
    void ApplyLauncherLook()
    {
        var img = string.IsNullOrEmpty(_launcherIcon) ? null : IconStore.LoadImage(_launcherIcon);
        LauncherImg.Source = img;
        LauncherImg.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
        LauncherGlyph.Visibility = img != null ? Visibility.Collapsed : Visibility.Visible;
        LauncherBtn.Tag = img != null ? "custom" : null;
        UpdateRunSep();
    }

    void Launcher_RightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var items = new List<(string, Action, Brush)>
        {
            ("Alterar ícone…", () =>
            {
                var path = PickIconFile();
                if (path == null) return;
                IconStore.DeleteOwn(_launcherIcon);
                _launcherIcon = path;
                ApplyLauncherLook();
                SavePinned();
            }, null)
        };
        if (!string.IsNullOrEmpty(_launcherIcon))
            items.Add(("Restaurar ícone padrão", () =>
            {
                IconStore.DeleteOwn(_launcherIcon);
                _launcherIcon = null;
                ApplyLauncherLook();
                SavePinned();
            }, null));
        items.Add((_launcherDivider ? "Remover divisor" : "Mostrar divisor", () =>
        {
            _launcherDivider = !_launcherDivider;
            ApplyLauncherLook();
            SavePinned();
        }, null));

        MenuKit.Show(LauncherBtn, "Lançador de aplicativos", LauncherImg.Source, items,
            open => { _menuOpen = open; _lastInside = DateTime.UtcNow; });
    }

    void AddFromDialog()
    {
        _modal = true;
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Adicionar ao dock",
                Filter = "Aplicativos e atalhos|*.exe;*.lnk;*.bat;*.cmd|Todos os arquivos|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog() == true)
                foreach (var f in dlg.FileNames) Pin(Path.GetFileNameWithoutExtension(f), f);
        }
        catch { }
        finally { _modal = false; _lastInside = DateTime.UtcNow; }
    }

    // =====================================================
    //  Arrastar e soltar
    //   - arrastar um ícone do dock: muda a posição (fixados e divisores)
    //   - arrastar um programa aberto (não fixado): fixa na posição solta
    //   - arrastar um app do lançador, ou um .exe / atalho do Windows: fixa na posição solta
    // =====================================================
    void Item_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AppEntry a)
        {
            _dragCandidate = a;
            _dragStart = e.GetPosition(this);
            SetPressed(fe, true);
        }
    }

    void Item_PreviewUp(object sender, MouseButtonEventArgs e)
    {
        _dragCandidate = null;
        SetPressed(sender as FrameworkElement, false);
    }

    void Item_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragCandidate == null || _internalDrag) return;
        if (sender is not FrameworkElement fe || !ReferenceEquals(fe.DataContext, _dragCandidate)) return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        StartDockDrag(fe, _dragCandidate);
    }

    void StartDockDrag(FrameworkElement src, AppEntry a)
    {
        _dragCandidate = null;
        HideTag();
        _internalDrag = true;
        _dragItem = a;
        try
        {
            var data = new DataObject();
            data.SetData(DockFormat, "1");
            DragGhost.Begin(a.IsDivider ? null : a.Icon, a.IsDivider ? "|" : a.Initial);
            DragDrop.DoDragDrop(src, data, DragDropEffects.Move);
        }
        catch { }
        finally
        {
            DragGhost.End();
            _internalDrag = false;
            _dragItem = null;
            HideDropLine();
            _lastInside = DateTime.UtcNow;
        }
    }

    // Posição (entre os fixados) onde o item solto em "p" (coordenadas da lista de fixados) deve entrar
    int InsertIndexAt(Point p)
    {
        for (int i = 0; i < _pinned.Count; i++)
        {
            if (PinnedList.ItemContainerGenerator.ContainerFromIndex(i) is FrameworkElement c && c.ActualWidth > 0)
            {
                var center = c.TranslatePoint(new Point(c.ActualWidth / 2, 0), PinnedList);
                if (p.X < center.X) return i;
            }
        }
        return _pinned.Count;
    }

    // Linha vertical que mostra onde o item vai entrar
    void ShowDropLine(int index)
    {
        try
        {
            FrameworkElement c;
            double offset;
            if (index < _pinned.Count && PinnedList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement cur)
            { c = cur; offset = 0; }
            else if (_pinned.Count > 0 && PinnedList.ItemContainerGenerator.ContainerFromIndex(_pinned.Count - 1) is FrameworkElement last)
            { c = last; offset = last.ActualWidth; }
            else
            { c = PinnedList; offset = 0; }

            var x = c.TransformToVisual(Root).Transform(new Point(offset, 0)).X;
            // o botão do lançador tem 44 px (como os ícones) dentro do espaço de 50 px dos itens:
            // a linha tem a altura do espaço inteiro (3 px acima e abaixo do botão)
            var topLeft = LauncherBtn.TransformToVisual(Root).Transform(new Point(0, -3));
            var bottom = LauncherBtn.TransformToVisual(Root).Transform(new Point(0, LauncherBtn.ActualHeight + 3)).Y;

            double pad = 6 * _scale;
            DropLine.Height = Math.Max(8, bottom - topLeft.Y - pad * 2);
            Canvas.SetLeft(DropLine, x - DropLine.Width / 2);
            Canvas.SetTop(DropLine, topLeft.Y + pad);
            DropLine.Visibility = Visibility.Visible;
        }
        catch { }
    }

    void HideDropLine() => DropLine.Visibility = Visibility.Collapsed;

    // Muda um fixado de lugar, ou fixa um programa aberto na posição "idx"
    void MoveOrPin(AppEntry a, int idx)
    {
        if (a.IsPinned)
        {
            int old = _pinned.IndexOf(a);
            if (old < 0) return;
            if (idx > old) idx--;   // a posição foi medida antes de tirar o item
            idx = Math.Max(0, Math.Min(idx, _pinned.Count - 1));
            if (idx == old) return;
            _pinned.Move(old, idx);
            SavePinned();
        }
        else if (!a.IsFrameWindow && !string.IsNullOrEmpty(a.Target))
            PinAt(a.Name, a.Target, idx);
    }

    // Fixa na posição "idx" (se já estava fixado, só muda de lugar)
    bool PinAt(string name, string target, int idx)
    {
        if (string.IsNullOrEmpty(target)) return false;
        var existing = _pinned.FirstOrDefault(p => !p.IsDivider && string.Equals(p.Target, target, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            MoveOrPin(existing, idx);
            return false;
        }
        idx = Math.Max(0, Math.Min(idx, _pinned.Count));
        _pinned.Insert(idx, MakePinned(name, target));
        SavePinned();
        _ = ScanAsync();
        return true;
    }

    void Pill_DragEnter(object sender, DragEventArgs e) => _dragging = true;
    void Pill_DragLeave(object sender, DragEventArgs e) { _dragging = false; _lastInside = DateTime.UtcNow; HideDropLine(); }

    void Pill_DragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetDataPresent(DockFormat)) e.Effects = DragDropEffects.Move;
        else if (e.Data.GetDataPresent(DragFormat) || e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effects = DragDropEffects.Copy;
        else { e.Effects = DragDropEffects.None; HideDropLine(); return; }

        ShowDropLine(InsertIndexAt(e.GetPosition(PinnedList)));
    }

    void Pill_Drop(object sender, DragEventArgs e)
    {
        _dragging = false;
        _lastInside = DateTime.UtcNow;
        HideDropLine();
        e.Handled = true;

        int idx = InsertIndexAt(e.GetPosition(PinnedList));

        // item do próprio dock: reordena
        if (e.Data.GetDataPresent(DockFormat) && _dragItem != null)
        {
            MoveOrPin(_dragItem, idx);
            return;
        }

        // app vindo do lançador
        if (e.Data.GetDataPresent(DragFormat) && e.Data.GetData(DragFormat) is string s)
        {
            int i = s.IndexOf('\u0001');
            if (i > 0) PinAt(s.Substring(0, i), s.Substring(i + 1), idx);
            return;
        }

        // .exe / atalho arrastado do Windows
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var f in files)
            {
                var ext = Path.GetExtension(f).ToLowerInvariant();
                if (ext is ".exe" or ".lnk" or ".bat" or ".cmd" or ".url")
                    if (PinAt(Path.GetFileNameWithoutExtension(f), f, idx)) idx++;
            }
        }
    }

    // Botão de grade: abre/fecha o lançador no centro da tela
    void Launcher_Click(object sender, MouseButtonEventArgs e) => _launcher.Toggle();

    // =====================================================
    //  Programas abertos
    // =====================================================
    async Task ScanAsync()
    {
        if (_scanning || !_enabled) return;
        _scanning = true;
        try
        {
            uint own = (uint)Environment.ProcessId;
            var infos = await Task.Run(() => ScanWindows(own));
            ApplyRunning(infos);
        }
        catch { }
        finally { _scanning = false; }
    }

    static string Flat(string s) =>
        new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    static bool Matches(AppEntry p, RunInfo i)
    {
        bool sameId = !string.IsNullOrEmpty(p.Aumid) && !string.IsNullOrEmpty(i.Aumid) &&
                      string.Equals(p.Aumid, i.Aumid, StringComparison.OrdinalIgnoreCase);
        if (sameId) return true;

        if (i.IsPwa)
        {
            // app web: nunca casa com o navegador (mesmo .exe); só com o próprio app (mesmo AppID ou mesmo nome)
            var pn = Flat(p.Name);
            return pn.Length >= 3 && pn == Flat(i.Name ?? i.Title);
        }

        if (!i.IsFrame && !string.IsNullOrEmpty(p.ResolvedExe) &&
            string.Equals(p.ResolvedExe, i.ExePath, StringComparison.OrdinalIgnoreCase)) return true;

        var a = Flat(p.Name);
        var b = Flat(i.Name ?? i.Title);
        return a.Length >= 4 && b.Length >= 4 && (a == b || a.Contains(b) || b.Contains(a));
    }

    void ApplyRunning(List<RunInfo> infos)
    {
        // fixados: marca os que estão abertos
        foreach (var p in _pinned.Where(x => !x.IsDivider))
        {
            var matches = infos.Where(i => Matches(p, i)).ToList();
            var m = matches.FirstOrDefault();
            p.IsRunning = m != null;
            p.Hwnd = m?.Hwnd ?? IntPtr.Zero;

            // app fixado pelo .exe: aprende o AppID do programa aberto e passa a usar o ícone do lançador
            if (m != null && !string.IsNullOrEmpty(m.Aumid) && !string.Equals(p.Aumid, m.Aumid, StringComparison.OrdinalIgnoreCase) &&
                ((!m.IsFrame && !string.IsNullOrEmpty(p.ResolvedExe) &&
                  string.Equals(p.ResolvedExe, m.ExePath, StringComparison.OrdinalIgnoreCase)) ||
                 Flat(p.Name) == Flat(m.Name ?? m.Title)))
            {
                p.Aumid = m.Aumid;
                p.FromLauncher = false;
            }
            if (!p.FromLauncher) StartAppIcons.Apply(p);   // tenta de novo a cada leitura até achar o ícone do lançador
            p.Windows = matches.SelectMany(i => i.Windows).ToList();
        }

        // abertos que não estão fixados
        var others = infos.Where(i => !_pinned.Any(p => !p.IsDivider && Matches(p, i))).ToList();

        foreach (var r in _running.ToList())
            if (!others.Any(o => o.Key == r.Key)) _running.Remove(r);

        foreach (var o in others)
        {
            var r = _running.FirstOrDefault(x => x.Key == o.Key);
            if (r != null)
            {
                r.Hwnd = o.Hwnd;
                r.Windows = o.Windows;
                if (string.IsNullOrEmpty(r.Aumid) && !string.IsNullOrEmpty(o.Aumid)) { r.Aumid = o.Aumid; r.FromLauncher = false; }
                if (!r.FromLauncher) StartAppIcons.Apply(r);   // app aberto (não fixado) também usa o ícone do lançador
                continue;
            }

            // app web (PWA): abre como app (pelo AppID), não como uma janela do navegador
            string pwaTarget = o.IsPwa && !string.IsNullOrEmpty(o.Aumid) &&
                               (StartAppIcons.NameFor(o.Aumid) != null || StartAppIcons.IsWebAppId(o.Aumid))
                               ? StartAppIcons.AppsFolderPrefix + o.Aumid : null;
            ImageSource icon = o.IsPwa ? (ShellIcon.GetCached(pwaTarget) ?? WindowIcon(o.Hwnd))
                             : o.IsFrame ? WindowIcon(o.Hwnd) : ShellIcon.GetCached(o.ExePath);
            icon ??= WindowIcon(o.Hwnd);
            var entry = new AppEntry
            {
                Key = o.Key,
                Name = o.Name ?? o.Title,
                Target = pwaTarget ?? o.ExePath,
                ResolvedExe = o.ExePath,
                Hwnd = o.Hwnd,
                Aumid = o.Aumid,
                IsFrameWindow = o.IsFrame,
                IsRunning = true,
                Windows = o.Windows,
                DefaultIcon = icon
            };
            entry.RefreshIcon();
            StartAppIcons.Apply(entry);   // mesmo ícone do lançador (se a lista já carregou)
            _running.Add(entry);
        }

        UpdateRunSep();
    }

    // Janelas "de verdade" que aparecem na barra de tarefas (de cima para baixo na ordem de empilhamento)
    static List<RunInfo> ScanWindows(uint ownPid)
    {
        var list = new List<RunInfo>();
        var byKey = new Dictionary<string, RunInfo>(StringComparer.OrdinalIgnoreCase);

        EnumWindows((h, _) =>
        {
            try
            {
                if (!IsWindowVisible(h)) return true;
                if (GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;

                int ex = GetWindowLong(h, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
                if (DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;

                int len = GetWindowTextLength(h);
                if (len == 0) return true;
                var title = new StringBuilder(len + 1);
                GetWindowText(h, title, title.Capacity);

                var cls = new StringBuilder(256);
                GetClassName(h, cls, 256);
                var c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return true;

                GetWindowThreadProcessId(h, out uint pid);
                if (pid == ownPid) return true;

                var exe = ImagePath(pid);
                if (string.IsNullOrEmpty(exe)) return true;

                bool frame = Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
                var aumid = AppIdentity.ForWindow(h, pid, frame);

                // app web (PWA) instalado pelo Edge/Chrome: janela de navegador que é, na verdade, outro app
                bool pwa = false;
                string pwaName = null;
                if (!frame)
                {
                    var product = DescribeExe(exe);
                    var ttl = title.ToString();
                    if (StartAppIcons.IsWebAppId(aumid)) pwa = true;
                    else if (IsChromiumBrowser(exe))
                    {
                        if (StartAppIcons.IsOtherAppId(aumid, exe, product)) pwa = true;
                        else if (!Flat(ttl).EndsWith(Flat(product)) &&
                                 StartAppIcons.TryMatchTitle(ttl, exe, product, out var mn, out var mt, out var ma))
                        {
                            pwa = true;
                            pwaName = mn;
                            if (!string.IsNullOrEmpty(ma)) aumid = ma;
                        }
                        StartAppIcons.LogWindow(exe, c, aumid, pwa, ttl);
                    }
                }
                if (pwa && string.IsNullOrEmpty(aumid)) aumid = Flat(title.ToString());
                var key = frame ? "w:" + h : pwa ? "pwa:" + aumid : exe;
                var wref = new WinRef { Hwnd = h, Title = title.ToString() };
                if (byKey.TryGetValue(key, out var existing)) { existing.Windows.Add(wref); return true; }

                var info = new RunInfo
                {
                    Key = key,
                    ExePath = exe,
                    Hwnd = h,
                    Title = title.ToString(),
                    Name = frame ? title.ToString()
                         : pwa ? (pwaName ?? StartAppIcons.NameFor(aumid) ?? title.ToString())
                         : DescribeExe(exe),
                    IsFrame = frame,
                    IsPwa = pwa,
                    Aumid = aumid
                };
                info.Windows.Add(wref);
                byKey[key] = info;
                list.Add(info);
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return list;
    }

    static readonly string[] Chromium =
    {
        "msedge.exe", "chrome.exe", "brave.exe", "vivaldi.exe", "opera.exe", "chromium.exe", "arc.exe", "thorium.exe", "yandex.exe"
    };

    static bool IsChromiumBrowser(string exe) =>
        Chromium.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase);

    static string ImagePath(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    static string DescribeExe(string exe)
    {
        lock (ExeNames)
        {
            if (ExeNames.TryGetValue(exe, out var n)) return n;
            string name = null;
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(exe);
                name = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription
                     : !string.IsNullOrWhiteSpace(vi.ProductName) ? vi.ProductName : null;
            }
            catch { }
            name = (name ?? Path.GetFileNameWithoutExtension(exe)).Trim();
            ExeNames[exe] = name;
            return name;
        }
    }

    // Ícone de uma janela exatamente como o dock mostra: o que você escolheu no lançador de apps
    // (ou, sem troca, o ícone do app no lançador). Usado pelo Win+Tab / Alt+Tab. Só na thread de interface.
    internal static ImageSource LauncherIconForWindow(IntPtr h, string title)
    {
        try
        {
            GetWindowThreadProcessId(h, out uint pid);
            var exe = ImagePath(pid);
            if (string.IsNullOrEmpty(exe)) return null;

            bool frame = Path.GetFileName(exe).Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
            var aumid = AppIdentity.ForWindow(h, pid, frame);

            bool pwa = false;
            string pwaName = null;
            if (!frame)
            {
                var product = DescribeExe(exe);
                if (StartAppIcons.IsWebAppId(aumid)) pwa = true;
                else if (IsChromiumBrowser(exe))
                {
                    if (StartAppIcons.IsOtherAppId(aumid, exe, product)) pwa = true;
                    else if (!Flat(title).EndsWith(Flat(product)) &&
                             StartAppIcons.TryMatchTitle(title, exe, product, out var mn, out var mt, out var ma))
                    {
                        pwa = true;
                        pwaName = mn;
                        if (!string.IsNullOrEmpty(ma)) aumid = ma;
                    }
                }
            }
            if (pwa && string.IsNullOrEmpty(aumid)) aumid = Flat(title);

            string pwaTarget = pwa && !string.IsNullOrEmpty(aumid) &&
                               (StartAppIcons.NameFor(aumid) != null || StartAppIcons.IsWebAppId(aumid))
                               ? StartAppIcons.AppsFolderPrefix + aumid : null;
            ImageSource icon = pwa ? (ShellIcon.GetCached(pwaTarget) ?? WindowIcon(h))
                             : frame ? WindowIcon(h) : ShellIcon.GetCached(exe);
            icon ??= WindowIcon(h);

            var entry = new AppEntry
            {
                Name = frame ? title : pwa ? (pwaName ?? StartAppIcons.NameFor(aumid) ?? title) : DescribeExe(exe),
                Target = pwaTarget ?? exe,
                ResolvedExe = exe,
                Aumid = aumid,
                IsFrameWindow = frame,
                DefaultIcon = icon
            };
            entry.RefreshIcon();           // ícone que você trocou (se houver)
            StartAppIcons.Apply(entry);    // senão, o do lançador
            return entry.Icon;
        }
        catch { return null; }
    }

    // Ícone da própria janela (apps da Loja ou quando o .exe não dá ícone)
    static ImageSource WindowIcon(IntPtr hwnd)
    {
        try
        {
            IntPtr icon = IntPtr.Zero;
            foreach (var kind in new[] { 1, 2, 0 })   // ICON_BIG, ICON_SMALL2, ICON_SMALL
            {
                SendMessageTimeout(hwnd, 0x007F /* WM_GETICON */, (IntPtr)kind, IntPtr.Zero, 2, 100, out icon);
                if (icon != IntPtr.Zero) break;
            }
            if (icon == IntPtr.Zero) icon = GetClassLongPtr(hwnd, -14 /* GCL_HICON */);
            if (icon == IntPtr.Zero) return null;

            var src = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }
}
