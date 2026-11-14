using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DockIsland;

// Ganchos globais (mouse + teclado) usados pelo popup de seleção.
// Eles só OBSERVAM: nunca engolem tecla nem clique (sempre chamam o próximo gancho) e não fazem
// trabalho dentro do callback — repassam o evento para a thread da interface e voltam na hora.
// Eventos injetados (SendInput, inclusive os do próprio programa) são ignorados.
internal sealed class SelectionHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13, WH_MOUSE_LL = 14;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_RBUTTONDOWN = 0x0204,
              WM_MBUTTONDOWN = 0x0207, WM_MOUSEWHEEL = 0x020A;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    struct MSLLHOOKSTRUCT { public int X, Y; public uint MouseData, Flags, Time; public IntPtr Extra; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string lpModuleName);

    readonly HookProc _mouseProc, _keyProc;   // em campos: o GC não pode coletar os delegates
    IntPtr _mouseHook, _keyHook;

    public event Action<int, int> MouseDown;   // botão esquerdo (x, y em pixels)
    public event Action<int, int> MouseUp;
    public event Action Dismiss;               // botão direito/do meio ou roda do mouse
    public event Action<int> KeyDown;          // vkCode
    public event Action<int> KeyUp;

    public SelectionHook()
    {
        _mouseProc = MouseCallback;
        _keyProc = KeyCallback;
        try
        {
            using var p = Process.GetCurrentProcess();
            var mod = GetModuleHandle(p.MainModule?.ModuleName);
            _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, mod, 0);
            _keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, mod, 0);
        }
        catch { }
    }

    static void Post(Action a)
    {
        try { System.Windows.Application.Current?.Dispatcher.BeginInvoke(a); } catch { }
    }

    IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                var m = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                if ((m.Flags & 1) == 0)   // 1 = injetado
                {
                    int x = m.X, y = m.Y;
                    switch (wParam.ToInt32())
                    {
                        case WM_LBUTTONDOWN: Post(() => MouseDown?.Invoke(x, y)); break;
                        case WM_LBUTTONUP: Post(() => MouseUp?.Invoke(x, y)); break;
                        case WM_RBUTTONDOWN:
                        case WM_MBUTTONDOWN:
                        case WM_MOUSEWHEEL: Post(() => Dismiss?.Invoke()); break;
                    }
                }
            }
            catch { }
        }
        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    IntPtr KeyCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            try
            {
                int vk = Marshal.ReadInt32(lParam);           // KBDLLHOOKSTRUCT.vkCode
                int flags = Marshal.ReadInt32(lParam, 8);     // KBDLLHOOKSTRUCT.flags
                if ((flags & 0x10) == 0)                      // LLKHF_INJECTED
                {
                    int msg = wParam.ToInt32();
                    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) Post(() => KeyDown?.Invoke(vk));
                    else if (msg == WM_KEYUP || msg == WM_SYSKEYUP) Post(() => KeyUp?.Invoke(vk));
                }
            }
            catch { }
        }
        return CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        if (_mouseHook != IntPtr.Zero) { UnhookWindowsHookEx(_mouseHook); _mouseHook = IntPtr.Zero; }
        if (_keyHook != IntPtr.Zero) { UnhookWindowsHookEx(_keyHook); _keyHook = IntPtr.Zero; }
    }
}
