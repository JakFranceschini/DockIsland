using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DockIsland;

// Um arquivo ou pasta guardado na aba "Arquivos"
public class ShelfItem
{
    public string Path { get; set; }
    public string Name { get; set; }
    public bool IsFolder { get; set; }
    public ImageSource Icon { get; set; }
    public string Subtitle { get; set; }
}

// =========================================================
//  Aba "Arquivos": arraste arquivos/pastas para a ilha e eles
//  ficam guardados ali até serem arrastados para outra pasta.
//  Lista salva em %AppData%\DockIsland\shelf.json
// =========================================================
public partial class MainWindow
{
    readonly ObservableCollection<ShelfItem> _shelf = new();
    bool _shelfDragOut;            // arrastando um item DA ilha para fora
    Point _shelfStart;             // onde o botão foi apertado
    ShelfItem _shelfPressed;       // item sobre o qual o botão foi apertado

    static string ShelfFile => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "shelf.json");

    void InitShelf()
    {
        ShelfList.ItemsSource = _shelf;
        _shelf.CollectionChanged += (_, __) =>
        {
            NoShelfText.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            UpdateShelfHeader();
        };
        try
        {
            if (File.Exists(ShelfFile))
            {
                var paths = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(ShelfFile)) ?? new();
                foreach (var p in paths) AddShelfPath(p, save: false);
            }
        }
        catch { }
        NoShelfText.Visibility = _shelf.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateShelfHeader();
    }

    void SaveShelf()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(ShelfFile)!);
            AtomicFile.WriteAllText(ShelfFile, JsonSerializer.Serialize(_shelf.Select(s => s.Path).ToList()));
        }
        catch { }
    }

    void UpdateShelfHeader() =>
        ShelfHeader.Text = _shelf.Count == 0 ? "Arquivos" : $"Arquivos ({_shelf.Count})";

    // Tira da lista o que não existe mais (apagado/movido por fora)
    void PruneShelf()
    {
        var gone = _shelf.Where(s => !(s.IsFolder ? Directory.Exists(s.Path) : File.Exists(s.Path))).ToList();
        foreach (var g in gone) _shelf.Remove(g);
        if (gone.Count > 0) SaveShelf();
    }

    bool AddShelfPath(string path, bool save = true)
    {
        try
        {
            bool isDir = Directory.Exists(path);
            if (!isDir && !File.Exists(path)) return false;
            if (_shelf.Any(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase))) return false;

            string sub;
            if (isDir) sub = "Pasta";
            else
            {
                var ext = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
                sub = (string.IsNullOrEmpty(ext) ? "Arquivo" : ext) + " · " + FormatSize(new FileInfo(path).Length);
            }

            _shelf.Insert(0, new ShelfItem
            {
                Path = path,
                Name = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path,
                IsFolder = isDir,
                Icon = FileShellIcon(path),
                Subtitle = sub
            });
            if (save) SaveShelf();
            return true;
        }
        catch { return false; }
    }

    static string FormatSize(long b)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b; int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return (i == 0 ? v.ToString("0") : v.ToString("0.#")) + " " + u[i];
    }

    // ---------- Ícone do Windows para o arquivo/pasta ----------
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEINFO
    {
        public IntPtr hIcon; public int iIcon; public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr SHGetFileInfo(string path, uint attrs, ref SHFILEINFO fi, uint cb, uint flags);

    static ImageSource FileShellIcon(string path)
    {
        try
        {
            var fi = new SHFILEINFO();
            const uint SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0;
            SHGetFileInfo(path, 0, ref fi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_ICON | SHGFI_LARGEICON);
            if (fi.hIcon == IntPtr.Zero) return null;
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(fi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            finally { DestroyIcon(fi.hIcon); }
        }
        catch { return null; }
    }

    // ---------- Soltar arquivos NA ilha ----------
    void Island_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        _leaveTimer.Stop();
        _peekTimer.Stop();
        if (_page != 5) SelectPage(5);
        if (Progress < 0.95) Expand();
        if (!_shelfDragOut) ShelfHeader.Text = "Solte para guardar";
    }

    void Island_DragOver(object sender, DragEventArgs e)
    {
        _leaveTimer.Stop();
        // não aceita o que a própria ilha está arrastando (o item fica onde está)
        e.Effects = !_shelfDragOut && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void Island_DragLeave(object sender, DragEventArgs e)
    {
        UpdateShelfHeader();
        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    void Island_Drop(object sender, DragEventArgs e)
    {
        UpdateShelfHeader();
        if (_shelfDragOut || e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        foreach (var f in files) AddShelfPath(f, save: false);
        SaveShelf();
        e.Handled = true;
    }

    // ---------- Arrastar itens PARA FORA da ilha ----------
    void ShelfItem_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ShelfItem it) return;
        _shelfPressed = it;
        _shelfStart = e.GetPosition(this);
    }

    void ShelfItem_PreviewMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _shelfPressed == null || _shelfDragOut) return;
        if (IsInsideButton(e.OriginalSource as DependencyObject)) return;   // botão de remover
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _shelfStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _shelfStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _shelfPressed;
        _shelfPressed = null;
        StartShelfDrag(new List<ShelfItem> { item }, (DependencyObject)sender);
    }

    void ShelfItem_Up(object sender, MouseButtonEventArgs e) => _shelfPressed = null;

    static bool IsInsideButton(DependencyObject d)
    {
        while (d != null)
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase) return true;
            d = d is Visual || d is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // Arrastar o título "Arquivos (n)" leva todos os itens de uma vez
    void ShelfHeader_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _shelfDragOut || _shelf.Count == 0) return;
        StartShelfDrag(_shelf.ToList(), (DependencyObject)sender);
    }

    void StartShelfDrag(List<ShelfItem> items, DependencyObject source)
    {
        PruneShelf();
        items = items.Where(i => _shelf.Contains(i)).ToList();
        if (items.Count == 0) return;

        var data = new DataObject(DataFormats.FileDrop, items.Select(i => i.Path).ToArray());
        _shelfDragOut = true;
        _leaveTimer.Stop();
        DragDropEffects result;
        try { result = DragDrop.DoDragDrop(source, data, DragDropEffects.Copy | DragDropEffects.Move); }
        catch { result = DragDropEffects.None; }
        _shelfDragOut = false;

        // soltou numa pasta: o item cumpriu a missão e sai da prateleira
        if (result != DragDropEffects.None)
        {
            foreach (var i in items) _shelf.Remove(i);
            SaveShelf();
        }
        UpdateShelfHeader();
        _leaveTimer.Stop();
        _leaveTimer.Start();   // se o mouse já saiu da ilha, ela recolhe
    }

    // ---------- Cliques ----------
    void ShelfItem_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || e.ClickCount < 2) return;
        if (sender is not FrameworkElement fe || fe.DataContext is not ShelfItem it) return;
        try { Process.Start(new ProcessStartInfo(it.Path) { UseShellExecute = true }); } catch { }
    }

    void ShelfRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ShelfItem it)
        {
            _shelf.Remove(it);
            SaveShelf();
        }
        e.Handled = true;
    }

    void ClearShelf_Click(object sender, RoutedEventArgs e)
    {
        _shelf.Clear();
        SaveShelf();
    }
}
