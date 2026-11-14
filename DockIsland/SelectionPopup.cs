using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;

namespace DockIsland;

// Popup de seleção: ao selecionar texto (com o mouse ou com o teclado) em qualquer programa,
// mostra uma barrinha com Recortar / Copiar / Colar / Pesquisar... ao lado do texto.
//
// Como descobre a seleção:
//  1) Os ganchos globais (SelectionHook) avisam que "pode ter havido uma seleção": soltar o mouse depois de
//     arrastar, duplo/triplo clique, Shift+clique, Shift+setas, Ctrl+A.
//  2) Lê a seleção real pelo UI Automation (TextPattern) do controle em foco: dá o texto, a posição na tela
//     e se o campo é editável. Não mexe na área de transferência.
//  3) "Modo compatível" (opcional): para programas sem TextPattern, manda um Ctrl+C, lê o texto e devolve
//     o que havia na área de transferência. Fica desligado por padrão.
// Senhas (campos protegidos) nunca são lidas. O texto da seleção só fica na memória enquanto o popup existe.
internal sealed class SelectionController : IDisposable
{
    // ---- ajustes ----
    const int MaxChars = 4000;        // tamanho máximo lido da seleção
    const int MouseDelayMs = 120;     // espera depois de soltar o mouse (o programa atualiza a seleção)
    const int KeyDelayMs = 260;       // espera depois de Shift+setas / Ctrl+A
    const int UiaTimeoutMs = 700;     // programa que não responde ao UI Automation é ignorado
    const int AutoHideMs = 12000;     // some sozinho depois desse tempo
    const int HoldMs = 300;           // segurar o botão esquerdo parado por esse tempo abre o menu
    const int Gap = 8;                // distância entre o texto e o popup (pixels)

