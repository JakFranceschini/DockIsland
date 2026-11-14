using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DockIsland;

// Um aplicativo (fixado no dock, aberto agora ou listado no lançador)
public class AppEntry : INotifyPropertyChanged
{
    ImageSource _icon;
    bool _running;

    public string Name { get; set; }
    public string Target { get; set; }       // .exe/.lnk ou "shell:AppsFolder\<AppID>"
    public string ResolvedExe { get; set; }  // .exe real (para casar com os programas abertos)
    public string Key { get; set; }          // identifica o item entre uma leitura e outra (programas abertos)
    public IntPtr Hwnd { get; set; }         // janela principal quando está aberto
    public bool IsPinned { get; set; }
    public bool IsFrameWindow { get; set; }  // app da Loja (janela hospedada pelo Windows)
    public bool IsDivider { get; set; }      // divisor do dock (não é um app)
    public string IconPath { get; set; }     // (legado) ícone personalizado
    public ImageSource DefaultIcon { get; set; }   // ícone padrão do app
    public bool FromLauncher { get; set; }         // já usa o ícone do lançador
    public string Aumid { get; set; }              // AppUserModelID (identidade do app no Windows; liga o dock ao app do lançador)
    public string GroupName { get; set; }          // categoria mostrada no lançador
    public int GroupOrder { get; set; }            // ordem da categoria no lançador

    // Chave do ícone personalizado: vale para o dock e para o lançador
    public string IconKey => IsFrameWindow ? "frame:" + Name : Target;

    // Ícone mostrado = personalizado (se houver) ou o padrão
    public void RefreshIcon() => Icon = IconStore.CustomFor(this) ?? DefaultIcon;

    // Todos os "nomes" pelos quais o ícone personalizado deste app é conhecido.
    // O mesmo app pode ser o atalho (.lnk) no lançador e o .exe no dock; casamos por
    // chave principal, por .exe e por nome, assim o ícone escolhido vale nos dois.
    public string[] IconKeys()
    {
        var l = new List<string> { IconKey };
        var exe = !string.IsNullOrEmpty(ResolvedExe) ? ResolvedExe
                : (Target != null && Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Target : null);
        if (!IsFrameWindow && !string.IsNullOrEmpty(exe)) l.Add("exe:" + exe);
        if (!string.IsNullOrWhiteSpace(Name)) l.Add("name:" + Name.Trim());
        if (IsExplorer(exe, Name)) l.Add("special:explorer");
        return l.ToArray();
    }

    // O Explorador de Arquivos aparece com nomes e destinos diferentes no lançador
    // (app do menu Iniciar, sem .exe) e no dock (explorer.exe, nome do idioma do Windows).
    // Uma chave comum faz os dois usarem o mesmo ícone.
    static readonly string[] ExplorerNames =
    {
        "Explorador de Arquivos", "Explorador do Windows", "Explorador de Ficheiros",
        "File Explorer", "Windows Explorer", "Explorer"
    };

    static bool IsExplorer(string exe, string name)
    {
        if (!string.IsNullOrEmpty(exe) &&
            string.Equals(Path.GetFileName(exe), "explorer.exe", StringComparison.OrdinalIgnoreCase)) return true;
        return !string.IsNullOrWhiteSpace(name) &&
            ExplorerNames.Any(n => string.Equals(n, name.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public string Initial => string.IsNullOrEmpty(Name) ? "•" : Name.Substring(0, 1).ToUpperInvariant();
    public Visibility FallbackVis => _icon == null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AppVis => IsDivider ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DividerVis => IsDivider ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RunningVis => _running ? Visibility.Visible : Visibility.Collapsed;

    // Janelas abertas deste app (a primeira é a de cima). Com mais de uma, o dock mostra um contador.
    List<WinRef> _windows = new();
    public List<WinRef> Windows
    {
        get => _windows;
        set
        {
            value ??= new List<WinRef>();
            if (_windows.Count == value.Count && _windows.Select(w => w.Hwnd).SequenceEqual(value.Select(w => w.Hwnd)))
            {
                _windows = value;   // mesmas janelas (só os títulos podem ter mudado)
                return;
            }
            _windows = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Windows)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(WindowCount)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(MultiVis)));
        }
    }
    // Selo de notificações (vermelho, com a contagem) no ícone do app
    public int NotifCount => NotifBadges.CountFor(this);
    public string NotifText { get { int c = NotifCount; return c > 99 ? "99+" : c.ToString(); } }
    public Visibility NotifVis => NotifCount > 0 ? Visibility.Visible : Visibility.Collapsed;
    public void RefreshNotif()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotifText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(NotifVis)));
    }

    public string WindowCount => _windows.Count.ToString();
    public Visibility MultiVis => _windows.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

    public bool IsRunning
    {
        get => _running;
        set
        {
            if (_running == value) return;
            _running = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningVis)));
        }
    }

    public ImageSource Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FallbackVis)));
        }
    }

    public event PropertyChangedEventHandler PropertyChanged;
}

