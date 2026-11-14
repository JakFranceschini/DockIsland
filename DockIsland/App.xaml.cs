using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace DockIsland;

public partial class App : Application
{
    private Mutex _mutex;

    // O programa se chamava "DynamicIsland": move a pasta de dados (ícones, categorias, dock, ajustes)
    // e passa a inicialização automática para o nome novo, sem perder nada.
    static void MigrateFromDynamicIsland()
    {
        try
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var oldDir = Path.Combine(appData, "DynamicIsland");
            var newDir = Path.Combine(appData, "DockIsland");
            if (Directory.Exists(oldDir) && !Directory.Exists(newDir))
            {
                Directory.Move(oldDir, newDir);
                // os caminhos dos ícones personalizados ficam salvos nos .json: aponta para a pasta nova
                foreach (var f in Directory.GetFiles(newDir, "*.json"))
                {
                    var t = File.ReadAllText(f);
                    var n = t.Replace(@"\\DynamicIsland\\", @"\\DockIsland\\").Replace(@"\DynamicIsland\", @"\DockIsland\");
                    if (n != t) File.WriteAllText(f, n);
                }
            }
        }
        catch { }
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (k?.GetValue("DynamicIsland") != null)
            {
                k.DeleteValue("DynamicIsland", false);
                k.SetValue("DockIsland", $"\"{Environment.ProcessPath}\" --autostart");
            }
        }
        catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        // Modo cliente: DockIsland.exe --notify "Título" "Mensagem"
        if (e.Args.Length >= 2 && e.Args[0] == "--notify")
        {
            try
            {
                using var client = new NamedPipeClientStream(".", MainWindow_PipeName, PipeDirection.Out);
                client.Connect(1500);
                using var w = new StreamWriter(client);
                w.Write(string.Join("\t", e.Args.Length > 3 ? e.Args[3] : "Terminal", e.Args[1], e.Args.Length > 2 ? e.Args[2] : ""));
            }
            catch { }
            Shutdown();
            return;
        }

        // Modo cliente: DockIsland.exe --quit  (o instalador usa para fechar o programa com segurança,
        // devolvendo a barra de tarefas do Windows)
        if (e.Args.Length >= 1 && e.Args[0] == "--quit")
        {
            try
            {
                using var client = new NamedPipeClientStream(".", MainWindow_PipeName, PipeDirection.Out);
                client.Connect(1500);
                using var w = new StreamWriter(client);
                w.Write("__quit__");
            }
            catch { }
            Shutdown();
            return;
        }

        // Modo auxiliar de sensores (elevado, sem janela): DockIsland.exe --sensors [--setup]
        if (e.Args.Length >= 1 && e.Args[0] == "--sensors")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var a = e.Args;
            Task.Run(() =>
            {
                try { SensorHelper.Run(a); } catch { }
                Dispatcher.BeginInvoke(new Action(Shutdown));
            });
            return;
        }

        MigrateFromDynamicIsland();   // traz os dados e a inicialização automática do nome antigo

        // Instância única
        _mutex = new Mutex(true, "DockIslandSingleton", out bool created);
        if (!created) { Shutdown(); return; }

        // Abertura junto com o Windows (--autostart, ou computador ligado há poucos minutos): modo "leve".
        // O programa roda com prioridade baixa e começa os recursos pesados aos poucos, para não competir
        // com a área de trabalho (explorer, antivírus, drivers) que está carregando nesse momento.
        Boot = e.Args.Contains("--autostart") || Environment.TickCount64 < 5 * 60 * 1000;
        if (Boot)
        {
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
            var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
            back.Tick += (_, _) =>
            {
                back.Stop();
                try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal; } catch { }
            };
            back.Start();
        }

        base.OnStartup(e);
        global::DockIsland.MainWindow.ApplySavedTheme();   // cores salvas (tema claro/escuro e destaque) antes de abrir a ilha
        var main = new MainWindow();
        main.Show();

        // A ilha já está na tela; o vigia de "sempre na frente" e o dock vêm depois, sem atrasar a abertura
        global::DockIsland.MainWindow.Stage(Boot ? 1200 : 400, () => TopmostGuard.Start());   // ilha, dock e lançador sempre acima dos outros programas (menos em tela cheia)
        global::DockIsland.MainWindow.Stage(Boot ? 2500 : 150, () =>
        {
            Dock = new DockWindow();
            Dock.SizeRequested += n => main.SetDockSize(n);
            Dock.ApplySettings(main.Settings.DockEnabled, main.Settings.HideTaskbar, main.Settings.DockSize);
        });
    }

    // Verdadeiro quando o programa abriu junto com o Windows (inicialização escalonada)
    public static bool Boot { get; private set; }

    public DockWindow Dock { get; private set; }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Dock?.RestoreTaskbar(); } catch { }
        try { ToastGuard.Stop(); } catch { }   // devolve os banners do Windows
        base.OnExit(e);
    }

    public const string MainWindow_PipeName = "DockIslandPipe";
}
