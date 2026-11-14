using System;
using System.Windows;

namespace DockIsland;

// Atalhos da tecla Windows:
//   Win sozinha -> abre/fecha o lançador de aplicativos
//   Win + S     -> abre a ilha na aba de busca (de novo: fecha)
public partial class MainWindow
{
    WinKeyHook _winHook;
    SwitcherWindow _switcher;   // Alt+Tab e Win+Tab no estilo da ilha

    void InitWinKeys()
    {
        // O seletor Alt+Tab/Win+Tab é criado quando o programa fica ocioso (não atrasa a abertura);
        // até lá os atalhos abaixo ignoram a chamada (todos usam _switcher?.).
        Dispatcher.BeginInvoke(new Action(() => { if (_winHook != null && _switcher == null) _switcher = new SwitcherWindow(); }),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        _winHook = new WinKeyHook(OnWinAlone, OnWinS)
        {
            SwitcherMode = () => _switcher?.ModeCode ?? 0,
            OnAltTab = shift => _switcher?.AltTab(shift),
            OnAltRelease = () => _switcher?.Commit(),
            OnWinTab = () => _switcher?.ToggleOverview(),
            OnSwitcherKey = (vk, shift) => _switcher?.Nav(vk, shift)
        };
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