    // ---------------------------------------------------------------- Win32
    [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RC { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO { public int Size; public RC Monitor, Work; public uint Flags; }

    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromPoint(PT pt, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);

    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1,
              VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_A = 0x41, VK_C = 0x43, VK_V = 0x56, VK_X = 0x58;

    // ---------------------------------------------------------------- estado
    sealed class SelInfo
    {
        public string Text;
        public Rect First = Rect.Empty, Last = Rect.Empty;   // primeira e última linha selecionadas (pixels)
        public bool Editable;
        public bool HasRect => !First.IsEmpty;
    }

    readonly Action<int> _ignoreClipboard;   // pede à ilha para não registrar a cópia de teste no histórico
    SelectionHook _hook;
    SelectionWindow _win;
    DispatcherTimer _watch, _hold;
    bool _enabled, _compat;

    int _token;                       // cada novo evento cancela a leitura anterior que ainda esperava
    bool _downOnPopup;
    bool _holdShown;                  // o menu desta pressão foi aberto pelo clique longo
    PT _downPt;
    long _lastDownTick;
    int _clicks;
    bool _keySel;                     // uma tecla de seleção (Shift+seta, Ctrl+A) está em andamento
    IntPtr _shownFg;
    long _shownAt;
    string _shownText;

    public SelectionController(Action<int> ignoreClipboard) => _ignoreClipboard = ignoreClipboard;

    // Liga/desliga conforme os Ajustes (sem ganchos instalados quando desligado)
    public void Apply(bool enabled, bool compat)
    {
        _compat = compat;
        if (enabled == _enabled) return;
        _enabled = enabled;
        if (enabled)
        {
            _hook = new SelectionHook();
            _hook.MouseDown += OnMouseDown;
            _hook.MouseUp += OnMouseUp;
            _hook.Dismiss += HidePopup;
            _hook.KeyDown += OnKeyDown;
            _hook.KeyUp += OnKeyUp;
        }
        else
        {
            _token++;
            _hold?.Stop();
            _hook?.Dispose();
            _hook = null;
            HidePopup();
        }
    }

    public void Dispose()
    {
        Apply(false, false);
        try { _win?.Close(); } catch { }
        _win = null;
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;
    static bool Shift => Down(VK_SHIFT);
    static bool Ctrl => Down(VK_CONTROL);

    // ---------------------------------------------------------------- eventos
    void OnMouseDown(int x, int y)
    {
        if (_win != null && _win.ContainsPoint(x, y)) { _downOnPopup = true; return; }
        _downOnPopup = false;
        _holdShown = false;
        _token++;
        HidePopup();

        long now = Environment.TickCount64;
        bool near = Math.Abs(x - _downPt.X) <= 4 && Math.Abs(y - _downPt.Y) <= 4;
        _clicks = (near && now - _lastDownTick <= GetDoubleClickTime()) ? _clicks + 1 : 1;
        _lastDownTick = now;
        _downPt = new PT { X = x, Y = y };

        // segurar o botão parado: abre o menu quando o tempo passar
        _hold ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(HoldMs) };
        _hold.Tick -= HoldTick;
        _hold.Tick += HoldTick;
        _hold.Stop();
        _hold.Start();
    }

    void HoldTick(object s, EventArgs e)
    {
        _hold.Stop();
        if (!_enabled || _downOnPopup || !Down(0x01 /*VK_LBUTTON*/)) return;
        GetCursorPos(out var p);
        if (Math.Abs(p.X - _downPt.X) > 6 || Math.Abs(p.Y - _downPt.Y) > 6) return;   // está arrastando
        _ = HoldAsync(p);
    }

    // Menu de clique longo: se há texto selecionado mostra o menu completo; senão, Colar / Selecionar tudo
    async Task HoldAsync(PT pt)
    {
        int tok = ++_token;
        try
        {
            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || IsOwnWindow(fg)) return;
            GetWindowThreadProcessId(fg, out uint pid);

            SelInfo info = null; bool editable = false;
            var work = Task.Run(() => (ReadUia((int)pid), FocusedEditable((int)pid)));
            if (await Task.WhenAny(work, Task.Delay(UiaTimeoutMs)) == work) (info, editable) = work.Result;
            if (tok != _token) return;

            if (info != null && !string.IsNullOrWhiteSpace(info.Text)) { _holdShown = true; Present(info, true, pt, fg); return; }

            _win ??= new SelectionWindow();
            _win.Clear();
            if (editable || _compat) _win.AddButton("\uE77F", "Colar", "Colar (Ctrl+V)", () => Send(VK_V));
            _win.AddButton("\uE8B3", "Selecionar tudo", "Selecionar tudo (Ctrl+A)", () => Send(VK_A));
            _holdShown = true;
            PlaceWindow(new SelInfo(), true, pt);
            _shownFg = fg; _shownAt = Environment.TickCount64; _shownText = null;
            StartWatch();
        }
        catch { }
    }

    // o controle em foco aceita edição? (usado quando não há seleção)
    static bool FocusedEditable(int pid)
    {
        try
        {
            var el = AutomationElement.FocusedElement;
            for (int i = 0; el != null && i < 4; i++)
            {
                if (el.Current.IsPassword) return false;
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out _) || el.TryGetCurrentPattern(ValuePattern.Pattern, out _))
                    return IsEditable(el, pid);
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }
        }
        catch { }
        return false;
    }

    void OnMouseUp(int x, int y)
    {
        _hold?.Stop();
        if (!_enabled || _downOnPopup || _holdShown) return;   // soltar o botão depois do clique longo não refaz o menu
        bool dragged = Math.Abs(x - _downPt.X) > 8 || Math.Abs(y - _downPt.Y) > 8;
        if (dragged || _clicks >= 2 || Shift)
            _ = CheckAsync(MouseDelayMs, true, new PT { X = x, Y = y });
    }

    static bool IsNav(int vk) => vk is >= 0x21 and <= 0x28;   // PgUp, PgDn, End, Home, setas

    void OnKeyDown(int vk)
    {
        if (!_enabled) return;
        bool modifier = vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT or VK_CONTROL or VK_LCONTROL or VK_RCONTROL or VK_MENU;
        if (modifier) return;

        if ((Shift && IsNav(vk)) || (Ctrl && vk == VK_A))
        {
            _keySel = true;      // a leitura vem quando a tecla é solta
            return;
        }
        // qualquer outra tecla (digitar, Ctrl+C...) encerra o popup
        _token++;
        HidePopup();
    }

    void OnKeyUp(int vk)
    {
        if (!_enabled || !_keySel) return;
        if (IsNav(vk) || vk == VK_A)
        {
            _keySel = false;
            GetCursorPos(out var p);
            _ = CheckAsync(KeyDelayMs, false, p);
        }
    }

    [DllImport("user32.dll")] static extern bool GetCursorPos(out PT p);

    // ---------------------------------------------------------------- leitura da seleção
    async Task CheckAsync(int delayMs, bool fromMouse, PT pt)
    {
        int tok = ++_token;
        try
        {
            await Task.Delay(delayMs);
            if (tok != _token || !_enabled) return;

            var fg = GetForegroundWindow();
            if (fg == IntPtr.Zero || IsOwnWindow(fg)) { HidePopup(); return; }
            GetWindowThreadProcessId(fg, out uint pid);

            SelInfo info = null;
            var work = Task.Run(() => ReadUia((int)pid));
            if (await Task.WhenAny(work, Task.Delay(UiaTimeoutMs)) == work) info = work.Result;
            if (tok != _token) return;

            if (info == null && _compat && fromMouse && !IsRiskyForProbe(fg))
                info = await ProbeCopyAsync();
            if (tok != _token) return;

            if (info == null || string.IsNullOrWhiteSpace(info.Text)) { HidePopup(); return; }
            Present(info, fromMouse, pt, fg);
        }
        catch { }
    }

    static bool IsOwnWindow(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(128);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    // Ctrl+C num terminal interrompe o programa; no Explorer/área de trabalho copia arquivos. Nunca usar o teste aí.
    static bool IsRiskyForProbe(IntPtr h)
    {
        var c = ClassOf(h);
        return c is "ConsoleWindowClass" or "CASCADIA_HOSTING_WINDOW_CLASS" or "mintty" or "VirtualConsoleClass"
            or "CabinetWClass" or "ExploreWClass" or "Progman" or "WorkerW" or "Shell_TrayWnd"
            or "#32770" or "TaskManagerWindow";
    }

    static readonly string[] ReadOnlyDocApps =
    {
        "chrome", "msedge", "firefox", "brave", "opera", "vivaldi", "iexplore", "arc", "thorium",
        "acrord32", "acrobat", "sumatrapdf", "foxitreader", "foxitpdfreader", "okular", "zathura"
    };

    // UI Automation: texto selecionado, onde está na tela e se dá para editar
    static SelInfo ReadUia(int pid)
    {
        try
        {
            var el = AutomationElement.FocusedElement;
            for (int i = 0; el != null && i < 6; i++)
            {
                if (el.Current.IsPassword) return null;   // nunca lê senhas

                if (el.TryGetCurrentPattern(TextPattern.Pattern, out var po) && po is TextPattern tp)
                {
                    var ranges = tp.GetSelection();
                    if (ranges == null || ranges.Length == 0) return null;

                    var sb = new StringBuilder();
                    foreach (var r in ranges)
                    {
                        if (sb.Length >= MaxChars) break;
                        sb.Append(r.GetText(MaxChars - sb.Length));
                    }
                    var text = sb.ToString();
                    if (string.IsNullOrWhiteSpace(text)) return null;

                    var info = new SelInfo { Text = text };
                    try
                    {
                        var rects = ranges[0].GetBoundingRectangles();
                        if (rects != null && rects.Length > 0) info.First = rects[0];
                        var lastRects = ranges[ranges.Length - 1].GetBoundingRectangles();
                        if (lastRects != null && lastRects.Length > 0) info.Last = lastRects[lastRects.Length - 1];
                        if (info.Last.IsEmpty) info.Last = info.First;
                    }
                    catch { }
                    info.Editable = IsEditable(el, pid);
                    return info;
                }
                el = TreeWalker.ControlViewWalker.GetParent(el);
            }
        }
        catch { }
        return null;
    }

    static bool IsEditable(AutomationElement el, int pid)
    {
        try
        {
            if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vo) && vo is ValuePattern vp)
                return !vp.Current.IsReadOnly;
            var ct = el.Current.ControlType;
            if (ct == ControlType.Edit) return true;
            if (ct == ControlType.Document)
            {
                using var p = Process.GetProcessById(pid);
                return !ReadOnlyDocApps.Contains(p.ProcessName.ToLowerInvariant());
            }
        }
        catch { }
        return false;
    }

    // Modo compatível: Ctrl+C -> lê -> devolve o que estava na área de transferência
    async Task<SelInfo> ProbeCopyAsync()
    {
        string old = null;
        bool hadText = false;
        try
        {
            var data = System.Windows.Clipboard.GetDataObject();
            var fmts = data?.GetFormats() ?? Array.Empty<string>();
            if (fmts.Length > 0)
            {
                // só mexe se houver texto puro: imagens/arquivos copiados não podem ser perdidos
                if (!System.Windows.Clipboard.ContainsText()) return null;
                old = System.Windows.Clipboard.GetText();
                hadText = true;
            }
        }
        catch { return null; }

        _ignoreClipboard?.Invoke(1800);
        uint seq = GetClipboardSequenceNumber();
        SendCombo(VK_C);
        for (int i = 0; i < 12 && GetClipboardSequenceNumber() == seq; i++) await Task.Delay(20);
        if (GetClipboardSequenceNumber() == seq) return null;

        string text = null;
        for (int i = 0; i < 4 && text == null; i++)
        {
            try { if (System.Windows.Clipboard.ContainsText()) text = System.Windows.Clipboard.GetText(); break; }
            catch { await Task.Delay(25); }
        }
        for (int i = 0; i < 5; i++)
        {
            try
            {
                if (hadText) System.Windows.Clipboard.SetText(old); else System.Windows.Clipboard.Clear();
                break;
            }
            catch { await Task.Delay(25); }
        }
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (text.Length > MaxChars) text = text.Substring(0, MaxChars);
        return new SelInfo { Text = text, Editable = false };
    }

    // ---------------------------------------------------------------- popup
    void Present(SelInfo info, bool fromMouse, PT pt, IntPtr fg)
    {
        _win ??= new SelectionWindow();
        string text = info.Text.Trim();
        if (_win.IsShown && _shownText == text) return;   // já está aberto para essa seleção: não refaz (evita piscar)

        _win.Clear();
        if (info.Editable)
        {
            _win.AddButton("", "Recortar", "Recortar (Ctrl+X)", () => Send(VK_X));
            _win.AddButton("", "Copiar", "Copiar (Ctrl+C)", () => Send(VK_C));
            _win.AddButton("", "Colar", "Colar (Ctrl+V)", () => Send(VK_V));
        }
        else
        {
            _win.AddButton("", "Copiar", "Copiar (Ctrl+C)", () => CopyText(text, info));
        }

        bool sep = false;
        void Sep() { if (!sep) { _win.AddSeparator(); sep = true; } }

        if (TryCalc(text, out var calc))
        {
            Sep();
            _win.AddButton("", "= " + calc, "Copiar o resultado", () => CopyText(calc, null));
        }
        else if (IsUrl(text, out var url))
        {
            Sep();
            _win.AddButton("", "Abrir", url, () => OpenUrl(url));
        }
        else
        {
            Sep();
            _win.AddButton("", "Pesquisar", "Pesquisar na web", () => OpenUrl(
                "https://www.google.com/search?q=" + Uri.EscapeDataString(text.Length > 200 ? text.Substring(0, 200) : text)));
        }

        PlaceWindow(info, fromMouse, pt);
        _shownFg = fg;
        _shownAt = Environment.TickCount64;
        _shownText = text;
        StartWatch();
    }

    void PlaceWindow(SelInfo info, bool fromMouse, PT pt)
    {
        var size = _win.MeasurePx();
        double w = size.Width, h = size.Height, m = _win.MarginPx;

        var mon = MonitorFromPoint(pt, 2 /*MONITOR_DEFAULTTONEAREST*/);
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        RC work = new RC { Left = 0, Top = 0, Right = 1920, Bottom = 1080 };
        if (GetMonitorInfo(mon, ref mi)) work = mi.Work;

        // âncora horizontal: o mouse (se a seleção foi feita com ele) ou o fim da seleção
        double ax = fromMouse || !info.HasRect ? pt.X : info.Last.Right;
        double x = ax - w / 2;
        x = Math.Max(work.Left + 4 - m, Math.Min(x, work.Right - w - 4 + m));

        // vertical: acima da primeira linha; se não couber, abaixo da última
        double y;
        if (info.HasRect)
        {
            y = info.First.Top - h + m - Gap;
            if (y + m < work.Top + 4) y = info.Last.Bottom - m + Gap;
        }
        else
        {
            y = pt.Y - h + m - 26;
            if (y + m < work.Top + 4) y = pt.Y - m + 26;
        }
        y = Math.Max(work.Top + 4 - m, Math.Min(y, work.Bottom - h - 4 + m));

        _win.ShowAtPx((int)Math.Round(x), (int)Math.Round(y));
    }

    void StartWatch()
    {
        _watch ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _watch.Tick -= WatchTick;
        _watch.Tick += WatchTick;
        _watch.Start();
    }

    void WatchTick(object s, EventArgs e)
    {
        if (_win == null || !_win.IsShown) { _watch?.Stop(); return; }
        // trocou de janela (Alt+Tab, clique em outro programa) ou passou muito tempo
        if (GetForegroundWindow() != _shownFg || Environment.TickCount64 - _shownAt > AutoHideMs) HidePopup();
    }

    void HidePopup()
    {
        _watch?.Stop();
        _shownText = null;
        _win?.HideNow();
    }

    // ---------------------------------------------------------------- ações
    // O popup nunca ativa: o foco continua no programa de origem, então os atalhos vão direto para ele.
    void Send(int vk)
    {
        HidePopup();
        _token++;
        Task.Delay(30).ContinueWith(_ => SendCombo(vk), TaskScheduler.Default);
    }

    void CopyText(string text, SelInfo info)
    {
        // texto vindo do popup (resultado da conta) ou programa onde o Ctrl+C é confiável
        bool viaKeys = info != null;
        HidePopup();
        _token++;
        if (viaKeys) { Task.Delay(30).ContinueWith(_ => SendCombo(VK_C), TaskScheduler.Default); return; }
        try { System.Windows.Clipboard.SetText(text); } catch { }
    }

    static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    static void SendCombo(int vk)
    {
        var list = new List<INPUT>();
        void Key(int k, bool up) => list.Add(new INPUT
        {
            type = 1,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = (ushort)k, dwFlags = up ? 2u : 0u } }
        });
        // solta Shift/Alt que ainda estejam apertados, para virar Ctrl+tecla puro
        if (Down(VK_SHIFT)) Key(VK_SHIFT, true);
        if (Down(VK_MENU)) Key(VK_MENU, true);
        bool ctrlWasDown = Down(VK_CONTROL);
        if (!ctrlWasDown) Key(VK_CONTROL, false);
        Key(vk, false);
        Key(vk, true);
        if (!ctrlWasDown) Key(VK_CONTROL, true);
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    // ---------------------------------------------------------------- detectores de tipo
    static bool IsUrl(string t, out string url)
    {
        url = null;
        if (t.Length > 500 || t.Contains(' ') || t.Contains('\n')) return false;
        if (Regex.IsMatch(t, @"^https?://\S+$", RegexOptions.IgnoreCase)) { url = t; return true; }
        if (Regex.IsMatch(t, @"^www\.\S+\.\S+$", RegexOptions.IgnoreCase)) { url = "https://" + t; return true; }
        return false;
    }

    // Conta simples selecionada ("12*3 + 4", "(2+3)^2", "15% de..." não): mostra o resultado
    static bool TryCalc(string text, out string result)
    {
        result = null;
        var s = text.Trim();
        if (s.Length < 3 || s.Length > 80) return false;
        if (!Regex.IsMatch(s, @"^[0-9\s.,+\-*/x×÷^()%]+$")) return false;
        // precisa ter um operador de verdade (evita datas "05-01-2024" e telefones)
        if (!Regex.IsMatch(s, @"[+*/×÷^]|\dx\d|\d\s*x\s*\d|\s-\s|\)\s*-|-\s*\(")) return false;

        s = s.Replace('×', '*').Replace('÷', '/').Replace('x', '*').Replace(" ", "");
        if (s.Contains(',') && !s.Contains('.')) s = s.Replace(',', '.');
        else s = s.Replace(",", "");
        try
        {
            double v = new Calc(s).Run();
            if (double.IsNaN(v) || double.IsInfinity(v)) return false;
            result = Math.Round(v, 10).ToString("0.##########", CultureInfo.GetCultureInfo("pt-BR"));
            return result != s;
        }
        catch { return false; }
    }

    sealed class Calc
    {
        readonly string _s; int _p;
        public Calc(string s) { _s = s; }
        public double Run() { var v = Expr(); if (_p != _s.Length) throw new FormatException(); return v; }
        bool At(char c) => _p < _s.Length && _s[_p] == c;
        double Expr()
        {
            var v = Term();
            while (At('+') || At('-')) { char o = _s[_p++]; var r = Term(); v = o == '+' ? v + r : v - r; }
            return v;
        }
        double Term()
        {
            var v = Pow();
            while (At('*') || At('/')) { char o = _s[_p++]; var r = Pow(); v = o == '*' ? v * r : v / r; }
            return v;
        }
        double Pow()
        {
            var b = Unary();
            if (At('^')) { _p++; return Math.Pow(b, Pow()); }
            return b;
        }
        double Unary()
        {
            if (At('-')) { _p++; return -Unary(); }
            if (At('+')) { _p++; return Unary(); }
            var v = Atom();
            while (At('%')) { _p++; v /= 100; }
            return v;
        }
        double Atom()
        {
            if (At('('))
            {
                _p++;
                var v = Expr();
                if (!At(')')) throw new FormatException();
                _p++;
                return v;
            }
            int st = _p;
            while (_p < _s.Length && (char.IsDigit(_s[_p]) || _s[_p] == '.')) _p++;
            if (st == _p) throw new FormatException();
            return double.Parse(_s.Substring(st, _p - st), CultureInfo.InvariantCulture);
        }
    }
}
