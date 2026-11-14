using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DockIsland;

// Atalhos globais com a tecla Windows:
//   Win (sozinha, apertar e soltar)  -> onWinAlone  (abre/fecha o lançador de apps)
//   Win + S  ou  Win + Q             -> onWinS      (abre a busca de pastas e arquivos da ilha)
// Os dois substituem o comportamento do Windows: o menu Iniciar e a busca do Windows não abrem.
// Qualquer outro atalho com Win (Win+E, Win+D, Win+Shift+S...) continua funcionando normalmente.
internal sealed class WinKeyHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_S = 0x53, VK_Q = 0x51, VK_BROWSER_SEARCH = 0xAA;   // 0xAA: tecla de multimídia "Pesquisar"
    const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
    const int VK_MASK = 0xE8;                 // tecla "não atribuída": cancela a abertura do menu Iniciar
    const int LLKHF_INJECTED = 0x10, LLKHF_ALTDOWN = 0x20;
    const int VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    const int VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    const uint KEYEVENTF_KEYUP = 0x0002;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string lpModuleName);
    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")]
    static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    readonly HookProc _proc;      // guardado em campo para o GC não coletar o delegate
    readonly Action _onWinAlone;
    readonly Action _onWinS;
    IntPtr _hook;

    // ---- seletor de janelas (Alt+Tab / Win+Tab) ----
    public Func<int> SwitcherMode;          // 0 fechado, 1 Alt+Tab, 2 visão geral
    public Action<bool> OnAltTab;           // Alt+Tab apertado (true = com Shift)
    public Action OnAltRelease;             // Alt solto depois de um Alt+Tab
    public Action OnWinTab;                 // Win+Tab apertado
    public Action<int, bool> OnSwitcherKey; // Tab / setas / Enter / Esc com o seletor aberto
    bool _ctrlDown;                         // Ctrl físico apertado (eventos injetados, como os do seletor, não contam)
    bool _altTab;                           // Alt+Tab em andamento (Alt ainda apertado)
    bool _tabSwallowed;                     // engolimos o Tab: falta engolir o "soltar Tab"

    bool _winDown;        // alguma tecla Windows está apertada
    bool _otherKey;       // outra tecla foi apertada junto com a Windows (e passou para o Windows)
    bool _swallowedCombo; // nós engolimos uma combinação (Win+S): o Iniciar precisa ser cancelado
    bool _bsDown;         // engolimos a tecla "Pesquisar" e falta engolir o "soltar"
    bool _sDown;          // engolimos o "S" e ainda falta engolir o "soltar S"

    public WinKeyHook(Action onWinAlone, Action onWinS)
    {
        _onWinAlone = onWinAlone;
        _onWinS = onWinS;
        _proc = Callback;
        try
        {
            using var p = Process.GetCurrentProcess();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(p.MainModule?.ModuleName), 0);
        }
        catch { _hook = IntPtr.Zero; }
    }

    static bool Down(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // Aperta e solta uma tecla sem função: o Windows entende "Win + outra tecla" e não abre o Iniciar
    static void MaskStartMenu()
    {
        keybd_event((byte)VK_MASK, 0, 0, UIntPtr.Zero);
        keybd_event((byte)VK_MASK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    static void Post(Action a)
    {
        // não faz o trabalho aqui dentro: o hook precisa responder rápido
        try { System.Windows.Application.Current?.Dispatcher.BeginInvoke(a); } catch { }
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);          // KBDLLHOOKSTRUCT.vkCode
            int flags = Marshal.ReadInt32(lParam, 8);    // KBDLLHOOKSTRUCT.flags
            int msg = wParam.ToInt32();

            // Teclas injetadas (inclusive a nossa máscara) passam direto, MENOS Win/S/Q: o Logitech Options+ e
            // programas parecidos enviam a tecla de pesquisa como "Win+S" simulado, e ela também deve abrir a busca.
            bool injected = (flags & LLKHF_INJECTED) != 0;
            bool searchKeys = vk == VK_LWIN || vk == VK_RWIN || vk == VK_S || vk == VK_Q || vk == VK_BROWSER_SEARCH;
            if (!injected || searchKeys)
            {
                bool down = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool up = msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (vk == 0x11 || vk == 0xA2 || vk == 0xA3) _ctrlDown = down;

                // Estado "preso": se o Windows não avisou que Win/Alt foram soltos (Win+L, Ctrl+Alt+Del, UAC, troca de
                // sessão...), os flags ficariam ligados e o Tab sozinho abriria o Win+Tab, o Alt+Tab voltaria ao do
                // Windows e Tab/setas/Enter/Esc seriam engolidos. Confere o estado real do teclado antes de decidir.
                bool isWinKey = vk == VK_LWIN || vk == VK_RWIN;
                bool isAltKey = vk == VK_LMENU || vk == VK_RMENU || vk == VK_MENU;
                if (!injected && _winDown && !isWinKey && !Down(VK_LWIN) && !Down(VK_RWIN))
                {
                    _winDown = false; _swallowedCombo = false; _otherKey = false;
                }
                if (_altTab && !isAltKey && !Down(VK_MENU))
                {
                    _altTab = false;
                    if (OnAltRelease != null) Post(OnAltRelease);   // Alt já tinha sido solto: confirma a escolha
                }
                // ----- Alt+Tab: o seletor da ilha substitui o do Windows -----
                if (vk == VK_TAB && down && (flags & LLKHF_ALTDOWN) != 0 && !_winDown && !_ctrlDown && OnAltTab != null)
                {
                    bool shift = Down(VK_SHIFT);
                    _altTab = true; _tabSwallowed = true;
                    Post(() => OnAltTab(shift));
                    return (IntPtr)1;
                }
                // ----- Win+Tab: visão geral das janelas -----
                if (vk == VK_TAB && down && _winDown && !_ctrlDown && (flags & LLKHF_ALTDOWN) == 0 && OnWinTab != null)
                {
                    _swallowedCombo = true;           // o Iniciar não pode abrir quando a Win for solta
                    if (_tabSwallowed)                // Tab repetido/segundo Tab com a Win segura: troca de janela
                    {
                        bool shift = Down(VK_SHIFT);
                        if (OnSwitcherKey != null) Post(() => OnSwitcherKey(VK_TAB, shift));
                    }
                    else Post(OnWinTab);
                    _tabSwallowed = true;
                    return (IntPtr)1;
                }
                if (vk == VK_TAB && up && _tabSwallowed)
                {
                    _tabSwallowed = false;
                    return (IntPtr)1;
                }
                // Alt solto: confirma a janela escolhida (e impede o Alt sozinho de focar o menu do programa)
                if (up && _altTab && (vk == VK_LMENU || vk == VK_RMENU || vk == VK_MENU))
                {
                    _altTab = false;
                    MaskStartMenu();
                    if (OnAltRelease != null) Post(OnAltRelease);
                }
                // Seletor aberto: Tab, setas, Enter e Esc são dele
                else if ((_altTab || (SwitcherMode?.Invoke() ?? 0) != 0) && OnSwitcherKey != null &&
                         (vk == VK_TAB || vk == VK_LEFT || vk == VK_RIGHT || vk == VK_UP || vk == VK_DOWN || vk == VK_RETURN || vk == VK_ESCAPE))
                {
                    if (down)
                    {
                        bool shift = Down(VK_SHIFT);
                        int key = vk;
                        Post(() => OnSwitcherKey(key, shift));
                    }
                    return (IntPtr)1;
                }

                // Tecla "Pesquisar" dedicada do teclado (VK_BROWSER_SEARCH): abre a busca da ilha
                if (vk == VK_BROWSER_SEARCH)
                {
                    if (down) { if (!_bsDown) { _bsDown = true; Post(_onWinS); } return (IntPtr)1; }
                    if (up && _bsDown) { _bsDown = false; return (IntPtr)1; }
                }

                if (vk == VK_LWIN || vk == VK_RWIN)
                {
                    if (down)
                    {
                        if (!_winDown)   // 1ª vez (as repetições de tecla presa são ignoradas)
                        {
                            _winDown = true;
                            _otherKey = false;
                            _swallowedCombo = false;
                        }
                    }
                    else if (up && _winDown)
                    {
                        _winDown = false;
                        if (_swallowedCombo)
                        {
                            MaskStartMenu();          // Win+S foi nosso: não deixa o Iniciar abrir
                        }
                        else if (!_otherKey)
                        {
                            MaskStartMenu();          // Win sozinha: o lançador substitui o Iniciar
                            Post(_onWinAlone);
                        }
                    }
                }
                else if ((vk == VK_S || vk == VK_Q) && (_winDown || _sDown))
                {
                    if (down && _sDown)
                    {
                        return (IntPtr)1;             // S preso (repetição): continua engolindo
                    }
                    else if (down && _winDown)
                    {
                        // só Win+S / Win+Q puros (Win+Shift+S, Win+Ctrl+S... seguem para o Windows)
                        if (!Down(VK_SHIFT) && !Down(VK_CONTROL) && !Down(VK_MENU))
                        {
                            _sDown = true; _swallowedCombo = true; Post(_onWinS);
                            return (IntPtr)1;         // engole o S: a busca do Windows não abre
                        }
                        _otherKey = true;
                    }
                    else if (up && _sDown)
                    {
                        _sDown = false;
                        return (IntPtr)1;             // engole o "soltar S"
                    }
                }
                else if (down && _winDown)
                {
                    _otherKey = true;                 // Win+E, Win+D, Win+Shift... seguem normalmente
                }
            }
        }
        return CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }
}
