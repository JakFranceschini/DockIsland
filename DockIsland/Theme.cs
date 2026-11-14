using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace DockIsland;

// Tema da ilha: claro/escuro + cor de destaque. As cores ficam em recursos dinâmicos (App.Resources),
// então trocar o tema atualiza a tela na hora, sem reiniciar.
//   FgBrush         texto principal            W_xx   "branco" com transparência xx (no tema claro vira preto)
//   AccentBrush     cor de destaque            AccentSelBrush  destaque a 50% (seleção de texto)
static class Theme
{
    public static bool Light { get; private set; }
    // avisa as janelas (lançador etc.) para repintarem o que foi montado por código
    public static event System.Action Changed;
    public static Color Accent { get; private set; } = Color.FromRgb(0x0A, 0x84, 0xFF);

    // transparências usadas no XAML (W_xx)
    static readonly byte[] Alphas = { 0x14, 0x18, 0x1A, 0x1F, 0x22, 0x24, 0x26, 0x2E, 0x33, 0x38, 0x3D, 0x40, 0x4D, 0x55, 0x66, 0x80, 0x99, 0xA0, 0xB0, 0xCC, 0xE6, 0xF2, 0xFF };

    public static readonly (string Name, string Hex)[] Palette =
    {
        ("Azul", "#0A84FF"), ("Roxo", "#BF5AF2"), ("Rosa", "#FF375F"), ("Vermelho", "#FF453A"),
        ("Laranja", "#FF9F0A"), ("Verde", "#30D158"), ("Ciano", "#40C8E0"),
    };

    static readonly Dictionary<byte, SolidColorBrush> _w = new();
    static SolidColorBrush _fg, _accent, _accentSoft;

    static SolidColorBrush Frz(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }

    public static Color Base => Light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White;

    // Cor de fundo (vidro) da ilha

    // "branco" com transparência a: no tema claro, o tom escuro correspondente
    public static SolidColorBrush W(byte a)
    {
        if (!_w.TryGetValue(a, out var b)) _w[a] = b = Frz(Color.FromArgb(a, Base.R, Base.G, Base.B));
        return b;
    }
    public static SolidColorBrush Fg => _fg ?? Frz(Colors.White);
    public static SolidColorBrush AccentBrush => _accent ?? Frz(Accent);
    public static SolidColorBrush TabActive => _accentSoft ?? Frz(Color.FromArgb(0x73, Accent.R, Accent.G, Accent.B));
    public static SolidColorBrush TabInactive => W(0xA0);
    // texto sobre a cor de destaque (aba selecionada)
    public static readonly SolidColorBrush OnAccent = Frz(Colors.White);

    public static Color ParseColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }

    public static void Apply(bool light, Color accent)
    {
        Light = light;
        Accent = accent;
        _w.Clear();
        _fg = Frz(Base);
        _accent = Frz(accent);
        _accentSoft = Frz(Color.FromArgb(0x73, accent.R, accent.G, accent.B));

        var res = Application.Current?.Resources;
        if (res == null) return;
        res["FgBrush"] = _fg;
        res["InvFgBrush"] = Frz(Light ? Colors.White : Colors.Black);   // texto sobre o botão "cheio" (Salvar)
        res["AccentBrush"] = _accent;
        res["AccentSelBrush"] = Frz(Color.FromArgb(0x80, accent.R, accent.G, accent.B));
        foreach (var a in Alphas) res["W_" + a.ToString("X2")] = W(a);
        Glass.Retint();
        Changed?.Invoke();
    }
}