// Uma janela aberta de um app
public class WinRef
{
    public IntPtr Hwnd { get; set; }
    public string Title { get; set; }
}

public class DockDto
{
    public string Name { get; set; }
    public string Target { get; set; }
    public string IconPath { get; set; }
    public bool Divider { get; set; }
}

// Arquivo dock.json: itens fixados (apps e divisores) + aparência do botão do lançador
public class DockFile
{
    public List<DockDto> Items { get; set; }
    public string LauncherIcon { get; set; }
    public bool LauncherDivider { get; set; } = true;
    public bool RunDivider { get; set; } = true;    // divisor entre fixados e abertos
}

// Ícones "de verdade" do Windows (arquivos, atalhos e apps da Loja) via IShellItemImageFactory
static class ShellIcon
{
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr hbitmap);
    }

    [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);

    static Guid _iid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    // Com cache (use só na thread de interface)
    public static ImageSource GetCached(string parsingName, int size = 64)
    {
        if (string.IsNullOrEmpty(parsingName)) return null;
        if (Cache.TryGetValue(parsingName, out var c)) return c;
        var icon = Get(parsingName, size);
        if (icon != null) Cache[parsingName] = icon;
        return icon;
    }

    public static bool IsCached(string parsingName) => parsingName != null && Cache.ContainsKey(parsingName);

    public static ImageSource Get(string parsingName, int size = 64)
    {
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, ref _iid, out var f);
            // 0x4 = SIIGBF_ICONONLY, 0x1 = SIIGBF_BIGGERSIZEOK
            if (f.GetImage(new SIZE { cx = size, cy = size }, 0x4 | 0x1, out hbm) != 0 || hbm == IntPtr.Zero) return null;
            var src = WithAlpha(hbm) ?? Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public short bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }
    [DllImport("gdi32.dll")] static extern int GetObject(IntPtr h, int cb, out BITMAP bm);

    [StructLayout(LayoutKind.Sequential)]
    struct BMIHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPels, biYPels, biClrUsed, biClrImportant;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct DIBSECTION
    {
        public BITMAP dsBm;
        public BMIHEADER dsBmih;
        public uint dsBf1, dsBf2, dsBf3;
        public IntPtr dshSection;
        public int dsOffset;
    }
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] static extern int GetDibSection(IntPtr h, int cb, out DIBSECTION ds);

    // Lê o bitmap 32 bits do shell mantendo a transparência (a conversão padrão do WPF a perde
    // e deixa cantos pretos). Retorna null se não for um bitmap 32 bits com pixels acessíveis.
    static BitmapSource WithAlpha(IntPtr hbm)
    {
        if (GetObject(hbm, Marshal.SizeOf<BITMAP>(), out var bm) == 0) return null;
        if (bm.bmBitsPixel != 32 || bm.bmBits == IntPtr.Zero || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
        int w = bm.bmWidth, h = Math.Abs(bm.bmHeight), stride = bm.bmWidthBytes;
        var px = new byte[stride * h];
        Marshal.Copy(bm.bmBits, px, 0, px.Length);

        // Alguns ícones do shell (ex.: Downloads) vêm com as linhas de baixo para cima (altura positiva no
        // cabeçalho do DIB); o WPF espera de cima para baixo, então é preciso inverter, senão o ícone fica de cabeça para baixo.
        try
        {
            if (GetDibSection(hbm, Marshal.SizeOf<DIBSECTION>(), out var ds) >= Marshal.SizeOf<DIBSECTION>() && ds.dsBmih.biHeight > 0)
            {
                var flipped = new byte[px.Length];
                for (int y = 0; y < h; y++)
                    Buffer.BlockCopy(px, y * stride, flipped, (h - 1 - y) * stride, stride);
                px = flipped;
            }
        }
        catch { }

        bool anyAlpha = false;
        for (int y = 0; y < h && !anyAlpha; y++)
            for (int x = 0; x < w; x++)
                if (px[y * stride + x * 4 + 3] != 0) { anyAlpha = true; break; }

        var fmt = anyAlpha ? PixelFormats.Bgra32 : PixelFormats.Bgr32;
        var bmp = BitmapSource.Create(w, h, 96, 96, fmt, null, px, stride);
        return bmp;
    }
}

// Abre o Explorador de Arquivos no local do aplicativo (selecionando o arquivo, quando possível)
// Lista de apps do menu Iniciar (a mesma que o lançador mostra) para o dock usar os MESMOS ícones:
// o dock fixa o .exe, cujo ícone pode ser diferente do ícone do app no lançador (apps da Loja, atalhos...).
static class StartAppIcons
{
    class Item
    {
        public string Name, Target, Aumid, Exe, FlatName;
    }

