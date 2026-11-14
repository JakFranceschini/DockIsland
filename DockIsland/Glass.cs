using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DockIsland;

// Fundo de "vidro fosco" (transparência + blur) para as superfícies do programa.
//
// Como funciona: as janelas são transparentes (AllowsTransparency) e o Windows não borra o que
// está atrás delas nesse modo. Então, para cada janela, o programa:
//   1) se exclui da captura de tela (SetWindowDisplayAffinity), para "enxergar através" de si mesmo;
//   2) tira uma foto da área da tela embaixo da janela, reduz e borra;
//   3) usa essa foto borrada (+ uma película de cor translúcida) como Background dos Borders marcados
//      com  local:Glass.Blur="True".  A cor/transparência é a do próprio Background escrito no XAML
//      (ex.: #99000000 = preto com 60% de opacidade).
// A foto é refeita a cada RefreshMs e acompanha o movimento/tamanho do Border (animações da ilha, dock...).
// Se a captura não for possível (Windows muito antigo, etc.), o Background translúcido do XAML continua
// valendo, só sem o blur.
public static class Glass
{
    // ---- ajustes ----
    const int RefreshMs = 2500;   // de quanto em quanto tempo o fundo é refeito (menor = acompanha mais rápido, gasta mais)
    const int BaseDown = 12;     // fator de redução antes do blur (maior = blur mais forte e mais leve)
    const int Passes = 3;        // passadas do blur (maior = mais suave)
    const int Radius = 3;        // raio de cada passada (na imagem reduzida)
    const int FastMs = 150;       // refaz o fundo nesse ritmo logo depois de uma mudança grande (trocar de janela, ir à área de trabalho...)
    const int FastForMs = 2000;   // por quanto tempo esse ritmo rápido dura
    const int HeavyDown = 6;      // blur do fundo do Win+Tab: leve, o wallpaper continua reconhecível (maior = mais desfocado)
    const int HeavyRadius = 2;    // raio do blur do Win+Tab
    const int ExcludeWaitMs = 60; // quanto tempo as janelas ficam "fora da captura" antes de cada foto

    public static readonly DependencyProperty BlurProperty =
        DependencyProperty.RegisterAttached("Blur", typeof(bool), typeof(Glass), new PropertyMetadata(false, OnBlurChanged));

    // Glass.Heavy="True" (junto com Blur): usa o desfoque forte. Vale para a janela inteira enquanto o Border estiver registrado.
    public static readonly DependencyProperty HeavyProperty =
        DependencyProperty.RegisterAttached("Heavy", typeof(bool), typeof(Glass), new PropertyMetadata(false));
    public static bool GetHeavy(DependencyObject d) => (bool)d.GetValue(HeavyProperty);
    public static void SetHeavy(DependencyObject d, bool v) => d.SetValue(HeavyProperty, v);

    public static bool GetBlur(DependencyObject d) => (bool)d.GetValue(BlurProperty);
    public static void SetBlur(DependencyObject d, bool v) => d.SetValue(BlurProperty, v);

    // ---------------------------------------------------------------- Win32
    [StructLayout(LayoutKind.Sequential)]
    struct RC { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct BMIH
    {
        public int Size, Width, Height;
        public short Planes, BitCount;
        public int Compression, SizeImage, XPels, YPels, ClrUsed, ClrImportant;
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RC r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")]
    static extern IntPtr CreateDIBSection(IntPtr dc, ref BMIH bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, uint rop);

    const uint WDA_EXCLUDEFROMCAPTURE = 0x11;            // Windows 10 2004+
    const uint SRCCOPY_CAPTUREBLT = 0x00CC0020 | 0x40000000;

    // ---------------------------------------------------------------- registro
    sealed class Entry
    {
        public Border B;
        public Brush Orig;          // Background original (a película translúcida escrita no XAML)
        public Color Tint;
        public Color Base;          // cor escrita no XAML (escura); Tint é a cor do tema atual
        public bool Heavy;          // desfoque forte
        public DrawingBrush Fill;   // pincel com a foto borrada (null = ainda não há foto)
        public Rect Vp;
    }

