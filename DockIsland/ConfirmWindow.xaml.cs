using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace DockIsland;

// Popup "Tem certeza?" no estilo da ilha (desligar / reiniciar)
public partial class ConfirmWindow : Window
{
    public ConfirmWindow(bool restart)
    {
        InitializeComponent();

        if (restart)
        {
            IconGlyph.Text = "\uE72C";
            TitleText.Text = "Reiniciar o computador?";
            OkBtn.Content = "Reiniciar";
        }
        else
        {
            IconGlyph.Text = "\uE7E8";
            TitleText.Text = "Desligar o computador?";
            OkBtn.Content = "Desligar";
        }

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; DialogResult = false; }
            else if (e.Key == Key.Enter) { e.Handled = true; DialogResult = true; }
        };

        Loaded += (_, __) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)));
            CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            Activate();
        };
    }

    void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
    void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
