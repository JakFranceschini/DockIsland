using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace DockIsland;

// Um resultado da busca (app, pasta ou arquivo)
public class SearchItem : INotifyPropertyChanged
{
    ImageSource _icon;

    public string Name { get; set; }
    public string Path { get; set; }        // caminho do arquivo/pasta ou "shell:AppsFolder\..." (apps)
    public string Subtitle { get; set; }
    public bool IsApp { get; set; }
    public bool IsFolder { get; set; }
    public Visibility LocVis => IsApp ? Visibility.Collapsed : Visibility.Visible;

    public ImageSource Icon
    {
        get => _icon;
        set { _icon = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon))); }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}

// =========================================================
//  Aba "Busca": campo de busca dentro da ilha. Procura apps (lista do lançador),
//  pastas e arquivos (índice do Windows Search; se ele não responder, varre as pastas do usuário).
//  A janela da ilha normalmente não ativa (não rouba foco); só enquanto a busca está aberta
//  ela passa a aceitar digitação, e volta ao normal ao sair.
// =========================================================
public partial class MainWindow
{
    readonly ObservableCollection<SearchItem> _hits = new();
    readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    CancellationTokenSource _searchCts;
    int _searchVer;
    bool _searchMode;
    IntPtr _prevFg;   // janela que estava em foco antes de abrir a busca (volta para ela ao sair)

    [DllImport("user32.dll", EntryPoint = "IsWindow")] static extern bool IsWindowHandle(IntPtr h);

    // Enquanto a busca está aberta e em foco, a ilha não recolhe sozinha
    bool SearchHolds => ((_page == 6 && _searchMode) || (_page == 7 && _notesMode)) && IsActive;   // busca ou notas abertas e em foco

    void InitSearch()
    {
        SearchList.ItemsSource = _hits;
        _searchTimer.Tick += (_, __) => { _searchTimer.Stop(); _ = RunSearchAsync(); };

        // clicou fora (outra janela ficou em foco): fecha a busca
        Deactivated += (_, __) =>
        {
            if (_page != 6) return;
            SelectPage(0);
            if (!Island.IsMouseOver && !_menuOpen) Collapse();
        };
    }

    void Search_Click(object sender, RoutedEventArgs e) =>
        SelectPage(_page == 6 ? 0 : 6);

