using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DockIsland;

// Barra flutuante de ações (Recortar / Copiar / Colar / Pesquisar...) que aparece ao lado do texto selecionado.
// Visual da ilha: vidro escuro com blur (Glass), cantos arredondados, borda clara fina.
// A janela NUNCA ativa: o foco continua no programa onde o texto está selecionado, então
// os botões mandam Ctrl+X / Ctrl+C / Ctrl+V para ele e a seleção não se perde.
public partial class SelectionWindow : Window
{
    const int GWL_EXSTYLE = -20;
    const int WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOOLWINDOW = 0x00000080;
    const uint SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, SWP_NOZORDER = 0x0004;

    [StructLayout(LayoutKind.Sequential)] struct RC { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RC r);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    IntPtr _hwnd;

    public SelectionWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        var ex = GetWindowLongPtr(_hwnd, GWL_EXSTYLE).ToInt64();
        SetWindowLongPtr(_hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW));
    }

    // ---------------------------------------------------------------- conteúdo
    public static FontFamily IconFont { get; } = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");

    public void Clear() => Bar.Children.Clear();

    public void AddButton(string glyph, string label, string tip, Action action)
    {
        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text = glyph, FontFamily = IconFont, FontSize = 14, Foreground = Theme.Fg,
            VerticalAlignment = VerticalAlignment.Center
        });
        if (!string.IsNullOrEmpty(label))
            sp.Children.Add(new TextBlock
            {
                Text = label, FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Theme.Fg,
                Margin = new Thickness(7, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
        var b = new Button { Style = (Style)FindResource("SelBtn"), Content = sp };
        if (!string.IsNullOrEmpty(tip)) b.ToolTip = tip;
        b.Click += (_, __) => action();
        Bar.Children.Add(b);
    }

    public void AddSeparator()
    {
        Bar.Children.Add(new Border
        {
            Width = 1, Height = 18, Margin = new Thickness(3, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = Theme.W(0x2E)
        });
    }

    // ---------------------------------------------------------------- posição / exibição
    public bool IsShown { get; private set; }

    // retângulo da janela em pixels (para saber se um clique caiu nela)
    public bool ContainsPoint(int x, int y)
    {
        if (!IsShown || _hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var r)) return false;
        return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom;
    }

    // tamanho em pixels que a janela vai ter com o conteúdo atual
    public Size MeasurePx()
    {
        Card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double k = Dpi();
        return new Size(Card.DesiredSize.Width * k, Card.DesiredSize.Height * k);
    }

    // margem do Card (o vidro fica 10 DIPs para dentro da janela)
    public double MarginPx => Card.Margin.Left * Dpi();

    double Dpi()
    {
        try { return VisualTreeHelper.GetDpi(this).DpiScaleX; } catch { return 1; }
    }

    bool _everShown;

    public void ShowAtPx(int x, int y)
    {
        double k = Dpi();
        bool wasShown = IsShown;

        // tamanho explícito (o Glass.Prepare precisa dele para tirar a foto do fundo na área certa)
        Card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Width = Math.Ceiling(Card.DesiredSize.Width);
        Height = Math.Ceiling(Card.DesiredSize.Height);
        Left = x / k;
        Top = y / k;
        IsShown = true;

        if (wasShown) { Glass.Refresh(); return; }

        // começa invisível: só aparece depois que o fundo borrado estiver pronto (sem piscada)
        Card.BeginAnimation(OpacityProperty, null);
        Card.Opacity = 0;

        if (_everShown)
        {
            Glass.Prepare(this);   // janela escondida: foto limpa do fundo, na posição e tamanho finais
            Show();
            FadeIn();
        }
        else
        {
            // primeira vez: o Glass só registra a janela depois do Loaded; espera isso e então tira a foto
            _everShown = true;
            Show();
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!IsShown) return;
                Glass.Prepare(this);
                    FadeIn();
            }), System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    // só opacidade: sem mexer na geometria, o fundo borrado (foto parada na tela) não "nada" atrás do menu
    void FadeIn() => Card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));

    public void HideNow()
    {
        if (!IsShown) return;
        IsShown = false;
        try
        {
            Card.BeginAnimation(OpacityProperty, null);
            Hide();
        }
        catch { }
    }
}