    // Programas que NÃO podem ser ligados a um app só pelo .exe (um .exe serve a vários atalhos)
    static readonly string[] SharedHosts = { "mmc.exe", "rundll32.exe", "control.exe", "msiexec.exe", "wscript.exe", "cscript.exe", "dllhost.exe" };

    const string Prefix = "shell:AppsFolder\\";

    static List<Item> _list = new();
    static DateTime _at = DateTime.MinValue;
    static Task _loading;

    public static bool Loaded => _list.Count > 0;

    public static Task EnsureAsync()
    {
        if (_list.Count > 0 && (DateTime.UtcNow - _at).TotalMinutes < 10) return Task.CompletedTask;
        return _loading ??= LoadAsync();
    }

    static async Task LoadAsync()
    {
        try
        {
            var l = await Task.Run(BuildList);
            if (l.Count > 0) { _list = l; _at = DateTime.UtcNow; }
        }
        catch { }
        finally { _loading = null; }
    }

    // Mesma lista que o lançador mostra (menu Iniciar + apps adicionados à mão),
    // já com o AppUserModelID e o .exe de cada um para o dock conseguir casar com os programas abertos.
    static List<Item> BuildList()
    {
        var items = new List<Item>();
        foreach (var d in LauncherWindow.QueryStartApps())
        {
            try
            {
                var it = new Item { Name = d.Name, Target = d.Target, FlatName = Flat(d.Name) };
                var id = d.Target.Substring(Prefix.Length);
                it.Aumid = id;
                if (!id.Contains('!'))   // app da Loja não é um caminho
                {
                    var p = AppLocation.ExpandKnownFolder(id);
                    if (p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(p)) it.Exe = p;
                    else if (p.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) it.Exe = DockWindow.ResolveExe(p);
                    else
                    {
                        // AppID simples (ex.: "MSEdge"): o programa está no atalho do menu Iniciar de mesmo nome
                        var t = AppLocation.StartMenuTarget(d.Name);
                        if (t != null && t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(t)) it.Exe = t;
                    }
                    if (it.Exe != null && SharedHosts.Contains(Path.GetFileName(it.Exe), StringComparer.OrdinalIgnoreCase)) it.Exe = null;
                }
                items.Add(it);
            }
            catch { }
        }

        foreach (var c in LauncherAppStore.All.ToList())
        {
            try { items.Add(new Item { Name = c.Name, Target = c.Target, FlatName = Flat(c.Name), Exe = DockWindow.ResolveExe(c.Target) }); }
            catch { }
        }
        return items;
    }

