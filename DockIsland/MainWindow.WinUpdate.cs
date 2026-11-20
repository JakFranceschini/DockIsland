using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace DockIsland;

// =========================================================
//  Botão de atualização da aba Sistema: avisa quando o
//  Windows Update tem atualizações disponíveis
// =========================================================
public partial class MainWindow
{
    static readonly SolidColorBrush UpdOrange = Frozen(0xFF, 0xFF, 0x9F, 0x0A);

    int _updCount = -1;                         // -1 = ainda não se sabe
    bool _updBusy;
    DateTime _updLastCheck = DateTime.MinValue;
    DispatcherTimer _updRecheck;

    void InitWinUpdate()
    {
        // 1ª conferência logo depois de abrir (só o que o Windows já sabe, rápido); a conferência online,
        // que consulta os servidores, vem em seguida e depois se repete de 6 em 6 horas.
        Once(TimeSpan.FromSeconds(20), () => _ = CheckWinUpdateAsync(false));
        Once(TimeSpan.FromSeconds(90), () => _ = CheckWinUpdateAsync(true));
        var every = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        every.Tick += (_, _) => _ = CheckWinUpdateAsync(true);
        every.Start();
    }

    static void Once(TimeSpan after, Action a)
    {
        var t = new DispatcherTimer { Interval = after };
        t.Tick += (_, _) => { t.Stop(); a(); };
        t.Start();
    }

    // o usuário abriu o Windows Update pelo botão: confere de novo daqui a pouco (ele pode ter instalado)
    void RecheckWinUpdateSoon()
    {
        _updRecheck ??= new DispatcherTimer { Interval = TimeSpan.FromMinutes(2) };
        _updRecheck.Stop();
        _updRecheck.Tick -= RecheckTick;
        _updRecheck.Tick += RecheckTick;
        _updRecheck.Start();
    }

    void RecheckTick(object sender, EventArgs e)
    {
        _updRecheck.Stop();
        _ = CheckWinUpdateAsync(true);
    }

    async Task CheckWinUpdateAsync(bool online)
    {
        if (_updBusy) return;
        _updBusy = true;
        _updLastCheck = DateTime.Now;
        try
        {
            int n = await Task.Run(() => QueryPendingUpdates(online));
            if (n >= 0) { _updCount = n; ApplyUpdateButton(); }
        }
        catch { }
        finally { _updBusy = false; }
    }

    // Pergunta ao Windows Update Agent quantas atualizações estão pendentes (não instaladas e não ocultas).
    // online = false usa o resultado da última busca do próprio Windows (rápido); true consulta os servidores.
    // Devolve -1 se não foi possível saber.
    static int QueryPendingUpdates(bool online)
    {
        try
        {
            var type = Type.GetTypeFromProgID("Microsoft.Update.Session");
            if (type == null) return -1;
            dynamic session = Activator.CreateInstance(type);
            dynamic searcher = session.CreateUpdateSearcher();
            searcher.Online = online;
            try
            {
                // BrowseOnly=0: só o que o Windows oferece de verdade (sem as opcionais)
                dynamic r = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software' and BrowseOnly=0");
                return (int)r.Updates.Count;
            }
            catch
            {
                dynamic r = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
                return (int)r.Updates.Count;
            }
        }
        catch { return -1; }
    }

    void ApplyUpdateButton()
    {
        bool has = _updCount > 0;
        UpdateDot.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        UpdateIcon.Foreground = has ? UpdOrange : SysBlue;
        UpdateTitle.Text = has ? "Atualização disponível" : "Buscar atualizações";
        UpdateSub.Text = has ? (_updCount == 1 ? "1 pendente" : _updCount + " pendentes") : "Windows Update";
        UpdateSub.Foreground = has ? UpdOrange : SysDim;
        // com atualização pendente abre a página normal (botão "Instalar"); sem, já inicia a busca
        UpdateBtn.Tag = has ? "ms-settings:windowsupdate" : "ms-settings:windowsupdate-action";
        UpdateBtn.ToolTip = has ? "Há atualizações do Windows disponíveis" : "Buscar atualizações do Windows";
    }
}
