using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace DockIsland;

public partial class App : Application
{
    private Mutex _mutex;

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr FindWindow(string cls, string title);

    // Na abertura junto com o Windows o Explorer (barra de tarefas e área de trabalho) ainda pode estar
    // carregando. Criar as janelas e esconder a barra nesse momento causa janelas fora do lugar, barra que
    // não some e travadas. Aqui o programa só dorme (sem usar processador) até o Explorer aparecer.
    static void WaitForShell()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            bool waited = false;
            while (sw.ElapsedMilliseconds < 25000)
            {
                if (FindWindow("Shell_TrayWnd", null) != IntPtr.Zero && FindWindow("Progman", null) != IntPtr.Zero) break;
                waited = true;
                Thread.Sleep(250);
            }
            if (waited) Thread.Sleep(1200);   // folga para o Explorer terminar de montar a barra (se já estava pronto, não espera)
        }
        catch { }
    }

    // Erros inesperados (ex.: um componente do Windows que ainda não respondeu no boot) não derrubam a ilha
    static void LogError(Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland");
            Directory.CreateDirectory(dir);
            var f = Path.Combine(dir, "erro.log");
            if (File.Exists(f) && new FileInfo(f).Length > 200_000) return;   // sem crescer sem limite
            File.AppendAllText(f, $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}] {ex}\r\n\r\n");
        }
        catch { }
    }

    // O WPF limita as animações a 60 quadros/s por padrão: em monitor de 120/144 Hz a ilha parece "travada".
    // Aqui libera até 144 (em monitor de 60 Hz nada muda).
    static App()
    {
        try { Timeline.DesiredFrameRateProperty.OverrideMetadata(typeof(Timeline), new FrameworkPropertyMetadata { DefaultValue = 144 }); }
        catch { }
    }

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

        // Instância única (primeiro de tudo: uma segunda cópia sai sem gastar nada)
        _mutex = new Mutex(true, "DockIslandSingleton", out bool created);
        if (!created) { Shutdown(); return; }

        DispatcherUnhandledException += (_, ev) => { LogError(ev.Exception); ev.Handled = true; };
        TaskScheduler.UnobservedTaskException += (_, ev) => { LogError(ev.Exception); ev.SetObserved(); };

        MigrateFromDynamicIsland();   // traz os dados e a inicialização automática do nome antigo

        // Abertura junto com o Windows (--autostart, ou computador ligado há poucos minutos): modo "leve".
        // O programa roda com prioridade baixa e começa os recursos pesados aos poucos, para não competir
        // com a área de trabalho (explorer, antivírus, drivers) que está carregando nesse momento.
        Boot = e.Args.Contains("--autostart") || Environment.TickCount64 < 5 * 60 * 1000;
        if (Boot) WaitForShell();   // só cria as janelas com a área de trabalho do Windows pronta
        // (a prioridade baixa só entra depois que a ilha e o dock já estão na tela; ver abaixo)
        base.OnStartup(e);
        global::DockIsland.MainWindow.ApplySavedTheme();   // cores salvas (tema claro/escuro e destaque) antes de abrir a ilha
        var main = new MainWindow();
        main.Show();

        // O dock nasce junto com a ilha, no mesmo momento (sem espera)
        Dock = new DockWindow();
        Dock.SizeRequested += n => main.SetDockSize(n);
        Dock.WidgetEnabled = main.WidgetOn;           // widgets do dock ligados/desligados nos Ajustes
        Dock.MediaCommand += main.MediaCommand;       // botões do widget de música
        Dock.WeatherCity = () => main.Settings.WeatherCity;   // cidade do widget de clima
        // tempo: no boot a rede ainda pode nem ter conectado, então só começa depois
        if (Boot) global::DockIsland.MainWindow.Stage(10000, () => Dock?.StartWeather());
        else Dock.StartWeather();                     // começa a buscar o tempo (só com o widget ligado)
        Dock.StartWidgets();                          // relógio do widget de hora
        main.StartDockWidgets();                      // widgets "último copiado" e "lixeira"
        Dock.ApplySettings(main.Settings.DockEnabled, main.Settings.HideTaskbar, main.Settings.DockSize);

        MemTrim.Start();                              // devolve memória ao Windows de tempos em tempos

        // Só agora, com ilha e dock já visíveis, entra o modo "leve" da abertura com o Windows:
        // prioridade baixa e recursos pesados começando aos poucos, sem competir com a área de trabalho.
        if (Boot)
        {
            try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.BelowNormal; } catch { }
            var back = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            back.Tick += (_, _) =>
            {
                back.Stop();
                try { System.Diagnostics.Process.GetCurrentProcess().PriorityClass = System.Diagnostics.ProcessPriorityClass.Normal; } catch { }
            };
            back.Start();
        }

        // vigia de "sempre na frente" e carga da lista de apps do lançador vêm depois, sem atrasar a abertura
        global::DockIsland.MainWindow.Stage(Boot ? 500 : 150, () => TopmostGuard.Start());
        global::DockIsland.MainWindow.Stage(Boot ? 8000 : 800, () => Dock?.PrewarmLauncher());
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
