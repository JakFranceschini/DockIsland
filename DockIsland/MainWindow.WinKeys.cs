using System;
using System.Windows;

namespace DockIsland;

// Atalhos da tecla Windows:
//   Win sozinha -> abre/fecha o lançador de aplicativos
//   Win + S     -> abre a ilha na aba de busca (de novo: fecha)
//   Win + N     -> abre a ilha na aba de notificações (de novo: fecha)
//   Win + W     -> troca para o próximo widget do dock
public partial class MainWindow
{
    WinKeyHook _winHook;
    SwitcherWindow _switcher;   // Alt+Tab e Win+Tab no estilo da ilha

    void InitWinKeys()
    {
        // Ganchos de teclado são globais: se a interface estiver ocupada (boot), o teclado de TODO o Windows
        // engasga. Na abertura com o Windows eles só entram depois que tudo já assentou.
        void Install()
        {
            if (_winHook != null) return;
            _winHook = new WinKeyHook(OnWinAlone, OnWinS)
            {
                SwitcherMode = () => _switcher?.ModeCode ?? 0,
                OnAltTab = shift => _switcher?.AltTab(shift),
                OnAltRelease = () => _switcher?.Commit(),
                OnWinTab = () => _switcher?.ToggleOverview(),
                OnWinN = OnWinN,
                OnWinW = OnWinW,
                OnSwitcherKey = (vk, shift) => _switcher?.Nav(vk, shift)
            };
            // O seletor Alt+Tab/Win+Tab é criado depois, com o programa ocioso; até lá os atalhos o ignoram (_switcher?.)
            Stage(1500, () => { if (_winHook != null && _switcher == null) _switcher = new SwitcherWindow(); });
        }
        if (App.Boot) Stage(10000, Install); else Install();
    }

    void ShutdownWinKeys()
    {
        _winHook?.Dispose();
        _winHook = null;
        try { _switcher?.Cancel(); _switcher?.Close(); } catch { }
        _switcher = null;
    }

    void OnWinAlone()
    {
        try
        {
            // visão geral aberta: a tecla Windows a fecha (indo para a janela sob o mouse, se houver)
            if (_switcher != null && _switcher.ModeCode != 0) { _switcher.CloseOverview(); return; }
            (Application.Current as App)?.Dock?.ToggleLauncher();
        }
        catch { }
    }

    void OnWinN()
    {
        try
        {
            // aba Notificações já aberta: o atalho fecha
            if (_page == 1 && Progress > 0.05)
            {
                SelectPage(0);
                Collapse();
                return;
            }

            if (Progress <= 0.05) Expand();
            SelectPage(1);
        }
        catch { }
    }

    // Win + W: passa para o próximo widget do dock (o de baixo, como a roda do mouse para baixo)
    void OnWinW()
    {
        try { (Application.Current as App)?.Dock?.NextWidget(); }
        catch { }
    }

    void OnWinS()
    {
        try
        {
            // busca já aberta: o atalho fecha
            if (_page == 6 && _searchMode && Progress > 0.05)
            {
                SelectPage(0);
                Collapse();
                return;
            }

            if (Progress <= 0.05) Expand();
            SelectPage(6);   // EnterSearch: a ilha passa a aceitar teclado e foca o campo
        }
        catch { }
    }
}
