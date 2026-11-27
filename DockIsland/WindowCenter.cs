using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DockIsland;

// Win + C: centraliza a janela ativa no meio da área útil do monitor em que ela está
// (a área útil descarta a barra de tarefas, então o centro é o centro "de verdade").
// Janelas maximizadas, minimizadas, em tela cheia, a área de trabalho e as janelas do próprio
// DockIsland são ignoradas.
internal static class WindowCenter
{
    [StructLayout(LayoutKind.Sequential)] struct RC { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct MONITORINFO { public int Size; public RC Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RC r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);

    const uint MONITOR_DEFAULTTONEAREST = 2;
    const uint SWP_NOSIZE = 0x0001, SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

    public static void CenterForeground()
    {
        try
        {
            var h = GetForegroundWindow();
            if (h == IntPtr.Zero || !IsWindowVisible(h) || IsIconic(h) || IsZoomed(h)) return;

            // não mexe nas janelas do próprio DockIsland
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == (uint)Process.GetCurrentProcess().Id) return;

            // não mexe na área de trabalho nem na barra de tarefas
            var sb = new StringBuilder(64);
            GetClassName(h, sb, sb.Capacity);
            string cls = sb.ToString();
            if (cls == "Progman" || cls == "WorkerW" || cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") return;

            if (!GetWindowRect(h, out var r)) return;
            var mon = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref mi)) return;

            int w = r.Right - r.Left, ht = r.Bottom - r.Top;
            // tela cheia de verdade (jogo, vídeo, F11): deixa quieto
            if (w >= mi.Monitor.Right - mi.Monitor.Left && ht >= mi.Monitor.Bottom - mi.Monitor.Top) return;

            int x = mi.Work.Left + ((mi.Work.Right - mi.Work.Left) - w) / 2;
            int y = mi.Work.Top + ((mi.Work.Bottom - mi.Work.Top) - ht) / 2;
            SetWindowPos(h, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
        }
        catch { }
    }
}
