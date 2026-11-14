using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;

namespace DockIsland;

// Ação rápida da aba Sistema: esvaziar a lixeira
public partial class MainWindow
{
    // o layout depende do empacotamento usado pelo cabeçalho do Windows: tenta os dois
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct SHQUERYRBINFO1 { public int cbSize; public long i64Size; public long i64NumItems; }
    [StructLayout(LayoutKind.Sequential)]
    struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    static extern int SHQueryRecycleBin(string root, ref SHQUERYRBINFO info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    static extern int SHQueryRecycleBin1(string root, ref SHQUERYRBINFO1 info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHEmptyRecycleBin(IntPtr hwnd, string root, uint flags);

    const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    async void RefreshTrash()
    {
        try
        {
            var (items, size) = await Task.Run(() =>
            {
                var a = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
                if (SHQueryRecycleBin(null, ref a) == 0) return (a.i64NumItems, a.i64Size);
                var b = new SHQUERYRBINFO1 { cbSize = Marshal.SizeOf<SHQUERYRBINFO1>() };
                if (SHQueryRecycleBin1(null, ref b) == 0) return (b.i64NumItems, b.i64Size);
                return (-1L, 0L);
            });
            TrashInfo.Text = items < 0 ? "" : items == 0 ? "Vazia"
                : $"{items} {(items == 1 ? "item" : "itens")} · {FormatSize(size)}";
            TrashInfo.Visibility = items < 0 ? Visibility.Collapsed : Visibility.Visible;
            TrashBtn.IsEnabled = items != 0;
            TrashBtn.Opacity = items == 0 ? 0.55 : 1;
        }
        catch { }
    }

    void Trash_Click(object sender, RoutedEventArgs e) => ShowConfirm("trash");

    async void EmptyTrash()
    {
        try
        {
            TrashInfo.Text = "Esvaziando…";
            await Task.Run(() => SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND));
        }
        catch { }
        RefreshTrash();
    }
}
