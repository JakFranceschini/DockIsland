using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Threading.Tasks;
using System.Text.Json;
using Microsoft.Win32;

namespace DockIsland;

// Esconde o balão (toast) do Windows para que a notificação apareça só na ilha.
// Usa a mesma opção de Configurações > Sistema > Notificações > (app) > "Mostrar banners de notificação":
// HKCU\...\Notifications\Settings\<app>\ShowBanner = 0.
// A notificação continua indo para a Central de Notificações e a ilha continua lendo ela normalmente.
// Ao sair, os valores originais são restaurados (e, se o programa fechar de forma inesperada,
// são restaurados na próxima abertura).
public static class ToastGuard
{
    const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    const string ValueName = "ShowBanner";

    static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "toast-restore.json");

    // app (AUMID) -> valor original de ShowBanner (null = não existia)
    static readonly Dictionary<string, int?> _originals = new();
    static readonly HashSet<string> _handled = new();   // apps já verificados nesta sessão
    static bool _active;

    public static void Start()
    {
        RestoreFromFile();   // sobra de uma sessão que fechou sem restaurar
        _active = true;
        Scan();
        StartBannerHider();
    }

    // Chame de tempos em tempos: apps novos ganham a chave só depois do primeiro toast
    public static void Scan()
    {
        if (!_active || _capture != null) return;   // modo leitura do balão: o registro não é mexido
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(BaseKey);
            if (root == null) return;
            foreach (var name in root.GetSubKeyNames()) Suppress(name);
        }
        catch { }
    }

    public static void Suppress(string aumid)
    {
        if (!_active || _capture != null || string.IsNullOrEmpty(aumid) || !_handled.Add(aumid)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{BaseKey}\{aumid}");
            if (key == null) return;
            var cur = key.GetValue(ValueName);
            int? original = cur is int i ? i : null;
            if (original == 0) return;   // já estava sem banner: não mexe
            _originals[aumid] = original;
            Save();
            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
        }
        catch { }
    }


    // Diagnóstico: %AppData%\\DockIsland\\notif.log
    public static void Log(string msg)
    {
        try
        {
            var p = Path.Combine(Path.GetDirectoryName(StatePath)!, "notif.log");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            if (File.Exists(p) && new FileInfo(p).Length > 100_000) File.Delete(p);
            File.AppendAllText(p, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
        }
        catch { }
    }

    // Modo alternativo (o Windows não deu acesso à lista de notificações): lê o texto do próprio balão
    // (Automação de UI), mostra na ilha e só então esconde o balão. Não mexe no registro, senão o balão
    // nem chegaria a existir para ser lido.
    static Action<string[]> _capture;
    static readonly HashSet<IntPtr> _bannerSeen = new();

    public static void StartCapture(Action<string[]> onBanner)
    {
        RestoreFromFile();   // sobra de uma sessão antiga que deixou ShowBanner=0
        _capture = onBanner;
        // se o registro já tinha sido mexido (ShowBanner=0), devolve: o balão precisa existir para ser lido
        Restore(_originals);
        _originals.Clear();
        _handled.Clear();
        try { File.Delete(StatePath); } catch { }
        _active = true;
        StartBannerHider();
    }

    static string[] ReadTexts(IntPtr h)
    {
        try
        {
            var task = Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(h);
                var list = new List<string>();
                var dump = new StringBuilder();
                var all = root.FindAll(TreeScope.Descendants, Condition.TrueCondition);
                int n = 0;
                foreach (AutomationElement e in all)
                {
                    string name = "", type = "";
                    try { name = (e.Current.Name ?? "").Trim(); type = e.Current.ControlType.ProgrammaticName; } catch { continue; }
                    if (n++ < 40) dump.Append("{").Append(type.Replace("ControlType.", "")).Append(":").Append(name.Replace("\n", "\\n")).Append("} ");
                    // textos do balão: Text e Group/Pane com nome; botões (Fechar, ações) ficam de fora
                    if (name.Length == 0) continue;
                    if (type.EndsWith("Button") || type.EndsWith("Image") || type.EndsWith("MenuItem")) continue;
                    if (!list.Contains(name)) list.Add(name);
                }
                // um item pode trazer o balão inteiro em várias linhas ("App\nTítulo\nTexto"): separa
                var lines = new List<string>();
                foreach (var item in list)
                    foreach (var ln in item.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                    {
                        var t = ln.Trim();
                        if (t.Length > 0 && !lines.Contains(t)) lines.Add(t);
                    }
                // sobrou só o nome da janela ("Nova notificação")? então não leu nada útil
                var rootName = (root.Current.Name ?? "").Trim();
                lines.RemoveAll(x => x.Equals(rootName, StringComparison.OrdinalIgnoreCase));
                Log("árvore do balão: " + dump);
                return lines.ToArray();
            });
            return task.Wait(800) ? task.Result : Array.Empty<string>();
        }
        catch (Exception ex) { Log("UIA: " + ex.Message); return Array.Empty<string>(); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);

    // ---------- Esconde o balão que já apareceu ----------
    // A opção "ShowBanner = 0" só vale para apps que já têm chave no registro (o 1º aviso de um app novo
    // escapava) e às vezes o Windows demora a reler o valor. Por isso, além disso, a janela do balão
    // (hospedada pelo ShellExperienceHost: pequena, no canto da tela) é escondida assim que aparece.
    // A notificação continua indo para a Central de Notificações, e a ilha continua lendo ela.
    static DispatcherTimer _hider;
    static readonly Dictionary<uint, bool> _isShell = new();
    static readonly HashSet<IntPtr> _logged = new();

    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

    const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_HIDEWINDOW = 0x80;

    static void StartBannerHider()
    {
        if (_hider != null) { _hider.Start(); return; }
        _hider = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(150) };
        _hider.Tick += (_, __) => { if (_active) HideBanners(); };
        Log("ocultador de balões iniciado" + (_capture != null ? " (modo leitura do balão)" : ""));
        _hider.Start();
    }

    static bool IsShellExperienceHost(uint pid)
    {
        if (_isShell.TryGetValue(pid, out var v)) return v;
        bool ok = false;
        try { using var p = Process.GetProcessById((int)pid); ok = p.ProcessName.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase)
                                   || p.ProcessName.Equals("ShellHost", StringComparison.OrdinalIgnoreCase); }   // Windows 11 24H2+: ShellHost
        catch { }
        if (_isShell.Count > 200) _isShell.Clear();
        _isShell[pid] = ok;
        return ok;
    }

    static void HideBanners()
    {
        try
        {
            var sb = new StringBuilder(64);
            var wa = System.Windows.SystemParameters.WorkArea;   // DIPs
            var nowSeen = new HashSet<IntPtr>();
            EnumWindows((h, _) =>
            {
                if (!IsWindowVisible(h)) return true;
                sb.Clear();
                if (GetClassName(h, sb, 64) == 0) return true;
                GetWindowThreadProcessId(h, out uint pid);
                if (!IsShellExperienceHost(pid)) return true;   // qualquer classe de janela: no Win11 novo o balão nem sempre é CoreWindow
                if (!GetWindowRect(h, out var r)) return true;
                double scale = Math.Max(1, GetDpiForWindow(h) / 96.0);
                double w = (r.R - r.L) / scale, hgt = (r.B - r.T) / scale;
                var tsb = new StringBuilder(128);
                GetWindowText(h, tsb, 128);
                string title = tsb.ToString();
                // o balão tem "notifica..." no título (New notification / Nova notificação / Neue Benachrichtigung...);
                // a Central de Notificações também, mas ela ocupa a altura toda
                bool titleHit = title.IndexOf("notifica", StringComparison.OrdinalIgnoreCase) >= 0
                             || title.IndexOf("benachrichtig", StringComparison.OrdinalIgnoreCase) >= 0;
                bool notTall = hgt <= wa.Height * 0.55 && hgt > 20;
                bool geom = hgt <= 300 && w >= 200 && w <= 700 && r.R / scale >= wa.Right - 100;
                bool small = notTall && (titleHit || geom);
                bool atRight = true;
                if (_logged.Add(h))
                    Log($"janela do shell: título='{title}' {w:0}x{hgt:0} em ({r.L / scale:0},{r.T / scale:0}) -> {(small ? "balão" : "ignorada")}");
                if (small && atRight)
                {
                    nowSeen.Add(h);
                    if (_capture != null)
                    {
                        if (_bannerSeen.Contains(h)) return true;   // já tentei ler este balão (e não consegui): deixa como está
                        _bannerSeen.Add(h);
                        var texts = ReadTexts(h);
                        Log("balão lido: [" + string.Join(" | ", texts) + "]");
                        // sem conseguir ler o texto, avisa mesmo assim (pílula + contador); o texto vem do banco do Windows
                        try { _capture(texts); } catch (Exception ex) { Log("captura: " + ex.Message); }
                    }
                    SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_HIDEWINDOW);
                }
                return true;
            }, IntPtr.Zero);
            _bannerSeen.IntersectWith(nowSeen);
            if (_logged.Count > 300) _logged.Clear();
        }
        catch { }
    }

    public static void Stop()
    {
        _active = false;
        _hider?.Stop();
        Restore(_originals);
        _originals.Clear();
        _handled.Clear();
        try { File.Delete(StatePath); } catch { }
    }

    static void Restore(Dictionary<string, int?> map)
    {
        foreach (var (aumid, original) in map)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{BaseKey}\{aumid}", true);
                if (key == null) continue;
                if (original == null) key.DeleteValue(ValueName, false);
                else key.SetValue(ValueName, original.Value, RegistryValueKind.DWord);
            }
            catch { }
        }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var dto = _originals.ToDictionary(k => k.Key, k => k.Value ?? -1);
            AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(dto));
        }
        catch { }
    }

    static void RestoreFromFile()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var dto = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(StatePath));
            if (dto != null) Restore(dto.ToDictionary(k => k.Key, k => k.Value < 0 ? (int?)null : k.Value));
            File.Delete(StatePath);
        }
        catch { }
    }
}
