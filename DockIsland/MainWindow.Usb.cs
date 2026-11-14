using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace DockIsland;

// =========================================================
//  Pendrive na pílula: ao conectar, a pílula alarga mostrando o nome e o botão de ejetar.
//  Fica ~8 s (segura enquanto o mouse está em cima). Detecta por WM_DEVICECHANGE + varredura a cada 3 s.
// =========================================================
public partial class MainWindow
{
    const int WM_DEVICECHANGE = 0x0219;

    // 0 = pílula normal, 1 = pílula mostrando o pendrive
    public static readonly DependencyProperty UsbPeekProperty =
        DependencyProperty.Register(nameof(UsbPeek), typeof(double), typeof(MainWindow),
            new PropertyMetadata(0.0, (d, e) => { var w = (MainWindow)d; w.ApplyProgress(w.Progress); }));

    public double UsbPeek
    {
        get => (double)GetValue(UsbPeekProperty);
        set => SetValue(UsbPeekProperty, value);
    }

    class UsbDrive
    {
        public string Root;     // "E:\"
        public string Label;
        public long Total, Free;
        public bool Ready;
    }

    readonly DispatcherTimer _usbPoll = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _usbHide = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly HashSet<string> _usbKnown = new(StringComparer.OrdinalIgnoreCase);    // unidades prontas já vistas
    readonly HashSet<string> _usbEjected = new(StringComparer.OrdinalIgnoreCase);  // ejetadas e ainda plugadas
    bool _usbScanBusy, _usbFirstScan = true, _usbEjecting, _usbDone;
    UsbDrive _usbCur;
    double _usbW = 260;          // largura da pílula com o pendrive
    double _usbHoldSec = 8;
    DateTime _usbShownAt;

    void InitUsb()
    {
        _usbPoll.Tick += (_, __) => ScanUsb();
        _usbPoll.Start();
        _usbHide.Tick += (_, __) =>
        {
            if (_usbEjecting || Island.IsMouseOver) return;   // segura enquanto o mouse está na ilha
            if ((DateTime.UtcNow - _usbShownAt).TotalSeconds >= _usbHoldSec) HideUsbPill();
        };
        ScanUsb();
    }

    static List<UsbDrive> ReadRemovable()
    {
        var res = new List<UsbDrive>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Removable) continue;
                var u = new UsbDrive { Root = d.Name };
                try { u.Ready = d.IsReady; } catch { }
                if (u.Ready)
                {
                    try { u.Label = d.VolumeLabel; u.Total = d.TotalSize; u.Free = d.AvailableFreeSpace; }
                    catch { u.Ready = false; }
                    if (u.Total <= 0) u.Ready = false;
                }
                res.Add(u);
            }
            catch { }
        }
        return res;
    }

    async void ScanUsb()
    {
        if (_usbScanBusy) return;
        _usbScanBusy = true;
        List<UsbDrive> list = null;
        try { list = await Task.Run(ReadRemovable); }
        catch { }
        _usbScanBusy = false;
        if (list == null) return;

        var present = new HashSet<string>(list.Select(d => d.Root), StringComparer.OrdinalIgnoreCase);
        _usbEjected.RemoveWhere(r => !present.Contains(r));   // desplugou de vez: pode aparecer de novo

        UsbDrive fresh = null;
        if (!_usbFirstScan)
            foreach (var d in list)
                if (d.Ready && !_usbKnown.Contains(d.Root) && !_usbEjected.Contains(d.Root)) fresh = d;

        _usbKnown.Clear();
        foreach (var d in list) if (d.Ready) _usbKnown.Add(d.Root);
        _usbFirstScan = false;

        // o pendrive que está na pílula foi removido
        if (_usbCur != null && !_usbDone && !_usbEjecting && !_usbKnown.Contains(_usbCur.Root))
            HideUsbPill();

        if (fresh != null)
        {
            RefreshDrives(force: true);                // aba Sistema atualiza a lista de unidades
            if (Progress > 0.05) return;               // ilha aberta: não mexe na pílula
            ShowUsbFor(fresh);
        }
    }

    void ShowUsbFor(UsbDrive d)
    {
        _pillIsBt = false;
        _usbCur = d;
        _usbDone = false;
        _usbEjecting = false;
        UsbEjectGlyph.Visibility = Visibility.Visible;
        UsbSettingsGlyph.Visibility = Visibility.Collapsed;
        UsbEjectBtn.ToolTip = "Ejetar";
        UsbIcon.Text = "\uE88E";
        UsbIcon.Foreground = Theme.Fg;
        UsbName.Text = string.IsNullOrWhiteSpace(d.Label) ? "Pendrive" : d.Label;
        UsbSub.Text = d.Root.TrimEnd('\\') + "  ·  " + FormatSize(d.Free) + " livres";
        UsbEjectBtn.Visibility = Visibility.Visible;
        UsbEjectBtn.Opacity = 1;
        PresentUsb(8);
    }

    // Mede o texto, alarga a pílula e (re)inicia a contagem para esconder
    void PresentUsb(double holdSec)
    {
        _usbHoldSec = holdSec;
        _usbShownAt = DateTime.UtcNow;
        double text = Math.Max(MeasureTextW(UsbName), MeasureTextW(UsbSub));
        double btn = UsbEjectBtn.Visibility == Visibility.Visible ? 10 + 26 : 0;
        // 16 (margem) + 20 (ícone) + 10 (espaço) + texto + botão + 14 (margem) + 2
        _usbW = Math.Clamp(Math.Ceiling(16 + 20 + 10 + text + btn + 14 + 2), 230, 420);
        ApplyProgress(Progress);

        if (!_usbHide.IsEnabled) _usbHide.Start();
        if (UsbPeek < 0.999)
        {
            BeginAnimation(UsbPeekProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(420))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
        }
    }

    void HideUsbPill()
    {
        _usbHide.Stop();
        _usbCur = null;
        _usbEjecting = false;
        if (UsbPeek <= 0) return;
        BeginAnimation(UsbPeekProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        });
    }

    async void UsbEject_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // não deixa o clique cair na ilha (expandir)
        if (_pillIsBt) { OpenBluetoothSettings(); HideUsbPill(); return; }   // faixa mostrando Bluetooth
        var d = _usbCur;
        if (d == null || _usbEjecting || _usbDone) return;

        _usbEjecting = true;
        string name = UsbName.Text;
        UsbSub.Text = "Ejetando…";
        UsbEjectBtn.Opacity = 0.4;

        UsbEject.Result res;
        try { res = await Task.Run(() => UsbEject.Eject(d.Root)); }
        catch { res = UsbEject.Result.Failed; }
        _usbEjecting = false;
        if (!ReferenceEquals(_usbCur, d)) return;   // saiu da pílula enquanto ejetava

        if (res == UsbEject.Result.Ok)
        {
            _usbEjected.Add(d.Root);
            _usbDone = true;
            UsbIcon.Text = "\uE73E";
            UsbIcon.Foreground = SysGreen;
            UsbName.Text = "Ejetado com segurança";
            UsbSub.Text = "Pode remover " + name;
            UsbEjectBtn.Visibility = Visibility.Collapsed;
            PresentUsb(2.4);
            RefreshDrives(force: true);
        }
        else
        {
            UsbEjectBtn.Opacity = 1;
            UsbSub.Text = res == UsbEject.Result.Busy ? "Em uso — feche os arquivos abertos" : "Não foi possível ejetar";
            PresentUsb(4);
        }
    }
}
