using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DockIsland;

public partial class MainWindow
{
    // Antes de criar a janela: aplica o tema salvo (a ilha já abre com as cores certas)
    public static void ApplySavedTheme()
    {
        var s = LoadSettings();
        Theme.Apply(s.LightTheme, Theme.ParseColor(s.Accent, Theme.Accent));
    }

    // Reaplica o tema em tudo o que é pintado por código
    void RefreshTheme()
    {
        Theme.Apply(_settings.LightTheme, Theme.ParseColor(_settings.Accent, Theme.Accent));   // também refaz o vidro de todas as janelas
        SelectPage(_page);
        ApplySettingsTab();
        UpdateDockSizeButtons();
        UpdateThemeButtons();
        BuildAccentSwatches();
        UpdateClock();
        if (_page == 4) RefreshSystem();
    }

    void InitThemeUi()
    {
        UpdateThemeButtons();
        BuildAccentSwatches();
    }

    void UpdateThemeButtons()
    {
        ThemeDarkBtn.Background = !_settings.LightTheme ? Theme.TabActive : Brushes.Transparent;
        ThemeDarkBtn.Foreground = !_settings.LightTheme ? Theme.OnAccent : Theme.TabInactive;
        ThemeLightBtn.Background = _settings.LightTheme ? Theme.TabActive : Brushes.Transparent;
        ThemeLightBtn.Foreground = _settings.LightTheme ? Theme.OnAccent : Theme.TabInactive;
    }

    void Theme_Click(object sender, RoutedEventArgs e)
    {
        _settings.LightTheme = (string)((Button)sender).Tag == "light";
        SaveSettings();
        RefreshTheme();
    }

    void BuildAccentSwatches()
    {
        AccentSwatches.Children.Clear();
        foreach (var (name, hex) in Theme.Palette)
        {
            bool sel = string.Equals(hex, _settings.Accent, System.StringComparison.OrdinalIgnoreCase);
            var dot = new Ellipse
            {
                Width = 20, Height = 20,
                Fill = new SolidColorBrush(Theme.ParseColor(hex, Colors.Gray)),
                Stroke = sel ? Theme.W(0xFF) : Brushes.Transparent,
                StrokeThickness = 2
            };
            var host = new Border
            {
                Width = 28, Height = 28, Margin = new Thickness(0, 0, 4, 0), Background = Brushes.Transparent,
                Cursor = Cursors.Hand, Child = dot, Tag = hex, ToolTip = name
            };
            host.MouseLeftButtonUp += (s, _) =>
            {
                _settings.Accent = (string)((Border)s).Tag;
                SaveSettings();
                RefreshTheme();
            };
            AccentSwatches.Children.Add(host);
        }
    }
}