    sealed class Surface
    {
        public HwndSource Src;
        public IntPtr Hwnd;
        public bool Affinity;
        public readonly List<Entry> Items = new();
        public bool Dirty = true;
        public bool Due;
        public bool Excl;       // está fora da captura agora
        public bool Periodic;   // refaz a foto de tempos em tempos (só ilha e dock)
        public long Last;
        public RC Box;
        public byte[] Buf;
        public ImageDrawing Img;
        public double W, H;
        public int Hash;
    }

    static readonly List<Surface> Surfaces = new();
    static DispatcherTimer _timer;

    static void OnBlurChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Border b) return;
        b.Loaded -= OnLoaded;
        b.Unloaded -= OnUnloaded;
        if ((bool)e.NewValue)
        {
            b.Loaded += OnLoaded;
            b.Unloaded += OnUnloaded;
            if (b.IsLoaded) Register(b);
        }
        else Unregister(b);
    }

    // No tema claro o vidro escuro (preto translúcido) vira um vidro claro mais opaco
    static Color MapTint(Color c)
    {
        if (!Theme.Light || c.R != 0 || c.G != 0 || c.B != 0) return c;
        return Color.FromArgb((byte)Math.Min(0xE6, c.A + 0x52), 0xF6, 0xF6, 0xF8);
    }

    /// Reaplica a cor do vidro em todas as janelas (troca de tema)
    public static void Retint()
    {
        try
        {
            foreach (var s in Surfaces)
            {
                foreach (var en in s.Items)
                {
                    en.Tint = MapTint(en.Base);
                    if (s.Img != null) Apply(s, en);
                }
                s.Dirty = true;
            }
        }
        catch { }
    }

    static void OnLoaded(object sender, RoutedEventArgs e) => Register((Border)sender);
    static void OnUnloaded(object sender, RoutedEventArgs e) => Unregister((Border)sender);

    static void Register(Border b)
    {
        try
        {
            var src = PresentationSource.FromVisual(b) as HwndSource;
            if (src == null || src.Handle == IntPtr.Zero) return;

            var s = Surfaces.Find(x => x.Src == src);
            if (s != null && s.Items.Exists(i => i.B == b)) return;

            var orig = b.Background;
            var en = new Entry
            {
                B = b,
                Orig = orig,
                Heavy = GetHeavy(b),
                Base = (orig as SolidColorBrush)?.Color ?? Color.FromArgb(0x99, 0, 0, 0)
            };
            en.Tint = MapTint(en.Base);

            if (s == null)
            {
                s = new Surface { Src = src, Hwnd = src.Handle };
                s.Periodic = src.RootVisual is MainWindow || src.RootVisual is DockWindow;
                s.Affinity = SetWindowDisplayAffinity(s.Hwnd, WDA_EXCLUDEFROMCAPTURE);
                if (s.Affinity) SetWindowDisplayAffinity(s.Hwnd, 0);   // só fica excluída durante a foto (ver Tick)
                Surfaces.Add(s);
            }
            s.Items.Add(en);
            s.Dirty = true;
            if (s.Affinity && s.Img != null) Apply(s, en);
            EnsureTimer();
        }
        catch { }
    }

    static void Unregister(Border b)
    {
        for (int i = Surfaces.Count - 1; i >= 0; i--)
        {
            var s = Surfaces[i];
            int k = s.Items.FindIndex(x => x.B == b);
            if (k < 0) continue;
            var en = s.Items[k];
            s.Items.RemoveAt(k);
            try { b.Background = en.Orig; } catch { }
            if (s.Items.Count == 0) Surfaces.RemoveAt(i);
        }
        if (Surfaces.Count == 0) _timer?.Stop();
    }

