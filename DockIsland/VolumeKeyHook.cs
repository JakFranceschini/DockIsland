using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DockIsland;

// Captura as teclas de volume (mudo, diminuir, aumentar) antes de o Windows tratá-las.
// Como a tecla nunca chega ao Windows, o popup de volume do sistema não aparece:
// quem ajusta o volume (e mostra na pílula) é a própria ilha.
internal sealed class VolumeKeyHook : IDisposable
{
    public const int VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;

    delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
    [DllImport("user32.dll")]
    static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")]
    static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string lpModuleName);

    readonly HookProc _proc;      // guardado em campo para o GC não coletar o delegate
    readonly Action<int> _onKey;
    IntPtr _hook;

    public VolumeKeyHook(Action<int> onKey)
    {
        _onKey = onKey;
        _proc = Callback;
        try
        {
            using var p = Process.GetCurrentProcess();
            _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(p.MainModule?.ModuleName), 0);
        }
        catch { _hook = IntPtr.Zero; }
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            int vk = Marshal.ReadInt32(lParam);   // KBDLLHOOKSTRUCT.vkCode
            if (vk is VK_VOLUME_MUTE or VK_VOLUME_DOWN or VK_VOLUME_UP)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    // não faz o trabalho aqui dentro: o hook precisa responder rápido
                    try { System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => _onKey(vk))); }
                    catch { }
                }
                return (IntPtr)1;   // engole a tecla (down e up): o Windows não mostra o popup
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
