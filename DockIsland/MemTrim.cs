using System;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace DockIsland;

// Mantém a memória baixa: de tempos em tempos, com a ilha fechada, faz uma coleta de lixo completa e devolve
// ao Windows as páginas que o programa não está usando (é o número que aparece no Gerenciador de Tarefas).
// As páginas voltam sozinhas se o programa precisar delas; não muda nada no funcionamento.
internal static class MemTrim
{
    [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr hProcess);

    static DispatcherTimer _t;

    public static void Start()
    {
        if (_t != null) return;
        _t = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(90) };
        _t.Tick += (_, _) => Trim();
        _t.Start();
        // primeira limpeza logo depois da abertura (a inicialização deixa bastante lixo para trás)
        var first = new DispatcherTimer(DispatcherPriority.ApplicationIdle) { Interval = TimeSpan.FromSeconds(20) };
        first.Tick += (_, _) => { first.Stop(); Trim(); };
        first.Start();
    }

    static void Trim()
    {
        try
        {
            // ilha aberta/animando: não atrapalha
            if (Application.Current?.MainWindow is MainWindow mw && mw.Progress > 0.001) return;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            using var p = Process.GetCurrentProcess();
            EmptyWorkingSet(p.Handle);
        }
        catch { }
    }
}
