using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace DockIsland;

public class AppSettings
{
    public bool ShowSeconds { get; set; } = true;
    public bool ExpandOnHover { get; set; } = true;
    public bool ExpandOnTrackChange { get; set; } = true;
    public bool ExpandOnNotification { get; set; } = true;
    public bool DockEnabled { get; set; } = true;
    public bool HideTaskbar { get; set; } = true;
    public bool SelectionPopup { get; set; } = true;    // popup Copiar/Colar ao selecionar texto
    public bool SelectionCompat { get; set; } = false;  // modo compatível (usa Ctrl+C) para programas sem UI Automation
    public int DockSize { get; set; } = 1;   // 0=P 1=M 2=G 3=GG
    public bool LightTheme { get; set; } = false;   // tema claro
    public string Accent { get; set; } = "#0A84FF";  // cor de destaque
}

public class NotifItem
{
    public string App { get; set; }
    public string Title { get; set; }
    public string Body { get; set; }
    public string Time { get; set; }
    public Brush IconBrush { get; set; }
    public string Initial { get; set; }
    public bool IsMusic { get; set; }   // notificação de "tocando agora": só a mais recente é mantida
    public string Target { get; set; }  // o que abrir ao clicar: AppUserModelId (apps) ou caminho do programa
    public uint WinId { get; set; }     // ID da notificação no Windows (0 = não veio do Windows): usado para removê-la de lá ao limpar
    public string Meta => string.IsNullOrWhiteSpace(App) ? Time : $"{App} · {Time}";
}

// Cartão de um aplicativo na aba Notificações: agrupa as notificações dele
public class NotifGroup
{
    public string App { get; set; }
    public Brush IconBrush { get; set; }
    public string Initial { get; set; }
    public string Target { get; set; }
    public List<NotifItem> Items { get; set; } = new();
    public string CountText => Items.Count.ToString();
}

public class ClipItem
{
    public string Hash { get; set; }
    public string Text { get; set; }          // texto copiado (null se for imagem)
    public byte[] PngBytes { get; set; }      // imagem original em PNG (null se for texto)
    public BitmapSource Thumb { get; set; }   // miniatura
    public int Width { get; set; }
    public int Height { get; set; }
    public string Time { get; set; }

    public bool IsImage => PngBytes != null;
    public string Subtitle => IsImage ? $"{Width} × {Height}" : "";
    public string Preview
    {
        get
        {
            if (IsImage) return "Captura de tela";
            var one = Text.Replace("\r", " ").Replace("\n", " ").Trim();
            return one.Length > 140 ? one.Substring(0, 140) : one;
        }
    }
}

public partial class MainWindow : Window
{
    // ---------- Dimensões da ilha ----------
    const double CollapsedW = 330, CollapsedH = 38, CollapsedR = 19;   // 330: cabe data + hora + ícones
    const double BottomPad = 12; // padding de baixo da ilha expandida (abaixo dos botões)
    const double Pad = 24;      // padding externo da ilha: igual nos 4 lados
    const double TabGap = 26;     // espaço entre a barra de abas e a página
    const double BottomGap = 5;   // espaço entre a página e os botões de baixo
    const double Gap = 10;      // espaço entre os itens, em todas as abas
    const double TabsH = 28;    // altura da barra de abas
    const double ExpandedR = 36;
    const double BaseViewH = 259;               // área expandida: abas + Gap + página (172) + Gap + botões
    const double BaseViewW = 500;               // largura base da área expandida
    const double ExpandedW = BaseViewW + 2 * Pad, ExpandedH = BaseViewH + Pad + BottomPad;
    const double NotifRowH = 42;                // altura de cada notificação dentro do cartão do app
    const double NotifGroupChromeH = 54;        // cartão: padding (24) + cabeçalho (30)
    const double NotifListMaxH = 330;           // altura máxima da lista (depois disso rola)
    const double NotifExtraW = 70;              // aba Notificações é mais larga
    const double NotifHeaderH = 32;             // cabeçalho (22) + Gap
    const int ClipTallCount = 5;                // acima disso a aba Clipboard fica mais alta
    const double ClipExtra = 170;               // altura extra da aba Clipboard com muitos itens
    const double SettingsExtra = 66;            // as seções Geral e Ilha dos Ajustes são um pouco mais altas
    const double SystemExtra = 250;             // a aba Sistema é mais alta (inclui a faixa da bandeja)...
    const double SystemExtraW = 130;            // ...e mais larga
    const double NotesExtra = 90;               // a aba Notas é mais alta (lista de notas)
    const double SearchExtra = 210;             // a aba Busca é mais alta (lista de resultados)...
    const double SearchExtraW = 190;            // ...e um pouco mais larga