    // ---------- Entrar / sair do modo de busca ----------
    void EnterSearch()
    {
        if (_searchMode) return;
        _searchMode = true;
        _leaveTimer.Stop();
        _peekTimer.Stop();

        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
        {
            var fg = GetForegroundWindow();
            _prevFg = fg != h ? fg : IntPtr.Zero;
            // a ilha passa a poder receber o teclado
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
        }

        _ = StartAppIcons.EnsureAsync();   // garante a lista de apps para a busca

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!_searchMode) return;
            try
            {
                Activate();
                if (h != IntPtr.Zero) SetForegroundWindow(h);
                SearchBox.Focus();
                Keyboard.Focus(SearchBox);
                SearchBox.SelectAll();
            }
            catch { }
        }), DispatcherPriority.Input);

        UpdateSearchState();
    }

    void LeaveSearch()
    {
        if (!_searchMode) return;
        _searchMode = false;
        _searchTimer.Stop();
        _searchCts?.Cancel();
        _searchVer++;
        SearchBox.Text = "";
        _hits.Clear();

        var h = new WindowInteropHelper(this).Handle;
        if (h != IntPtr.Zero)
        {
            bool weAreForeground = GetForegroundWindow() == h;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE);
            // devolve o foco para o programa em que você estava
            if (weAreForeground && _prevFg != IntPtr.Zero && IsWindowHandle(_prevFg))
            {
                try { SetForegroundWindow(_prevFg); } catch { }
            }
        }
        _prevFg = IntPtr.Zero;
    }

    // ---------- Campo de busca ----------
    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        bool has = SearchBox.Text.Length > 0;
        SearchHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        SearchClear.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _searchTimer.Stop();
        if (!_searchMode) return;

        if (SearchBox.Text.Trim().Length < 2)
        {
            _searchCts?.Cancel();
            _searchVer++;
            _hits.Clear();
            UpdateSearchState();
            return;
        }
        _searchTimer.Start();   // espera um instante depois da última tecla
    }

    void SearchClear_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "";
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                MoveSearchSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSearchSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                {
                    var it = SearchList.SelectedItem as SearchItem ?? _hits.FirstOrDefault();
                    if (it != null)
                        OpenHit(it, location: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
                    e.Handled = true;
                    break;
                }
            case Key.Escape:
                if (SearchBox.Text.Length > 0) SearchBox.Text = "";
                else
                {
                    SelectPage(0);
                    if (!Island.IsMouseOver) Collapse();
                }
                e.Handled = true;
                break;
        }
    }

    void MoveSearchSelection(int delta)
    {
        if (_hits.Count == 0) return;
        int i = Math.Clamp(SearchList.SelectedIndex + delta, 0, _hits.Count - 1);
        SearchList.SelectedIndex = i;
        SearchList.ScrollIntoView(_hits[i]);
    }

    // ---------- Resultados ----------
    void UpdateSearchState(bool searching = false)
    {
        bool empty = _hits.Count == 0;
        SearchList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        SearchStatus.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        if (!empty) return;
        SearchStatus.Text = SearchBox.Text.Trim().Length < 2 ? "Digite para buscar"
                          : searching ? "Buscando…"
                          : "Nenhum resultado";
    }

    async Task RunSearchAsync()
    {
        var q = SearchBox.Text.Trim();
        if (q.Length < 2 || !_searchMode) return;

        int ver = ++_searchVer;
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var ct = cts.Token;

        // 1) apps: já estão na memória, aparecem na hora
        var results = new List<SearchItem>();
        foreach (var (name, target) in StartAppIcons.SearchApps(q, 5))
            results.Add(new SearchItem { Name = name, Path = target, Subtitle = "Aplicativo", IsApp = true });

        _hits.Clear();
        foreach (var r in results) _hits.Add(r);
        if (_hits.Count > 0) SearchList.SelectedIndex = 0;
        UpdateSearchState(searching: true);
        _ = LoadHitIconsAsync(ver);

        // 2) pastas e arquivos (em segundo plano)
        List<FileHit> files;
        try { files = await Task.Run(() => WinSearch.Query(q, 40, ct), ct); }
        catch { return; }   // cancelada por uma busca mais nova
        if (ver != _searchVer || ct.IsCancellationRequested) return;

        foreach (var f in files)
        {
            var dir = f.Folder ? System.IO.Path.GetDirectoryName(f.Path.TrimEnd('\\', '/')) : System.IO.Path.GetDirectoryName(f.Path);
            _hits.Add(new SearchItem
            {
                Name = f.Name,
                Path = f.Path,
                IsFolder = f.Folder,
                Subtitle = (f.Folder ? "Pasta · " : "") + (string.IsNullOrEmpty(dir) ? f.Path : dir)
            });
        }
        if (SearchList.SelectedIndex < 0 && _hits.Count > 0) SearchList.SelectedIndex = 0;
        UpdateSearchState();
        _ = LoadHitIconsAsync(ver);
    }

    // Ícones aos poucos, sem travar a digitação
    async Task LoadHitIconsAsync(int ver)
    {
        foreach (var it in _hits.ToList())
        {
            if (it.Icon != null) continue;
            await Dispatcher.Yield(DispatcherPriority.Background);
            if (ver != _searchVer) return;
            it.Icon = it.IsApp ? ShellIcon.GetCached(it.Path)
                                : ShellIcon.Get(it.Path, 48) ?? FileShellIcon(it.Path);
        }
    }

    // ---------- Abrir ----------
    void SearchItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SearchItem it) return;
        OpenHit(it, location: false);
    }

    // Clique direito: abre a pasta do item
    void SearchItem_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SearchItem it) return;
        if (!it.IsApp) OpenHit(it, location: true);
        e.Handled = true;
    }

    void SearchLoc_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not SearchItem it) return;
        OpenHit(it, location: true);
    }

    void OpenHit(SearchItem it, bool location)
    {
        try
        {
            if (location && !it.IsApp)
                Process.Start("explorer.exe", "/select,\"" + it.Path + "\"");
            else
            {
                var psi = new ProcessStartInfo(it.Path) { UseShellExecute = true };
                if (!it.IsApp && File.Exists(it.Path)) psi.WorkingDirectory = System.IO.Path.GetDirectoryName(it.Path) ?? "";
                Process.Start(psi);
            }
        }
        catch { }

        _prevFg = IntPtr.Zero;   // o item aberto fica com o foco
        SelectPage(0);
        Collapse();
    }
}

