using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using Lhm = LibreHardwareMonitor.Hardware;

namespace DockIsland;

// =========================================================
//  Temperaturas de CPU, GPU, memória e placa-mãe (aba Sistema)
//
//  O Windows não informa essas temperaturas por conta própria; elas vêm da biblioteca
//  LibreHardwareMonitorLib:
//    - GPU: lida direto pelo programa (NVIDIA/AMD/Intel), sem permissão especial.
//    - CPU, placa-mãe e memória: exigem o driver PawnIO e um processo com permissão de
//      administrador. Para não rodar a ilha inteira como administrador (isso quebraria o
//      arrastar-e-soltar do Explorador), existe um auxiliar pequeno e sem janela:
//          DockIsland.exe --sensors [--setup]
//      Ele lê os sensores e escreve %AppData%\DockIsland\temps.json a cada 2 s; a ilha só lê o arquivo.
//
//  Ativar (uma vez, pede permissão de administrador): o auxiliar roda com --setup, instala o
//  PawnIO se faltar (winget), registra a tarefa agendada "DockIslandSensors" (ao entrar no Windows,
//  com privilégios altos, sem pedir permissão de novo) e cria o marcador sensors.on.
//  Desativar: a ilha apaga sensors.on; o auxiliar percebe, remove a tarefa e fecha.
//  O auxiliar também fecha sozinho ~30 s depois que a ilha fecha.
// =========================================================

public class TempSnapshot
{
    public long Time { get; set; }      // milissegundos Unix (UTC) da leitura
    public float? Cpu { get; set; }
    public float? Gpu { get; set; }
    public float? Mem { get; set; }
    public float? Board { get; set; }
}

// Lê os sensores de temperatura com a LibreHardwareMonitorLib
public sealed class HwReader : IDisposable
{
    readonly Lhm.Computer _pc;

    public HwReader(bool cpu, bool gpu, bool memory, bool board)
    {
        _pc = new Lhm.Computer
        {
            IsCpuEnabled = cpu,
            IsGpuEnabled = gpu,
            IsMemoryEnabled = memory,
            IsMotherboardEnabled = board
        };
        _pc.Open();
    }

    record struct Sample(Lhm.HardwareType Type, string Name, float Value);

    static void Collect(Lhm.IHardware hw, List<Sample> into)
    {
        try { hw.Update(); } catch { }
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType == Lhm.SensorType.Temperature && s.Value.HasValue)
                into.Add(new Sample(hw.HardwareType, s.Name ?? "", s.Value.Value));
        }
        foreach (var sub in hw.SubHardware) Collect(sub, into);
    }

    // Leituras fora desta faixa são lixo (sensor sem ligação, driver ausente...)
    static bool Valid(float v) => v > 1f && v < 125f;

    static float? Pick(IEnumerable<Sample> items, bool fallbackToMax, params string[] prefer)
    {
        var list = items.Where(x => Valid(x.Value)).ToList();
        foreach (var p in prefer)
        {
            var m = list.Where(x => x.Name.Contains(p, StringComparison.OrdinalIgnoreCase)).ToList();
            if (m.Count > 0) return m.Max(x => x.Value);
        }
        return fallbackToMax && list.Count > 0 ? list.Max(x => x.Value) : null;
    }

    static readonly string[] CpuLike = { "CPU", "Core", "Tctl", "Tdie", "Package", "PECI" };

    public TempSnapshot Read()
    {
        var all = new List<Sample>();
        foreach (var hw in _pc.Hardware) Collect(hw, all);

        var cpu = all.Where(s => s.Type == Lhm.HardwareType.Cpu);
        var gpu = all.Where(s => s.Type is Lhm.HardwareType.GpuNvidia or Lhm.HardwareType.GpuAmd or Lhm.HardwareType.GpuIntel);
        var mem = all.Where(s => s.Type == Lhm.HardwareType.Memory);
        // temperaturas da placa-mãe vêm do Super I/O / controlador embarcado; os nomes só são
        // confiáveis quando contêm "Motherboard", "System", "Chipset"... (sem isso fica "—", sem chutar)
        var board = all.Where(s => s.Type is Lhm.HardwareType.Motherboard or Lhm.HardwareType.SuperIO or Lhm.HardwareType.EmbeddedController)
                       .Where(s => !CpuLike.Any(k => s.Name.Contains(k, StringComparison.OrdinalIgnoreCase)));

        return new TempSnapshot
        {
            Cpu = Pick(cpu, true, "Package", "Tctl", "Tdie", "Core Max"),
            Gpu = Pick(gpu, true, "GPU Core"),
            Mem = Pick(mem, true),
            Board = Pick(board, false, "Motherboard", "Mainboard", "System", "Board", "Chipset", "PCH")
        };
    }

    public void Dispose()
    {
        try { _pc.Close(); } catch { }
    }
}

// Auxiliar elevado + arquivos de comunicação com a ilha
public static class SensorHelper
{
    const string TaskName = "DockIslandSensors";

    public static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland");
    public static string TempsFile => Path.Combine(Dir, "temps.json");
    public static string MarkerFile => Path.Combine(Dir, "sensors.on");   // existe = leitura completa ativada

    public static bool Enabled => File.Exists(MarkerFile);

    static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ---------- Lado da ilha ----------

