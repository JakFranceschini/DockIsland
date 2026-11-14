using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DockIsland;

// Grade que reparte toda a largura disponível entre as colunas: sem sobra vazia só do lado direito.
// Cada célula tem no mínimo MinCellWidth; o número de colunas é o que couber na largura.
public class FillWrapPanel : Panel
{
    public double MinCellWidth { get; set; } = 116;

    int _cols = 1;
    double _cellW;
    readonly List<double> _rowH = new();

    protected override Size MeasureOverride(Size available)
    {
        int n = InternalChildren.Count;
        _rowH.Clear();
        if (n == 0) return new Size(0, 0);

        if (double.IsInfinity(available.Width))
        {
            _cols = n;
            _cellW = MinCellWidth;
        }
        else
        {
            _cols = Math.Max(1, (int)(available.Width / MinCellWidth));
            _cellW = available.Width / _cols;
        }

        for (int i = 0; i < n; i += _cols)
        {
            double h = 0;
            for (int j = i; j < Math.Min(i + _cols, n); j++)
            {
                InternalChildren[j].Measure(new Size(_cellW, double.PositiveInfinity));
                h = Math.Max(h, InternalChildren[j].DesiredSize.Height);
            }
            _rowH.Add(h);
        }

        double width = double.IsInfinity(available.Width) ? _cellW * n : available.Width;
        return new Size(width, _rowH.Sum());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        int n = InternalChildren.Count;
        double y = 0;
        for (int i = 0, row = 0; i < n; i += _cols, row++)
        {
            double h = row < _rowH.Count ? _rowH[row] : 0;
            for (int j = i; j < Math.Min(i + _cols, n); j++)
                InternalChildren[j].Arrange(new Rect((j - i) * _cellW, y, _cellW, h));
            y += h;
        }
        return finalSize;
    }
}

// Lançador: janela grande no centro da tela com os aplicativos instalados organizados
// em blocos de categorias (criadas pelo usuário), cada bloco com os apps em grade.
public partial class LauncherWindow : Window
{
    readonly ObservableCollection<AppEntry> _apps = new();
    List<AppEntry> _shown = new();       // apps visíveis, na ordem da tela (Enter abre o primeiro)
    bool _menuOpen, _loadingApps, _busy;
    Func<string, string> _nameApply;     // ação do pedido de nome (devolve erro ou null)
    DateTime _appsLoadedAt = DateTime.MinValue;

    // arrastar e soltar
    const string DragFormat = "DI_App";
    Point _dragStart;
    AppEntry _dragApp;
    bool _dragging;
    Border _hotBlock, _hotTile;

    const double BaseWidth = 1040, BaseHeight = 760;
    static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    static Brush BlockBg => Theme.W(0x10);
    static Brush BlockBorder => Theme.W(0x1C);
    static Brush Dim => Theme.W(0x66);
    static Brush HotBorder => Theme.W(0xB0);
    static Brush HotTile => Theme.W(0x40);
    static SolidColorBrush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    // Nome do bloco dos apps sem categoria
    static string OtherName => CategoryStore.All.Count == 0 ? "Todos os aplicativos" : "Sem categoria";

    public bool IsOpen => IsVisible;
    public event Action<string, string> PinRequested;   // nome, destino