public readonly record struct FileHit(string Name, string Path, bool Folder);

// =========================================================
//  Busca de pastas e arquivos
//  1) Windows Search (o índice do Windows) via ADODB, sem pacotes extras
//  2) se o índice não responder ou achar pouco: varre as pastas do usuário e a raiz dos discos
// =========================================================
static class WinSearch
{
    public static List<FileHit> Query(string text, int max, CancellationToken ct)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(5).ToArray();
        if (words.Length == 0) return new List<FileHit>();

        var found = new List<FileHit>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var h in QueryIndex(words, 150, ct))
                if (Acceptable(h.Path) && seen.Add(h.Path)) found.Add(h);
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // índice desligado ou com poucos resultados: completa varrendo as pastas
        if (found.Count < 12)
        {
            try { found.AddRange(Scan(words, 40, ct, seen)); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return found
            .Select(h => (Hit: h, Score: Score(h, words[0])))
            .OrderBy(x => x.Score)
            .Take(max)
            .Select(x => x.Hit)
            .ToList();
    }

    // Quanto menor, melhor: nome exato, começa com a busca, palavra começa com a busca, contém
    static double Score(FileHit h, string first)
    {
        var name = h.Name ?? "";
        var bare = System.IO.Path.GetFileNameWithoutExtension(name);
        double s;
        if (string.Equals(bare, first, StringComparison.CurrentCultureIgnoreCase) ||
            string.Equals(name, first, StringComparison.CurrentCultureIgnoreCase)) s = 0;
        else if (name.StartsWith(first, StringComparison.CurrentCultureIgnoreCase)) s = 1;
        else if (name.IndexOf(" " + first, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                 name.IndexOf("_" + first, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                 name.IndexOf("-" + first, StringComparison.CurrentCultureIgnoreCase) >= 0) s = 2;
        else s = 3;
        return h.Folder ? s - 0.5 : s;   // pastas um pouco na frente
    }

    // Só caminhos de verdade (nada de e-mails do índice) e sem lixo do sistema
    static bool Acceptable(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 4) return false;
        bool drive = char.IsLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        if (!drive && !path.StartsWith("\\\\")) return false;
        if (path.IndexOf("\\$Recycle.Bin", StringComparison.OrdinalIgnoreCase) >= 0) return false;
        if (path.IndexOf("\\AppData\\", StringComparison.OrdinalIgnoreCase) >= 0 &&
            path.IndexOf("\\Microsoft\\Windows\\Start Menu", StringComparison.OrdinalIgnoreCase) < 0) return false;
        // atalhos do menu Iniciar já aparecem como aplicativos
        if (path.IndexOf("\\Start Menu\\", StringComparison.OrdinalIgnoreCase) >= 0 &&
            path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    static string RealPath(string url)
    {
        try
        {
            if (string.IsNullOrEmpty(url) || !url.StartsWith("file:", StringComparison.OrdinalIgnoreCase)) return null;
            var p = Uri.UnescapeDataString(url.Substring(5)).Replace('/', '\\');
            return p.Length > 3 ? p : null;
        }
        catch { return null; }
    }

    static string Sql(string s) => s.Replace("'", "''");

    // ---------- 1) Índice do Windows Search ----------
    static List<FileHit> QueryIndex(string[] words, int fetch, CancellationToken ct)
    {
        var list = new List<FileHit>();
        var type = Type.GetTypeFromProgID("ADODB.Connection");
        if (type == null) return list;

        dynamic conn = null, rs = null;
        try
        {
            conn = Activator.CreateInstance(type);
            conn.Open("Provider=Search.CollatorDSO;Extended Properties='Application=Windows';");

            var where = string.Join(" AND ", words.Select(w => "System.FileName LIKE '%" + Sql(w) + "%'"));
            var sql = "SELECT TOP " + fetch + " System.FileName, System.ItemUrl, System.ItemPathDisplay FROM SystemIndex " +
                      "WHERE SCOPE='file:' AND " + where + " ORDER BY System.DateModified DESC";

            try { rs = conn.Execute(sql); }
            catch
            {
                var rtype = Type.GetTypeFromProgID("ADODB.Recordset");
                rs = Activator.CreateInstance(rtype);
                rs.Open(sql, conn);
            }

            while (!rs.EOF)
            {
                ct.ThrowIfCancellationRequested();
                object n = rs.Fields[0].Value;
                object u = rs.Fields[1].Value;
                object p = rs.Fields[2].Value;
                var name = n as string;
                // ItemPathDisplay vem com nomes traduzidos (C:\Usuários\...), que não existem de verdade;
                // o caminho real está em ItemUrl ("file:C:/Users/...")
                var path = RealPath(u as string) ?? (p as string);
                if (!string.IsNullOrEmpty(path))
                {
                    bool folder = Directory.Exists(path);
                    list.Add(new FileHit(string.IsNullOrEmpty(name) ? System.IO.Path.GetFileName(path) : name, path, folder));
                }
                rs.MoveNext();
            }
        }
        finally
        {
            try { rs?.Close(); } catch { }
            try { conn?.Close(); } catch { }
            try { if (rs != null) Marshal.FinalReleaseComObject(rs); } catch { }
            try { if (conn != null) Marshal.FinalReleaseComObject(conn); } catch { }
        }
        return list;
    }

    // ---------- 2) Varredura de pastas (plano B) ----------
    static readonly string[] SkipDirs =
    {
        "Windows", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin",
        "System Volume Information", "AppData", "node_modules", ".git", "Recovery"
    };

    static IEnumerable<(string Dir, int Limit)> Roots()
    {
        string P(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var profile = P(Environment.SpecialFolder.UserProfile);
        var user = new[]
        {
            P(Environment.SpecialFolder.DesktopDirectory), P(Environment.SpecialFolder.MyDocuments),
            System.IO.Path.Combine(profile, "Downloads"), P(Environment.SpecialFolder.MyPictures),
            P(Environment.SpecialFolder.MyVideos), P(Environment.SpecialFolder.MyMusic)
        };
        foreach (var d in user.Where(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)).Distinct(StringComparer.OrdinalIgnoreCase))
            yield return (d, 5);
        if (Directory.Exists(profile)) yield return (profile, 1);

        foreach (var drv in DriveInfo.GetDrives())
        {
            bool ok;
            try { ok = drv.DriveType == DriveType.Fixed && drv.IsReady; } catch { ok = false; }
            if (ok) yield return (drv.RootDirectory.FullName, 2);
        }
    }

    static List<FileHit> Scan(string[] words, int need, CancellationToken ct, HashSet<string> seen)
    {
        var found = new List<FileHit>();
        var sw = Stopwatch.StartNew();
        var q = new Queue<(string Dir, int Depth, int Limit)>();
        foreach (var (dir, limit) in Roots()) q.Enqueue((dir, 0, limit));

        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false
        };

        while (q.Count > 0 && found.Count < need && sw.ElapsedMilliseconds < 1500)
        {
            ct.ThrowIfCancellationRequested();
            var (dir, depth, limit) = q.Dequeue();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos("*", opts); }
            catch { continue; }

            foreach (var fi in entries)
            {
                bool isDir = (fi.Attributes & FileAttributes.Directory) != 0;
                var name = fi.Name;
                if (words.All(w => name.IndexOf(w, StringComparison.CurrentCultureIgnoreCase) >= 0) && seen.Add(fi.FullName))
                    found.Add(new FileHit(name, fi.FullName, isDir));
                if (isDir && depth < limit && !SkipDirs.Contains(name, StringComparer.OrdinalIgnoreCase))
                    q.Enqueue((fi.FullName, depth + 1, limit));
            }
        }
        return found;
    }
}