    static string Flat(string s) => new string((s ?? "").Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

    // Família do pacote a partir do caminho do .exe de um app da Loja:
    // ...\WindowsApps\Microsoft.YourPhone_1.2.3.0_x64__8wekyb3d8bbwe\... -> Microsoft.YourPhone_8wekyb3d8bbwe
    static string PackageFamily(string exe)
    {
        try
        {
            if (string.IsNullOrEmpty(exe)) return null;
            int i = exe.IndexOf("\\WindowsApps\\", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var rest = exe.Substring(i + "\\WindowsApps\\".Length);
            int end = rest.IndexOf('\\');
            var full = end > 0 ? rest.Substring(0, end) : rest;
            var parts = full.Split('_');                 // nome _ versão _ arquitetura _ _ publicador
            if (parts.Length < 2) return null;
            return parts[0] + "_" + parts[parts.Length - 1];
        }
        catch { return null; }
    }

    // Destino (shell:AppsFolder\... ou caminho) do app do lançador que corresponde a este app do dock, ou null
    public static string Find(string name, string exe, string aumid = null)
    {
        var list = _list;
        if (list.Count == 0) return null;

        // 1) mesma identidade no Windows (AppUserModelID): a ligação mais segura, vale para apps da Loja e clássicos
        if (!string.IsNullOrEmpty(aumid))
            foreach (var d in list)
                if (d.Aumid != null && string.Equals(d.Aumid, aumid, StringComparison.OrdinalIgnoreCase)) return d.Target;

        // 2) app da Loja pelo caminho do .exe (pasta do pacote)
        var pfn = PackageFamily(exe);
        if (pfn != null)
        {
            var pre = pfn + "!";
            foreach (var d in list)
                if (d.Aumid != null && d.Aumid.StartsWith(pre, StringComparison.OrdinalIgnoreCase)) return d.Target;
        }

        var a = Flat(name);

        // 3) mesmo programa (.exe); havendo vários atalhos para o mesmo .exe, prefere o de mesmo nome
        if (!string.IsNullOrEmpty(exe))
        {
            Item first = null;
            foreach (var d in list)
            {
                if (d.Exe == null || !string.Equals(d.Exe, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (a.Length > 0 && d.FlatName == a) return d.Target;
                first ??= d;
            }
            if (first != null) return first.Target;
        }

        // 4) mesmo nome
        if (a.Length == 0) return null;
        foreach (var d in list) if (d.FlatName == a) return d.Target;
        if (a.Length >= 4)
            foreach (var d in list)
            {
                var b = d.FlatName;
                if (b.Length >= 4 && (a.Contains(b) || b.Contains(a))) return d.Target;
            }
        return null;
    }

    // Apps do lançador cujo nome contém todas as palavras (sem diferenciar maiúsculas nem acentos)
    public static List<(string Name, string Target)> SearchApps(string text, int max)
    {
        var res = new List<(string Name, string Target)>();
        var words = (text ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return res;
        var ci = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        const System.Globalization.CompareOptions opt =
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;

        var hits = new List<(Item It, int Score)>();
        foreach (var d in _list)
        {
            if (string.IsNullOrEmpty(d.Name)) continue;
            bool all = true;
            foreach (var w in words)
                if (ci.IndexOf(d.Name, w, opt) < 0) { all = false; break; }
            if (!all) continue;
            hits.Add((d, ci.IsPrefix(d.Name, words[0], opt) ? 0 : 1));
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in hits.OrderBy(x => x.Score).ThenBy(x => x.It.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!seen.Add(h.It.Name)) continue;
            res.Add((h.It.Name, h.It.Target));
            if (res.Count >= max) break;
        }
        return res;
    }

    // Nome que o lançador mostra para este AppUserModelID (ou null)
    public static string NameFor(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        var list = _list;
        foreach (var d in list)
            if (d.Aumid != null && string.Equals(d.Aumid, aumid, StringComparison.OrdinalIgnoreCase)) return d.Name;
        return null;
    }

    // Janela de app web (PWA) instalado pelo Edge/Chrome: o AppID dela tem "_crx_"
    public static bool IsWebAppId(string aumid) =>
        !string.IsNullOrEmpty(aumid) && aumid.Contains("_crx_", StringComparison.OrdinalIgnoreCase);

    public const string AppsFolderPrefix = Prefix;

    // O AppID da janela é o de OUTRO app da lista (não o do navegador que a hospeda)? Ex.: app web do Edge/Chrome.
    public static bool IsOtherAppId(string aumid, string exe, string product)
    {
        if (string.IsNullOrEmpty(aumid)) return false;
        var prod = Flat(product);
        foreach (var d in _list)
        {
            if (d.Aumid == null || !string.Equals(d.Aumid, aumid, StringComparison.OrdinalIgnoreCase)) continue;
            if (d.FlatName == prod) return false;
            if (d.Exe != null && !string.IsNullOrEmpty(exe) && string.Equals(d.Exe, exe, StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        return false;
    }

    // Janela de navegador cujo título é o nome de um app do lançador (ex.: "Acumulei", "Inbox - Gmail"):
    // é um app web aberto como app. Nunca devolve o próprio navegador.
    public static bool TryMatchTitle(string title, string exe, string product, out string name, out string target, out string aumid)
    {
        name = target = aumid = null;
        var t = Flat(title);
        if (t.Length < 3) return false;
        var prod = Flat(product);
        Item best = null;
        foreach (var d in _list)
        {
            if (d.FlatName.Length < 3 || d.FlatName == prod) continue;
            if (d.Exe != null && !string.IsNullOrEmpty(exe) && string.Equals(d.Exe, exe, StringComparison.OrdinalIgnoreCase)) continue;
            if (d.FlatName == t) { best = d; break; }
            if (t.Contains(d.FlatName) && (best == null || d.FlatName.Length > best.FlatName.Length)) best = d;
        }
        if (best == null) return false;
        name = best.Name; target = best.Target; aumid = best.Aumid;
        return true;
    }

    static readonly object LogLock = new();
    static readonly HashSet<string> WinLogged = new(StringComparer.OrdinalIgnoreCase);

    // Diagnóstico das janelas de navegador: %AppData%\DockIsland\windows-debug.log
    public static void LogWindow(string exe, string cls, string aumid, bool pwa, string title)
    {
        try
        {
            var key = exe + "|" + aumid + "|" + pwa;
            lock (LogLock)
            {
                if (!WinLogged.Add(key)) return;
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "windows-debug.log"),
                    DateTime.Now.ToString("HH:mm:ss ") + (pwa ? "APP  " : "NAVEG") + " | " + Path.GetFileName(exe) + " | classe=" + cls +
                    " | aumid=" + aumid + " | titulo=" + (title.Length > 60 ? title.Substring(0, 60) : title) + Environment.NewLine);
            }
        }
        catch { }
    }

    static readonly HashSet<string> BadIds = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Logged = new(StringComparer.OrdinalIgnoreCase);

    // Troca o ícone padrão do app do dock pelo do lançador (ícone personalizado continua valendo por cima).
    // Vale para QUALQUER app do dock: fixado ou só aberto. Retorna true quando o ícone do lançador foi aplicado.
    public static bool Apply(AppEntry e)
    {
        if (e == null || e.IsDivider) return false;
        if (e.FromLauncher) return true;
        if (e.Target != null && e.Target.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))   // já é o do lançador
        {
            var own = ShellIcon.GetCached(e.Target);
            if (own == null) return false;
            if (!ReferenceEquals(e.DefaultIcon, own)) { e.DefaultIcon = own; e.RefreshIcon(); }
            e.FromLauncher = true;
            return true;
        }

        var exe = e.ResolvedExe ?? e.Target;
        var t = Find(e.Name, exe, e.Aumid);
        ImageSource icon = t != null ? ShellIcon.GetCached(t) : null;

        // sem item correspondente na lista: o Windows entrega o ícone do app direto pelo AppUserModelID
        if (icon == null && !string.IsNullOrEmpty(e.Aumid) && !BadIds.Contains(e.Aumid))
        {
            t = Prefix + e.Aumid;
            icon = ShellIcon.GetCached(t);
            if (icon == null) BadIds.Add(e.Aumid);
        }

        Log(e, t, icon != null);
        if (icon == null) return false;
        e.DefaultIcon = icon;
        e.FromLauncher = true;
        e.RefreshIcon();
        return true;
    }

    // Registro para diagnóstico: %AppData%\DockIsland\icons-debug.log (uma linha por app/resultado)
    static void Log(AppEntry e, string target, bool ok)
    {
        try
        {
            if (!Loaded) return;
            var line = (ok ? "OK   " : "FAIL ") + e.Name + " | exe=" + (e.ResolvedExe ?? e.Target) +
                       " | aumid=" + e.Aumid + " | icone=" + target;
            if (!Logged.Add(line)) return;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "icons-debug.log"), DateTime.Now.ToString("HH:mm:ss ") + line + Environment.NewLine);
        }
        catch { }
    }
}

// Descobre o AppUserModelID (a "identidade" do app no Windows) de uma janela aberta.
// É o mesmo valor que o Get-StartApps devolve como AppID, então liga o app aberto ao app do lançador.
static class AppIdentity
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern int GetApplicationUserModelId(IntPtr hProcess, ref uint len, System.Text.StringBuilder id);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);
    [DllImport("shell32.dll")]
    static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    struct PropertyKey { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    struct PropVariant { public ushort vt, r1, r2, r3; public IntPtr p, p2; }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new();

    // frame: janela de app da Loja hospedada pelo ApplicationFrameHost (só a própria janela sabe o AppID)
    public static string ForWindow(IntPtr hwnd, uint pid, bool frame)
    {
        var key = hwnd + ":" + pid;
        if (Cache.TryGetValue(key, out var c)) return c;

        string id = frame ? FromWindow(hwnd) ?? FromProcess(pid) : FromProcess(pid) ?? FromWindow(hwnd);
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (Cache.Count > 500) Cache.Clear();
            Cache[key] = id;
            return id;
        }
        return null;
    }

    // App empacotado (Loja/MSIX): o próprio processo conhece o AppID
    static string FromProcess(uint pid)
    {
        IntPtr h = IntPtr.Zero;
        try
        {
            h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            uint len = 130;
            var sb = new System.Text.StringBuilder((int)len);
            return GetApplicationUserModelId(h, ref len, sb) == 0 ? sb.ToString() : null;
        }
        catch { return null; }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    // System.AppUserModel.ID definido na janela (apps clássicos como o Explorador, e apps da Loja no frame)
    static string FromWindow(IntPtr hwnd)
    {
        IPropertyStore store = null;
        try
        {
            var iid = new Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99");
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out store) != 0 || store == null) return null;
            var key = new PropertyKey { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
            if (store.GetValue(ref key, out var pv) != 0) return null;
            try { return pv.vt == 31 /* VT_LPWSTR */ && pv.p != IntPtr.Zero ? Marshal.PtrToStringUni(pv.p) : null; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return null; }
        finally { if (store != null) { try { Marshal.ReleaseComObject(store); } catch { } } }
    }
}

static class AppLocation
{
    static readonly Dictionary<string, Func<string>> KnownFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        ["{6D809377-6AF0-444B-8957-A3773F02200E}"] = () => Environment.GetEnvironmentVariable("ProgramW6432"),
        ["{905E63B6-C1BF-494E-B29C-65B732D3D21A}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        ["{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.System),
        ["{D65231B0-B2F1-4857-A4CE-A8E7C6EA7D27}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
        ["{F38BF404-1D43-42F2-9305-67DE0B28FC23}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        ["{3EB685DB-65F9-4CF6-A03A-E3EF65729F3D}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        ["{F1B32785-6FBA-4FCF-9D55-7B8E7F157091}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ["{5E6C858F-0E22-4760-9AFE-EA3317B67173}"] = () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    };

    const string AppsFolder = "shell:AppsFolder\\";

    // target: .exe/.lnk/... ou "shell:AppsFolder\<AppID>"; name: nome do app (usado como alternativa)
    public static async void Open(string target, string name = null)
    {
        try
        {
            var path = await Task.Run(() => Resolve(target, name));
            if (string.IsNullOrEmpty(path)) return;

            if (File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + path + "\"");
            else if (Directory.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", "\"" + path + "\"");
        }
        catch { }
    }

    static string Resolve(string target, string name)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(target)) return null;

            if (target.StartsWith(AppsFolder, StringComparison.OrdinalIgnoreCase))
            {
                var id = target.Substring(AppsFolder.Length);

                // app da Loja (AppID = família!app): pasta de instalação do pacote
                int bang = id.IndexOf('!');
                if (bang > 0)
                {
                    var pfn = id.Substring(0, bang);
                    if (pfn.All(ch => char.IsLetterOrDigit(ch) || ch == '.' || ch == '_' || ch == '-'))
                    {
                        var dir = PowerShellLine("(Get-AppxPackage | Where-Object { $_.PackageFamilyName -eq '" + pfn +
                                                 "' } | Select-Object -First 1).InstallLocation");
                        if (!string.IsNullOrWhiteSpace(dir) && Directory.Exists(dir)) return dir;
                    }
                }

                // programa clássico: AppID pode ser um caminho, com ou sem pasta conhecida na frente
                var p = ExpandKnownFolder(id);
                if (File.Exists(p) || Directory.Exists(p)) return p;

                return FindStartMenuShortcut(name);
            }

            if (target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)) return ShortcutTarget(target) ?? (File.Exists(target) ? target : null);
            return File.Exists(target) || Directory.Exists(target) ? target : null;
        }
        catch { return null; }
    }

    internal static string ExpandKnownFolder(string id)
    {
        if (id.StartsWith("{"))
        {
            int end = id.IndexOf('}');
            if (end > 0 && KnownFolders.TryGetValue(id.Substring(0, end + 1), out var f))
            {
                var root = f();
                if (!string.IsNullOrEmpty(root)) return Path.Combine(root, id.Substring(end + 1).TrimStart('\\'));
            }
        }
        return Environment.ExpandEnvironmentVariables(id);
    }

    // Destino de um atalho .lnk (o programa de verdade), se existir
    static string ShortcutTarget(string lnk)
    {
        try
        {
            dynamic sh = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!);
            dynamic sc = sh.CreateShortcut(lnk);
            string t = sc.TargetPath;
            return !string.IsNullOrWhiteSpace(t) && (File.Exists(t) || Directory.Exists(t)) ? t : null;
        }
        catch { return null; }
    }

    internal static string StartMenuTarget(string name) => FindStartMenuShortcut(name);

    // Procura o atalho do app no menu Iniciar (pelo nome) e devolve o programa para o qual ele aponta
    static string FindStartMenuShortcut(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
        };
        foreach (var r in roots)
        {
            try
            {
                if (!Directory.Exists(r)) continue;
                foreach (var f in Directory.EnumerateFiles(r, "*.lnk", SearchOption.AllDirectories))
                    if (string.Equals(Path.GetFileNameWithoutExtension(f), name, StringComparison.CurrentCultureIgnoreCase))
                        return ShortcutTarget(f) ?? f;
            }
            catch { }
        }
        return null;
    }