    // ---------- Animação (0 = pílula, 1 = expandida) ----------
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.Register(nameof(Progress), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, (d, e) => ((MainWindow)d).ApplyProgress((double)e.NewValue)));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    // Altura extra em pixels (0 = altura normal). Depende da aba (ver TargetExtra).
    public static readonly DependencyProperty ExtraProperty =
        DependencyProperty.Register(nameof(Extra), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, (d, e) => { var w = (MainWindow)d; w.ApplyProgress(w.Progress); }));

    public double Extra
    {
        get => (double)GetValue(ExtraProperty);
        set => SetValue(ExtraProperty, value);
    }

    // Largura extra em pixels da ilha expandida (aba Sistema é mais larga)
    public static readonly DependencyProperty ExtraWProperty =
        DependencyProperty.Register(nameof(ExtraW), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, (d, e) => { var w = (MainWindow)d; w.ApplyProgress(w.Progress); }));

    public double ExtraW
    {
        get => (double)GetValue(ExtraWProperty);
        set => SetValue(ExtraWProperty, value);
    }

    // Largura da pílula recolhida: 330 com mídia, só data + hora quando não há player aberto
    public static readonly DependencyProperty BaseWProperty =
        DependencyProperty.Register(nameof(BaseW), typeof(double), typeof(MainWindow),
            new PropertyMetadata(CollapsedW, (d, e) => { var w = (MainWindow)d; w.ApplyProgress(w.Progress); }));

    public double BaseW
    {
        get => (double)GetValue(BaseWProperty);
        set => SetValue(BaseWProperty, value);
    }

    // 0 = pílula normal, 1 = pílula alargada mostrando a faixa que acabou de trocar
    public static readonly DependencyProperty TrackPeekProperty =
        DependencyProperty.Register(nameof(TrackPeek), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, (d, e) => { var w = (MainWindow)d; w.ApplyProgress(w.Progress); }));

    public double TrackPeek
    {
        get => (double)GetValue(TrackPeekProperty);
        set => SetValue(TrackPeekProperty, value);
    }

    static double Lerp(double a, double b, double t) => a + (b - a) * t;
    static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    void ApplyProgress(double p)
    {
        double extra = Extra, extraW = ExtraW, tp = TrackPeek, vp = VolPeek, jp = JobPeek, up = UsbPeek;
        // faixa nova: a pílula vai até onde termina o texto (_peekW); sem medida, usa o padrão
        double peekTarget = _peekW > 0 ? _peekW : BaseW;
        double baseJ = Lerp(BaseW, JobW, jp);   // progresso de cópia/download na pílula
        double trackW = Lerp(baseJ, peekTarget, tp);
        double usbW = Lerp(trackW, _usbW, up);   // pendrive na pílula
        double pillW = Lerp(usbW, VolW, vp);
        Island.Width = Math.Max(pillW, Lerp(pillW, ExpandedW + extraW, p));
        Island.Height = Math.Max(CollapsedH, Lerp(CollapsedH, ExpandedH + extra, p));
        ExpandedView.Height = BaseViewH + extra;
        ExpandedView.Width = BaseViewW + extraW;
        Island.CornerRadius = new CornerRadius(Math.Max(CollapsedR, Lerp(CollapsedR, ExpandedR, p)));
        double notExpanded = Clamp01(1 - p * 3);
        double notVol = Clamp01(1 - vp * 2.5);
        double notUsb = Clamp01(1 - up * 2.5);
        CollapsedView.Opacity = notExpanded * Clamp01(1 - tp * 2.5) * notVol * Clamp01(1 - jp * 2.5) * notUsb;
        TrackPeekView.Opacity = notExpanded * Clamp01((tp - 0.35) / 0.65) * notVol * notUsb;
        VolPeekView.Opacity = notExpanded * Clamp01((vp - 0.35) / 0.65);
        JobPeekView.Opacity = notExpanded * Clamp01((jp - 0.35) / 0.65) * Clamp01(1 - tp * 2.5) * notVol * notUsb;
        UsbPeekView.Opacity = notExpanded * Clamp01((up - 0.35) / 0.65) * notVol;
        UsbPeekView.IsHitTestVisible = p < 0.05 && up > 0.7;
        ExpandedView.Opacity = Clamp01((p - 0.45) / 0.55);
        ExpandedView.IsHitTestVisible = p > 0.7;
    }

    void AnimateTo(double target, IEasingFunction ease, int ms)
    {
        BeginAnimation(ProgressProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease
        });
    }

    void Expand()
    {
        HideTrackPill();
        HideVolumePill();
        HideUsbPill();
        AnimateTo(1, new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }, 480);
        if (_page == 4) RefreshSystem();
    }

    void Collapse()
    {
        HideConfirm();
        AnimateTo(0, new CubicEase { EasingMode = EasingMode.EaseInOut }, 320);
    }

    // ---------- Estado ----------
    readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    readonly DispatcherTimer _leaveTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly DispatcherTimer _trackTimer = new() { Interval = TimeSpan.FromMilliseconds(3500) };
    readonly ObservableCollection<NotifItem> _notifs = new();
    readonly ObservableCollection<NotifGroup> _groups = new();
    readonly ObservableCollection<ClipItem> _clips = new();
    readonly Border[] _eq;
    int _unread;
    AppSettings _settings = new();
    SelectionController _selection;
    public AppSettings Settings => _settings;
    string _lastTrackKey;
    static readonly CultureInfo PtBr = new("pt-BR");

    public MainWindow()
    {
        InitializeComponent();

        _eq = new[] { EqBar0, EqBar1, EqBar2, EqBar3 };

        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
        ApplyProgress(0);

        NotifList.ItemsSource = _groups;
        ClipList.ItemsSource = _clips;
        InitSearch();
        InitNotes();
        _notifs.CollectionChanged += (_, __) =>
        {
            NoNotifText.Visibility = _notifs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RebuildGroups();
            NotifBadges.Update(_notifs);   // selos nos ícones do dock e do lançador
            if (_page == 1) UpdateExtra();   // a ilha cresce/encolhe conforme o conteúdo (até o máximo)
            UpdateNotifMore();
        };
        _clips.CollectionChanged += (_, __) =>
        {
            NoClipText.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_page == 2) UpdateExtra();   // com mais de ClipTallCount itens a área fica mais alta
        };
        // Lista de arquivos e histórico do clipboard (%AppData%\DockIsland) carregam logo depois da
        // primeira pintura: a ilha aparece antes de ler/decodificar esses arquivos.
        Stage(App.Boot ? 6000 : 300, () => { InitShelf(); InitClipStore(); });
        Stage(App.Boot ? 14000 : 3000, () => SensorHelper.StartTaskIfEnabled());   // helper elevado de temperaturas: por último

        _settings = LoadSettings();
        SwAuto.IsChecked = IsAutoStart();
        if (SwAuto.IsChecked == true) { RestoreStartupDelay(); EnsureAutoStartArg(); }
        SwSeconds.IsChecked = _settings.ShowSeconds;
        SwHover.IsChecked = _settings.ExpandOnHover;
        SwTrack.IsChecked = _settings.ExpandOnTrackChange;
        SwNotif.IsChecked = _settings.ExpandOnNotification;
        SwDock.IsChecked = _settings.DockEnabled;
        SwTaskbar.IsChecked = _settings.HideTaskbar;
        SwSelPopup.IsChecked = _settings.SelectionPopup;
        SwSelCompat.IsChecked = _settings.SelectionCompat;
        SyncTempSwitch();
        InitThemeUi();
        _selection = new SelectionController(ms => _ignoreUntil = DateTime.UtcNow.AddMilliseconds(ms));
        _selection.Apply(_settings.SelectionPopup, _settings.SelectionCompat);
        ApplySettingsTab();
        UpdateDockSizeButtons();
        SelectPage(0);
        UpdateClock();
        InitSystemArea();
        InitVolumePill();
        InitWinKeys();   // tecla Windows = lançador; Win+S = busca da ilha
        // monitores em segundo plano (download/cópia, Bluetooth, pendrive): entram um de cada vez, depois do boot
        Stage(App.Boot ? 5000 : 600, InitJobs);
        Stage(App.Boot ? 7000 : 900, InitBtPill);    // dispositivo Bluetooth conectado: nome na pílula
        Stage(App.Boot ? 9000 : 1200, InitUsb);       // pendrive conectado: nome + ejetar na pílula

        _clock.Tick += (_, __) => { UpdateClock(); UpdateTimeline(); };
        _clock.Start();

        _leaveTimer.Tick += (_, __) =>
        {
            _leaveTimer.Stop();
            if (!Island.IsMouseOver && !_menuOpen && !_shelfDragOut && !SearchHolds) Collapse();
        };
        _trackTimer.Tick += (_, __) => HideTrackPill();
        _peekTimer.Tick += (_, __) =>
        {
            _peekTimer.Stop();
            if (!Island.IsMouseOver && !_menuOpen && !_shelfDragOut && !SearchHolds) Collapse();
        };

        Loaded += async (_, __) =>
        {
            _ = PipeServerAsync();
            if (App.Boot) await Task.Delay(1500);   // deixa a área de trabalho do Windows terminar de carregar
            await InitMediaAsync();
            if (App.Boot) await Task.Delay(2500);
            await InitNotificationsAsync();
        };
    }

    // =====================================================
    //  Janela: não rouba foco, some do Alt+Tab, ouve clipboard
    // =====================================================
    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000;
    const int WS_EX_TOOLWINDOW = 0x00000080;
    const int WM_CLIPBOARDUPDATE = 0x031D;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern uint ExtractIconEx(string file, int index, IntPtr[] large, IntPtr[] small, uint count);
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr hIcon);

    HwndSource _src;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var h = new WindowInteropHelper(this).Handle;
        SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        _src = HwndSource.FromHwnd(h);
        _src.AddHook(WndProc);
        AddClipboardFormatListener(h);
    }

    protected override void OnClosed(EventArgs e)
    {
        try { RemoveClipboardFormatListener(new WindowInteropHelper(this).Handle); } catch { }
        FlushClips();
        FlushNotes();
        ShutdownTemps();
        ShutdownVolumePill();
        ShutdownWinKeys();
        _selection?.Dispose();
        base.OnClosed(e);
    }

    // =====================================================
    //  Ilha sempre por cima (inclusive do lançador de apps)
    // =====================================================
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    const uint SWP_NOSIZE = 0x0001, SWP_NOMOVE = 0x0002, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

    // Reposiciona a ilha no topo da pilha de janelas "sempre na frente", sem roubar o foco
    public void KeepOnTop()
    {
        try
        {
            var h = new WindowInteropHelper(this).Handle;
            if (h != IntPtr.Zero)
                SetWindowPos(h, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        }
        catch { }
    }

    // =====================================================
    //  Roda do mouse na ilha aberta: troca de aba
    // =====================================================
    static readonly int[] TabOrder = { 0, 2, 7, 5, 4, 1 };   // mesma ordem visual das abas: Música, Clipboard, Notas, Arquivos, Sistema, Notificações
    int _lastWheelTick;

    void Island_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // só com a ilha aberta e sem a confirmação de desligar na frente
        if (Progress < 0.9 || _confirmShown) return;

        if (_page == 6) return;   // busca: a roda só rola a lista de resultados

        var src = e.OriginalSource as DependencyObject;

        // o cartão de volume já usa a roda para ajustar o volume
        if (page4Volume(src)) return;

        // listas com rolagem própria (notificações / clipboard) continuam rolando normalmente
        if (_page == 1 && NotifScroll.ScrollableHeight > 0 && IsInside(src, NotifScroll)) return;
        if (_page == 2 && IsInsideScrollable(src, ClipList)) return;
        if (_page == 5 && IsInsideScrollable(src, ShelfList)) return;
        if (_page == 7 && IsInsideScrollable(src, NoteList)) return;

        // evita pular várias abas de uma vez (touchpad / roda livre)
        int now = Environment.TickCount;
        if (unchecked(now - _lastWheelTick) < 220) { e.Handled = true; return; }
        _lastWheelTick = now;

        int idx = Array.IndexOf(TabOrder, _page);
        int next;
        if (idx < 0) next = 0;                                   // estava nos Ajustes: volta para a primeira aba
        else next = Math.Clamp(idx + (e.Delta < 0 ? 1 : -1), 0, TabOrder.Length - 1);
        if (next == idx) { e.Handled = true; return; }

        SelectPage(TabOrder[next]);
        e.Handled = true;
    }

    bool page4Volume(DependencyObject src) => _page == 4 && IsInside(src, VolCard);

    static bool IsInside(DependencyObject src, DependencyObject container)
    {
        while (src != null)
        {
            if (ReferenceEquals(src, container)) return true;
            src = src is Visual || src is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(src)
                : LogicalTreeHelper.GetParent(src);
        }
        return false;
    }

    static bool IsInsideScrollable(DependencyObject src, DependencyObject list)
    {
        if (!IsInside(src, list)) return false;
        var sv = FindScrollViewer(list);
        return sv != null && sv.ScrollableHeight > 0;
    }

    static ScrollViewer FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var r = FindScrollViewer(VisualTreeHelper.GetChild(d, i));
            if (r != null) return r;
        }
        return null;
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) OnClipboardChanged();
        else if (msg == WM_DEVICECHANGE) Dispatcher.BeginInvoke(new Action(ScanUsb));   // pendrive plugado/removido
        return IntPtr.Zero;
    }

    // =====================================================
    //  Hover / expandir / recolher
    // =====================================================
    // Verdadeiro se o mouse está sobre a pílula original (e não sobre as laterais da faixa alargada)
    bool OverCollapsedPill(MouseEventArgs e)
    {
        double x = e.GetPosition(Island).X, w = Island.ActualWidth;
        return x >= (w - BaseW) / 2 && x <= (w + BaseW) / 2;
    }

    void Island_MouseEnter(object sender, MouseEventArgs e)
    {
        // faixa aparecendo na pílula: se foi ela que "alcançou" o cursor ao alargar, não expande
        if ((TrackPeek > 0.02 || VolPeek > 0.02 || JobPeek > 0.02) && Progress < 0.05 && !OverCollapsedPill(e)) return;
        // pendrive na pílula: passar o mouse não expande (o botão de ejetar precisa ficar ao alcance)
        if (UsbPeek > 0.02 && Progress < 0.05) return;
        _leaveTimer.Stop();
        _peekTimer.Stop();
        if (_settings.ExpandOnHover) Expand();
    }

    // Quando "expandir ao passar o mouse" está desligado, expande ao clicar
    void Island_Click(object sender, MouseButtonEventArgs e)
    {
        if ((_settings.ExpandOnHover && UsbPeek <= 0.02) || Progress > 0.5) return;
        _leaveTimer.Stop();
        _peekTimer.Stop();
        Expand();
    }

    void Island_MouseLeave(object sender, MouseEventArgs e)
    {
        // tirou o mouse da ilha: cancela a pergunta. Ignora a saída causada pela própria ilha
        // encolhendo para o tamanho da aba Música logo depois de abrir a pergunta.
        if (unchecked(Environment.TickCount - _confirmOpenedTick) > ConfirmGraceMs) HideConfirm();
        _leaveTimer.Stop();
        _leaveTimer.Start();
    }

    // Faixa nova: a pílula só alarga para os lados, sem expandir a ilha inteira
    // Largura da pílula da faixa = capa + texto (título ou artista, o que for maior)
    double _peekW;

    // Mede o texto direto com FormattedText (não depende do layout do WPF), então a pílula
    // termina exatamente onde o maior texto (título ou artista) termina.
    double MeasureTextW(TextBlock tb)
    {
        if (string.IsNullOrEmpty(tb.Text)) return 0;
        var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
            tb.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        return ft.WidthIncludingTrailingWhitespace;
    }

    void MeasurePeekWidth()
    {
        double text = Math.Max(MeasureTextW(PeekTitle), MeasureTextW(PeekArtist));
        // 9 (margem) + 26 (capa) + 10 (espaço) + texto + 14 (margem) + 2 (arredondamento)
        _peekW = Math.Clamp(Math.Ceiling(9 + 26 + 10 + text + 14 + 2), 120, 460);
        ApplyProgress(Progress);
    }

    void ShowTrackPill(int ms = 3500)
    {
        if (Progress > 0.05) return;   // ilha já expandida: a aba Música já mostra a faixa
        if (VolPeek > 0.02) return;    // volume na pílula tem prioridade (dura só ~1,5 s)
        if (UsbPeek > 0.02) return;    // pendrive na pílula também
        MeasurePeekWidth();
        _trackTimer.Stop();
        _trackTimer.Interval = TimeSpan.FromMilliseconds(ms);
        // sem BackEase: ele ultrapassa o alvo e a pílula passaria do fim do texto
        BeginAnimation(TrackPeekProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
        _trackTimer.Start();
    }

    void HideTrackPill()
    {
        _trackTimer.Stop();
        if (TrackPeek <= 0) { _peekIsNotif = false; return; }
        var hide = new DoubleAnimation(0, TimeSpan.FromMilliseconds(320))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        hide.Completed += (_, __) => { if (TrackPeek <= 0.001) _peekIsNotif = false; };
        BeginAnimation(TrackPeekProperty, hide);
    }

    // ---------- Notificação na pílula (mesma faixa alargada da troca de música) ----------
    bool _peekIsNotif;   // a faixa da pílula está mostrando uma notificação (a música não sobrescreve)

    static string OneLine(string t) =>
        string.IsNullOrWhiteSpace(t) ? "" : System.Text.RegularExpressions.Regex.Replace(t, @"\s+", " ").Trim();

    // Visual da faixa para música: capa + nota musical
    void SetPeekMusicLook()
    {
        PeekIconBox.Background = new SolidColorBrush(Color.FromRgb(0x1C, 0x1C, 0x1E));
        PeekGlyph.Text = "♫";
        PeekGlyph.FontFamily = new FontFamily("Segoe UI Symbol");
        PeekGlyph.FontSize = 13;
        PeekGlyph.FontWeight = FontWeights.Normal;
        PeekGlyph.Foreground = Theme.W(0x40);
    }

    void ShowNotifPill(string app, string title, string body, ImageSource icon, Brush fallback, string initial)
    {
        if (Progress > 0.05) return;   // ilha aberta: a lista da aba Notificações já mostra
        if (VolPeek > 0.02) return;    // volume na pílula tem prioridade

        _peekIsNotif = true;
        title = OneLine(title); body = OneLine(body); app = OneLine(app);
        if (title.Length > 0) { PeekTitle.Text = title; PeekArtist.Text = body.Length > 0 ? body : app; }
        else { PeekTitle.Text = app; PeekArtist.Text = body; }

        // ícone do aplicativo (ou inicial colorida, se o app não tem ícone)
        if (icon != null)
        {
            PeekIconBox.Background = Brushes.Transparent;
            PeekGlyph.Text = "";
            PeekArtBrush.ImageSource = icon;
        }
        else
        {
            PeekArtBrush.ImageSource = null;
            PeekIconBox.Background = fallback ?? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3C));
            PeekGlyph.Text = initial ?? "•";
            PeekGlyph.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
            PeekGlyph.FontSize = 13;
            PeekGlyph.FontWeight = FontWeights.Bold;
            PeekGlyph.Foreground = Theme.Fg;
        }

        ShowTrackPill(4500);
    }

    void Peek(int page)
    {
        SelectPage(page);
        Expand();
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    // =====================================================
    //  Abas
    // =====================================================
    void Tab_Click(object sender, RoutedEventArgs e) =>
        SelectPage(int.Parse((string)((Button)sender).Tag));

    static Brush TabActiveBg => Theme.TabActive;     // cor de destaque translúcida, combina com o vidro
    static Brush TabInactiveFg => Theme.TabInactive;

    int _page;   // 0 música, 1 notificações, 2 clipboard, 3 ajustes, 4 sistema, 5 arquivos, 6 busca, 7 notas

    // Altura extra (em pixels) que a aba atual pede além da altura base
    double TargetExtra()
    {
        if (_confirmShown) return 0;   // confirmação (desligar/reiniciar/lixeira): sempre do tamanho da aba Música
        switch (_page)
        {
            case 1:
                double list = 0;
                foreach (var g in _groups) list += NotifGroupChromeH + g.Items.Count * NotifRowH + Gap;
                if (list > 0) list -= Gap;                         // sem o espaço depois do último
                list = Math.Min(list, NotifListMaxH);
                return Math.Max(0, NotifHeaderH + list - (BaseViewH - 2 * TabsH - TabGap - BottomGap));
            case 2:
                return _clips.Count > ClipTallCount ? ClipExtra : 0;
            case 3:
                return _setTab == 0 ? SettingsExtra + 108 : _setTab == 1 ? SettingsExtra : 0;
            case 4:
                return SystemExtra;
            case 6:
                return SearchExtra;
            case 7:
                return NotesExtra;
            default:
                return 0;
        }
    }

    void UpdateExtra()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        BeginAnimation(ExtraProperty, new DoubleAnimation(TargetExtra(), TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        double targetW = _confirmShown ? 0 : _page == 4 ? SystemExtraW : _page == 1 ? NotifExtraW : _page == 6 ? SearchExtraW : 0;
        BeginAnimation(ExtraWProperty, new DoubleAnimation(targetW, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    void SelectPage(int i)
    {
        bool wasSearch = _page == 6;
        bool wasNotes = _page == 7;
        _page = i;
        PageMusic.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
        PagePanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
        PageClip.Visibility = i == 2 ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = i == 3 ? Visibility.Visible : Visibility.Collapsed;
        PageSystem.Visibility = i == 4 ? Visibility.Visible : Visibility.Collapsed;
        PageShelf.Visibility = i == 5 ? Visibility.Visible : Visibility.Collapsed;
        PageSearch.Visibility = i == 6 ? Visibility.Visible : Visibility.Collapsed;
        PageNotes.Visibility = i == 7 ? Visibility.Visible : Visibility.Collapsed;
        if (i == 6) EnterSearch();
        else if (wasSearch) LeaveSearch();
        if (i == 7) EnterNotes();
        else if (wasNotes) LeaveNotes();
        if (i == 5) PruneShelf();
        UpdateExtra();
        GearBtn.Foreground = i == 3 ? Theme.OnAccent : TabInactiveFg;
        GearBtn.Background = i == 3 ? TabActiveBg : Brushes.Transparent;
        SearchBtn.Foreground = i == 6 ? Theme.OnAccent : TabInactiveFg;
        SearchBtn.Background = i == 6 ? TabActiveBg : Brushes.Transparent;

        var tabs = new[] { (TabMusic, 0), (TabClip, 2), (TabNotes, 7), (TabShelf, 5), (TabSys, 4), (TabPanel, 1) };
        foreach (var (btn, page) in tabs)
        {
            btn.Background = page == i ? TabActiveBg : Brushes.Transparent;
            btn.Foreground = page == i ? Theme.OnAccent : TabInactiveFg;
        }

        if (i == 1)
        {
            _unread = 0;
            BadgeCount.Text = "0";
            Badge.Visibility = Visibility.Collapsed;
            UpdatePillWidth();
            NotifScroll.ScrollToTop();
            UpdateNotifMore();
        }
        if (i == 4) OnSystemPageShown();
    }

    // =====================================================
    //  Relógio e data
    // =====================================================
    void UpdateClock()
    {
        var now = DateTime.Now;
        MiniTime.Text = now.ToString(_settings.ShowSeconds ? "HH:mm:ss" : "HH:mm");
        // data na pílula, ex.: "Sáb, 7 de nov"
        var wd = now.ToString("ddd", PtBr).TrimEnd('.');
        MiniDate.Text = $"{char.ToUpper(wd[0], PtBr)}{wd.Substring(1)}, {now.Day} de {now.ToString("MMM", PtBr).TrimEnd('.')}";
        var d = now.ToString("dddd, d 'de' MMMM", PtBr);
        DateText.Text = char.ToUpper(d[0], PtBr) + d.Substring(1);
        // widget de hora da aba Sistema
        ClockBig.Text = now.ToString("HH:mm");
        ClockSec.Text = _settings.ShowSeconds ? now.ToString(":ss") : "";
        ClockDate.Text = DateText.Text;
        UpdatePillWidth();
    }

    // Sem player de música: a pílula fica só do tamanho da data + hora.
    // Com player (Spotify, YouTube...): largura normal, com espaço para equalizador e capa.
    double _pillTarget = double.NaN;

    void UpdatePillWidth()
    {
        // largura = margens + bloco da esquerda (hora no pior caso) + bloco da direita
        // (divisor, bolinha, equalizador, capa — só o que estiver visível; cada item já traz seu espaço de 12)
        // A hora tem largura FIXA (a do pior caso, dígitos largos): assim, quando os dígitos mudam a cada
        // segundo, nada ao lado dela (divisor, data, ícones) se mexe e a pílula não "pisca" de tamanho.
        var ft = new FormattedText(_settings.ShowSeconds ? "88:88:88" : "88:88",
            CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(MiniTime.FontFamily, MiniTime.FontStyle, MiniTime.FontWeight, MiniTime.FontStretch),
            MiniTime.FontSize, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        double timeW = Math.Ceiling(ft.WidthIncludingTrailingWhitespace) + 1;
        if (Math.Abs(MiniTime.Width - timeW) > 0.01 || double.IsNaN(MiniTime.Width)) MiniTime.Width = timeW;

        // largura = margens + bloco da esquerda + bloco da direita
        // (divisor, equalizador, capa, sino — só o que estiver visível; cada item já traz seu espaço de 12)
        var inf = new Size(double.PositiveInfinity, double.PositiveInfinity);
        MiniLeft.Measure(inf);
        MiniRight.Measure(inf);

        double target = Math.Ceiling(36 + MiniLeft.DesiredSize.Width + MiniRight.DesiredSize.Width);

        if (!double.IsNaN(_pillTarget) && Math.Abs(_pillTarget - target) < 0.5) return;
        bool first = double.IsNaN(_pillTarget);
        _pillTarget = target;

        if (first) { BaseW = target; return; }
        BeginAnimation(BaseWProperty, new DoubleAnimation(target, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    // =====================================================
    //  Música (Windows Media Transport Controls)
    // =====================================================
    GlobalSystemMediaTransportControlsSessionManager _mgr;
    GlobalSystemMediaTransportControlsSession _session;
    static Geometry _playGeo, _pauseGeo;

    async Task InitMediaAsync()
    {
        try
        {
            _playGeo = (Geometry)FindResource("PlayGeo");
            _pauseGeo = (Geometry)FindResource("PauseGeo");
            _mgr = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _mgr.CurrentSessionChanged += (_, __) => Dispatcher.BeginInvoke(new Action(AttachSession));
            AttachSession();
        }
        catch { /* sem suporte a mídia */ }
    }

    void AttachSession()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaProps;
            _session.PlaybackInfoChanged -= OnPlayback;
        }

        _session = _mgr?.GetCurrentSession();

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaProps;
            _session.PlaybackInfoChanged += OnPlayback;
        }

        _ = RefreshMediaAsync();
    }

    void OnMediaProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) =>
        Dispatcher.BeginInvoke(new Action(async () => await RefreshMediaAsync()));

    void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) =>
        Dispatcher.BeginInvoke(new Action(async () => await RefreshMediaAsync()));

    async Task RefreshMediaAsync()
    {
        var s = _session;
        MiniSep.Visibility = s != null ? Visibility.Visible : Visibility.Collapsed;
        UpdatePillWidth();
        if (s == null)
        {
            TitleText.Text = "Nada tocando";
            ArtistText.Text = "Abra o Spotify, YouTube, etc.";
            PlayIcon.Data = _playGeo;
            ArtBrush.ImageSource = null;
            MiniArtBrush.ImageSource = null;
            MiniArt.Visibility = Visibility.Collapsed;
            UpdatePillWidth();
            AppIconBox.Visibility = Visibility.Collapsed;
            AppNameText.Text = "";
            SetEqualizer(false);
            _lastTrackKey ??= "";
            ProgFill.Width = 0;
            TimeCur.Text = TimeEnd.Text = "0:00";
            return;
        }

        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var info = s.GetPlaybackInfo();
            bool playing = info.PlaybackStatus ==
                           GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            TitleText.Text = string.IsNullOrWhiteSpace(props.Title) ? "Sem título" : props.Title;
            ArtistText.Text = string.IsNullOrWhiteSpace(props.Artist) ? props.AlbumTitle : props.Artist;
            PlayIcon.Data = playing ? _pauseGeo : _playGeo;
            SetEqualizer(playing);
            ApplyAppInfo(s.SourceAppUserModelId);

            // Trocou de faixa: expande sempre na aba de música
            var trackKey = $"{s.SourceAppUserModelId}|{props.Title}|{props.Artist}";
            bool trackChanged = _lastTrackKey != null && trackKey != _lastTrackKey
                                && !string.IsNullOrWhiteSpace(props.Title) && _settings.ExpandOnTrackChange;
            _lastTrackKey = trackKey;
            bool peekFree = !_peekIsNotif || trackChanged;   // notificação na pílula não é sobrescrita pela música
            if (peekFree)
            {
                _peekIsNotif = false;
                SetPeekMusicLook();
                PeekTitle.Text = TitleText.Text;
                PeekArtist.Text = ArtistText.Text;
            }

            BitmapImage img = null;
            if (props.Thumbnail != null)
            {
                try
                {
                    using var rs = await props.Thumbnail.OpenReadAsync();
                    using var st = rs.AsStreamForRead();
                    var ms = new MemoryStream();
                    await st.CopyToAsync(ms);
                    ms.Position = 0;
                    img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                }
                catch { img = null; }
            }

            ArtBrush.ImageSource = img;
            MiniArtBrush.ImageSource = img;
            if (peekFree) PeekArtBrush.ImageSource = img;
            MiniArt.Visibility = img != null ? Visibility.Visible : Visibility.Collapsed;
            UpdatePillWidth();
            UpdateTimeline();
            if (trackChanged) ShowTrackPill();
        }
        catch { }
    }

    // ---------- Equalizador animado (modo compacto) ----------
    bool _eqOn;

    void SetEqualizer(bool on)
    {
        if (_eqOn == on) return;
        _eqOn = on;
        Eq.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdatePillWidth();
        for (int i = 0; i < _eq.Length; i++)
        {
            var st = (ScaleTransform)_eq[i].RenderTransform;
            if (on)
            {
                st.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.25, 1.0,
                    TimeSpan.FromMilliseconds(360 + i * 110))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                });
            }
            else
            {
                st.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                st.ScaleY = 0.3;
            }
        }
    }

    // ---------- App de origem (ícone + nome) ----------
    readonly Dictionary<string, ImageSource> _iconCache = new();

    static (string name, Color color) DescribeApp(string aumid)
    {
        var a = (aumid ?? "").ToLowerInvariant();
        if (a.Contains("spotify")) return ("Spotify", Color.FromRgb(0x1D, 0xB9, 0x54));
        if (a.Contains("msedge") || a.Contains("edge")) return ("Microsoft Edge", Color.FromRgb(0x0A, 0x84, 0xFF));
        if (a.Contains("chrome")) return ("Google Chrome", Color.FromRgb(0xEA, 0x43, 0x35));
        if (a.Contains("firefox")) return ("Firefox", Color.FromRgb(0xFF, 0x71, 0x39));
        if (a.Contains("brave")) return ("Brave", Color.FromRgb(0xFB, 0x54, 0x2B));
        if (a.Contains("opera")) return ("Opera", Color.FromRgb(0xFF, 0x1B, 0x2D));
        if (a.Contains("vlc")) return ("VLC", Color.FromRgb(0xFF, 0x88, 0x00));
        if (a.Contains("applemusic") || a.Contains("itunes")) return ("Apple Music", Color.FromRgb(0xFF, 0x37, 0x5F));
        if (a.Contains("zune") || a.Contains("mediaplayer")) return ("Media Player", Color.FromRgb(0x0A, 0x84, 0xFF));
        if (a.Contains("tidal")) return ("TIDAL", Color.FromRgb(0x20, 0x20, 0x20));
        if (a.Contains("deezer")) return ("Deezer", Color.FromRgb(0xA2, 0x38, 0xFF));

        var key = AppKey(aumid);
        if (string.IsNullOrEmpty(key)) return ("Player", Color.FromRgb(0x3A, 0x3A, 0x3C));
        var seg = key.Contains('.') ? key.Substring(key.LastIndexOf('.') + 1) : key;
        return (seg, Color.FromRgb(0x3A, 0x3A, 0x3C));
    }

    static string AppKey(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        var id = aumid;
        int bang = id.IndexOf('!');
        if (bang >= 0) id = id.Substring(bang + 1);
        if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id.Substring(0, id.Length - 4);
        return id;
    }

    static IEnumerable<string> ProcessCandidates(string aumid)
    {
        var key = AppKey(aumid);
        if (!string.IsNullOrEmpty(key))
        {
            yield return key;
            if (key.Contains('.')) yield return key.Substring(key.LastIndexOf('.') + 1);
        }
        var a = (aumid ?? "").ToLowerInvariant();
        if (a.Contains("terminal")) yield return "WindowsTerminal";
        if (a.Contains("spotify")) yield return "Spotify";
        if (a.Contains("msedge") || a.Contains("edge")) yield return "msedge";
        if (a.Contains("chrome")) yield return "chrome";
        if (a.Contains("firefox")) yield return "firefox";
        if (a.Contains("brave")) yield return "brave";
        if (a.Contains("opera")) yield return "opera";
        if (a.Contains("vlc")) yield return "vlc";
        if (a.Contains("itunes")) yield return "iTunes";
        if (a.Contains("tidal")) yield return "TIDAL";
        if (a.Contains("deezer")) yield return "Deezer";
    }

    static ImageSource IconFromFile(string path)
    {
        var large = new IntPtr[1];
        try
        {
            if (ExtractIconEx(path, 0, large, null, 1) == 0 || large[0] == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
        finally { if (large[0] != IntPtr.Zero) DestroyIcon(large[0]); }
    }

    // Ícone do app como aparece no lançador: o personalizado (Alterar ícone…) ou o do Windows para o app
    readonly Dictionary<string, ImageSource> _launcherIcons = new();

    ImageSource LauncherIcon(string idOrPath, string name, bool isPath = false)
    {
        try
        {
            string target = string.IsNullOrEmpty(idOrPath) ? null : (isPath ? idOrPath : "shell:AppsFolder\\" + idOrPath);
            string ck = (target ?? "") + "|" + (name ?? "");
            if (_launcherIcons.TryGetValue(ck, out var c)) return c;

            var entry = new AppEntry { Name = name, Target = target };
            var icon = IconStore.CustomFor(entry);
            if (icon == null && target != null) icon = ShellIcon.Get(target, 128);   // grande, para ficar nítido
            _launcherIcons[ck] = icon;
            return icon;
        }
        catch { return null; }
    }

    ImageSource GetAppIcon(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        if (_iconCache.TryGetValue(aumid, out var cached)) return cached;

        foreach (var cand in ProcessCandidates(aumid).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var p in Process.GetProcessesByName(cand))
                {
                    string path = null;
                    try { path = p.MainModule?.FileName; } catch { }
                    if (string.IsNullOrEmpty(path)) continue;
                    var icon = IconFromFile(path);
                    if (icon != null)
                    {
                        _iconCache[aumid] = icon;
                        return icon;
                    }
                }
            }
            catch { }
        }
        return null;
    }

    void ApplyAppInfo(string aumid)
    {
        var (name, color) = DescribeApp(aumid);
        var icon = GetAppIcon(aumid);

        AppIconBox.ToolTip = $"Tocando em {name}";
        AppNameText.Text = name;
        AppIconBox.Visibility = Visibility.Visible;

        if (icon != null)
        {
            AppIconBox.Background = new ImageBrush(icon) { Stretch = Stretch.Uniform };
            AppInitial.Text = "";
        }
        else
        {
            AppIconBox.Background = new SolidColorBrush(color);
            AppInitial.Text = string.IsNullOrEmpty(name) ? "♪" : name.Substring(0, 1).ToUpper();
        }
    }

    // ---------- Linha do tempo ----------
    void UpdateTimeline()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            var tl = s.GetTimelineProperties();
            var info = s.GetPlaybackInfo();
            double dur = (tl.EndTime - tl.StartTime).TotalSeconds;
            if (dur <= 0)
            {
                ProgFill.Width = 0;
                TimeCur.Text = TimeEnd.Text = "--:--";
                return;
            }

            double pos = tl.Position.TotalSeconds;
            if (info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
            pos = Math.Max(0, Math.Min(dur, pos));

            ProgFill.Width = ProgHit.ActualWidth * (pos / dur);
            TimeCur.Text = TimeSpan.FromSeconds(pos).ToString(@"m\:ss");
            TimeEnd.Text = TimeSpan.FromSeconds(dur).ToString(@"m\:ss");
        }
        catch { }
    }

    async void ProgHit_Click(object sender, MouseButtonEventArgs e)
    {
        if (_session == null || ProgHit.ActualWidth <= 0) return;
        try
        {
            var tl = _session.GetTimelineProperties();
            var dur = tl.EndTime - tl.StartTime;
            if (dur.TotalSeconds <= 0) return;
            double r = Math.Clamp(e.GetPosition(ProgHit).X / ProgHit.ActualWidth, 0, 1);
            var target = tl.StartTime + TimeSpan.FromTicks((long)(dur.Ticks * r));
            await _session.TryChangePlaybackPositionAsync(target.Ticks);
            ProgFill.Width = ProgHit.ActualWidth * r;
        }
        catch { }
    }

    async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        try { if (_session != null) await _session.TryTogglePlayPauseAsync(); } catch { }
    }

    async void Next_Click(object sender, RoutedEventArgs e)
    {
        try { if (_session != null) await _session.TrySkipNextAsync(); } catch { }
    }

    async void Prev_Click(object sender, RoutedEventArgs e)
    {
        try { if (_session != null) await _session.TrySkipPreviousAsync(); } catch { }
    }

    // =====================================================
    //  Notificações
    // =====================================================
    UserNotificationListener _listener;
    readonly HashSet<uint> _seen = new();
    bool _firstPoll = true;

    static readonly string[] MusicApps =
    {
        "spotify", "applemusic", "itunes", "zunemusic", "mediaplayer", "tidal", "deezer",
        "amazonmusic", "youtubemusic", "ytmusic", "soundcloud", "vlc", "foobar", "musicbee", "aimp", "winamp"
    };

    // Toast de "tocando agora"? App de música conhecido, ou o app da sessão de mídia atual
    // com o título da faixa no texto (evita confundir notificações de navegador).
    bool IsMusicToast(string aumid, IEnumerable<string> texts)
    {
        var a = (aumid ?? "").ToLowerInvariant();
        if (a.Length == 0) return false;
        if (MusicApps.Any(a.Contains)) return true;

        var src = (_session?.SourceAppUserModelId ?? "").ToLowerInvariant();
        var title = TitleText.Text;
        if (src.Length > 0 && (src == a || src.Contains(a) || a.Contains(src))
            && !string.IsNullOrWhiteSpace(title) && title != "Nada tocando" && title != "Sem título")
            return texts.Any(t => !string.IsNullOrEmpty(t) && t.IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0);
        return false;
    }

    void AddNotification(string app, string title, string body, bool peek = true, ImageSource icon = null,
        bool isMusic = false, string target = null, uint winId = 0)
    {
        Brush brush;
        string initial;
        if (icon != null)
        {
            brush = new ImageBrush(icon) { Stretch = Stretch.Uniform };
            brush.Freeze();
            initial = "";
        }
        else
        {
            brush = new SolidColorBrush(DescribeApp(app).color);
            initial = string.IsNullOrWhiteSpace(app) ? "•" : app.Substring(0, 1).ToUpper();
        }

        // Música: fica só a notificação da faixa atual (as anteriores são removidas)
        if (isMusic)
            foreach (var old in _notifs.Where(n => n.IsMusic).ToList()) _notifs.Remove(old);

        _notifs.Insert(0, new NotifItem
        {
            App = app,
            Title = title,
            Body = body,
            Time = DateTime.Now.ToString("HH:mm"),
            IconBrush = brush,
            Initial = initial,
            IsMusic = isMusic,
            Target = target,
            WinId = winId
        });
        while (_notifs.Count > 20) _notifs.RemoveAt(_notifs.Count - 1);

        // Música entra na lista em silêncio: não expande a ilha nem acende a bolinha
        if (!peek || isMusic) return;

        if (PagePanel.Visibility != Visibility.Visible)
        {
            _unread++;
            BadgeCount.Text = _unread > 9 ? "9+" : _unread.ToString();
            Badge.Visibility = Visibility.Visible;
            UpdatePillWidth();
        }
        // a notificação aparece na própria pílula (alargada, como na troca de faixa), sem expandir a ilha
        if (_settings.ExpandOnNotification) ShowNotifPill(app, title, body, icon, brush, initial);
    }

    // Limpar de verdade: além da lista da ilha, remove as notificações do próprio Windows. Senão elas continuam
    // guardadas lá e voltam para a ilha quando o programa é reiniciado.
    void RemoveFromWindows(IEnumerable<NotifItem> items)
    {
        var l = _listener;
        if (l == null) return;
        foreach (var id in items.Where(i => i.WinId != 0).Select(i => i.WinId).Distinct().ToList())
            try { l.RemoveNotification(id); } catch { }
    }

    void ClearNotifs_Click(object sender, RoutedEventArgs e)
    {
        RemoveFromWindows(_notifs.ToList());
        try { _listener?.ClearNotifications(); } catch { }   // pega também as que passaram do limite da lista
        _notifs.Clear();
    }

    // Clique numa notificação (ou no nome do app): abre o aplicativo dela
    void NotifOpen_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string target || string.IsNullOrWhiteSpace(target)) return;
        e.Handled = true;
        try
        {
            if (File.Exists(target))
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            else
                // AppUserModelId: abre o app (ou traz para frente, se já estiver aberto) — vale para apps da Loja e programas clássicos
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + target) { UseShellExecute = true });
        }
        catch { }
    }

    // Agrupa as notificações por aplicativo (o app com a notificação mais recente fica no topo)
    void RebuildGroups()
    {
        double offset = NotifScroll.VerticalOffset;
        _groups.Clear();
        foreach (var grp in _notifs.GroupBy(n => n.App ?? ""))
        {
            var first = grp.First();
            _groups.Add(new NotifGroup
            {
                App = string.IsNullOrWhiteSpace(grp.Key) ? "Notificações" : grp.Key,
                IconBrush = first.IconBrush,
                Initial = first.Initial,
                Target = grp.Select(x => x.Target).FirstOrDefault(t => !string.IsNullOrEmpty(t)),
                Items = grp.ToList()
            });
        }
        // mantém a posição da rolagem depois de remontar a lista
        Dispatcher.BeginInvoke(new Action(() => NotifScroll.ScrollToVerticalOffset(offset)),
            DispatcherPriority.Loaded);
    }

    void ClearGroup_Click(object sender, RoutedEventArgs e)
    {
        var app = (sender as FrameworkElement)?.Tag as string;
        var group = _notifs.Where(n => (string.IsNullOrWhiteSpace(n.App) ? "Notificações" : n.App) == app).ToList();
        RemoveFromWindows(group);
        foreach (var n in group) _notifs.Remove(n);
    }

    // A roda do mouse rola em passos fixos
    void NotifScroll_Wheel(object sender, MouseWheelEventArgs e)
    {
        NotifScroll.ScrollToVerticalOffset(NotifScroll.VerticalOffset - Math.Sign(e.Delta) * 60);
        e.Handled = true;
    }

    void NotifScroll_Changed(object sender, ScrollChangedEventArgs e) => UpdateNotifMore();

    // Aviso no cabeçalho quando ainda há notificações abaixo da área visível
    void UpdateNotifMore()
    {
        bool more = NotifScroll.ScrollableHeight - NotifScroll.VerticalOffset > 4;
        NotifMoreText.Text = more ? "role para ver mais" : "";
    }

    // Tenta ler as notificações reais do Windows (exige permissão do sistema)
    async Task InitNotificationsAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                _listener = null;
                return;
            }

            // A ilha consegue ler as notificações: agora é seguro esconder o balão do Windows
            ToastGuard.Start();

            await PollNotifsAsync();
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += async (_, __) => await PollNotifsAsync();
            t.Start();
        }
        catch { _listener = null; }
    }

    async Task PollNotifsAsync()
    {
        if (_listener == null) return;
        try
        {
            ToastGuard.Scan();   // apps novos ganham a chave de configuração só depois do 1º toast
            var list = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var n in list.OrderBy(x => x.CreationTime))
            {
                ToastGuard.Suppress(n.AppInfo?.AppUserModelId);
                if (!_seen.Add(n.Id)) continue;
                var binding = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
                if (binding == null) continue;

                var texts = binding.GetTextElements().Select(t => t.Text).ToList();
                var title = texts.FirstOrDefault() ?? "";
                var body = string.Join(" ", texts.Skip(1));
                // mesmo ícone do lançador de apps (inclui ícones personalizados); se não achar, usa o logo do app
                ImageSource icon = LauncherIcon(n.AppInfo?.AppUserModelId, n.AppInfo?.DisplayInfo?.DisplayName);
                if (icon == null)
                try
                {
                    var logoRef = n.AppInfo.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64));
                    using var rs = await logoRef.OpenReadAsync();
                    using var st = rs.AsStreamForRead();
                    var ms = new MemoryStream();
                    await st.CopyToAsync(ms);
                    ms.Position = 0;
                    var img = new BitmapImage();
                    img.BeginInit();
                    img.CacheOption = BitmapCacheOption.OnLoad;
                    img.StreamSource = ms;
                    img.EndInit();
                    img.Freeze();
                    icon = img;
                }
                catch { icon = null; }
                icon ??= GetAppIcon(n.AppInfo?.AppUserModelId);

                bool music = IsMusicToast(n.AppInfo?.AppUserModelId, texts);
                AddNotification(n.AppInfo?.DisplayInfo?.DisplayName ?? "Windows", title, body,
                    peek: !_firstPoll, icon: icon, isMusic: music, target: n.AppInfo?.AppUserModelId, winId: n.Id);
            }
            _firstPoll = false;
        }
        catch { }
    }

    // Recebe notificações via: DockIsland.exe --notify "Título" "Mensagem"
    async Task PipeServerAsync()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(App.MainWindow_PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server);
                var msg = await reader.ReadToEndAsync();
                if (msg == "__quit__") { Application.Current.Shutdown(); return; }   // DockIsland.exe --quit (instalador)
                var p = msg.Split('\t');
                if (p.Length >= 2)
                {
                    var appArg = p[0];
                    bool isPath = false;
                    try { isPath = File.Exists(appArg); } catch { }
                    var icon = LauncherIcon(isPath ? appArg : null, isPath ? Path.GetFileNameWithoutExtension(appArg) : appArg, isPath)
                               ?? (isPath ? IconFromFile(appArg) : GetAppIcon(appArg));
                    var display = isPath ? Path.GetFileNameWithoutExtension(appArg) : appArg;
                    AddNotification(display, p[1], p.Length > 2 ? p[2] : "", icon: icon, target: isPath ? appArg : null);
                }
            }
            catch { await Task.Delay(500); }
        }
    }

    // =====================================================
    //  Histórico da área de transferência (texto + capturas de tela)
    // =====================================================
    const int MaxClips = 10;   // histórico guardado (também depois de fechar o programa)
    const int MaxImages = 10;
    DateTime _ignoreUntil = DateTime.MinValue;

    async void OnClipboardChanged()
    {
        if (DateTime.UtcNow < _ignoreUntil) return; // fomos nós que copiamos

        for (int i = 0; i < 8; i++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text))
                        AddClipItem(new ClipItem
                        {
                            Hash = "T:" + text,
                            Text = text,
                            Time = DateTime.Now.ToString("HH:mm")
                        });
                    return;
                }

                if (Clipboard.ContainsImage())
                {
                    var bmp = Clipboard.GetImage();
                    if (bmp != null)
                    {
                        if (bmp.CanFreeze) bmp.Freeze();
                        var item = await Task.Run(() => BuildImageItem(bmp));
                        AddClipItem(item);
                        return;
                    }
                }
            }
            catch { }

            // A origem (ex.: Ferramenta de Captura) pode ainda estar gravando; tenta de novo.
            await Task.Delay(90);
        }
    }

    static ClipItem BuildImageItem(BitmapSource source)
    {
        // Converte para Bgr32 (descarta canal alfa: prints de tela às vezes vêm com alfa zerado)
        var bmp = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0);
        bmp.Freeze();

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bmp));
        using var ms = new MemoryStream();
        enc.Save(ms);
        var bytes = ms.ToArray();

        double scale = Math.Min(1.0, 180.0 / Math.Max(1, bmp.PixelWidth));
        var thumb = new TransformedBitmap(bmp, new ScaleTransform(scale, scale));
        thumb.Freeze();

        return new ClipItem
        {
            Hash = "I:" + Convert.ToHexString(SHA1.HashData(bytes)),
            PngBytes = bytes,
            Thumb = thumb,
            Width = bmp.PixelWidth,
            Height = bmp.PixelHeight,
            Time = DateTime.Now.ToString("HH:mm")
        };
    }

    void AddClipItem(ClipItem item)
    {
        var existing = _clips.FirstOrDefault(c => c.Hash == item.Hash);
        if (existing != null) _clips.Remove(existing);
        _clips.Insert(0, item);

        // limita imagens (consomem mais memória) e o total
        var imgs = _clips.Where(c => c.IsImage).ToList();
        for (int i = MaxImages; i < imgs.Count; i++) _clips.Remove(imgs[i]);
        while (_clips.Count > MaxClips) _clips.RemoveAt(_clips.Count - 1);
    }

    async void ClipItem_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not ClipItem item) return;

        _ignoreUntil = DateTime.UtcNow.AddMilliseconds(900);
        bool ok = false;
        for (int i = 0; i < 6 && !ok; i++)
        {
            try
            {
                if (item.IsImage)
                {
                    var img = new BitmapImage();
                    using (var ms = new MemoryStream(item.PngBytes))
                    {
                        img.BeginInit();
                        img.CacheOption = BitmapCacheOption.OnLoad;
                        img.StreamSource = ms;
                        img.EndInit();
                    }
                    img.Freeze();
                    Clipboard.SetImage(img);
                }
                else
                {
                    Clipboard.SetText(item.Text);
                }
                ok = true;
            }
            catch { await Task.Delay(70); }
        }

        // sobe o item para o topo da lista
        int idx = _clips.IndexOf(item);
        if (idx > 0) _clips.Move(idx, 0);

        ClipHeader.Text = ok ? "Copiado ✓" : "Não foi possível copiar";
        await Task.Delay(1100);
        ClipHeader.Text = "Área de transferência";
    }

    void ClearClip_Click(object sender, RoutedEventArgs e) => _clips.Clear();

    // =====================================================
    //  Menu de contexto (botão direito na ilha)
    // =====================================================
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    // O Windows espera ~10 s depois do login para abrir os programas da inicialização.
    // Zerar esse atraso (só para o usuário atual) faz a ilha abrir junto com a área de trabalho.
    // (Versões antigas zeravam esse atraso, o que fazia o programa abrir junto com a área de trabalho e
    // travar o início do Windows. Agora o padrão do Windows é devolvido.)
    public static void RestoreStartupDelay()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize", true);
            if (k == null) return;
            if (k.GetValue("StartupDelayInMSec") is int d && d == 0) k.DeleteValue("StartupDelayInMSec", false);
            if (k.GetValue("WaitforIdleState") is int w && w == 0) k.DeleteValue("WaitforIdleState", false);
        }
        catch { }
    }

    // A inicialização automática passa a abrir com --autostart (modo leve, escalonado)
    static void EnsureAutoStartArg()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
            if (k?.GetValue("DockIsland") is string v && !v.Contains("--autostart"))
                k.SetValue("DockIsland", $"\"{Environment.ProcessPath}\" --autostart");
        }
        catch { }
    }

    // Executa depois de um tempo, no fim da fila da interface (não disputa com a pintura da tela)
    public static void Stage(int delayMs, Action a)
    {
        void Run() => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => { try { a(); } catch { } }), DispatcherPriority.Background);
        if (delayMs <= 0) { Run(); return; }
        var t = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(delayMs) };
        t.Tick += (_, _) => { t.Stop(); Run(); };
        t.Start();
    }

    static bool IsAutoStart()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue("DockIsland") != null;
    }

    static void SetAutoStart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, true);
        if (k == null) return;
        if (on) { k.SetValue("DockIsland", $"\"{Environment.ProcessPath}\" --autostart"); RestoreStartupDelay(); }
        else k.DeleteValue("DockIsland", false);
    }

    static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "settings.json");

    static AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            AtomicFile.WriteAllText(SettingsPath, JsonSerializer.Serialize(_settings,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    void Setting_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowSeconds = SwSeconds.IsChecked == true;
        _settings.ExpandOnHover = SwHover.IsChecked == true;
        _settings.ExpandOnTrackChange = SwTrack.IsChecked == true;
        _settings.ExpandOnNotification = SwNotif.IsChecked == true;
        _settings.DockEnabled = SwDock.IsChecked == true;
        _settings.HideTaskbar = SwTaskbar.IsChecked == true;
        _settings.SelectionPopup = SwSelPopup.IsChecked == true;
        _settings.SelectionCompat = SwSelCompat.IsChecked == true;
        _selection?.Apply(_settings.SelectionPopup, _settings.SelectionCompat);
        (Application.Current as App)?.Dock?.ApplySettings(_settings.DockEnabled, _settings.HideTaskbar, _settings.DockSize);
        try { SetAutoStart(SwAuto.IsChecked == true); } catch { }
        SaveSettings();
        UpdateClock();
    }

    // ----- Ajustes: seções Ilha / Dock -----
    int _setTab;

    void SettingsTab_Click(object sender, RoutedEventArgs e)
    {
        _setTab = int.Parse((string)((Button)sender).Tag);
        ApplySettingsTab();
    }

    void ApplySettingsTab()
    {
        SetGeneral.Visibility = _setTab == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetIsland.Visibility = _setTab == 1 ? Visibility.Visible : Visibility.Collapsed;
        SetDock.Visibility = _setTab == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (_page == 3) UpdateExtra();
        foreach (var (btn, i) in new[] { (SetTabGeneral, 0), (SetTabIsland, 1), (SetTabDock, 2) })
        {
            btn.Background = i == _setTab ? TabActiveBg : Brushes.Transparent;
            btn.Foreground = i == _setTab ? Theme.OnAccent : TabInactiveFg;
        }
    }

    void DockSize_Click(object sender, RoutedEventArgs e) => SetDockSize(int.Parse((string)((Button)sender).Tag));

    // Chamado pelos Ajustes e pelo menu do dock
    public void SetDockSize(int size)
    {
        _settings.DockSize = size;
        UpdateDockSizeButtons();
        (Application.Current as App)?.Dock?.ApplySettings(_settings.DockEnabled, _settings.HideTaskbar, _settings.DockSize);
        SaveSettings();
    }

    void UpdateDockSizeButtons()
    {
        foreach (var (btn, i) in new[] { (DockSz0, 0), (DockSz1, 1), (DockSz2, 2), (DockSz3, 3) })
        {
            btn.Background = i == _settings.DockSize ? TabActiveBg : Brushes.Transparent;
            btn.Foreground = i == _settings.DockSize ? Theme.OnAccent : TabInactiveFg;
        }
    }

    // ---------- Desligar / reiniciar ----------
    // A pergunta aparece dentro da própria ilha (sobre o conteúdo expandido).
    bool _confirmShown;
    int _confirmOpenedTick;            // quando a pergunta abriu (a ilha encolhe e o mouse "sai" sem querer)
    const int ConfirmGraceMs = 800;    // tempo em que a saída do mouse é ignorada (animação de 320 ms + folga)
    string _confirmMode = "off";   // "off" desligar | "restart" reiniciar | "trash" esvaziar a lixeira

    void Power_Click(object sender, RoutedEventArgs e) =>
        ShowConfirm((string)((Button)sender).Tag == "restart" ? "restart" : "off");

    void ShowConfirm(string mode)
    {
        _confirmMode = mode;
        _confirmShown = true;
        _confirmOpenedTick = Environment.TickCount;
        switch (mode)
        {
            case "trash":
                ConfirmIcon.Text = "\uE74D";
                ConfirmTitle.Text = "Esvaziar a lixeira?";
                ConfirmSub.Text = "Os itens da lixeira serão apagados de vez e não poderão ser recuperados.";
                ConfirmOkBtn.Content = "Esvaziar";
                break;
            case "sensors":
                ConfirmIcon.Text = "\uE946";
                ConfirmTitle.Text = "Ativar temperaturas?";
                ConfirmSub.Text = "Para ler CPU, placa-mãe e memória, o Windows exige permissão de administrador e o driver PawnIO (instalado se faltar). A permissão é pedida só uma vez.";
                ConfirmOkBtn.Content = "Ativar";
                break;
            case "restart":
                ConfirmIcon.Text = "\uE72C";
                ConfirmTitle.Text = "Reiniciar o computador?";
                ConfirmSub.Text = "Salve seu trabalho antes de continuar. Os programas abertos serão fechados.";
                ConfirmOkBtn.Content = "Reiniciar";
                break;
            default:
                ConfirmIcon.Text = "\uE7E8";
                ConfirmTitle.Text = "Desligar o computador?";
                ConfirmSub.Text = "Salve seu trabalho antes de continuar. Os programas abertos serão fechados.";
                ConfirmOkBtn.Content = "Desligar";
                break;
        }

        bool info = mode == "sensors";   // pergunta informativa: azul em vez de vermelho
        ConfirmIcon.Foreground = info ? SysBlue : SysRed;
        ConfirmIconBox.Background = info ? Frozen(0x26, 0x0A, 0x84, 0xFF) : Frozen(0x26, 0xFF, 0x45, 0x3A);
        ConfirmOkBtn.Background = info ? SysBlue : SysRed;

        _menuOpen = true;   // a ilha não recolhe enquanto a pergunta está aberta
        _leaveTimer.Stop();
        _peekTimer.Stop();

        UpdateExtra();   // ilha assume o tamanho da aba Música

        ConfirmOverlay.Visibility = Visibility.Visible;
        ConfirmOverlay.IsHitTestVisible = true;
        ConfirmOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
    }

    void HideConfirm()
    {
        if (!_confirmShown) return;
        _confirmShown = false;
        _menuOpen = false;
        UpdateExtra();   // volta ao tamanho da aba atual
        ConfirmOverlay.IsHitTestVisible = false;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        anim.Completed += (_, __) => { if (!_confirmShown) ConfirmOverlay.Visibility = Visibility.Collapsed; };
        ConfirmOverlay.BeginAnimation(OpacityProperty, anim);
    }

    void ConfirmCancel_Click(object sender, RoutedEventArgs e)
    {
        HideConfirm();
        SyncTempSwitch();   // cancelou "Ativar temperaturas": o interruptor volta ao estado real
    }

    void ConfirmOk_Click(object sender, RoutedEventArgs e)
    {
        string mode = _confirmMode;
        HideConfirm();
        if (mode == "trash") { EmptyTrash(); return; }
        if (mode == "sensors") { StartSensorSetup(); return; }
        bool restart = mode == "restart";
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", restart ? "/r /t 0" : "/s /t 0")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }

    void Gear_Click(object sender, RoutedEventArgs e) =>
        SelectPage(PageSettings.Visibility == Visibility.Visible ? 0 : 3);

    void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        _leaveTimer.Stop();
        _peekTimer.Stop();
        SelectPage(3);
        Expand();
    }

    void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