    public LauncherWindow()
    {
        InitializeComponent();

        // trocou o tema (claro/escuro): redesenha os blocos, que são montados por código
        Theme.Changed += () => { try { if (_apps != null && _apps.Count > 0) RebuildBlocks(); } catch { } };

        // o ícone do app acompanha o mouse durante o arrasto
        GiveFeedback += (_, __) => DragGhost.Move();

        // a ilha dinâmica fica sempre por cima do lançador: toda vez que o lançador
        // for ativado / clicado / mostrado, a ilha volta para o topo da pilha
        Activated += (_, __) => RaiseIsland();
        PreviewMouseDown += (_, __) => RaiseIsland();
        IsVisibleChanged += (_, __) => Glass.Quiet = IsVisible && !_prewarming;   // sem refresh da ilha/dock (e da captura) com o lançador na tela

        // selos de notificação: atualiza a contagem de cada app
        NotifBadges.Changed += () => { foreach (var a in _apps) a.RefreshNotif(); };

        // ícone trocado (aqui ou no dock): atualiza a grade
        IconStore.Changed += key =>
        {
            foreach (var a in _apps)
                if (a.IconKeys().Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase))) a.RefreshIcon();
        };
    }

    // Coloca a ilha na frente do lançador (sem tirar o foco dele)
    void RaiseIsland()
    {
        if (_prewarming) return;
        void Do() => (Application.Current?.MainWindow as MainWindow)?.KeepOnTop();
        Do();
        // o Windows termina de reordenar as janelas logo após a ativação: repete uma vez, sem atraso visível
        Dispatcher.BeginInvoke(new Action(Do), DispatcherPriority.ApplicationIdle);
    }

    bool _prewarming;
    int _openedAt;

    // Chamado na inicialização: cria a janela, carrega apps e ícones e "aquece" o desenho
    // uma vez, fora da tela, para a primeira abertura não piscar nem demorar
    public async void Prewarm()
    {
        try
        {
            _prewarming = true;
            Opacity = 0;
            ShowActivated = false;
            Left = -20000; Top = -20000;
            Show();
            await EnsureAppsAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Hide();
        }
        catch { }
        finally
        {
            ShowActivated = true;
            _prewarming = false;
        }
    }

    public void Toggle()
    {
        // o próprio clique no botão acabou de fechá-lo (a perda de foco inicia o fade de saída): não reabre
        if (unchecked(Environment.TickCount - _deactHidAt) < 700 && (_closing || !IsVisible)) { }
        else if (_closing) Open();   // clicou de novo durante o fade de saída (sem ter sido pelo foco): reabre
        else if (IsVisible && !_prewarming) Hide();
        else Open();
    }

    int _deactHidAt = -100000;

    // ---- fade de saída: a janela some suavemente em vez de cortar de uma vez ----
    bool _closing;
    const int FadeInMs = 220, FadeOutMs = 180;

    public new void Hide()
    {
        if (_closing) return;
        // sem fade quando não há nada para ver (aquecimento, já invisível, já transparente)
        if (_prewarming || !IsVisible || Opacity <= 0.02) { base.Hide(); return; }

        _closing = true;
        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(FadeOutMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        anim.Completed += (_, _) =>
        {
            if (!_closing) return;   // reaberto no meio do fade
            _closing = false;
            base.Hide();
        };
        BeginAnimation(OpacityProperty, anim);
    }

    public async void Open()
    {
        if (_closing)
        {
            // pediu para abrir enquanto sumia: volta a aparecer a partir de onde está
            _closing = false;
            BeginAnimation(OpacityProperty, new DoubleAnimation(Opacity, 1, TimeSpan.FromMilliseconds(FadeInMs))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
            Activate();
            return;
        }
        if (IsVisible && !_prewarming) return;

        // janela grande, sempre cabendo na tela
        Width = Math.Min(BaseWidth, SystemParameters.PrimaryScreenWidth - 40);
        Height = Math.Min(BaseHeight, SystemParameters.PrimaryScreenHeight - 60);

        // centro exato da tela principal
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;

        Glass.Quiet = true;
        _openedAt = Environment.TickCount;
        bool hadText = !string.IsNullOrEmpty(SearchBox.Text);
        SearchBox.Text = "";                 // (se havia texto, isto já reconstrói os blocos)
        NameOverlay.Visibility = Visibility.Collapsed;
        _nameApply = null;
        if (!hadText) RebuildBlocks();
        BlocksScroll.ScrollToTop();

        // aparece já no lugar, só com um fade rápido (sem animação de escala: ela refazia a
        // sombra da janela inteira a cada quadro e causava a "piscada")
        BeginAnimation(OpacityProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        CardScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        CardScale.ScaleX = CardScale.ScaleY = 1;
        Opacity = 0;
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = (SystemParameters.PrimaryScreenHeight - Height) / 2;
        Glass.Prepare(this);   // foto do fundo já pronta antes de aparecer (sem piscada)

        // Abre fora da tela, espera o WPF desenhar o quadro transparente e só então move para o centro.
        // (uma janela com transparência reaparece por 1 quadro com a imagem antiga, a da última vez em que
        // ficou visível: era isso que piscava a partir da 2ª abertura)
        double cx = Left, cy = Top;
        Left = -20000; Top = -20000;
        Show();
        await WaitFramesAsync(2, 120);   // quadros desenhados de verdade (o Yield sozinho deixava passar 1 quadro antigo, às vezes)
        if (!IsVisible) return;   // foi fechada nesse instante
        Left = cx; Top = cy;
        Activate();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeInMs))
        { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });

        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        await EnsureAppsAsync();
    }

    // Espera o WPF desenhar n quadros (ou o tempo limite): garante que a opacidade 0 já chegou à janela antes de ela aparecer
    static async Task WaitFramesAsync(int n, int timeoutMs)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int left = n;
        EventHandler h = (_, _) => { if (--left <= 0) tcs.TrySetResult(true); };
        System.Windows.Media.CompositionTarget.Rendering += h;
        try { await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs)); }
        finally { System.Windows.Media.CompositionTarget.Rendering -= h; }
    }

    void Window_Deactivated(object sender, EventArgs e)
    {
        // ignora a perda de foco logo ao abrir (o Windows às vezes alterna o foco nesse instante)
        if (unchecked(Environment.TickCount - _openedAt) < 400) return;
        if (IsVisible && !_closing && !_menuOpen && !_busy && !_prewarming) { _deactHidAt = Environment.TickCount; Hide(); }
    }

    void Window_KeyDown(object sender, KeyEventArgs e)
    {
        // pedido de nome aberto: Enter salva, Esc cancela
        if (NameOverlay.Visibility == Visibility.Visible)
        {
            if (e.Key == Key.Escape) { HideNamePrompt(); e.Handled = true; }
            else if (e.Key == Key.Enter) { ConfirmName(); e.Handled = true; }
            return;
        }

        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            if (_shown.FirstOrDefault() is AppEntry first)
            {
                Launch(first.Target);
                (Application.Current?.MainWindow as MainWindow)?.ClearNotifsFor(first);
                Hide();
            }
            e.Handled = true;
        }
    }

    void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        RebuildBlocks();
        BlocksScroll.ScrollToTop();
    }

    static void Launch(string target)
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (File.Exists(target)) psi.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
            Process.Start(psi);
        }
        catch { }
    }

    void App_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AppEntry a) return;
        Launch(a.Target);
        (Application.Current?.MainWindow as MainWindow)?.ClearNotifsFor(a);
        Hide();
    }

    void App_RightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.DataContext is not AppEntry a) return;
        e.Handled = true;
        var items = new List<(string, Action, Brush)>
        {
            ("Abrir", () => { Launch(a.Target); Hide(); }, null),
            ("Fixar no dock", () => PinRequested?.Invoke(a.Name, a.Target), null),
            ("Abrir local do app", () => AppLocation.Open(a.Target, a.Name), null),
            ("Alterar ícone…", () => ChangeIcon(a), null)
        };
        if (IconStore.HasFor(a))
            items.Add(("Restaurar ícone padrão", () => IconStore.RemoveFor(a), null));

        // categorias: mover o app para uma delas, tirar da atual ou criar uma nova
        var current = CategoryStore.Get(a.Target);
        foreach (var c in CategoryStore.All)
        {
            var cat = c;
            if (string.Equals(cat, current, StringComparison.OrdinalIgnoreCase))
                items.Add(($"Remover de “{cat}”", () => { CategoryStore.Assign(a.Target, null); RefreshGroups(); }, null));
            else
                items.Add(($"Mover para “{cat}”", () => { CategoryStore.Assign(a.Target, cat); RefreshGroups(); }, null));
        }
        items.Add(("Nova categoria com este app…", () => PromptNewCategory(a.Target), null));
        items.Add(("Remover do lançador", () => RemoveFromLauncher(a), Red));

        MenuKit.Show(fe, a.Name, a.Icon, items, open => _menuOpen = open);
    }

    void ChangeIcon(AppEntry a)
    {
        _busy = true;   // o diálogo tira o foco da janela, mas ela não deve fechar
        try
        {
            var path = IconStore.Pick();
            if (path != null)
            {
                a.ResolvedExe ??= DockWindow.ResolveExe(a.Target);   // para o dock reconhecer o mesmo app pelo .exe
                IconStore.SetFor(a, path);
            }
        }
        finally
        {
            _busy = false;
            if (IsVisible) Activate();
        }
    }

    // ---------- Arrastar e soltar ----------
    // Arraste um app: solte sobre outro app (troca de posição / entra na categoria dele),
    // sobre um bloco (vai para o fim da categoria) ou sobre o dock (fixa lá).

    void App_PreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is AppEntry a)
        {
            _dragApp = a;
            _dragStart = e.GetPosition(this);
        }
    }

    void App_PreviewUp(object sender, MouseButtonEventArgs e) => _dragApp = null;

    void App_Move(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragApp == null || _dragging) return;
        if (sender is not FrameworkElement fe || !ReferenceEquals(fe.DataContext, _dragApp)) return;

        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        StartDrag(fe, _dragApp);
    }

    void StartDrag(FrameworkElement src, AppEntry app)
    {
        _dragApp = null;
        _dragging = true;
        _busy = true;   // arrastar para o dock tira o foco da janela, mas ela não deve fechar
        try
        {
            var data = new DataObject();
            data.SetData(DragFormat, app.Name + "\u0001" + app.Target);
            DragGhost.Begin(app.Icon, app.Initial);
            DragDrop.DoDragDrop(src, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        catch { }
        finally
        {
            DragGhost.End();
            ClearHot();
            _dragging = false;
            _busy = false;
            if (IsVisible) Activate();
        }
    }

    AppEntry DraggedApp(DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DragFormat) && e.Data.GetData(DragFormat) is string s)
        {
            int i = s.IndexOf('\u0001');
            if (i >= 0)
            {
                var target = s.Substring(i + 1);
                return _apps.FirstOrDefault(a => string.Equals(a.Target, target, StringComparison.OrdinalIgnoreCase));
            }
        }
        return null;
    }

    // Sobe a árvore visual até o cartão de app (Tag = "tile")
    static FrameworkElement FindTile(DependencyObject d)
    {
        while (d != null)
        {
            if (d is FrameworkElement fe && fe.Tag is string s && s == "tile") return fe;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // Rola a lista sozinha quando o mouse chega perto do topo ou do fim
    void Blocks_PreviewDragOver(object sender, DragEventArgs e)
    {
        var p = e.GetPosition(BlocksScroll);
        const double edge = 48;
        if (p.Y < edge) BlocksScroll.ScrollToVerticalOffset(BlocksScroll.VerticalOffset - 18);
        else if (p.Y > BlocksScroll.ActualHeight - edge) BlocksScroll.ScrollToVerticalOffset(BlocksScroll.VerticalOffset + 18);
    }

    void Block_DragOver(Border block, DragEventArgs e)
    {
        e.Handled = true;
        if (!e.Data.GetDataPresent(DragFormat)) { e.Effects = DragDropEffects.None; return; }
        e.Effects = DragDropEffects.Move;

        SetHotBlock(block);
        var tile = FindTile(e.OriginalSource as DependencyObject) as Border;
        if (tile != null && ReferenceEquals(tile.DataContext, DraggedApp(e))) tile = null;   // sobre si mesmo
        SetHotTile(tile);
    }

    void Block_Drop(string category, DragEventArgs e)
    {
        e.Handled = true;
        var app = DraggedApp(e);
        var over = (FindTile(e.OriginalSource as DependencyObject))?.DataContext as AppEntry;
        ClearHot();
        if (app != null) DropApp(app, category, over);
    }

    void SetHotBlock(Border b)
    {
        if (ReferenceEquals(_hotBlock, b)) return;
        if (_hotBlock != null) _hotBlock.BorderBrush = BlockBorder;
        _hotBlock = b;
        if (b != null) b.BorderBrush = HotBorder;
    }

    void SetHotTile(Border t)
    {
        if (ReferenceEquals(_hotTile, t)) return;
        _hotTile?.ClearValue(Border.BackgroundProperty);
        _hotTile = t;
        if (t != null) t.Background = HotTile;
    }

    void ClearHot()
    {
        SetHotBlock(null);
        SetHotTile(null);
    }

    // Apps (visíveis) de uma categoria, na ordem em que aparecem (category == null: sem categoria)
    List<AppEntry> CategoryItems(string category) =>
        _apps.Where(a => !CategoryStore.IsHidden(a.Target))
             .Where(a => category == null
                 ? a.GroupOrder == int.MaxValue
                 : a.GroupOrder != int.MaxValue && string.Equals(a.GroupName, category, StringComparison.OrdinalIgnoreCase))
             .OrderBy(a => CategoryStore.OrderOf(a.Target))
             .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
             .ToList();

    // Coloca o app na categoria; "before" = app sobre o qual foi solto (null = fim da categoria)
    void DropApp(AppEntry app, string category, AppEntry before)
    {
        if (ReferenceEquals(before, app)) return;

        var list = CategoryItems(category);
        int oldIdx = list.FindIndex(a => ReferenceEquals(a, app));
        int beforeOrig = before == null ? -1 : list.FindIndex(a => ReferenceEquals(a, before));

        if (oldIdx >= 0) list.RemoveAt(oldIdx);

        int ti = before == null ? list.Count : list.FindIndex(a => ReferenceEquals(a, before));
        if (ti < 0) ti = list.Count;
        // arrastando para a frente na mesma categoria, o app cai depois do alvo (troca de lugar)
        if (before != null && oldIdx >= 0 && oldIdx < beforeOrig) ti++;
        ti = Math.Min(ti, list.Count);

        list.Insert(ti, app);
        CategoryStore.Assign(app.Target, category);
        CategoryStore.SetOrder(list.Select(a => a.Target));
        RefreshGroups();
    }

    // ---------- Blocos de categorias ----------

    void SetGroup(AppEntry e)
    {
        var c = CategoryStore.Get(e.Target);
        e.GroupName = c ?? OtherName;
        e.GroupOrder = c == null ? int.MaxValue : CategoryStore.IndexOf(c);
    }

    // Reaplica as categorias em todos os apps e redesenha os blocos
    void RefreshGroups()
    {
        foreach (var a in _apps) SetGroup(a);
        RebuildBlocks();
    }

    void RebuildBlocks()
    {
        var q = SearchBox.Text?.Trim();
        bool searching = !string.IsNullOrEmpty(q);

        var visible = _apps
            .Where(a => !CategoryStore.IsHidden(a.Target))
            .Where(a => !searching || a.Name.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0)
            .OrderBy(a => CategoryStore.OrderOf(a.Target))
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _shown = new List<AppEntry>();
        BlocksPanel.Children.Clear();

        // um bloco por categoria, na ordem em que foram criadas (vazias também aparecem)
        foreach (var cat in CategoryStore.All)
        {
            var items = visible.Where(a => a.GroupOrder != int.MaxValue
                && string.Equals(a.GroupName, cat, StringComparison.OrdinalIgnoreCase)).ToList();
            if (searching && items.Count == 0) continue;   // na busca só mostra blocos com resultado
            BlocksPanel.Children.Add(MakeBlock(cat, items, true));
            _shown.AddRange(items);
        }

        var others = visible.Where(a => a.GroupOrder == int.MaxValue).ToList();
        // com categorias, o bloco "Sem categoria" fica sempre visível (mesmo vazio) para receber apps arrastados
        if (others.Count > 0 || (!searching && CategoryStore.All.Count > 0))
        {
            BlocksPanel.Children.Add(MakeBlock(OtherName, others, false));
            _shown.AddRange(others);
        }

        EmptyText.Visibility = _apps.Count > 0 && BlocksPanel.Children.Count == 0
            ? Visibility.Visible : Visibility.Collapsed;
    }

    Border MakeBlock(string title, List<AppEntry> items, bool editable)
    {
        // cabeçalho: nome + quantidade (+ botão ⋯ nas categorias)
        var head = new Grid { Margin = new Thickness(4, 0, 0, 10), Background = Brushes.Transparent };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var name = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        name.Children.Add(new TextBlock
        {
            Text = title, Foreground = Theme.Fg, FontSize = 15.5, FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        name.Children.Add(new TextBlock
        {
            Text = items.Count.ToString(), Foreground = Dim, FontSize = 12.5,
            Margin = new Thickness(10, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center
        });
        head.Children.Add(name);

        if (editable)
        {
            var more = new Border
            {
                Style = (Style)FindResource("PillBtn"),
                Width = 34, Height = 26, CornerRadius = new CornerRadius(13),
                Child = new TextBlock
                {
                    Text = "\uE712", FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 14, Foreground = Theme.Fg,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            more.MouseLeftButtonUp += (_, e) => { e.Handled = true; CategoryMenu(more, title); };
            Grid.SetColumn(more, 1);
            head.Children.Add(more);
            head.MouseRightButtonUp += (_, e) => { e.Handled = true; CategoryMenu(head, title); };
        }

        var body = new StackPanel();
        body.Children.Add(head);

        if (items.Count > 0)
        {
            body.Children.Add(new ItemsControl
            {
                ItemsSource = items,
                ItemTemplate = (DataTemplate)FindResource("AppTemplate"),
                ItemsPanel = (ItemsPanelTemplate)FindResource("GridPanel"),
                Focusable = false
            });
        }
        else
        {
            body.Children.Add(new TextBlock
            {
                Text = editable
                    ? "Categoria vazia. Arraste apps para cá (ou use o botão direito > “Mover para…”)."
                    : "Arraste um app para cá para tirá-lo de uma categoria.",
                Foreground = Dim, FontSize = 12.5, Margin = new Thickness(4, 0, 0, 12)
            });
        }

        var block = new Border
        {
            Background = BlockBg,
            BorderBrush = BlockBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(22),
            Padding = new Thickness(16, 14, 16, 8),
            Margin = new Thickness(0, 0, 0, 14),
            AllowDrop = true,
            Child = body
        };

        // soltar um app aqui: entra nesta categoria ("Sem categoria" = tira da categoria)
        string category = editable ? title : null;
        block.DragOver += (_, e) => Block_DragOver(block, e);
        block.Drop += (_, e) => Block_Drop(category, e);
        return block;
    }

    void CategoryMenu(FrameworkElement owner, string cat)
    {
        var items = new List<(string, Action, Brush)>
        {
            ("Renomear…", () => PromptRename(cat), null),
            ("Excluir categoria", () => { CategoryStore.Delete(cat); RefreshGroups(); }, Red)
        };
        MenuKit.Show(owner, cat, null, items, open => _menuOpen = open, PlacementMode.Bottom);
    }

    // ---------- Adicionar / remover apps ----------

    // App adicionado à mão some de vez; app do Windows só é escondido (dá para restaurar)
    void RemoveFromLauncher(AppEntry a)
    {
        if (LauncherAppStore.Contains(a.Target))
        {
            LauncherAppStore.Remove(a.Target);
            CategoryStore.Assign(a.Target, null);
            _apps.Remove(a);
        }
        else CategoryStore.Hide(a.Target);
        RebuildBlocks();
    }

    void AddApp_Click(object sender, MouseButtonEventArgs e) => PickAndAddApps();

    void PickAndAddApps()
    {
        string[] files = null;
        _busy = true;   // o diálogo tira o foco da janela, mas ela não deve fechar
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Adicionar aplicativo ao lançador",
                Filter = "Programas e atalhos|*.exe;*.lnk;*.bat;*.cmd;*.url|Todos os arquivos|*.*",
                Multiselect = true
            };
            if (dlg.ShowDialog(this) == true) files = dlg.FileNames;
        }
        catch { }
        finally
        {
            _busy = false;
            if (IsVisible) Activate();
        }

        if (files == null || files.Length == 0) return;
        foreach (var f in files) AddCustomApp(f);
        RebuildBlocks();
        BlocksScroll.ScrollToTop();
    }

    void AddFolder_Click(object sender, MouseButtonEventArgs e) => PickAndAddFolders();

    void PickAndAddFolders()
    {
        string[] dirs = null;
        _busy = true;   // o diálogo tira o foco da janela, mas ela não deve fechar
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Adicionar pasta ao lançador",
                Multiselect = true
            };
            if (dlg.ShowDialog(this) == true) dirs = dlg.FolderNames;
        }
        catch { }
        finally
        {
            _busy = false;
            if (IsVisible) Activate();
        }

        if (dirs == null || dirs.Length == 0) return;
        foreach (var d in dirs) AddCustomApp(d);
        RebuildBlocks();
        BlocksScroll.ScrollToTop();
    }

    void AddCustomApp(string path)
    {
        if (string.IsNullOrEmpty(path) || !(File.Exists(path) || Directory.Exists(path))) return;

        var existing = _apps.FirstOrDefault(a => string.Equals(a.Target, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            CategoryStore.Unhide(path);   // já estava na lista (talvez removido): só volta a aparecer
            return;
        }

        var name = NameFor(path);
        if (LauncherAppStore.Add(name, path)) _apps.Add(MakeCustomEntry(name, path));
    }

    AppEntry MakeCustomEntry(string name, string path)
    {
        var entry = new AppEntry { Name = name, Target = path, DefaultIcon = ShellIcon.GetCached(path) };
        entry.RefreshIcon();
        SetGroup(entry);
        return entry;
    }

    // Coloca na lista os apps adicionados à mão que ainda não estão nela
    bool AddMissingCustomApps()
    {
        bool added = false;
        foreach (var c in LauncherAppStore.All)
        {
            if (!File.Exists(c.Target) && !Directory.Exists(c.Target)) continue;
            if (_apps.Any(a => string.Equals(a.Target, c.Target, StringComparison.OrdinalIgnoreCase))) continue;
            _apps.Add(MakeCustomEntry(c.Name, c.Target));
            added = true;
        }
        return added;
    }

    // Nome amigável: descrição do .exe (ex.: "Spotify") ou nome do arquivo
    static string NameFor(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var trimmed = path.TrimEnd('\\', '/');
                var n = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(n) ? path : n;   // raiz de unidade (C:\): mostra o caminho
            }
            if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            {
                var v = FileVersionInfo.GetVersionInfo(path);
                var d = v.FileDescription?.Trim();
                if (!string.IsNullOrEmpty(d)) return d;
                var p = v.ProductName?.Trim();
                if (!string.IsNullOrEmpty(p)) return p;
            }
        }
        catch { }
        return Path.GetFileNameWithoutExtension(path);
    }

    void NewCategory_Click(object sender, MouseButtonEventArgs e) => PromptNewCategory();

    void PromptNewCategory(string assignTarget = null)
    {
        ShowNamePrompt("Nova categoria", "", name =>
        {
            var err = CategoryStore.Add(name);
            if (err != null) return err;
            if (assignTarget != null) CategoryStore.Assign(assignTarget, name);
            RefreshGroups();
            return null;
        });
    }

    void PromptRename(string old)
    {
        ShowNamePrompt("Renomear categoria", old, name =>
        {
            var err = CategoryStore.Rename(old, name);
            if (err != null) return err;
            RefreshGroups();
            return null;
        });
    }

    // ---------- Pedido de nome (dentro da própria janela, para ela não fechar) ----------

    void ShowNamePrompt(string title, string initial, Func<string, string> apply)
    {
        _nameApply = apply;
        NameTitle.Text = title;
        NameBox.Text = initial;
        NameError.Visibility = Visibility.Collapsed;
        NameOverlay.Visibility = Visibility.Visible;

        // o menu de contexto ainda pode estar fechando: foca depois que ele sair
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Activate();
            NameBox.Focus();
            Keyboard.Focus(NameBox);
            NameBox.SelectAll();
        }), DispatcherPriority.ApplicationIdle);
    }

    void HideNamePrompt()
    {
        NameOverlay.Visibility = Visibility.Collapsed;
        _nameApply = null;
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    void ConfirmName()
    {
        var err = _nameApply?.Invoke(NameBox.Text.Trim());
        if (err != null)
        {
            NameError.Text = err;
            NameError.Visibility = Visibility.Visible;
            return;
        }
        HideNamePrompt();
    }

    void NameOk_Click(object sender, MouseButtonEventArgs e) => ConfirmName();
    void NameCancel_Click(object sender, MouseButtonEventArgs e) => HideNamePrompt();

    // ---------- Lista de apps ----------

    // Lista os apps do menu Iniciar (programas clássicos + apps da Loja)
    internal static List<DockDto> QueryStartApps()
    {
        var result = new List<DockDto>();
        try
        {
            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; " +
                "Get-StartApps | Sort-Object Name | ConvertTo-Json -Compress\"")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            using var p = Process.Start(psi);
            var json = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            if (string.IsNullOrWhiteSpace(json)) return result;

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var items = root.ValueKind == JsonValueKind.Array
                ? root.EnumerateArray().ToList()
                : new List<JsonElement> { root };

            foreach (var it in items)
            {
                var name = it.TryGetProperty("Name", out var n) ? n.GetString() : null;
                var id = it.TryGetProperty("AppID", out var a) ? a.GetString() : null;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id)) continue;
                result.Add(new DockDto { Name = name, Target = "shell:AppsFolder\\" + id });
            }
        }
        catch { }
        return result;
    }

    async Task EnsureAppsAsync()
    {
        if (_loadingApps) return;
        if (_apps.Count > 0 && (DateTime.UtcNow - _appsLoadedAt).TotalMinutes < 10) return;

        _loadingApps = true;
        LoadingText.Text = "Carregando aplicativos…";
        LoadingText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var list = await Task.Run(QueryStartApps);
            if (list.Count > 0)
            {
                var fresh = list.GroupBy(x => x.Target, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
                bool Eq(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

                // A lista é relida a cada 10 min, na primeira abertura depois disso. Se nada mudou, a grade NÃO é
                // refeita (refazer com o lançador na tela apagava os ícones e fazia a janela piscar).
                bool same = _apps.Count > 0
                    && fresh.All(f => _apps.Any(a => Eq(a.Target, f.Target) && a.Name == f.Name))
                    && _apps.All(a => fresh.Any(f => Eq(f.Target, a.Target)) || LauncherAppStore.All.Any(c => Eq(c.Target, a.Target)));

                if (same) _appsLoadedAt = DateTime.UtcNow;
                else
                {
                    var prev = _apps.ToList();   // reaproveita os ícones já carregados
                    _apps.Clear();
                    foreach (var d in fresh)
                    {
                        var entry = new AppEntry
                        {
                            Name = d.Name,
                            Target = d.Target,
                            DefaultIcon = prev.FirstOrDefault(p0 => Eq(p0.Target, d.Target))?.DefaultIcon
                                          ?? (ShellIcon.IsCached(d.Target) ? ShellIcon.GetCached(d.Target) : null)
                        };
                        entry.RefreshIcon();
                        SetGroup(entry);
                        _apps.Add(entry);
                    }
                    _appsLoadedAt = DateTime.UtcNow;
                    RebuildBlocks();
                }
            }
            if (AddMissingCustomApps()) RebuildBlocks();
            // ícones que você escolheu aqui passam a valer também para o app aberto no dock
            foreach (var app in _apps.ToList())
                if (IconStore.Has(app.IconKey)) { app.ResolvedExe ??= DockWindow.ResolveExe(app.Target); IconStore.Learn(app); }
            LoadingText.Text = "Nenhum aplicativo encontrado";
            LoadingText.Visibility = _apps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            // ícones aos poucos, sem travar a janela
            foreach (var app in _apps.ToList())
            {
                if (app.DefaultIcon != null) continue;
                await Dispatcher.Yield(DispatcherPriority.Background);
                var icon = ShellIcon.GetCached(app.Target);
                if (icon != null)
                {
                    app.DefaultIcon = icon;
                    app.RefreshIcon();
                }
            }
        }
        catch { }
        finally { _loadingApps = false; }
    }
}