    static string PowerShellLine(string command)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -Command \"" + command.Replace("\"", "\\\"") + "\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = System.Diagnostics.Process.Start(psi);
        var line = p.StandardOutput.ReadLine();
        p.WaitForExit(10000);
        return line?.Trim();
    }
}

// Ícone que acompanha o mouse enquanto um app é arrastado (lançador e dock).
// Janela pequena, transparente, que não ativa nem recebe cliques (não atrapalha o "soltar").
static class DragGhost
{
    [DllImport("user32.dll")] static extern bool GetCursorPos(out GhostPoint p);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int index, int value);
    [StructLayout(LayoutKind.Sequential)] struct GhostPoint { public int X, Y; }

    static Window _w;
    static IntPtr _h;

    public static void Begin(ImageSource icon, string letter)
    {
        End();
        try
        {
            FrameworkElement el;
            if (icon != null)
                el = new Image { Source = icon, Width = 40, Height = 40 };
            else
                el = new Border
                {
                    Width = 40, Height = 40, CornerRadius = new CornerRadius(20),
                    Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C)),
                    Child = new TextBlock
                    {
                        Text = string.IsNullOrEmpty(letter) ? "•" : letter, Foreground = Brushes.White,
                        FontSize = 16, FontWeight = FontWeights.Bold,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
            el.Opacity = 0.92;
            RenderOptions.SetBitmapScalingMode(el, BitmapScalingMode.HighQuality);

            var grid = new Grid { Width = 44, Height = 44 };
            grid.Children.Add(el);

            _w = new Window
            {
                WindowStyle = WindowStyle.None, AllowsTransparency = true, Background = Brushes.Transparent,
                ShowInTaskbar = false, ShowActivated = false, Topmost = true, ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.WidthAndHeight, IsHitTestVisible = false,
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000,
                Content = grid
            };
            _w.SourceInitialized += (_, __) =>
            {
                var h = new WindowInteropHelper(_w).Handle;
                // 0x20 TRANSPARENT (atravessa cliques) | 0x08000000 NOACTIVATE | 0x80 TOOLWINDOW
                SetWindowLong(h, -20, GetWindowLong(h, -20) | 0x20 | 0x08000000 | 0x80);
            };
            _w.Show();
            _h = new WindowInteropHelper(_w).Handle;
            Move();
        }
        catch { End(); }
    }

    // Segue o cursor (um pouco deslocado, para o cursor nunca ficar em cima do ícone)
    public static void Move()
    {
        if (_w == null || _h == IntPtr.Zero) return;
        try
        {
            if (GetCursorPos(out var p))
                SetWindowPos(_h, IntPtr.Zero, p.X + 16, p.Y + 16, 0, 0, 0x0001 | 0x0004 | 0x0010);   // NOSIZE | NOZORDER | NOACTIVATE
        }
        catch { }
    }

    public static void End()
    {
        try { _w?.Close(); } catch { }
        _w = null;
        _h = IntPtr.Zero;
    }
}

