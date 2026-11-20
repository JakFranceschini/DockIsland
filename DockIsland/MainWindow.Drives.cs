using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DockIsland;

// Aba Sistema: espaço livre por unidade (C:, D:, pendrives...) com barra de uso.
public partial class MainWindow
{
    DateTime _drivesAt = DateTime.MinValue;
    bool _drivesBusy;

    static Brush DriveTrack => Theme.W(0x40);
    static Brush DriveOk => Theme.AccentBrush;
    static readonly Brush DriveWarn = Frz("#FF9F0A"), DriveFull = Frz("#FF453A");
    static Brush DriveSub => Theme.W(0xB0);

    static Brush Frz(string hex)
    {
        var b = (Brush)new BrushConverter().ConvertFromString(hex);
        b.Freeze();
        return b;
    }

    async void RefreshDrives(bool force = false)
    {
        if (_drivesBusy) return;
        if (!force && (DateTime.UtcNow - _drivesAt).TotalSeconds < 20) return;
        _drivesBusy = true;
        try
        {
            var list = await Task.Run(() =>
            {
                var res = new List<(string Name, long Total, long Free)>();
                foreach (var d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.DriveType != DriveType.Fixed && d.DriveType != DriveType.Removable && d.DriveType != DriveType.Network) continue;
                        if (!d.IsReady || d.TotalSize <= 0) continue;
                        res.Add((d.Name, d.TotalSize, d.AvailableFreeSpace));
                    }
                    catch { }
                }
                return res;
            });
            _drivesAt = DateTime.UtcNow;
            BuildDrives(list);
        }
        catch { }
        finally { _drivesBusy = false; }
    }

    void BuildDrives(List<(string Name, long Total, long Free)> list)
    {
        DrivesPanel.Children.Clear();
        if (list.Count == 0)
        {
            DrivesPanel.Children.Add(new TextBlock { Text = "Nenhuma unidade encontrada", Foreground = DriveSub, FontSize = 12 });
            return;
        }

        const int Max = 4;
        foreach (var d in list.Take(Max))
        {
            double usedFrac = Math.Min(1, Math.Max(0, 1.0 - (double)d.Free / d.Total));
            var color = usedFrac >= 0.9 ? DriveFull : usedFrac >= 0.75 ? DriveWarn : DriveOk;
            string root = d.Name;

            var row = new Grid { Height = 21, Background = Brushes.Transparent, Cursor = Cursors.Hand };
            row.ToolTip = $"{root.TrimEnd('\\')}  ·  {FormatSize(d.Free)} livres de {FormatSize(d.Total)}  ({usedFrac * 100:0}% usado)";
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(84) });

            row.Children.Add(new TextBlock
            {
                Text = root.TrimEnd('\\'), Foreground = Theme.Fg, FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });

            // barra: trilho + preenchimento proporcional ao uso
            var bar = new Grid { Height = 6, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            bar.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = DriveTrack });
            var fill = new Grid();
            fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(usedFrac, 0.02), GridUnitType.Star) });
            fill.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - usedFrac, 0.0001), GridUnitType.Star) });
            fill.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = color });
            bar.Children.Add(fill);
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);

            var txt = new TextBlock
            {
                Text = FormatSize(d.Free) + " livres", Foreground = DriveSub, FontSize = 11.5, TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(txt, 2);
            row.Children.Add(txt);

            row.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                try { Process.Start(new ProcessStartInfo(root) { UseShellExecute = true }); } catch { }
            };
            DrivesPanel.Children.Add(row);
        }

        if (list.Count > Max)
            DrivesPanel.Children.Add(new TextBlock
            {
                Text = "+ " + (list.Count - Max) + (list.Count - Max == 1 ? " unidade" : " unidades"),
                Foreground = DriveSub, FontSize = 11, Margin = new Thickness(30, 2, 0, 0)
            });
    }
}