    // Última leitura do auxiliar (null se ele não está rodando / o arquivo está velho)
    public static TempSnapshot ReadSnapshot()
    {
        try
        {
            if (!File.Exists(TempsFile)) return null;
            var s = JsonSerializer.Deserialize<TempSnapshot>(File.ReadAllText(TempsFile));
            if (s == null || NowMs() - s.Time > 10000) return null;
            return s;
        }
        catch { return null; }
    }

    // Pede permissão de administrador (uma vez) e roda o auxiliar em modo de instalação
    public static void StartElevatedSetup()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--sensors --setup")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch { }   // cancelou a permissão: nada acontece
    }

    // Dispara a tarefa agendada (já tem privilégios, sem pedir de novo). Devolve o código de saída
    // do schtasks (0 = ok; diferente de 0 = tarefa inexistente/falhou; -1 = nem rodou).
    public static int RunTask()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", $"/Run /TN \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p == null) return -1;
            if (!p.WaitForExit(5000)) return 0;
            return p.ExitCode;
        }
        catch { return -1; }
    }

    // Ao abrir a ilha com a leitura ativada: inicia o auxiliar
    public static void StartTaskIfEnabled()
    {
        if (!Enabled) return;
        Task.Run(() => RunTask());
    }

    // Desativa: o auxiliar vê que o marcador sumiu, remove a tarefa e fecha
    public static void Disable()
    {
        try { File.Delete(MarkerFile); } catch { }
    }

    // ---------- Lado do auxiliar (processo elevado, sem janela) ----------

    static bool MainAlive()
    {
        try
        {
            if (Mutex.TryOpenExisting("DockIslandSingleton", out var m)) { m.Dispose(); return true; }
        }
        catch { }
        return false;
    }

    static bool PawnIoInstalled() =>
        Directory.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO"));

    static void EnsurePawnIo()
    {
        if (PawnIoInstalled()) return;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("winget.exe",
                "install --id namazso.PawnIO -e --silent --accept-package-agreements --accept-source-agreements")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            p?.WaitForExit(240000);
        }
        catch { }   // sem winget: instale o PawnIO manualmente (pawnio.eu)
    }

    static int RunSchtasks(string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("schtasks.exe", args)
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            if (p == null) return -1;
            p.WaitForExit(15000);
            return p.ExitCode;
        }
        catch { return -1; }
    }

    static void CreateTask()
    {
        var exe = Environment.ProcessPath!;
        var user = WindowsIdentity.GetCurrent().Name;
        // XML (e não /SC ONLOGON) para desligar as travas padrão: parar na bateria, limite de 3 dias...
        var xml =
            "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
            "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
            "  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>" + SecurityElement.Escape(user) + "</UserId></LogonTrigger></Triggers>\n" +
            "  <Principals><Principal id=\"Author\"><UserId>" + SecurityElement.Escape(user) + "</UserId>" +
            "<LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
            "  <Settings>\n" +
            "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\n" +
            "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\n" +
            "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\n" +
            "    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>\n" +
            "    <AllowStartOnDemand>true</AllowStartOnDemand>\n" +
            "    <Enabled>true</Enabled>\n" +
            "  </Settings>\n" +
            "  <Actions Context=\"Author\"><Exec><Command>" + SecurityElement.Escape(exe) + "</Command>" +
            "<Arguments>--sensors</Arguments></Exec></Actions>\n" +
            "</Task>\n";

        var tmp = Path.Combine(Path.GetTempPath(), "DockIslandSensors.xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{tmp}\" /F");
        }
        catch { }
        finally { try { File.Delete(tmp); } catch { } }
    }

    static void DeleteTask() => RunSchtasks($"/Delete /TN \"{TaskName}\" /F");

    // Ponto de entrada: DockIsland.exe --sensors [--setup]
    public static void Run(string[] args)
    {
        bool setup = args.Contains("--setup");
        try { Directory.CreateDirectory(Dir); } catch { }

        if (setup)
        {
            EnsurePawnIo();
            CreateTask();
            try { File.WriteAllText(MarkerFile, "1"); } catch { }
        }
        else if (!Enabled)
        {
            DeleteTask();   // iniciado pela tarefa, mas a leitura foi desativada: limpa e sai
            return;
        }

        // um único auxiliar por vez
        using var single = new Mutex(true, "DockIslandSensorsSingleton", out bool created);
        if (!created) return;

        HwReader reader = null;
        try { reader = new HwReader(cpu: true, gpu: false, memory: true, board: true); }
        catch { }

        var lastMain = DateTime.UtcNow;
        bool mainSeen = false;
        try
        {
            while (true)
            {
                if (!Enabled) { DeleteTask(); break; }

                // Iniciado junto com o Windows, o auxiliar pode subir bem antes da ilha (que tem atraso de
                // inicialização): só fecha 30 s depois de a ilha ter sido vista e sumido; se ela nunca
                // aparecer, espera 10 min.
                if (MainAlive()) { lastMain = DateTime.UtcNow; mainSeen = true; }
                else if ((DateTime.UtcNow - lastMain).TotalSeconds > (mainSeen ? 30 : 600)) break;

                TempSnapshot snap;
                try { snap = reader?.Read() ?? new TempSnapshot(); }
                catch { snap = new TempSnapshot(); }
                snap.Time = NowMs();

                try
                {
                    var tmp = TempsFile + ".tmp";
                    File.WriteAllText(tmp, JsonSerializer.Serialize(snap));
                    File.Move(tmp, TempsFile, true);
                }
                catch { }

                Thread.Sleep(2000);
            }
        }
        finally
        {
            reader?.Dispose();
            try { File.Delete(TempsFile); } catch { }
        }
    }
}