// Menu de contexto no estilo da ilha
static class MenuKit
{
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out POINT p);
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }

    static bool AnyMouseButtonDown() =>
        (GetAsyncKeyState(0x01) & 0x8000) != 0 ||   // esquerdo
        (GetAsyncKeyState(0x02) & 0x8000) != 0 ||   // direito
        (GetAsyncKeyState(0x04) & 0x8000) != 0;     // meio

    // O clique está dentro da parte visível do menu (sem a margem transparente da sombra)?
    static bool IsInsideMenu(ContextMenu menu, POINT pt)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(menu);
            var tl = menu.PointToScreen(new Point(0, 0));
            double m = 12 * dpi.DpiScaleX;   // margem do modelo do menu
            return pt.X >= tl.X + m && pt.X <= tl.X + menu.ActualWidth * dpi.DpiScaleX - m
                && pt.Y >= tl.Y + m && pt.Y <= tl.Y + menu.ActualHeight * dpi.DpiScaleY - m;
        }
        catch { return true; }   // na dúvida, não fecha
    }

    public static void Show(FrameworkElement owner, string title, ImageSource icon,
        IList<(string text, Action act, Brush fg)> items, Action<bool> onOpenChanged = null,
        PlacementMode placement = PlacementMode.Top)
    {
        var res = Application.Current.Resources;
        var menu = new ContextMenu
        {
            PlacementTarget = owner,
            Placement = placement,
            Style = (Style)res["IslandMenu"]
        };

        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 8, 12, 8) };
        if (icon != null)
            head.Children.Add(new Image
            {
                Source = icon, Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
        head.Children.Add(new TextBlock
        {
            Text = title, Foreground = Theme.Fg, FontSize = 13, FontWeight = FontWeights.SemiBold,
            MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center
        });
        RenderOptions.SetBitmapScalingMode(head, BitmapScalingMode.HighQuality);
        menu.Items.Add(new MenuItem { Header = head, Style = (Style)res["IslandMenuHeader"] });
        menu.Items.Add(new Separator { Style = (Style)res["IslandMenuSep"] });

        foreach (var (text, act, fg) in items)
        {
            if (text == "-") { menu.Items.Add(new Separator { Style = (Style)res["IslandMenuSep"] }); continue; }
            var mi = new MenuItem { Header = text, Style = (Style)res["IslandMenuItem"] };
            if (fg != null) mi.Foreground = fg;
            var a = act;
            if (a == null) mi.IsEnabled = false;   // ação nula: item desativado
            else mi.Click += (_, __) => a();
            menu.Items.Add(mi);
        }

        // Os programas do dock/ilha não ficam ativos, então o Windows não avisa o menu quando
        // o clique é fora dele. Por isso vigiamos o mouse e fechamos nós mesmos.
        var watcher = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
        bool wasDown = true;
        watcher.Tick += (_, __) =>
        {
            bool down = AnyMouseButtonDown();
            if (down && !wasDown && menu.IsOpen && GetCursorPos(out var pt) && !IsInsideMenu(menu, pt))
                menu.IsOpen = false;
            wasDown = down;
        };

        menu.Opened += (_, __) => { onOpenChanged?.Invoke(true); wasDown = AnyMouseButtonDown(); watcher.Start(); };
        menu.Closed += (_, __) => { watcher.Stop(); onOpenChanged?.Invoke(false); };
        menu.IsOpen = true;
    }
}


