using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DockIsland;

// Seletor de janelas no estilo da ilha (vidro escuro, cantos arredondados). Substitui:
//   Alt + Tab  -> faixa centralizada com as janelas abertas; Tab / Shift+Tab / setas trocam, soltar o Alt abre a escolhida
//   Win + Tab  -> visão geral: todas as janelas em grade; clique (ou setas + Enter) abre, X fecha a janela, Esc sai
// As teclas chegam pelo WinKeyHook (gancho global de teclado); a janela nunca rouba o foco.
public sealed class SwitcherWindow : Window
{
    enum Mode { Closed, Alt, Overview }

    sealed class Item
    {
        public IntPtr Hwnd;
        public string Title;
        public ImageSource Icon;
        public bool Minimized;
        public Border Tile;
        public Image Thumb;
        public FrameworkElement Placeholder;
        public Border ThumbBox;     // caixa onde a prévia aparece
        public IntPtr Dwm;          // miniatura ao vivo do Windows (DWM), 0 = não registrada
    }

    // ---------------------------------------------------------------- Win32
    static class N
    {
        public delegate bool EnumProc(IntPtr h, IntPtr l);

        [StructLayout(LayoutKind.Sequential)] public struct RC { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        public struct BMIH
        {
            public int Size, Width, Height;
            public short Planes, BitCount;
            public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
        }

        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr h, int index, int value);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RC r);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr h, int index);
        [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out PT p);
        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public uint dwFlags;
            public RC rcDestination, rcSource;
            public byte opacity;
            public int fVisible, fSourceClientAreaOnly;
        }
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumb);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES p);
        [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] public static extern int CombineRgn(IntPtr dst, IntPtr a, IntPtr b, int mode);
        [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] public static extern int SetStretchBltMode(IntPtr dc, int mode);
        [DllImport("gdi32.dll")]
        public static extern bool StretchBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, uint rop);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateDIBSection(IntPtr dc, ref BMIH bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    }

    const int GWL_EXSTYLE = -20;
    const int WS_EX_TRANSPARENT = 0x20;
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x08000000;
    static readonly IntPtr HWND_TOPMOST = new(-1);
    const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10;

    // ---------------------------------------------------------------- visual
    static Brush Frozen(string hex) { var b = (Brush)new BrushConverter().ConvertFromString(hex); b.Freeze(); return b; }
    static readonly Brush Clear = Brushes.Transparent;
    // cores do tema atual (claro/escuro): propriedades, lidas a cada vez que a visão geral é montada
    static Brush SelBg => Theme.W(0x38);
    static Brush SelBorder => Theme.W(0x99);
    static Brush ThumbBg => Theme.W(0x1F);
    static Brush VeilGlass => Theme.Light ? Frozen("#40FFFFFF") : Frozen("#40000000");
    static Brush CardBg => Theme.Light ? Frozen("#E6F6F6F8") : Frozen("#B3000000");
    static Brush CardBorder => Theme.W(0x4D);
    static Brush PillBg => Theme.W(0x24);
    static Brush PillHover => Theme.W(0x3D);
    static Brush Soft => Theme.W(0xA0);
    static readonly Brush CloseHot = Frozen("#FF453A");
    static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    readonly Grid _root = new();
    readonly Border _veil = new();
    readonly Border _card = new();
    readonly TextBlock _header = new();
    readonly ScrollViewer _scroll = new();
    // grade com número FIXO de colunas (a de quebra automática podia colocar menos cartões por linha que o previsto)
    readonly System.Windows.Controls.Primitives.UniformGrid _panel = new() { Rows = 0, HorizontalAlignment = HorizontalAlignment.Left };
    readonly WrapPanel _desks = new() { Margin = new Thickness(0, 8, 0, 0) };
    readonly TextBlock _deskLabel = new();
    readonly Border _divider = new();
    readonly DispatcherTimer _showTimer = new() { Interval = TimeSpan.FromMilliseconds(110) };

    // efeito de abertura do Win+Tab: o conteúdo do cartão sobe e cresce suavemente enquanto a janela aparece
    readonly ScaleTransform _fxScale = new(1, 1);
    readonly TranslateTransform _fxMove = new(0, 0);
    byte _thumbA = 255;       // opacidade das prévias ao vivo (sobem de 0 a 255 na abertura do Win+Tab)

    const int MaxCols = 5;   // janelas por linha (Alt+Tab)
    const int MaxColsOverview = 5;   // janelas por linha (Win+Tab); o número real é escolhido para tudo caber na tela
    int _gridCols = 4;               // colunas escolhidas nesta abertura (Win+Tab)
    const double ThumbRadius = 18;   // arredondamento dos cantos das prévias (iguais para a prévia ao vivo e para a foto)
    const double ThumbPad = 8;   // espaço igual entre a prévia da janela e a borda da caixa, nos 4 lados
    double _tileW = 232, _thumbH = 124;   // tamanho do cartão e da miniatura nesta abertura
    Mode _mode = Mode.Closed;
    List<Item> _items = new();
    int _sel, _cols = 1;
    bool _uiShown;
    int _gen;                 // identifica a rodada de capturas (as antigas são descartadas)
    IntPtr _hwnd;
    Window _host;             // janela auxiliar (sem transparência) onde o Windows desenha as prévias ao vivo
    IntPtr _hostHwnd;

    /// 0 = fechado, 1 = Alt+Tab, 2 = visão geral (Win+Tab)
    public int ModeCode => (int)_mode;

    public SwitcherWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI Variable Text, Segoe UI");
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        Title = "Janelas";
        Width = 400; Height = 300;
        Left = -20000; Top = -20000;

        _veil.Background = Clear;
        _veil.MouseLeftButtonDown += (s, e) =>
        {
            if (_mode == Mode.Overview && ReferenceEquals(e.OriginalSource, _veil)) CloseUi();
        };

        _card.Margin = new Thickness(30);
        _card.Padding = new Thickness(22);
        _card.CornerRadius = new CornerRadius(34);
        _card.Background = CardBg;
        _card.BorderBrush = CardBorder;
        _card.BorderThickness = new Thickness(1);
        _card.HorizontalAlignment = HorizontalAlignment.Center;
        _card.VerticalAlignment = VerticalAlignment.Center;
        Glass.SetBlur(_card, true);

        _header.Foreground = Soft;
        _header.FontSize = 12.5;
        _header.FontWeight = FontWeights.SemiBold;
        _header.Margin = new Thickness(6, 0, 0, 8);
        _header.Visibility = Visibility.Collapsed;

        _deskLabel.Text = "ÁREAS DE TRABALHO";
        _deskLabel.Foreground = Soft;
        _deskLabel.FontSize = 11.5;
        _deskLabel.FontWeight = FontWeights.SemiBold;
        _deskLabel.Margin = new Thickness(6, 0, 0, 0);
        _deskLabel.Visibility = Visibility.Collapsed;

        _divider.Height = 1;
        _divider.Background = Theme.W(0x1F);
        Theme.Changed += () => { try { _card.Background = CardBg; _divider.Background = Theme.W(0x1F); } catch { } };   // trocou o tema
        _divider.Margin = new Thickness(6, 8, 6, 16);
        _divider.Visibility = Visibility.Collapsed;

        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _scroll.Focusable = false;
        _scroll.Content = _panel;

        var stack = new StackPanel();
        stack.RenderTransformOrigin = new Point(0.5, 0.5);
        stack.RenderTransform = new TransformGroup { Children = { _fxScale, _fxMove } };
        stack.Children.Add(_deskLabel);
        stack.Children.Add(_desks);
        stack.Children.Add(_divider);
        stack.Children.Add(_header);
        stack.Children.Add(_scroll);
        _card.Child = stack;
        _veil.Child = _card;
        _root.Children.Add(_veil);
        Content = _root;

        _scroll.ScrollChanged += (_, _) => LayoutThumbs();
        IconStore.Changed += _ => Dispatcher.BeginInvoke(new Action(() => IconCache.Clear()));   // ícone trocado: refaz
        _showTimer.Tick += (_, _) => { _showTimer.Stop(); if (_mode == Mode.Alt) ShowUi(); };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        N.SetWindowLong(_hwnd, GWL_EXSTYLE, N.GetWindowLong(_hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    // ================================================================ entradas (chamadas pelo gancho de teclado)

    // Alt+Tab apertado (1ª vez abre; as seguintes trocam de janela)
    public void AltTab(bool reverse)
    {
        try
        {
            if (_mode == Mode.Overview) CloseUi();
            if (_mode == Mode.Closed)
            {
                _items = Collect();
                if (_items.Count == 0) return;
                _sel = _items.Count > 1 ? (reverse ? _items.Count - 1 : 1) : 0;
                _mode = Mode.Alt;
                _showTimer.Stop();
                _showTimer.Start();   // toque rápido de Alt+Tab troca direto, sem mostrar a faixa
            }
            else Step(reverse ? -1 : 1);
        }
        catch { CloseUi(); }
    }

    // Alt solto: abre a janela escolhida
    public void Commit()
    {
        try
        {
            if (_mode != Mode.Alt) return;
            ActivateAndClose(_sel);
        }
        catch { CloseUi(); }
    }

    // Win+Tab: abre / fecha a visão geral
    public void ToggleOverview()
    {
        try
        {
            if (_mode == Mode.Overview) { CloseOverview(); return; }
            if (_mode == Mode.Alt) CloseUi();
            _items = Collect();
            _sel = _items.Count > 1 ? 1 : 0;
            _mode = Mode.Overview;
            ShowUi();
        }
        catch { CloseUi(); }
    }

    public void Cancel() => CloseUi();

    // Fecha a visão geral (Win+Tab de novo, Win, Esc): se o mouse está parado em cima de uma janela, vai para ela
    public void CloseOverview()
    {
        try
        {
            if (_mode == Mode.Overview)
            {
                int i = HoverIndex();
                if (i >= 0) { ActivateAndClose(i); return; }
            }
        }
        catch { }
        CloseUi();
    }

    // Índice do cartão que está sob o cursor agora (-1 = nenhum); não depende de o WPF ter avisado o MouseEnter
    int HoverIndex()
    {
        try
        {
            if (!IsVisible || !N.GetCursorPos(out var pt)) return -1;
            double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (k <= 0) k = 1;
            var v0 = _scroll.PointToScreen(new Point(0, 0));
            if (!new Rect(v0.X, v0.Y, _scroll.ActualWidth * k, _scroll.ActualHeight * k).Contains(pt.X, pt.Y)) return -1;
            for (int i = 0; i < _panel.Children.Count && i < _items.Count; i++)
            {
                if (_panel.Children[i] is not FrameworkElement t || !t.IsVisible) continue;
                var p0 = t.PointToScreen(new Point(0, 0));
                if (new Rect(p0.X, p0.Y, t.ActualWidth * k, t.ActualHeight * k).Contains(pt.X, pt.Y)) return i;
            }
        }
        catch { }
        return -1;
    }

    // Tab / setas / Enter / Esc enquanto o seletor está aberto
    public void Nav(int vk, bool shift)
    {
        try
        {
            if (_mode == Mode.Closed) return;
            switch (vk)
            {
                case 0x09: Step(shift ? -1 : 1); break;        // Tab
                case 0x27: Step(1); break;                      // →
                case 0x25: Step(-1); break;                     // ←
                case 0x28: MoveRow(1); break;                   // ↓
                case 0x26: MoveRow(-1); break;                  // ↑
                case 0x0D: ActivateAndClose(_sel); break;       // Enter
                case 0x1B: CloseOverview(); break;              // Esc
            }
        }
        catch { CloseUi(); }
    }

    // ================================================================ lógica

    void Step(int dir)
    {
        int n = _items.Count;
        if (n == 0) return;
        _sel = ((_sel + dir) % n + n) % n;
        UpdateSelection();
    }

    void MoveRow(int dir)
    {
        int n = _items.Count;
        if (n == 0) return;
        int t = _sel + dir * Math.Max(1, _cols);
        if (t >= 0 && t < n) { _sel = t; UpdateSelection(); }
    }

    void ActivateAndClose(int index)
    {
        IntPtr h = index >= 0 && index < _items.Count ? _items[index].Hwnd : IntPtr.Zero;
        CloseUi();
        if (h == IntPtr.Zero) return;
        try
        {
            if (N.IsIconic(h)) N.ShowWindow(h, 9);   // SW_RESTORE
            N.SwitchToThisWindow(h, true);
            N.SetForegroundWindow(h);
        }
        catch { }
    }

    void CloseUi()
    {
        ShowChrome();
        ReleaseThumbs();
        _mode = Mode.Closed;
        _showTimer.Stop();
        _uiShown = false;
        _gen++;
        try
        {
            if (IsVisible)
            {
                BeginAnimation(OpacityProperty, null);
                Hide();
            }
        }
        catch { }
        _panel.Children.Clear();
        _items = new List<Item>();
    }

    // Janelas que aparecem no Alt+Tab, de cima para baixo na ordem de empilhamento
    static List<Item> Collect()
    {
        var list = new List<Item>();
        uint own = (uint)Environment.ProcessId;
        N.EnumWindows((h, _) =>
        {
            try
            {
                if (!N.IsWindowVisible(h)) return true;
                if (N.GetWindow(h, 4 /* GW_OWNER */) != IntPtr.Zero) return true;
                int ex = N.GetWindowLong(h, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return true;
                if (N.DwmGetWindowAttribute(h, 14 /* DWMWA_CLOAKED */, out int cloaked, 4) == 0 && cloaked != 0) return true;

                int len = N.GetWindowTextLength(h);
                if (len == 0) return true;
                var title = new StringBuilder(len + 1);
                N.GetWindowText(h, title, title.Capacity);

                var cls = new StringBuilder(256);
                N.GetClassName(h, cls, 256);
                var c = cls.ToString();
                if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd") return true;

                N.GetWindowThreadProcessId(h, out uint pid);
                if (pid == own) return true;

                list.Add(new Item
                {
                    Hwnd = h,
                    Title = title.ToString(),
                    Minimized = N.IsIconic(h),
                    Icon = CachedLauncherIcon(h, title.ToString()) ?? WindowIcon(h)   // mesmo ícone do dock/lançador
                });
            }
            catch { }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    // Resolver o ícone de cada janela custa (processo, identidade do app, lista do lançador): guarda o resultado
    // por janela+título e só refaz quando algo muda (ícone trocado, título novo). Abrir fica bem mais rápido.
    static readonly Dictionary<string, ImageSource> IconCache = new();

    static ImageSource CachedLauncherIcon(IntPtr h, string title)
    {
        var key = h.ToString() + "|" + title;
        if (IconCache.TryGetValue(key, out var c)) return c;
        var icon = DockWindow.LauncherIconForWindow(h, title);
        if (icon != null && StartAppIcons.Loaded)   // só guarda com a lista do lançador pronta (senão o ícone sairia errado)
        {
            if (IconCache.Count > 300) IconCache.Clear();
            IconCache[key] = icon;
        }
        return icon;
    }

    static ImageSource WindowIcon(IntPtr hwnd)
    {
        try
        {
            IntPtr icon = IntPtr.Zero;
            foreach (var kind in new[] { 1, 2, 0 })   // ICON_BIG, ICON_SMALL2, ICON_SMALL
            {
                N.SendMessageTimeout(hwnd, 0x007F /* WM_GETICON */, (IntPtr)kind, IntPtr.Zero, 2, 80, out icon);
                if (icon != IntPtr.Zero) break;
            }
            if (icon == IntPtr.Zero) icon = N.GetClassLongPtr(hwnd, -14 /* GCL_HICON */);
            if (icon == IntPtr.Zero) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch { return null; }
    }

    // ================================================================ interface

    async void ShowUi()
    {
        if (_uiShown) return;
        _uiShown = true;
        int openGen = _gen;   // se fechar e abrir de novo antes de terminar, esta abertura antiga se descarta
        bool overview = _mode == Mode.Overview;

        if (overview) HideChrome();   // Win+Tab: ilha e dock não aparecem, nunca

        // o lançador aberto ficava por cima do seletor (as prévias ao vivo apareciam sobre ele): fecha antes
        try
        {
            foreach (var w in Application.Current.Windows.OfType<LauncherWindow>().ToList())
                if (w.IsOpen && w.Left > -5000) w.Toggle();
        }
        catch { }

        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        ComputeTileSize(overview);
        double tileW = _tileW, thumbH = _thumbH;
        ApplyWidths(overview);

        _panel.Children.Clear();
        foreach (var it in _items) _panel.Children.Add(BuildTile(it, tileW, thumbH, overview));

        _header.Visibility = _deskLabel.Visibility = _divider.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeader();
        AddEmptyText();
        _scroll.MaxHeight = Math.Max(200, sh - (overview ? 230 : 200));
        // Win+Tab: o wallpaper atrás vira um desfoque forte (foco nas janelas); Alt+Tab: sem véu
        if (overview)
        {
            Glass.SetHeavy(_veil, true);
            _veil.Background = VeilGlass;     // cor/transparência da película sobre o blur
            Glass.SetBlur(_veil, true);
        }
        else
        {
            Glass.SetBlur(_veil, false);      // devolve o Background original
            Glass.SetHeavy(_veil, false);
            _veil.Background = Clear;
        }
        BuildDesktops(overview);
        UpdateSelection();
        foreach (var it in _items) if (it.Placeholder != null) it.Placeholder.Opacity = 0;   // o ícone só aparece se a miniatura não vier

        if (overview)
        {
            Width = sw; Height = sh;
            Left = 0; Top = 0;
        }
        else
        {
            _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Width = Math.Ceiling(_card.DesiredSize.Width);
            Height = Math.Ceiling(_card.DesiredSize.Height);
            Left = (sw - Width) / 2;
            Top = (sh - Height) / 2;
        }

        // posição inicial do efeito (só Win+Tab); Alt+Tab continua direto
        _fxScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _fxScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _fxMove.BeginAnimation(TranslateTransform.YProperty, null);
        _fxScale.ScaleX = _fxScale.ScaleY = 1;   // sem zoom/subida: as prévias ao vivo não acompanham a animação
        _fxMove.Y = 0;

        Opacity = 0;
        Glass.Prepare(this);   // fundo borrado já pronto antes de aparecer (sem piscada)

        // abre fora da tela, espera o WPF desenhar e só então move para o lugar (evita 1 quadro com imagem antiga)
        double cx = Left, cy = Top;
        Left = -20000; Top = -20000;
        Show();
        try { N.SetWindowPos(_hwnd != IntPtr.Zero ? _hwnd : new WindowInteropHelper(this).Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE); } catch { }
        await Dispatcher.Yield(DispatcherPriority.Background);
        if (!_uiShown || openGen != _gen) return;   // fechou (ou fechou e reabriu) nesse instante
        Left = cx; Top = cy;
        if (overview)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        else BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(70)));

        // prévias ao vivo (como no Win+Tab original): entram quando o fade termina
        int gen = _gen;
        await Task.Delay(overview ? 25 : 55);
        if (!_uiShown || gen != _gen) return;
        UpdateLayout();
        _thumbA = overview ? (byte)0 : (byte)255;
        ShowThumbs();
        if (overview) FadeInThumbs(gen);
        StartCaptures();   // plano B (foto) para as janelas que o Windows não aceitou como miniatura

        // janelas sem miniatura nem foto (ex.: protegidas): aí sim mostra o ícone no lugar
        await Task.Delay(450);
        if (!_uiShown || gen != _gen) return;
        foreach (var it in _items)
            if (it.Placeholder != null && it.Dwm == IntPtr.Zero && it.Thumb?.Source == null) it.Placeholder.Opacity = 1;
    }

    // Cantos arredondados também na foto (plano B): recorta exatamente o retângulo que a imagem ocupa
    static void RoundThumb(Image img)
    {
        try
        {
            if (img.Source is not BitmapSource bs || bs.Width < 1 || bs.Height < 1 || img.ActualWidth < 1 || img.ActualHeight < 1)
            {
                img.Clip = null;
                return;
            }
            double sc = Math.Min(img.ActualWidth / bs.Width, img.ActualHeight / bs.Height);
            double w = bs.Width * sc, h = bs.Height * sc;
            double r = Math.Min(ThumbRadius, Math.Min(w, h) / 2);
            var clip = new RectangleGeometry(new Rect((img.ActualWidth - w) / 2, (img.ActualHeight - h) / 2, w, h), r, r);
            clip.Freeze();
            img.Clip = clip;
        }
        catch { }
    }

    // ---------------------------------------------------------------- ilha e dock somem durante o Win+Tab
    /// Visão geral (Win+Tab) aberta: o TopmostGuard não traz a ilha e o dock de volta para a frente.
    public static bool OverviewOpen { get; private set; }
    static bool _chromeHidden;   // estático: a restauração forçada (RestoreChromeNow) precisa zerar o mesmo estado

    static List<Window> ChromeWindows() =>
        (Application.Current?.Windows.Cast<Window>() ?? Enumerable.Empty<Window>())
            .Where(w => (w is MainWindow || w is DockWindow) && w.IsVisible).ToList();

    // some com a ilha e o dock (fade rápido) e eles deixam de receber cliques; a visão geral fica limpa
    void HideChrome()
    {
        OverviewOpen = true;
        if (_chromeHidden) return;
        _chromeHidden = true;
        foreach (var w in ChromeWindows())
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero) N.SetWindowLong(h, GWL_EXSTYLE, N.GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT);
                w.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(90)));
            }
            catch { }
        }
    }

    // ao fechar: a ilha e o dock voltam com fade
    void ShowChrome()
    {
        OverviewOpen = false;
        if (!_chromeHidden) return;
        _chromeHidden = false;
        foreach (var w in ChromeWindows())
        {
            try
            {
                var win = w;
                var h = new WindowInteropHelper(win).Handle;
                if (h != IntPtr.Zero) N.SetWindowLong(h, GWL_EXSTYLE, N.GetWindowLong(h, GWL_EXSTYLE) & ~WS_EX_TRANSPARENT);
                var fade = new DoubleAnimation(1, TimeSpan.FromMilliseconds(160));
                fade.Completed += (_, _) =>
                {
                    if (OverviewOpen) return;   // abriu de novo no meio do fade
                    win.BeginAnimation(OpacityProperty, null);
                    win.Opacity = 1;
                };
                win.BeginAnimation(OpacityProperty, fade);
            }
            catch { }
        }
    }

    // Rede de segurança (chamada pelo TopmostGuard): a visão geral já não existe, mas a ilha e o dock ficaram
    // invisíveis / sem aceitar clique. Devolve tudo ao normal na hora, sem animação.
    public static void RestoreChromeNow()
    {
        OverviewOpen = false;
        _chromeHidden = false;
        foreach (var w in ChromeWindows())
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h != IntPtr.Zero) N.SetWindowLong(h, GWL_EXSTYLE, N.GetWindowLong(h, GWL_EXSTYLE) & ~WS_EX_TRANSPARENT);
                w.BeginAnimation(OpacityProperty, null);
                w.Opacity = 1;
            }
            catch { }
        }
    }

    // As prévias ao vivo sobem de transparentes para opacas em ~180 ms
    void FadeInThumbs(int gen)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var t = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        t.Tick += (_, _) =>
        {
            if (!_uiShown || gen != _gen) { t.Stop(); return; }
            double p = Math.Min(1, clock.ElapsedMilliseconds / 110.0);
            p = 1 - Math.Pow(1 - p, 3);
            _thumbA = (byte)Math.Round(255 * p);
            foreach (var it in _items)
            {
                if (it.Dwm == IntPtr.Zero) continue;
                var props = new N.DWM_THUMBNAIL_PROPERTIES { dwFlags = 0x4, opacity = _thumbA };   // só a opacidade
                N.DwmUpdateThumbnailProperties(it.Dwm, ref props);
            }
            if (p >= 1) t.Stop();
        };
        t.Start();
    }

    // ---------------------------------------------------------------- prévias ao vivo (DWM)
    static IntPtr HostHook(IntPtr h, int msg, IntPtr w, IntPtr l, ref bool handled)
    {
        if (msg == 0x0084 /* WM_NCHITTEST */) { handled = true; return (IntPtr)(-1); /* HTTRANSPARENT: cliques passam */ }
        return IntPtr.Zero;
    }

    void EnsureHost()
    {
        if (_host != null) return;
        _host = new Window
        {
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
            Topmost = true, Background = Frozen("#1C1C1E"), WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -20000, Top = -20000, Width = 10, Height = 10, Title = "Prévias"
        };
        _host.SourceInitialized += (_, _) =>
        {
            _hostHwnd = new WindowInteropHelper(_host).Handle;
            N.SetWindowLong(_hostHwnd, GWL_EXSTYLE, N.GetWindowLong(_hostHwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            HwndSource.FromHwnd(_hostHwnd)?.AddHook(HostHook);
        };
    }

    void ShowThumbs()
    {
        try
        {
            EnsureHost();
            if (!_host.IsVisible) _host.Show();
            if (_hostHwnd == IntPtr.Zero || !N.GetWindowRect(_hwnd, out var wr)) return;

            N.SetWindowRgn(_hostHwnd, N.CreateRectRgn(0, 0, 0, 0), false);   // nada visível até as prévias ficarem prontas
            N.SetWindowPos(_hostHwnd, HWND_TOPMOST, wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top, SWP_NOACTIVATE | 0x40 /* SHOWWINDOW */);

            foreach (var it in _items)
            {
                if (it.Dwm != IntPtr.Zero) continue;
                if (N.DwmRegisterThumbnail(_hostHwnd, it.Hwnd, out var th) == 0 && th != IntPtr.Zero) it.Dwm = th;
            }
            LayoutThumbs();
        }
        catch { }
    }

    // Posiciona cada prévia dentro da sua caixa (mantendo a proporção) e recorta a janela auxiliar nesses retângulos
    void LayoutThumbs()
    {
        try
        {
            if (_host == null || !_host.IsVisible || _hostHwnd == IntPtr.Zero || !_uiShown) return;
            N.SetWindowPos(_hostHwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);   // sempre acima do cartão
            double k = VisualTreeHelper.GetDpi(this).DpiScaleX;
            if (k <= 0) k = 1;

            var vp = _scroll.TransformToAncestor(_root).TransformBounds(new Rect(0, 0, _scroll.ActualWidth, _scroll.ActualHeight));
            IntPtr all = N.CreateRectRgn(0, 0, 0, 0);
            IntPtr view = N.CreateRectRgn((int)(vp.Left * k), (int)(vp.Top * k), (int)Math.Ceiling(vp.Right * k), (int)Math.Ceiling(vp.Bottom * k));
            bool any = false;

            foreach (var it in _items)
            {
                var box = it.ThumbBox;
                if (it.Dwm == IntPtr.Zero || box == null || box.ActualWidth < 4 || !box.IsVisible) continue;
                if (N.DwmQueryThumbnailSourceSize(it.Dwm, out var sz) != 0 || sz.cx <= 0 || sz.cy <= 0) continue;

                var r = box.TransformToAncestor(_root).TransformBounds(new Rect(0, 0, box.ActualWidth, box.ActualHeight));
                r.Inflate(-ThumbPad, -ThumbPad);
                double bw = r.Width * k, bh = r.Height * k;
                double sc = Math.Min(Math.Min(bw / sz.cx, bh / sz.cy), 1.0);
                double w = Math.Max(1, sz.cx * sc), h = Math.Max(1, sz.cy * sc);
                double x = r.Left * k + (bw - w) / 2, y = r.Top * k + (bh - h) / 2;
                int xi = (int)Math.Round(x), yi = (int)Math.Round(y), wi = (int)Math.Round(w), hi = (int)Math.Round(h);

                var props = new N.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 0x1 | 0x4 | 0x8,   // destino | opacidade | visível
                    rcDestination = new N.RC { Left = xi, Top = yi, Right = xi + wi, Bottom = yi + hi },
                    opacity = _thumbA,
                    fVisible = 1
                };
                N.DwmUpdateThumbnailProperties(it.Dwm, ref props);

                int rad = (int)Math.Round(2 * Math.Min(ThumbRadius * k, Math.Min(wi, hi) / 2.0));   // diâmetro da curva do canto
                IntPtr piece = N.CreateRoundRectRgn(xi, yi, xi + wi + 1, yi + hi + 1, rad, rad);
                N.CombineRgn(all, all, piece, 2 /* RGN_OR */);
                N.DeleteObject(piece);
                it.Placeholder.Visibility = Visibility.Collapsed;
                any = true;
            }

            N.CombineRgn(all, all, view, 1 /* RGN_AND */);
            N.DeleteObject(view);
            N.SetWindowRgn(_hostHwnd, all, true);   // o sistema passa a ser dono do recorte
            if (!any) _host.Hide();
        }
        catch { }
    }

    void ReleaseThumbs()
    {
        try
        {
            foreach (var it in _items)
                if (it.Dwm != IntPtr.Zero) { N.DwmUnregisterThumbnail(it.Dwm); it.Dwm = IntPtr.Zero; }
            if (_host != null && _host.IsVisible) _host.Hide();
        }
        catch { }
    }

    // Win+Tab: escolhe colunas e tamanho para TODAS as janelas caberem na tela sem rolar
    // (poucas janelas = miniaturas grandes; muitas = menores). Alt+Tab mantém o tamanho compacto.
    // Altura da caixa da prévia para uma largura de cartão: a prévia (proporção da tela) + o mesmo espaço nos 4 lados
    static double ThumbHeightFor(double tileW)
    {
        double asp = SystemParameters.PrimaryScreenHeight / Math.Max(1, SystemParameters.PrimaryScreenWidth);
        double innerW = tileW - 23 - 2 * ThumbPad;           // 23 = padding + borda do cartão
        return Math.Round(innerW * asp + 2 * ThumbPad);
    }

    void ComputeTileSize(bool overview)
    {
        if (!overview) { _tileW = 232; _thumbH = ThumbHeightFor(_tileW); return; }

        double sw = SystemParameters.PrimaryScreenWidth, sh = SystemParameters.PrimaryScreenHeight;
        double maxRow = sw * 0.88 - 130;
        double availH = sh - 260;                 // altura útil da lista (o resto é cabeçalho, áreas de trabalho e margens)
        double asp = sh / Math.Max(1, sw);
        const double extraH = 72;                 // cabeçalho + margens de cada cartão (fora da miniatura)
        int n = Math.Max(1, _items.Count);

        double best = 0; int bestCols = 1;
        for (int cols = 1; cols <= Math.Min(n, MaxColsOverview); cols++)
        {
            int rows = (int)Math.Ceiling(n / (double)cols);
            double byWidth = maxRow / cols * 0.85 - 6;
            double thumbMax = availH / rows - extraH;                          // altura que a caixa pode ter
            double byHeight = (thumbMax - 2 * ThumbPad) / asp + 23 + 2 * ThumbPad;
            double t = Math.Min(Math.Min(byWidth, byHeight), 430);
            if (t >= best - 0.5) { best = Math.Max(best, t); bestCols = cols; }   // empate: prefere mais colunas (menos linhas)
        }

        _gridCols = bestCols;
        _tileW = Math.Floor(Math.Max(best, 200));   // piso: com janelas demais o resto rola
        _thumbH = ThumbHeightFor(_tileW);
    }

    // colunas (no máx. MaxCols) e largura do painel; o cartão encolhe/cresce junto quando janelas saem da lista
    void ApplyWidths(bool overview)
    {
        double sw = SystemParameters.PrimaryScreenWidth;
        double tileOuter = _tileW + 6;
        double maxRow = overview ? sw * 0.88 - 130 : sw - 200;
        int colLimit = overview ? _gridCols : MaxCols;
        _cols = Math.Max(1, Math.Min(Math.Min(_items.Count, colLimit), (int)Math.Floor(maxRow / tileOuter)));
        double w = _cols * tileOuter;
        if (overview) w = Math.Max(w, Math.Min(sw * 0.88 - 130, 820));   // largura mínima: botões das áreas lado a lado
        _panel.Columns = _cols;
        _panel.Width = _items.Count == 0 ? w : _cols * tileOuter;   // largura exata das colunas: cartões alinhados à esquerda, sem folga entre eles
        _desks.Width = w;
    }

    void AddEmptyText()
    {
        if (_items.Count != 0) return;
        _panel.Children.Add(new TextBlock
        {
            Text = "Nenhuma janela aberta nesta área de trabalho", Foreground = Soft, FontSize = 14,
            Margin = new Thickness(6, 14, 6, 14), TextAlignment = TextAlignment.Center, Width = _panel.Width - 12
        });
    }

    FrameworkElement BuildTile(Item it, double tileW, double thumbH, bool overview)
    {
        var tile = new Border
        {
            Width = tileW,
            Margin = new Thickness(3),
            Padding = new Thickness(10),
            CornerRadius = new CornerRadius(20),
            BorderThickness = new Thickness(1.5),
            BorderBrush = Clear,
            Background = Clear,
            Cursor = Cursors.Hand
        };
        it.Tile = tile;

        // cabeçalho: ícone + título (+ X na visão geral)
        var head = new Grid { Height = 24 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        FrameworkElement SmallIcon(double size)
        {
            if (it.Icon != null)
                return new Image { Source = it.Icon, Width = size, Height = size };
            var letter = string.IsNullOrEmpty(it.Title) ? "•" : it.Title.Substring(0, 1).ToUpperInvariant();
            return new Border
            {
                Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Background = Frozen("#3A3A3C"),
                Child = new TextBlock
                {
                    Text = letter, Foreground = Brushes.White, FontWeight = FontWeights.Bold, FontSize = size * 0.5,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        var ico = SmallIcon(22);
        ico.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(ico);

        var txt = new TextBlock
        {
            Text = it.Title, Foreground = Theme.Fg, FontSize = 13, FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(9, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(txt, 1);
        head.Children.Add(txt);

        if (overview)
        {
            var x = new Border
            {
                Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Clear, Cursor = Cursors.Hand,
                ToolTip = "Fechar janela",
                Child = new TextBlock
                {
                    Text = "", FontFamily = IconFont, FontSize = 11, Foreground = Theme.Fg,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
            x.MouseEnter += (_, _) => x.Background = CloseHot;
            x.MouseLeave += (_, _) => x.Background = Clear;
            x.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseItem(it); };
            Grid.SetColumn(x, 2);
            head.Children.Add(x);
        }

        // miniatura
        var thumb = new Border
        {
            Height = thumbH, Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(12),
            Background = ThumbBg, ClipToBounds = true
        };
        it.ThumbBox = thumb;
        var g = new Grid();
        it.Thumb = new Image { Stretch = Stretch.Uniform, Margin = new Thickness(ThumbPad) };
        RenderOptions.SetBitmapScalingMode(it.Thumb, BitmapScalingMode.HighQuality);
        var roundImg = it.Thumb;
        roundImg.SizeChanged += (_, _) => RoundThumb(roundImg);
        var big = SmallIcon(48);
        big.HorizontalAlignment = HorizontalAlignment.Center;
        big.VerticalAlignment = VerticalAlignment.Center;
        it.Placeholder = big;
        g.Children.Add(big);
        g.Children.Add(it.Thumb);
        thumb.Child = g;

        var st = new StackPanel();
        st.Children.Add(head);
        st.Children.Add(thumb);
        tile.Child = st;

        tile.MouseEnter += (_, _) =>
        {
            int i = _items.IndexOf(it);
            if (_mode == Mode.Overview && i >= 0 && i != _sel) { _sel = i; UpdateSelection(false); }
        };
        tile.MouseLeftButtonUp += (_, e) =>
        {
            int i = _items.IndexOf(it);
            if (i >= 0) { e.Handled = true; ActivateAndClose(i); }
        };
        return tile;
    }

    void UpdateSelection(bool bringIntoView = true)
    {
        for (int i = 0; i < _items.Count; i++)
        {
            var t = _items[i].Tile;
            if (t == null) continue;
            bool on = i == _sel;
            t.Background = on ? SelBg : Clear;
            t.BorderBrush = on ? SelBorder : Clear;
        }
        if (bringIntoView && _sel >= 0 && _sel < _items.Count) _items[_sel].Tile?.BringIntoView();
    }

    // X da visão geral: fecha a janela de verdade e tira o cartão da lista
    void CloseItem(Item it)
    {
        try { N.PostMessage(it.Hwnd, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); } catch { }
        int i = _items.IndexOf(it);
        if (i < 0) return;
        if (it.Dwm != IntPtr.Zero) { N.DwmUnregisterThumbnail(it.Dwm); it.Dwm = IntPtr.Zero; }
        _items.RemoveAt(i);
        _panel.Children.Remove(it.Tile);
        if (_sel >= _items.Count) _sel = Math.Max(0, _items.Count - 1);
        ApplyWidths(_mode == Mode.Overview);
        AddEmptyText();
        UpdateSelection(false);
        UpdateHeader();
        // o cartão encolheu: refaz as prévias depois do novo layout
        Dispatcher.BeginInvoke(new Action(() => { UpdateLayout(); LayoutThumbs(); }), DispatcherPriority.Loaded);
    }

    // ================================================================ áreas de trabalho (desktops virtuais)
    // Lista lida do registro do Windows; trocar / criar / fechar usam os atalhos do próprio Windows
    // (Win+Ctrl+setas, Win+Ctrl+D, Win+Ctrl+F4), que funcionam em qualquer versão do Windows 10/11.

    static List<(Guid Id, string Name)> ListDesktops(out int current)
    {
        current = 0;
        var res = new List<(Guid, string)>();
        try
        {
            int session = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            string baseKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer";
            byte[] ids = null, cur = null;
            foreach (var path in new[] { baseKey + @"\SessionInfo\" + session + @"\VirtualDesktops", baseKey + @"\VirtualDesktops" })
            {
                using var k = Registry.CurrentUser.OpenSubKey(path);
                if (k == null) continue;
                ids = k.GetValue("VirtualDesktopIDs") as byte[];
                cur = k.GetValue("CurrentVirtualDesktop") as byte[];
                if (ids != null && ids.Length >= 16) break;
            }
            if (ids == null || ids.Length < 16) return res;

            for (int i = 0; i + 16 <= ids.Length; i += 16)
            {
                var g = new Guid(new ReadOnlySpan<byte>(ids, i, 16));
                string name = null;
                try
                {
                    using var dk = Registry.CurrentUser.OpenSubKey(baseKey + @"\VirtualDesktops\Desktops\" + g.ToString("B").ToUpperInvariant());
                    name = dk?.GetValue("Name") as string;
                }
                catch { }
                if (string.IsNullOrWhiteSpace(name)) name = "Área de Trabalho " + (res.Count + 1);
                if (cur != null && cur.Length >= 16 && g == new Guid(new ReadOnlySpan<byte>(cur, 0, 16))) current = res.Count;
                res.Add((g, name));
            }
        }
        catch { }
        return res;
    }

    // Win + Ctrl + tecla, em sequência (com pausa entre as trocas para o Windows acompanhar); roda em segundo plano
    static void WinCtrl(byte key, bool extended, int times = 1) =>
        WinCtrlSeq(new List<(byte, bool, int)> { (key, extended, times) });

    static void WinCtrlSeq(List<(byte Key, bool Ext, int Times)> steps)
    {
        Task.Run(async () =>
        {
            await Task.Delay(90);
            foreach (var st in steps)
            {
                uint ext = st.Ext ? 1u : 0u;
                for (int i = 0; i < st.Times; i++)
                {
                    N.keybd_event(0x5B, 0, 0, UIntPtr.Zero);
                    N.keybd_event(0x11, 0, 0, UIntPtr.Zero);
                    N.keybd_event(st.Key, 0, ext, UIntPtr.Zero);
                    N.keybd_event(st.Key, 0, ext | 2u, UIntPtr.Zero);
                    N.keybd_event(0x11, 0, 2u, UIntPtr.Zero);
                    N.keybd_event(0x5B, 0, 2u, UIntPtr.Zero);
                    await Task.Delay(st.Key == 0x73 ? 450 : 260);
                }
            }
        });
    }

    // Fecha QUALQUER área de trabalho: vai até ela, fecha (Win+Ctrl+F4) e volta para onde o usuário estava.
    // Ao fechar, o Windows leva as janelas dela para a vizinha e pára na área da esquerda (ou na da direita, se era a primeira).
    static void CloseDesktop(int target, int cur)
    {
        var steps = new List<(byte, bool, int)>();
        int d = target - cur;
        if (d != 0) steps.Add((d > 0 ? (byte)0x27 : (byte)0x25, true, Math.Abs(d)));
        steps.Add((0x73, false, 1));
        if (target != cur)
        {
            int landing = target > 0 ? target - 1 : 0;
            int back = cur > target ? cur - 1 : cur;
            int d2 = back - landing;
            if (d2 != 0) steps.Add((d2 > 0 ? (byte)0x27 : (byte)0x25, true, Math.Abs(d2)));
        }
        WinCtrlSeq(steps);
    }

    void UpdateHeader() =>
        _header.Text = _items.Count == 0 ? "JANELAS" : "JANELAS · " + _items.Count;

    void BuildDesktops(bool overview)
    {
        _desks.Children.Clear();
        _desks.Visibility = overview ? Visibility.Visible : Visibility.Collapsed;
        if (!overview) return;

        var list = ListDesktops(out int cur);
        Brush accent = Theme.W(0xF2), dark = Theme.Light ? Brushes.White : Frozen("#FF111111");

        // botões no mesmo estilo dos botões do lançador de apps (pílula de vidro; o atual fica branco)
        for (int i = 0; i < list.Count; i++)
        {
            int idx = i;
            bool isCur = i == cur;
            var fg = isCur ? dark : Theme.Fg;
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            row.Children.Add(new TextBlock
            {
                Text = "\uE7F4", FontFamily = IconFont, FontSize = 13, Foreground = fg,
                VerticalAlignment = VerticalAlignment.Center
            });
            row.Children.Add(new TextBlock
            {
                Text = list[i].Name, Foreground = fg, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
            if (list.Count > 1)
            {
                var hover = isCur ? (Theme.Light ? Theme.W(0x33) : Frozen("#26000000")) : Theme.W(0x33);
                var x = new Border
                {
                    Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Margin = new Thickness(12, 0, -8, 0),
                    Background = Clear, Cursor = Cursors.Hand, ToolTip = "Fechar esta área de trabalho",
                    Child = new TextBlock
                    {
                        Text = "\uE711", FontFamily = IconFont, FontSize = 10, Foreground = fg,
                        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                    }
                };
                x.MouseEnter += (_, _) => x.Background = hover;
                x.MouseLeave += (_, _) => x.Background = Clear;
                x.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseUi(); CloseDesktop(idx, cur); };
                row.Children.Add(x);
            }
            var chip = new Border
            {
                Height = 42, Padding = new Thickness(18, 0, 18, 0), Margin = new Thickness(0, 0, 10, 0),
                CornerRadius = new CornerRadius(14), Child = row, Cursor = isCur ? Cursors.Arrow : Cursors.Hand,
                Background = isCur ? accent : PillBg
            };
            if (!isCur)
            {
                chip.MouseEnter += (_, _) => chip.Background = PillHover;
                chip.MouseLeave += (_, _) => chip.Background = PillBg;
                chip.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    int diff = idx - cur;
                    CloseUi();
                    if (diff != 0) WinCtrl(diff > 0 ? (byte)0x27 : (byte)0x25, true, Math.Abs(diff));
                };
            }
            _desks.Children.Add(chip);
        }

        // "+ Nova área de trabalho": igual ao botão "+ Adicionar app" do lançador
        var add = new Border
        {
            Height = 42, Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(14),
            Background = PillBg, Cursor = Cursors.Hand,
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "\uE710", FontFamily = IconFont, FontSize = 13, Foreground = Theme.Fg, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = "Nova área de trabalho", Foreground = Theme.Fg, FontSize = 13.5, FontWeight = FontWeights.SemiBold,
                                    Margin = new Thickness(9, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center }
                }
            }
        };
        add.MouseEnter += (_, _) => add.Background = PillHover;
        add.MouseLeave += (_, _) => add.Background = PillBg;
        add.MouseLeftButtonUp += (_, e) => { e.Handled = true; CloseUi(); WinCtrl(0x44 /* D */, false); };
        _desks.Children.Add(add);
    }

    // ================================================================ miniaturas

    void StartCaptures()
    {
        int gen = _gen;
        var list = _items.Where(i => !i.Minimized && i.Dwm == IntPtr.Zero).ToList();
        if (list.Count == 0) return;
        // a janela escolhida primeiro
        if (_sel >= 0 && _sel < _items.Count)
        {
            var first = _items[_sel];
            if (list.Remove(first)) list.Insert(0, first);
        }
        bool overview = _mode == Mode.Overview;
        int maxW = overview ? 1100 : 480, maxH = overview ? 700 : 300;

        Task.Run(() =>
        {
            foreach (var it in list)
            {
                if (gen != _gen) return;
                var bs = Capture(it.Hwnd, maxW, maxH);
                if (bs == null) continue;
                var item = it;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (gen != _gen) return;
                    item.Thumb.Source = bs;
                    RoundThumb(item.Thumb);
                    item.Placeholder.Visibility = Visibility.Collapsed;
                }));
            }
        });
    }

    // Foto da janela (mesmo escondida atrás de outras), reduzida
    static BitmapSource Capture(IntPtr h, int maxW, int maxH)
    {
        if (!N.GetWindowRect(h, out var r)) return null;
        int w = r.Right - r.Left, ht = r.Bottom - r.Top;
        if (w < 8 || ht < 8 || w > 12000 || ht > 12000) return null;

        IntPtr sdc = N.GetDC(IntPtr.Zero);
        IntPtr mdc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        IntPtr ddc = IntPtr.Zero, dib = IntPtr.Zero, dold = IntPtr.Zero;
        try
        {
            mdc = N.CreateCompatibleDC(sdc);
            bmp = N.CreateCompatibleBitmap(sdc, w, ht);
            if (mdc == IntPtr.Zero || bmp == IntPtr.Zero) return null;
            old = N.SelectObject(mdc, bmp);
            if (!N.PrintWindow(h, mdc, 2 /* PW_RENDERFULLCONTENT */) && !N.PrintWindow(h, mdc, 0)) return null;

            double k = Math.Min(1.0, Math.Min((double)maxW / w, (double)maxH / ht));
            int tw = Math.Max(1, (int)(w * k)), th = Math.Max(1, (int)(ht * k));

            var bi = new N.BMIH { Size = 40, Width = tw, Height = -th, Planes = 1, BitCount = 32 };
            ddc = N.CreateCompatibleDC(sdc);
            dib = N.CreateDIBSection(sdc, ref bi, 0, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            dold = N.SelectObject(ddc, dib);
            N.SetStretchBltMode(ddc, 4 /* HALFTONE */);
            N.StretchBlt(ddc, 0, 0, tw, th, mdc, 0, 0, w, ht, 0x00CC0020 /* SRCCOPY */);

            var buf = new byte[tw * th * 4];
            Marshal.Copy(bits, buf, 0, buf.Length);
            var bs = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgr32, null, buf, tw * 4);
            bs.Freeze();
            return bs;
        }
        catch { return null; }
        finally
        {
            if (dold != IntPtr.Zero) N.SelectObject(ddc, dold);
            if (dib != IntPtr.Zero) N.DeleteObject(dib);
            if (ddc != IntPtr.Zero) N.DeleteDC(ddc);
            if (old != IntPtr.Zero) N.SelectObject(mdc, old);
            if (bmp != IntPtr.Zero) N.DeleteObject(bmp);
            if (mdc != IntPtr.Zero) N.DeleteDC(mdc);
            N.ReleaseDC(IntPtr.Zero, sdc);
        }
    }
}
