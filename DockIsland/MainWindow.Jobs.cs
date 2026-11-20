using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DockIsland;

// =========================================================
//  Progresso de cópias e downloads na pílula
//   - Cópias/movimentos/exclusões do Explorador: lê a janela "OperationStatusWindow"
//     (porcentagem real).
//   - Downloads: acompanha a pasta Downloads (.crdownload, .part...). Os navegadores não
//     informam o tamanho total, então a barra fica "correndo" e mostra o tamanho e a velocidade.
// =========================================================
public partial class MainWindow
{
    const double JobW = 300;   // largura da pílula mostrando o progresso

    // 0 = pílula normal, 1 = pílula mostrando o progresso
    public static readonly DependencyProperty JobPeekProperty =
        DependencyProperty.Register(nameof(JobPeek), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, (d, e) => { var w = (MainWindow)d; w.ApplyProgress(w.Progress); }));

    public double JobPeek
    {
        get => (double)GetValue(JobPeekProperty);
        set => SetValue(JobPeekProperty, value);
    }

    class JobInfo
    {
        public string Icon;      // glifo Segoe Fluent Icons
        public string Label;
        public string Right;     // texto à direita (porcentagem ou velocidade)
        public double Percent;   // 0..100, ou -1 = não dá para saber
        public bool Done;
    }

    readonly DispatcherTimer _jobTimer = new() { Interval = TimeSpan.FromMilliseconds(1000) };
    bool _jobBusy;
    bool _jobShown;          // pílula de progresso visível
    bool _jobWasActive;      // no ciclo anterior havia algo em andamento
    string _jobKind;         // "copy" | "dl" do ciclo anterior
    DateTime _jobDoneUntil;
    bool _jobIndeterminate;

    // estado dos downloads (só mexido pela tarefa de varredura, uma por vez)
    readonly Dictionary<string, (long size, DateTime t, DateTime growth, double speed)> _dl =
        new(StringComparer.OrdinalIgnoreCase);

    void InitJobs()
    {
        _jobTimer.Tick += async (_, __) =>
        {
            if (_jobBusy) return;
            _jobBusy = true;
            JobInfo info = null; string kind = null;
            try { (info, kind) = await Task.Run(ScanJobs); }
            catch { }
            _jobBusy = false;
            ApplyJob(info, kind);
        };
        _jobTimer.Start();
    }

    void ApplyJob(JobInfo info, string kind)
    {
        var now = DateTime.UtcNow;
        if (info != null)
        {
            _jobWasActive = true;
            _jobKind = kind;
        }
        else if (_jobWasActive)
        {
            // terminou agora: mostra "concluído" por um instante
            _jobWasActive = false;
            _jobDoneUntil = now.AddMilliseconds(2200);
            info = new JobInfo
            {
                Icon = "",
                Label = _jobKind == "dl" ? "Download concluído" : "Concluído",
                Right = "100%", Percent = 100, Done = true
            };
        }
        else if (_jobShown && now < _jobDoneUntil)
        {
            return;   // continua mostrando o "concluído"
        }

        if (info == null) { HideJobPill(); return; }
        ShowJobPill(info);
    }

    void ShowJobPill(JobInfo j)
    {
        JobIcon.Text = j.Icon;
        JobLabel.Text = j.Label;
        JobRight.Text = j.Right ?? "";
        double trackW = JobTrack.ActualWidth > 0 ? JobTrack.ActualWidth : 150;

        if (j.Percent >= 0)
        {
            StopIndeterminate();
            JobFill.Width = trackW * Math.Clamp(j.Percent / 100.0, 0, 1);
            JobFill.Background = j.Done ? SysGreen : SysWhite;
        }
        else
        {
            StartIndeterminate(trackW);
        }

        _jobShown = true;
        if (JobPeek < 0.999)
        {
            BeginAnimation(JobPeekProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    void HideJobPill()
    {
        _jobShown = false;
        StopIndeterminate();
        if (JobPeek <= 0) return;
        BeginAnimation(JobPeekProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    static readonly Brush SysGreen = new SolidColorBrush(Color.FromRgb(0x30, 0xD1, 0x58));

    void StartIndeterminate(double trackW)
    {
        if (_jobIndeterminate) return;
        _jobIndeterminate = true;
        JobFill.Width = 44;
        JobFill.Background = SysWhite;
        JobFillShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-44, trackW, TimeSpan.FromMilliseconds(1100))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    void StopIndeterminate()
    {
        if (!_jobIndeterminate) return;
        _jobIndeterminate = false;
        JobFillShift.BeginAnimation(TranslateTransform.XProperty, null);
        JobFillShift.X = 0;
    }

    // =====================================================
    //  Varredura (roda fora da thread de interface)
    // =====================================================
    (JobInfo, string) ScanJobs()
    {
        var copy = ScanCopy();
        if (copy != null) return (copy, "copy");
        var dl = ScanDownloads();
        if (dl != null) return (dl, "dl");
        return (null, null);
    }

    // ---------- Cópia / mover / excluir do Explorador ----------
    delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);

    static readonly Regex PctRx = new(@"(\d{1,3})\s*%", RegexOptions.Compiled);

    static JobInfo ScanCopy()
    {
        IntPtr found = IntPtr.Zero;
        var cls = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            cls.Clear();
            GetClassName(h, cls, 64);
            // o Windows mantém uma janela dessas oculta mesmo sem nada copiando: só vale a visível
            if (cls.ToString() == "OperationStatusWindow" && IsWindowVisible(h)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        if (found == IntPtr.Zero) return null;

        double pct = -1;
        string header = null;
        var sb = new StringBuilder(256);
        GetWindowText(found, sb, 256);
        var title = sb.ToString();

        try
        {
            var root = AutomationElement.FromHandle(found);
            var pb = root.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ProgressBar));
            if (pb != null && pb.TryGetCurrentPattern(RangeValuePattern.Pattern, out var o))
            {
                var rv = ((RangeValuePattern)o).Current;
                if (rv.Maximum > rv.Minimum) pct = (rv.Value - rv.Minimum) / (rv.Maximum - rv.Minimum) * 100;
            }
            var texts = root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text));
            foreach (AutomationElement t in texts)
            {
                var n = t.Current.Name ?? "";
                if (Regex.IsMatch(n, @"^(Copiando|Movendo|Excluindo|Copying|Moving|Deleting|Recycling|Movendo para)", RegexOptions.IgnoreCase))
                { header = n; break; }
            }
        }
        catch { }

        if (pct < 0)
        {
            var m = PctRx.Match(title);
            if (m.Success) pct = Math.Min(100, double.Parse(m.Groups[1].Value));
        }

        // sem texto de operação e sem progresso: janela vazia, não há nada sendo copiado
        if (header == null && pct <= 0) return null;

        string verb = "Copiando";
        string label = header;
        if (!string.IsNullOrWhiteSpace(label))
        {
            int de = label.IndexOf(" de ", StringComparison.OrdinalIgnoreCase);
            if (de < 0) de = label.IndexOf(" from ", StringComparison.OrdinalIgnoreCase);
            if (de > 0) label = label.Substring(0, de);
        }
        else label = verb;

        bool deleting = Regex.IsMatch(label, @"^(Excluindo|Deleting|Recycling)", RegexOptions.IgnoreCase);
        return new JobInfo
        {
            Icon = deleting ? "" : "",
            Label = label.Trim(),
            Percent = pct,
            Right = pct >= 0 ? $"{Math.Round(pct)}%" : "…"
        };
    }

    // ---------- Downloads ----------
    static readonly string[] PartialExts = { ".crdownload", ".part", ".opdownload", ".download", ".partial" };

    static string DownloadsDir
    {
        get
        {
            try
            {
                var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
                if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var ptr) == 0 && ptr != IntPtr.Zero)
                {
                    try { return Marshal.PtrToStringUni(ptr); }
                    finally { Marshal.FreeCoTaskMem(ptr); }
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    JobInfo ScanDownloads()
    {
        string dir = DownloadsDir;
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        var now = DateTime.UtcNow;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var active = new List<(string path, long size, double speed, DateTime growth)>();

        try
        {
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                var ext = Path.GetExtension(f);
                if (!PartialExts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                long size;
                try { size = new FileInfo(f).Length; } catch { continue; }
                seen.Add(f);

                if (_dl.TryGetValue(f, out var st))
                {
                    double dt = Math.Max(0.2, (now - st.t).TotalSeconds);
                    if (size > st.size)
                    {
                        double inst = (size - st.size) / dt;
                        _dl[f] = (size, now, now, st.speed <= 0 ? inst : st.speed * 0.5 + inst * 0.5);   // suaviza a velocidade
                    }
                    else _dl[f] = (size, now, st.growth, st.growth == default ? 0 : st.speed);
                }
                else _dl[f] = (size, now, now, 0);

                var cur = _dl[f];
                if ((now - cur.growth).TotalSeconds < 4) active.Add((f, size, cur.speed, cur.growth));
            }
        }
        catch { return null; }

        foreach (var k in _dl.Keys.Where(k => !seen.Contains(k)).ToList()) _dl.Remove(k);
        if (active.Count == 0) return null;

        var main = active.OrderByDescending(a => a.growth).First();
        double speed = active.Sum(a => a.speed);
        string name = Path.GetFileNameWithoutExtension(main.path);
        if (name.StartsWith("Unconfirmed", StringComparison.OrdinalIgnoreCase) || name.Length == 0) name = "arquivo";

        string label = active.Count > 1 ? $"Baixando {active.Count} arquivos" : "Baixando " + name;
        string right = speed > 1024 ? FormatSize((long)speed) + "/s" : FormatSize(main.size);
        return new JobInfo { Icon = "", Label = label, Percent = -1, Right = right };
    }
}