// Ícones personalizados, salvos por aplicativo (valem no dock e no lançador)
static class IconStore
{
    static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static event Action<string> Changed;

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "icons");

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "icons.json");

    static IconStore()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                if (d != null) foreach (var kv in d) Map[kv.Key] = kv.Value;
            }
        }
        catch { }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(Map, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static bool Has(string key) => !string.IsNullOrEmpty(key) && Map.ContainsKey(key);

    // Todas as chaves com ícone personalizado (usado pela bandeja para casar com os apps do lançador)
    public static List<string> AllKeys() => Map.Keys.ToList();

    // ----- por aplicativo (chave principal + .exe + nome) -----
    public static bool HasFor(AppEntry e) => e.IconKeys().Any(Has);

    public static ImageSource CustomFor(AppEntry e)
    {
        foreach (var k in e.IconKeys())
        {
            var img = Custom(k);
            if (img != null) return img;
        }
        return null;
    }

    public static void SetFor(AppEntry e, string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        foreach (var k in e.IconKeys()) Set(k, path);
    }

    public static void RemoveFor(AppEntry e)
    {
        // também remove outras chaves que usam a mesma cópia do ícone (ex.: o atalho do lançador)
        var paths = e.IconKeys().Where(Has).Select(k => Map[k]).ToList();
        foreach (var k in e.IconKeys()) Remove(k);
        foreach (var path in paths.Where(p => p.StartsWith(Dir, StringComparison.OrdinalIgnoreCase)).Distinct())
            foreach (var k in Map.Where(kv => string.Equals(kv.Value, path, StringComparison.OrdinalIgnoreCase))
                                 .Select(kv => kv.Key).ToList())
                Remove(k);
    }

    // Ícone já escolhido pela chave principal (ex.: no lançador): vale também para o .exe e o nome,
    // para o dock mostrar o mesmo ícone
    public static void Learn(AppEntry e)
    {
        if (!Map.TryGetValue(e.IconKey, out var path)) return;
        var added = new List<string>();
        foreach (var k in e.IconKeys().Skip(1))
            if (!Map.ContainsKey(k)) { Map[k] = path; added.Add(k); }
        if (added.Count == 0) return;
        Save();
        foreach (var k in added) Changed?.Invoke(k);
    }

    public static ImageSource Custom(string key)
    {
        if (string.IsNullOrEmpty(key) || !Map.TryGetValue(key, out var path)) return null;
        if (Cache.TryGetValue(path, out var c)) return c;
        var img = LoadImage(path);
        if (img != null) Cache[path] = img;
        return img;
    }

    public static void Set(string key, string path)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(path)) return;
        Map.TryGetValue(key, out var old);
        Map[key] = path;
        if (old != null && !string.Equals(old, path, StringComparison.OrdinalIgnoreCase)) DeleteOwnIfUnused(old);
        Save();
        Changed?.Invoke(key);
    }

    public static void Remove(string key)
    {
        if (!Has(key)) return;
        var old = Map[key];
        Map.Remove(key);
        DeleteOwnIfUnused(old);
        Save();
        Changed?.Invoke(key);
    }

    static void DeleteOwnIfUnused(string path)
    {
        if (Map.Values.Any(v => string.Equals(v, path, StringComparison.OrdinalIgnoreCase))) return;
        DeleteOwn(path);
    }

    // Apaga o arquivo só se for uma cópia guardada pelo próprio programa
    public static void DeleteOwn(string path)
    {
        try
        {
            if (!string.IsNullOrEmpty(path) && path.StartsWith(Dir, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                Cache.Remove(path);
                File.Delete(path);
            }
        }
        catch { }
    }

    // Imagem (png, ico, jpg, bmp, gif) ou programa (.exe/.dll/.lnk) -> ImageSource
    public static ImageSource LoadImage(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext is ".exe" or ".dll" or ".lnk") return ShellIcon.Get(path, 128);

            var dec = BitmapDecoder.Create(new Uri(path), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = dec.Frames.OrderByDescending(f => f.PixelWidth).First();   // .ico: maior tamanho
            frame.Freeze();
            return frame;
        }
        catch { return null; }
    }

    // Escolhe o arquivo do ícone; imagens são copiadas para a pasta do programa
    public static string Pick()
    {
        try
        {
            var dlg = new OpenFileDialog
            {
                Title = "Escolher ícone",
                Filter = "Imagens e ícones|*.png;*.ico;*.jpg;*.jpeg;*.bmp;*.gif|Programas|*.exe;*.dll|Todos os arquivos|*.*"
            };
            if (dlg.ShowDialog() != true) return null;
            if (LoadImage(dlg.FileName) == null)
            {
                MessageBox.Show("Não foi possível ler esse arquivo como ícone.", "Ícone", MessageBoxButton.OK, MessageBoxImage.Warning);
                return null;
            }

            var ext = Path.GetExtension(dlg.FileName).ToLowerInvariant();
            if (ext is ".exe" or ".dll" or ".lnk") return dlg.FileName;

            Directory.CreateDirectory(Dir);
            var dest = Path.Combine(Dir, Guid.NewGuid().ToString("N") + ext);
            File.Copy(dlg.FileName, dest, true);
            return dest;
        }
        catch { return null; }
    }
}