    static void EnsureTimer()
    {
        if (_timer == null)
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
            _timer.Tick += (_, _) => Tick();
        }
        if (!_timer.IsEnabled) _timer.Start();
    }

    // ---------------------------------------------------------------- laço principal
    /// Lançador na tela: a ilha e o dock não pedem fotos novas (cada foto liga/desliga a exclusão de captura das janelas
    /// visíveis, e isso fazia o lançador piscar)
    public static volatile bool Quiet;

    static bool _excluded;
    static long _excludeAt;

    // As janelas só ficam excluídas da captura de tela durante a foto (~60 ms a cada RefreshMs);
    // no resto do tempo aparecem normalmente em prints e gravações.
    static void SetExcluded(bool on)
    {
        _excluded = on;
        foreach (var s in Surfaces)
        {
            if (!s.Affinity) continue;
            if (on) { if (!s.Due && !IsWindowVisible(s.Hwnd)) continue; s.Excl = true; }   // todas as janelas visíveis saem da foto: o menu/popup borra o fundo de verdade, não a janela que está por baixo
            else { if (!s.Excl) continue; s.Excl = false; }
            try { SetWindowDisplayAffinity(s.Hwnd, on ? WDA_EXCLUDEFROMCAPTURE : 0); } catch { }
        }
    }

    // ---- ferramentas de captura de tela: enquanto uma estiver em uso, nenhuma janela sai da captura ----
    static long _holdUntil;
    static IntPtr _fg;
    static long _fastUntil;

    // Pede fotos novas do fundo em ritmo rápido por um instante (o fundo atrás da ilha/dock mudou)
    public static void Refresh() => _fastUntil = Environment.TickCount64 + FastForMs;
    static bool _fgIsTool;
    static readonly string[] CaptureTools =
        { "screenclippinghost", "snippingtool", "screensketch", "snipandsketch", "obs64", "obs32", "sharex", "greenshot", "lightshot", "picpick", "screenpresso" };

    static bool IsCaptureToolWindow(IntPtr h)
    {
        try
        {
            if (h == IntPtr.Zero) return false;
            GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return false;
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            string n = p.ProcessName.ToLowerInvariant();
            foreach (var t in CaptureTools) if (n == t) return true;
        }
        catch { }
        return false;
    }

    static bool CaptureToolBusy(long now)
    {
        // atalhos: PrintScreen e Win+Shift(+S)
        bool win = (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;
        bool shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        if ((GetAsyncKeyState(0x2C) & 0x8000) != 0 || (win && shift)) _holdUntil = now + 6000;

        // programa de captura na frente
        var fg = GetForegroundWindow();
        if (fg != _fg)
        {
            _fg = fg; _fgIsTool = IsCaptureToolWindow(fg);
            _fastUntil = now + FastForMs;   // janela da frente mudou (ex.: foi para a área de trabalho): o fundo mudou junto
        }
        if (_fgIsTool) _holdUntil = Math.Max(_holdUntil, now + 2000);

        return now < _holdUntil;
    }

    static void Tick()
    {
        long now = Environment.TickCount64;

        if (CaptureToolBusy(now))
        {
            // captura de tela em andamento: devolve as janelas na hora e não tira foto agora
            if (_excluded)
            {
                foreach (var x in Surfaces) x.Due = false;
                SetExcluded(false);
            }
        }
        else if (_excluded)
        {
            // fase 2: as janelas já saíram da captura -> tira a foto das que precisam e volta ao normal
            if (now - _excludeAt >= ExcludeWaitMs)
            {
                for (int i = 0; i < Surfaces.Count; i++)
                {
                    var s = Surfaces[i];
                    if (!s.Due) continue;
                    s.Due = false;
                    try
                    {
                        if (s.Src.IsDisposed || !IsWindowVisible(s.Hwnd) || !GetWindowRect(s.Hwnd, out var r) || r.Left <= -10000 || r.Top <= -10000) continue;
                        Capture(s, r, now);
                    }
                    catch { }
                }
                SetExcluded(false);
            }
        }
        else
        {
            // fase 1: vê quem precisa de foto nova; se alguém precisar, tira todas as janelas da captura
            bool any = false;
            for (int i = 0; i < Surfaces.Count; i++)
            {
                var s = Surfaces[i];
                s.Due = false;
                try
                {
                    if (!s.Affinity || s.Src.IsDisposed) continue;
                    if (Quiet && s.Periodic) continue;

                    if (!IsWindowVisible(s.Hwnd))
                    {
                        // janela escondida: guarda a foto (ela é refeita quando a janela voltar)
                        continue;
                    }

                    if (!GetWindowRect(s.Hwnd, out var r)) continue;
                    if (r.Left <= -10000 || r.Top <= -10000) continue;   // fora da tela (aquecimento/abertura): não fotografa
                    bool moved = Math.Abs(r.Left - s.Box.Left) > 2 || Math.Abs(r.Top - s.Box.Top) > 2 || Math.Abs(r.Right - s.Box.Right) > 2 || Math.Abs(r.Bottom - s.Box.Bottom) > 2;
                    if (s.Dirty || moved || (s.Periodic && now - s.Last >= (now < _fastUntil ? FastMs : RefreshMs))) { s.Due = true; any = true; }
                }
                catch { }
            }
            if (any) { SetExcluded(true); _excludeAt = now; }
        }

        // acompanha o movimento/tamanho dos Borders (animações) com a última foto
        for (int i = 0; i < Surfaces.Count; i++)
        {
            var s = Surfaces[i];
            try
            {
                if (s.Img == null || s.Src.IsDisposed) continue;
                for (int k = 0; k < s.Items.Count; k++) Track(s, s.Items[k]);
            }
            catch { }
        }
    }

    // Tira a foto ANTES de a janela aparecer (janela escondida, na posição/tamanho finais), para ela já abrir
    // com o blur pronto, sem a "piscada" de trocar o fundo translúcido simples pelo borrado.
    public static void Prepare(Window w)
    {
        try
        {
            var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            var s = Surfaces.Find(x => x.Hwnd == h);
            if (s == null || !s.Affinity || s.Src.IsDisposed) return;
            var ct = s.Src.CompositionTarget;
            if (ct == null) return;
            double k = ct.TransformToDevice.M11;
            if (k <= 0) k = 1;
            int x0 = (int)Math.Round(w.Left * k), y0 = (int)Math.Round(w.Top * k);
            var r = new RC { Left = x0, Top = y0, Right = x0 + (int)Math.Round(w.Width * k), Bottom = y0 + (int)Math.Round(w.Height * k) };
            Capture(s, r, Environment.TickCount64);
        }
        catch { }
    }

    static void Capture(Surface s, RC r, long now)
    {
        s.Dirty = false;
        s.Last = now;
        s.Box = r;

        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w < 2 || h < 2) return;

        var ct = s.Src.CompositionTarget;
        if (ct == null) return;
        double k = ct.TransformToDevice.M11;
        if (k <= 0) k = 1;

        var full = Grab(s, r.Left, r.Top, w, h);
        if (full == null) return;

        bool heavy = false;
        foreach (var en0 in s.Items) if (en0.Heavy) { heavy = true; break; }
        int down = Math.Max(4, (int)Math.Round((heavy ? HeavyDown : BaseDown) * k));
        int dw = Math.Max(1, w / down), dh = Math.Max(1, h / down);
        var px = Downscale(full, w, h, dw, dh);
        for (int i = 0; i < Passes; i++) BoxBlur(px, dw, dh, heavy ? HeavyRadius : Radius);

        // nada mudou desde a última foto: não precisa refazer os pincéis
        int hash = 17;
        for (int i = 0; i < px.Length; i += 3) hash = unchecked(hash * 31 + px[i]);
        double dipW = w / k, dipH = h / k;
        if (s.Img != null && hash == s.Hash && Math.Abs(dipW - s.W) < 0.01 && Math.Abs(dipH - s.H) < 0.01) return;
        s.Hash = hash;

        var bmp = BitmapSource.Create(dw, dh, 96, 96, PixelFormats.Bgr32, null, px, dw * 4);
        bmp.Freeze();
        var img = new ImageDrawing(bmp, new Rect(0, 0, dipW, dipH));
        img.Freeze();

        s.W = dipW;
        s.H = dipH;
        s.Img = img;
        foreach (var en in s.Items) Apply(s, en);
    }

    // ---------------------------------------------------------------- pincéis
    // Retângulo da janela inteira, escrito nas coordenadas do próprio Border (acompanha escala/deslocamento)
    static Rect LocalRect(Surface s, Entry en)
    {
        var root = s.Src.RootVisual as Visual;
        if (root == null) return Rect.Empty;
        return root.TransformToDescendant(en.B).TransformBounds(new Rect(0, 0, s.W, s.H));
    }

    static void Apply(Surface s, Entry en)
    {
        try
        {
            var vp = LocalRect(s, en);
            if (vp.IsEmpty || double.IsNaN(vp.X) || double.IsNaN(vp.Y)) return;

            var tint = new SolidColorBrush(en.Tint);
            tint.Freeze();
            var g = new DrawingGroup();
            g.Children.Add(s.Img);
            g.Children.Add(new GeometryDrawing(tint, null, new RectangleGeometry(new Rect(0, 0, s.W, s.H))));
            g.Freeze();

            var br = new DrawingBrush(g)
            {
                Stretch = Stretch.Fill,
                TileMode = TileMode.None,
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = vp
            };
            en.Fill = br;
            en.Vp = vp;
            en.B.Background = br;
        }
        catch { }
    }

    // Mantém a foto "parada na tela" mesmo quando o Border se move/cresce (animações)
    static void Track(Surface s, Entry en)
    {
        if (en.Fill == null) { Apply(s, en); return; }
        Rect vp;
        try { vp = LocalRect(s, en); } catch { return; }
        if (vp.IsEmpty || double.IsNaN(vp.X) || double.IsNaN(vp.Y)) return;
        if (Math.Abs(vp.X - en.Vp.X) < 0.05 && Math.Abs(vp.Y - en.Vp.Y) < 0.05 &&
            Math.Abs(vp.Width - en.Vp.Width) < 0.05 && Math.Abs(vp.Height - en.Vp.Height) < 0.05) return;
        en.Fill.Viewport = vp;
        en.Vp = vp;
    }

    // ---------------------------------------------------------------- captura e blur
    static byte[] Grab(Surface s, int x, int y, int w, int h)
    {
        IntPtr scr = GetDC(IntPtr.Zero);
        if (scr == IntPtr.Zero) return null;
        IntPtr mem = IntPtr.Zero, dib = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            mem = CreateCompatibleDC(scr);
            var bi = new BMIH { Size = Marshal.SizeOf<BMIH>(), Width = w, Height = -h, Planes = 1, BitCount = 32 };
            dib = CreateDIBSection(scr, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero) return null;
            old = SelectObject(mem, dib);
            if (!BitBlt(mem, 0, 0, w, h, scr, x, y, SRCCOPY_CAPTUREBLT)) return null;

            int len = w * h * 4;
            if (s.Buf == null || s.Buf.Length != len) s.Buf = new byte[len];
            Marshal.Copy(bits, s.Buf, 0, len);
            return s.Buf;
        }
        catch { return null; }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, scr);
        }
    }

    // Média por blocos (olhando 1 pixel em cada 2) -> imagem pequena em BGRA
    static byte[] Downscale(byte[] src, int w, int h, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (int dy = 0; dy < dh; dy++)
        {
            int y0 = dy * h / dh, y1 = Math.Max(y0 + 1, (dy + 1) * h / dh);
            for (int dx = 0; dx < dw; dx++)
            {
                int x0 = dx * w / dw, x1 = Math.Max(x0 + 1, (dx + 1) * w / dw);
                int b = 0, g = 0, r = 0, n = 0;
                for (int yy = y0; yy < y1; yy += 2)
                {
                    int row = yy * w * 4;
                    for (int xx = x0; xx < x1; xx += 2)
                    {
                        int i = row + xx * 4;
                        b += src[i]; g += src[i + 1]; r += src[i + 2]; n++;
                    }
                }
                int o = (dy * dw + dx) * 4;
                dst[o] = (byte)(b / n);
                dst[o + 1] = (byte)(g / n);
                dst[o + 2] = (byte)(r / n);
                dst[o + 3] = 255;
            }
        }
        return dst;
    }

    static void BoxBlur(byte[] px, int w, int h, int r)
    {
        var tmp = new byte[px.Length];
        BlurPass(px, tmp, w, h, r, true);
        BlurPass(tmp, px, w, h, r, false);
    }

    static void BlurPass(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int len = horizontal ? w : h;
        int lines = horizontal ? h : w;
        int step = horizontal ? 4 : w * 4;
        int lineStep = horizontal ? w * 4 : 4;
        int div = 2 * r + 1;

        for (int l = 0; l < lines; l++)
        {
            int baseIdx = l * lineStep;
            for (int c = 0; c < 3; c++)
            {
                int sum = 0;
                for (int i = -r; i <= r; i++) sum += src[baseIdx + Math.Clamp(i, 0, len - 1) * step + c];
                for (int i = 0; i < len; i++)
                {
                    dst[baseIdx + i * step + c] = (byte)(sum / div);
                    int add = Math.Min(i + r + 1, len - 1);
                    int sub = Math.Max(i - r, 0);
                    sum += src[baseIdx + add * step + c] - src[baseIdx + sub * step + c];
                }
            }
        }
    }
}
