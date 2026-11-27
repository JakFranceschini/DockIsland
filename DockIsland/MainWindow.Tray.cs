using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace DockIsland;

public class TrayItem
{
    public string Name { get; set; }
    public ImageSource Icon { get; set; }
    public string Initial { get; set; }
    public Visibility FallbackVis => Icon == null ? Visibility.Visible : Visibility.Collapsed;
    public AutomationElement Element { get; set; }   // null = só conhecido pelo registro
    public string ProcName { get; set; }             // para "Fechar programa"
    public bool OnScreen { get; set; }               // ícone visível na barra (dá para abrir o menu original)
}

// Dados lidos em segundo plano (sem objetos de WPF que dependem da thread de interface)
class TrayEntry
{
    public string Name;
    public AutomationElement Element;
    public bool Promoted;          // aparece direto na barra (e não só no "^")
    public bool OnScreen;
    public string ExePath;
    public string ProcName;
    public byte[] Png;
    public ImageSource Icon;
    public string LauncherTarget;   // app do lançador que corresponde a este item (para usar o mesmo ícone)
}

// =========================================================
//  Bandeja do sistema (ícones ao lado do relógio) dentro da aba Sistema
//  Fontes: UI Automation na barra de tarefas (ícones visíveis e ocultos abertos)
//  + registro NotifyIconSettings do Windows 11 (ícones ocultos + imagem do ícone).
// =========================================================
public partial class MainWindow
{
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token, out IntPtr path);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out TrayPoint p);
    [DllImport("user32.dll")] static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [StructLayout(LayoutKind.Sequential)] struct TrayPoint { public int X, Y; }

    bool _menuOpen;   // menu do ícone aberto: a ilha não recolhe enquanto isso
    const int MaxTrayShown = 12;
    readonly ObservableCollection<TrayItem> _trayItems = new();
        bool _trayBusy;
    DateTime _trayLast = DateTime.MinValue;
    string _traySig = "";

    List<TrayEntry> _lastTrayEntries = new();

    void InitTrayArea()
    {
        TrayList.ItemsSource = _trayItems;

        // ícone trocado no lançador/dock: a bandeja acompanha
        IconStore.Changed += _ => Dispatcher.BeginInvoke(new Action(() =>
        {
            _traySig = "";
            ApplyTray(_lastTrayEntries);
        }));
    }

    async Task RefreshTrayAsync(bool force = false)
    {
        if (_trayBusy) return;
        if (!force && (DateTime.UtcNow - _trayLast).TotalSeconds < 4) return;
        _trayBusy = true;
        try
        {
            // lista de apps do lançador (a mesma do dock): a bandeja usa os mesmos ícones dela
            var ensure = StartAppIcons.EnsureAsync();
            if (!StartAppIcons.Loaded) await Task.WhenAny(ensure, Task.Delay(8000));

            var entries = await Task.Run(ReadTray);
            ApplyTray(entries);
        }
        catch { }
        finally
        {
            _trayLast = DateTime.UtcNow;
            _trayBusy = false;
        }
    }

    void ApplyTray(List<TrayEntry> entries)
    {
        _lastTrayEntries = entries;
        var sig = string.Join("|", entries.Select(e => e.Name + "~" + e.LauncherTarget));
        if (sig == _traySig && _trayItems.Count == Math.Min(entries.Count, MaxTrayShown))
        {
            // mesma lista: só renova as referências de clique
            for (int i = 0; i < _trayItems.Count; i++)
            {
                _trayItems[i].Element = entries[i].Element;
                _trayItems[i].OnScreen = entries[i].OnScreen;
            }
            return;
        }
        _traySig = sig;

        _trayItems.Clear();
        foreach (var e in entries.Take(MaxTrayShown))
        {
            _trayItems.Add(new TrayItem
            {
                Name = e.Name,
                Icon = LauncherTrayIcon(e) ?? e.Icon,   // mesmo ícone que o lançador de apps mostra
                Initial = e.Name.Length > 0 ? e.Name.Substring(0, 1).ToUpper() : "•",
                Element = e.Element,
                ProcName = e.ProcName,
                OnScreen = e.OnScreen
            });
        }

        int more = entries.Count - MaxTrayShown;
        TrayMoreText.Text = more > 0 ? $"+{more}" : "";
        NoTrayText.Visibility = _trayItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // Ícone que o lançador mostra para este app: o que você trocou lá ou, se não trocou, o do próprio app
    // (menu Iniciar). Só na thread de interface.
    static ImageSource LauncherTrayIcon(TrayEntry e)
    {
        try
        {
            ImageSource img = null;
            if (!string.IsNullOrEmpty(e.LauncherTarget)) img = IconStore.Custom(e.LauncherTarget);   // trocado no lançador
            img ??= CustomTrayIcon(e);                                                               // trocado, achado por nome/.exe
            if (img == null && !string.IsNullOrEmpty(e.LauncherTarget)) img = ShellIcon.GetCached(e.LauncherTarget);
            return img;
        }
        catch { return null; }
    }

    // Procura, entre os ícones que você trocou no lançador, o que pertence a este item da bandeja.
    // Ordem: .exe exato > nome exato > mesmo nome de .exe > nome parecido > atalho/app do lançador.
    static ImageSource CustomTrayIcon(TrayEntry e)
    {
        try
        {
            string exeBase = !string.IsNullOrEmpty(e.ExePath) ? Path.GetFileNameWithoutExtension(e.ExePath) : null;
            string procBase = e.ProcName ?? exeBase;
            var name = (e.Name ?? "").Trim();
            var normName = NormTray(name);

            ImageSource img;
            if (!string.IsNullOrEmpty(e.ExePath) && (img = IconStore.Custom("exe:" + e.ExePath)) != null) return img;
            if (name.Length > 0 && (img = IconStore.Custom("name:" + name)) != null) return img;
            if (normName.Length > 0 && (img = IconStore.Custom("name:" + normName)) != null) return img;

            var keys = IconStore.AllKeys();

            // mesmo nome de executável (ex.: o app do lançador aponta para outro caminho do mesmo .exe)
            if (!string.IsNullOrEmpty(procBase))
                foreach (var k in keys.Where(k => k.StartsWith("exe:", StringComparison.OrdinalIgnoreCase)))
                    if (string.Equals(Path.GetFileNameWithoutExtension(k.Substring(4)), procBase, StringComparison.OrdinalIgnoreCase)
                        && (img = IconStore.Custom(k)) != null) return img;

            // nome parecido ("Segurança do Windows: sem ações necessárias" ~ "Segurança do Windows")
            foreach (var k in keys.Where(k => k.StartsWith("name:", StringComparison.OrdinalIgnoreCase)))
                if (TrayNameMatch(e.Name, k.Substring(5)) && (img = IconStore.Custom(k)) != null) return img;

            // atalho / app da Loja do lançador (chave = caminho): compara pelo nome do arquivo
            foreach (var k in keys)
            {
                if (k.StartsWith("exe:", StringComparison.OrdinalIgnoreCase) || k.StartsWith("name:", StringComparison.OrdinalIgnoreCase)
                    || k.StartsWith("special:", StringComparison.OrdinalIgnoreCase)) continue;
                string label;
                if (k.StartsWith("frame:", StringComparison.OrdinalIgnoreCase)) label = k.Substring(6);
                else
                {
                    var last = k.Substring(k.LastIndexOfAny(new[] { '\\', '/' }) + 1);   // depois da última barra
                    var ext = Path.GetExtension(last).ToLowerInvariant();
                    label = ext is ".lnk" or ".exe" ? Path.GetFileNameWithoutExtension(last) : last;   // app da Loja: ID inteiro
                }
                if (string.IsNullOrEmpty(label)) continue;
                if ((TrayNameMatch(e.Name, label) || (!string.IsNullOrEmpty(procBase) && TrayNameMatch(procBase, label)))
                    && (img = IconStore.Custom(k)) != null) return img;
            }
        }
        catch { }
        return null;
    }

    // Clique no ícone: menu com Abrir / Menu original do ícone / Fechar programa
    void TrayItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not TrayItem item) return;
        e.Handled = true;

        // Mesmo menu (fundo, cabeçalho e fechamento ao clicar fora) usado no resto do programa.
        // Ação nula = item desativado.
        static Action If(bool ok, Action a) => ok ? a : null;
        var items = new List<(string, Action, Brush)>
        {
            ("Abrir", If(item.Element != null || item.ProcName != null, () => OpenTrayItem(item)), null),
            ("Mais opções do ícone…", If(item.Element != null && item.OnScreen, () => ShowNativeTrayMenu(item.Element)), null),
            ("-", null, null),
            ("Fechar programa", If(!string.IsNullOrEmpty(item.ProcName), () => CloseTrayProgram(item.ProcName)),
                new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)))
        };
        MenuKit.Show(fe, item.Name, item.Icon, items, open =>
        {
            _menuOpen = open;
            _leaveTimer.Stop();
            if (!open) _leaveTimer.Start();
        }, PlacementMode.Bottom);
    }

    void OpenTrayItem(TrayItem item)
    {
        var el = item.Element;
        var proc = item.ProcName;
        _ = Task.Run(() =>
        {
            try
            {
                if (el != null) { ((InvokePattern)el.GetCurrentPattern(InvokePattern.Pattern)).Invoke(); return; }
            }
            catch { }
            try
            {
                foreach (var p in Process.GetProcessesByName(proc ?? ""))
                    using (p)
                        if (p.MainWindowHandle != IntPtr.Zero)
                        {
                            ShowWindow(p.MainWindowHandle, 9);   // SW_RESTORE
                            SetForegroundWindow(p.MainWindowHandle);
                            return;
                        }
            }
            catch { }
        });
    }

    // Abre o menu de contexto de verdade do programa: clique direito no ícone da barra
    static void ShowNativeTrayMenu(AutomationElement el)
    {
        if (el == null) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var r = el.Current.BoundingRectangle;
                if (r.IsEmpty || el.Current.IsOffscreen) return;
                GetCursorPos(out var old);
                SetCursorPos((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2));
                await Task.Delay(60);
                mouse_event(0x0008, 0, 0, 0, UIntPtr.Zero);   // botão direito: pressiona
                mouse_event(0x0010, 0, 0, 0, UIntPtr.Zero);   // botão direito: solta
                await Task.Delay(150);
                SetCursorPos(old.X, old.Y);
            }
            catch { }
        });
    }

    // Encerra o programa (tenta fechar a janela e, se continuar vivo, finaliza o processo)
    void CloseTrayProgram(string proc)
    {
        if (string.IsNullOrEmpty(proc) || proc.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var ps = Process.GetProcessesByName(proc);
                foreach (var p in ps) { try { p.CloseMainWindow(); } catch { } }
                await Task.Delay(700);
                foreach (var p in ps) { try { if (!p.HasExited) p.Kill(); } catch { } finally { p.Dispose(); } }
            }
            catch { }
            await Task.Delay(500);
            _ = Dispatcher.BeginInvoke(new Action(async () => await RefreshTrayAsync(true)));
        });
    }

    // ---------------------------------------------------------
    //  Leitura (roda em thread de segundo plano)
    // ---------------------------------------------------------
    static readonly string[] TraySystemNames =
    {
        // ícones do próprio Windows que já têm cartão na aba Sistema (ou não fazem sentido aqui)
        "volume", "alto-falante", "speaker", "rede", "network", "wi-fi", "wifi", "ethernet", "internet",
        "bateria", "battery", "indicador de entrada", "input indicator", "idioma",
        "language", "teclado", "keyboard", "centro de a", "central de a", "action center", "notifica",
        "mostrar ícones ocultos", "show hidden icons", "mostrar icones ocultos", "relógio", "relogio",
        "clock", "mostrar área de trabalho", "show desktop", "bluetooth", "configurações rápidas", "quick settings"
    };

    static readonly string[] TrayIgnoreExe =
    {
        "explorer", "sihost", "shellexperiencehost", "systemsettings", "ctfmon", "sndvol", "textinputhost"
    };

    static bool IsSystemTrayName(string name)
    {
        var n = (name ?? "").Trim().ToLowerInvariant();
        return n.Length == 0 || TraySystemNames.Any(n.StartsWith);
    }

    // "Dropbox: Atualizado" -> "dropbox"
    static string NormTray(string s)
    {
        s = (s ?? "").Trim();
        int cut = s.IndexOfAny(new[] { ':', '\n', '\r' });
        if (cut > 0) s = s.Substring(0, cut);
        int dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0) s = s.Substring(0, dash);
        int par = s.IndexOf(" (", StringComparison.Ordinal);
        if (par > 0) s = s.Substring(0, par);
        return s.Trim().ToLowerInvariant();
    }

    static bool TrayNameMatch(string a, string b)
    {
        var x = NormTray(a);
        var y = NormTray(b);
        if (x.Length < 3 || y.Length < 3) return false;
        return x == y || x.Contains(y) || y.Contains(x);
    }

    static readonly Dictionary<string, string> TrayAumidAlias = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SecurityHealthSystray"] = "Microsoft.SecHealthUI_8wekyb3d8bbwe!SecHealthUI",   // Segurança do Windows
        ["Segurança do Windows"] = "Microsoft.SecHealthUI_8wekyb3d8bbwe!SecHealthUI",
        ["Windows Security"] = "Microsoft.SecHealthUI_8wekyb3d8bbwe!SecHealthUI",
    };

    static List<TrayEntry> ReadTray()
    {
        var list = new List<TrayEntry>();

        void Add(AutomationElement el, bool promoted)
        {
            string name;
            try { name = (el.Current.Name ?? "").Trim(); } catch { return; }
            if (IsSystemTrayName(name)) return;
            if (list.Any(x => NormTray(x.Name) == NormTray(name))) return;
            bool onScreen = false;
            try { onScreen = !el.Current.IsOffscreen && !el.Current.BoundingRectangle.IsEmpty; } catch { }
            list.Add(new TrayEntry { Name = name, Element = el, Promoted = promoted, OnScreen = onScreen });
        }

        try
        {
            var root = AutomationElement.RootElement;
            var btn = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
            var xamlTray = new OrCondition(
                new PropertyCondition(AutomationElement.ClassNameProperty, "SystemTray.NormalButton"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "SystemTrayIcon"));

            // 1) Ícones visíveis ao lado do relógio
            foreach (AutomationElement bar in root.FindAll(TreeScope.Children,
                         new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd")))
            {
                var notify = bar.FindFirst(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ClassNameProperty, "TrayNotifyWnd"));
                if (notify != null)   // Windows 10 / Windows 11 antigo
                    foreach (AutomationElement b in notify.FindAll(TreeScope.Descendants, btn)) Add(b, true);

                foreach (AutomationElement b in bar.FindAll(TreeScope.Descendants, xamlTray)) Add(b, true);   // Windows 11 novo
            }

            // 2) Ícones ocultos (bandeja "^") quando a janela deles está acessível
            foreach (var cls in new[] { "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland" })
                foreach (AutomationElement w in root.FindAll(TreeScope.Children,
                             new PropertyCondition(AutomationElement.ClassNameProperty, cls)))
                {
                    foreach (AutomationElement b in w.FindAll(TreeScope.Descendants, xamlTray)) Add(b, false);
                    foreach (AutomationElement b in w.FindAll(TreeScope.Descendants, btn)) Add(b, false);
                }
        }
        catch { }

        MergeRegistry(list);

        foreach (var e in list)
        {
            e.Icon = ResolveTrayIcon(e);
            // mesmo app no lançador (por identidade, .exe ou nome)
            try
            {
                // processos da bandeja que são "o mesmo app" do lançador, mas com outro nome (identidade do Windows)
                string aumid = null;
                foreach (var k in new[] { e.ProcName, string.IsNullOrEmpty(e.ExePath) ? null : Path.GetFileNameWithoutExtension(e.ExePath), e.Name })
                    if (!string.IsNullOrEmpty(k) && TrayAumidAlias.TryGetValue(k.Trim(), out aumid)) break;

                e.LauncherTarget = StartAppIcons.Find(NormTray(e.Name), e.ExePath, aumid)
                                   ?? (string.IsNullOrEmpty(e.ProcName) ? null : StartAppIcons.Find(e.ProcName, e.ExePath, aumid))
                                   ?? (aumid != null ? StartAppIcons.AppsFolderPrefix + aumid : null);
            }
            catch { }
        }

        return list;
    }

    // Windows 11 22H2+: HKCU\Control Panel\NotifyIconSettings guarda todo app que já teve ícone na
    // bandeja (caminho do .exe, se é visível e uma imagem do ícone). Aqui só valem os que estão abertos.
    static void MergeRegistry(List<TrayEntry> list)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (key == null) return;

            bool uiaFoundAny = list.Count > 0;
            var runningCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            foreach (var sub in key.GetSubKeyNames())
            {
                using var s = key.OpenSubKey(sub);
                if (s == null) continue;

                var exe = ResolveKnownPath(s.GetValue("ExecutablePath") as string);
                var tip = s.GetValue("InitialTooltip") as string;
                bool promoted = s.GetValue("IsPromoted") is int p && p == 1;
                var png = s.GetValue("IconSnapshot") as byte[];
                var exeBase = string.IsNullOrEmpty(exe) ? "" : Path.GetFileNameWithoutExtension(exe);

                if (exeBase.Length == 0 || TrayIgnoreExe.Contains(exeBase.ToLowerInvariant())) continue;

                if (!runningCache.TryGetValue(exeBase, out var running))
                {
                    try { var ps = Process.GetProcessesByName(exeBase); running = ps.Length > 0; foreach (var q in ps) q.Dispose(); } catch { running = false; }
                    runningCache[exeBase] = running;
                }
                if (!running) continue;

                var match = list.FirstOrDefault(x => TrayNameMatch(x.Name, tip) || TrayNameMatch(x.Name, exeBase));
                if (match != null)
                {
                    match.ExePath ??= exe;
                    match.ProcName ??= exeBase;
                    match.Png ??= png;
                    continue;
                }

                // aberto, mas só conhecido pelo registro: entra se for oculto (ou se a UIA não achou nada)
                var name = !string.IsNullOrWhiteSpace(tip) ? tip.Trim() : exeBase;
                if (IsSystemTrayName(name)) continue;
                if (promoted && uiaFoundAny) continue;
                list.Add(new TrayEntry { Name = name, ExePath = exe, ProcName = exeBase, Png = png, Promoted = promoted });
            }
        }
        catch { }
    }

    static string ResolveKnownPath(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try
        {
            // formato "{GUID da pasta conhecida}\subpasta\app.exe"
            if (raw.StartsWith("{") && raw.IndexOf("}\\", StringComparison.Ordinal) > 0)
            {
                int end = raw.IndexOf('}');
                if (Guid.TryParse(raw.Substring(0, end + 1), out var g)
                    && SHGetKnownFolderPath(g, 0, IntPtr.Zero, out var ptr) == 0 && ptr != IntPtr.Zero)
                {
                    try
                    {
                        var folder = Marshal.PtrToStringUni(ptr);
                        return Path.Combine(folder, raw.Substring(end + 2));
                    }
                    finally { Marshal.FreeCoTaskMem(ptr); }
                }
            }
            return Environment.ExpandEnvironmentVariables(raw);
        }
        catch { return raw; }
    }

    // Ícone em alta resolução (48 px) direto do .exe: bem mais nítido que o de 16/32 px
    static ImageSource HiResIconFromFile(string path, int size = 48)
    {
        var h = new IntPtr[1];
        try
        {
            if (PrivateExtractIcons(path, 0, size, size, h, null, 1, 0) > 0 && h[0] != IntPtr.Zero)
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(h[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
        }
        catch { }
        finally { if (h[0] != IntPtr.Zero) DestroyIcon(h[0]); }
        return IconFromFile(path);
    }

    static readonly ConcurrentDictionary<string, (ImageSource icon, string proc, string exe)> _trayResolved = new();

    static ImageSource ResolveTrayIcon(TrayEntry e)
    {
        var key = NormTray(e.Name);
        if (_trayResolved.TryGetValue(key, out var c))
        {
            e.ProcName ??= c.proc;
            e.ExePath ??= c.exe;
            if (c.icon != null) return c.icon;
        }

        // sem caminho conhecido: procura o processo cujo nome lembra o do item
        if (string.IsNullOrEmpty(e.ExePath))
        {
            var flat = key.Replace(" ", "");
            if (flat.Length >= 4)
            {
                try
                {
                    foreach (var p in Process.GetProcesses())
                    {
                        string pn;
                        try { pn = p.ProcessName.ToLowerInvariant(); } catch { continue; }
                        if (pn.Length < 4 || !(pn == flat || flat.StartsWith(pn) || pn.StartsWith(flat))) continue;
                        string path = null;
                        try { path = p.MainModule?.FileName; } catch { }
                        if (string.IsNullOrEmpty(path)) continue;
                        e.ExePath = path;
                        e.ProcName ??= p.ProcessName;
                        break;
                    }
                }
                catch { }
            }
        }

        ImageSource icon = null;
        if (!string.IsNullOrEmpty(e.ExePath) && File.Exists(e.ExePath))
            icon = HiResIconFromFile(e.ExePath);

        if (icon == null && e.Png != null && e.Png.Length > 0)
        {
            try
            {
                var img = new BitmapImage();
                using var ms = new MemoryStream(e.Png);
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = ms;
                img.EndInit();
                img.Freeze();
                icon = img;
            }
            catch { icon = null; }
        }

        if (e.ProcName == null && !string.IsNullOrEmpty(e.ExePath))
            e.ProcName = Path.GetFileNameWithoutExtension(e.ExePath);

        _trayResolved[key] = (icon, e.ProcName, e.ExePath);
        return icon;
    }
}
