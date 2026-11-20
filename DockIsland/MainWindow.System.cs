using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Bt = Windows.Devices.Bluetooth;
using DevEnum = Windows.Devices.Enumeration;
using Net = Windows.Networking.Connectivity;
using Radios = Windows.Devices.Radios;

namespace DockIsland;

// =========================================================
//  Aba "Sistema": o que normalmente fica na barra de tarefas
//  (rede, bluetooth, volume, idioma do teclado, calendário)
// =========================================================
public partial class MainWindow
{
    // ---------- Win32 ----------
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    // ---------- Pincéis ----------
    static SolidColorBrush Frozen(byte a, byte r, byte g, byte b)
    {
        var br = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        br.Freeze();
        return br;
    }

    static SolidColorBrush SysWhite => Theme.Fg;
    static SolidColorBrush SysDim => Theme.W(0x80);
    static SolidColorBrush SysTrack => Theme.W(0x40);
    static SolidColorBrush SysBlue => Theme.AccentBrush;
    static readonly SolidColorBrush SysRed = Frozen(0xFF, 0xFF, 0x45, 0x3A);

    // ---------- Estado ----------
    readonly DispatcherTimer _sysTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    DateTime _calMonth = new(DateTime.Today.Year, DateTime.Today.Month, 1);
    DateTime _calBuiltDay = DateTime.MinValue;
    bool _volDrag;
    bool _btBusy;
    int _btLastTick;
    bool _btForce;
    double _lastVol;
    bool _lastMuted;

    void InitSystemArea()
    {
        // Cabeçalho do calendário (domingo primeiro, como no Windows pt-BR)
        foreach (var d in "DSTQQSS")
        {
            CalWeek.Children.Add(new TextBlock
            {
                Text = d.ToString(),
                FontSize = 11,
                Foreground = SysDim,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }

        InitTrayArea();

        // a barra de volume só conhece sua largura depois do layout
        VolHit.SizeChanged += (_, __) => ShowVolume(_lastVol, _lastMuted);

        _sysTimer.Tick += (_, __) =>
        {
            if (_page == 4 && Progress > 0.5) RefreshSystem();
        };
        _sysTimer.Start();

        InitWinUpdate();
    }

    void OnSystemPageShown()
    {
        _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        BuildCalendar();
        RefreshSystem();
        RefreshTrash();
        RefreshDrives(force: true);
        _btForce = true;
        _ = RefreshTrayAsync(force: true);
        if ((DateTime.Now - _updLastCheck).TotalSeconds > 60) _ = CheckWinUpdateAsync(false);   // só o que o Windows já sabe: rápido
    }

    void RefreshSystem()
    {
        RefreshNetwork();
        RefreshVolume();
        RefreshDrives();
        RefreshTemps();
        _ = RefreshBluetoothAsync();
        _ = RefreshTrayAsync();

        // virou o dia com a aba aberta: atualiza o "hoje" do calendário
        if (_calBuiltDay != DateTime.Today)
        {
            _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
            BuildCalendar();
        }
    }

    void Tile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button b && b.Tag is string uri && uri.Length > 0)
        {
            if (ReferenceEquals(b, UpdateBtn)) RecheckWinUpdateSoon();   // depois de abrir o Windows Update, confere de novo
            try { Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true }); }
            catch
            {
                // ms-settings:windowsupdate-action não existe em versões antigas: abre a página normal
                if (uri.EndsWith("-action"))
                    try { Process.Start(new ProcessStartInfo(uri.Substring(0, uri.Length - 7)) { UseShellExecute = true }); } catch { }
            }
        }
    }

    // =====================================================
    //  Rede (Wi-Fi / Ethernet)
    // =====================================================
    void RefreshNetwork()
    {
        string glyph = "\uE774", label = "Rede", name = "Sem conexão", detail = "Desconectado";
        int bars = -1;
        bool connected = false;

        try
        {
            var p = Net.NetworkInformation.GetInternetConnectionProfile();
            if (p == null)
            {
                // sem internet, mas pode haver rede local
                p = Net.NetworkInformation.GetConnectionProfiles()
                    .FirstOrDefault(x => x.GetNetworkConnectivityLevel() != Net.NetworkConnectivityLevel.None);
            }

            if (p != null)
            {
                var lvl = p.GetNetworkConnectivityLevel();
                connected = lvl != Net.NetworkConnectivityLevel.None;

                if (p.IsWlanConnectionProfile)
                {
                    glyph = "\uE701"; label = "Wi-Fi";
                    bars = p.GetSignalBars() ?? 0;
                }
                else if (p.IsWwanConnectionProfile)
                {
                    glyph = "\uE774"; label = "Rede móvel";
                }
                else
                {
                    glyph = "\uE839"; label = "Ethernet";
                }

                name = string.IsNullOrWhiteSpace(p.ProfileName) ? label : p.ProfileName;
                detail = lvl switch
                {
                    Net.NetworkConnectivityLevel.InternetAccess => "Conectado",
                    Net.NetworkConnectivityLevel.ConstrainedInternetAccess => "Acesso limitado",
                    Net.NetworkConnectivityLevel.LocalAccess => "Sem internet",
                    _ => "Desconectado"
                };
            }
        }
        catch { }

        NetIcon.Text = glyph;
        NetIcon.Foreground = connected ? SysBlue : SysDim;
        NetLabel.Text = label;
        NetName.Text = name;
        NetDetail.Text = detail;

        NetBars.Visibility = bars >= 0 ? Visibility.Visible : Visibility.Collapsed;
        int filled = bars < 0 ? 0 : (int)Math.Round(bars * 4 / 5.0);
        var barViews = new[] { NetBar0, NetBar1, NetBar2, NetBar3 };
        for (int i = 0; i < barViews.Length; i++)
            barViews[i].Background = i < filled ? SysWhite : SysTrack;
    }

    // =====================================================
    //  Bluetooth
    // =====================================================
    async Task RefreshBluetoothAsync()
    {
        if (_btBusy) return;
        // enumerar dispositivos Bluetooth é caro: no máximo a cada 6 s (e sempre ao abrir a aba)
        if (_btLastTick != 0 && unchecked(Environment.TickCount - _btLastTick) < 6000 && !_btForce) return;
        _btForce = false;
        _btLastTick = Environment.TickCount;
        _btBusy = true;
        try
        {
            var radios = await Radios.Radio.GetRadiosAsync();
            var radio = radios.FirstOrDefault(r => r.Kind == Radios.RadioKind.Bluetooth);

            if (radio == null || radio.State == Radios.RadioState.Disabled)
            {
                BtIcon.Foreground = SysDim;
                BtName.Text = "Indisponível";
                BtDetail.Text = "Sem adaptador";
                return;
            }

            if (radio.State != Radios.RadioState.On)
            {
                BtIcon.Foreground = SysDim;
                BtName.Text = "Desligado";
                BtDetail.Text = "Rádio desativado";
                return;
            }

            var names = new List<string>();
            await AddConnectedBtNamesAsync(
                () => Bt.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Bt.BluetoothConnectionStatus.Connected), names);
            await AddConnectedBtNamesAsync(
                () => Bt.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Bt.BluetoothConnectionStatus.Connected), names);

            BtIcon.Foreground = SysBlue;
            if (names.Count == 0)
            {
                BtName.Text = "Ligado";
                BtDetail.Text = "Nenhum dispositivo";
            }
            else
            {
                BtName.Text = string.Join(", ", names);
                BtDetail.Text = names.Count == 1 ? "1 conectado" : $"{names.Count} conectados";
            }
        }
        catch
        {
            BtIcon.Foreground = SysDim;
            BtName.Text = "Indisponível";
            BtDetail.Text = "";
        }
        finally { _btBusy = false; }
    }

    static async Task AddConnectedBtNamesAsync(Func<string> selector, List<string> names)
    {
        try
        {
            var infos = await DevEnum.DeviceInformation.FindAllAsync(selector());
            foreach (var info in infos)
            {
                var n = info.Name;
                if (!string.IsNullOrWhiteSpace(n) && !names.Contains(n)) names.Add(n);
            }
        }
        catch { }
    }

    // =====================================================
    //  Volume
    // =====================================================
    void RefreshVolume()
    {
        if (_volDrag) return;
        if (AudioVolume.TryGet(out var level, out var muted)) ShowVolume(level, muted);
        else { VolText.Text = "—"; VolFill.Width = 0; VolKnob.Margin = new Thickness(0); }
    }

    void ShowVolume(double level, bool muted)
    {
        _lastVol = level;
        _lastMuted = muted;
        VolBtn.Content = (muted || level <= 0.001) ? "\uE74F" : "\uE767";
        VolFill.Width = Math.Max(0, VolHit.ActualWidth * level);
        VolFill.Background = muted ? SysDim : SysWhite;
        // bolinha: centro no fim da barra, sem sair do trilho
        double kw = VolKnob.Width;
        VolKnob.Margin = new Thickness(Math.Clamp(VolHit.ActualWidth * level - kw / 2, 0, Math.Max(0, VolHit.ActualWidth - kw)), 0, 0, 0);
        VolKnob.Background = muted ? SysDim : SysWhite;
        VolText.Text = muted ? "Mudo" : $"{Math.Round(level * 100)}%";
    }

    void SetVolFromMouse(MouseEventArgs e)
    {
        if (VolHit.ActualWidth <= 0) return;
        double v = Math.Clamp(e.GetPosition(VolHit).X / VolHit.ActualWidth, 0, 1);
        AudioVolume.SetLevel((float)v);
        ShowVolume(v, false);
    }

    void Vol_Down(object sender, MouseButtonEventArgs e)
    {
        _volDrag = true;
        VolHit.CaptureMouse();
        SetVolFromMouse(e);
        e.Handled = true;
    }

    void Vol_Move(object sender, MouseEventArgs e)
    {
        if (_volDrag) SetVolFromMouse(e);
    }

    void Vol_Up(object sender, MouseButtonEventArgs e)
    {
        if (!_volDrag) return;
        _volDrag = false;
        VolHit.ReleaseMouseCapture();
        e.Handled = true;
    }

    // Roda do mouse sobre o cartão: ±2% por "clique"
    void Vol_Wheel(object sender, MouseWheelEventArgs e)
    {
        if (!AudioVolume.TryGet(out var level, out _)) return;
        double v = Math.Clamp(level + e.Delta / 120.0 * 0.02, 0, 1);
        AudioVolume.SetLevel((float)v);
        RefreshVolume();
        e.Handled = true;
    }

    void VolMute_Click(object sender, RoutedEventArgs e)
    {
        if (!AudioVolume.TryGet(out _, out var muted)) return;
        AudioVolume.SetMute(!muted);
        RefreshVolume();
    }

    // =====================================================
    //  Calendário do mês
    // =====================================================
    void BuildCalendar()
    {
        var today = DateTime.Today;
        _calBuiltDay = today;

        var title = _calMonth.ToString("MMMM yyyy", PtBr);
        CalTitle.Text = char.ToUpper(title[0], PtBr) + title.Substring(1);

        CalDays.Children.Clear();
        var start = _calMonth.AddDays(-(int)_calMonth.DayOfWeek);   // domingo da 1ª semana exibida

        for (int i = 0; i < 42; i++)
        {
            var day = start.AddDays(i);
            bool inMonth = day.Month == _calMonth.Month;
            bool isToday = day.Date == today;

            var label = new TextBlock
            {
                Text = day.Day.ToString(),
                FontSize = 12.5,
                FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = isToday ? Brushes.White : (inMonth ? Theme.W(0xE6) : SysTrack),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            CalDays.Children.Add(new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = isToday ? SysRed : Brushes.Transparent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Child = label
            });
        }
    }

    void CalPrev_Click(object sender, RoutedEventArgs e)
    {
        _calMonth = _calMonth.AddMonths(-1);
        BuildCalendar();
    }

    void CalNext_Click(object sender, RoutedEventArgs e)
    {
        _calMonth = _calMonth.AddMonths(1);
        BuildCalendar();
    }

    void CalTitle_Click(object sender, MouseButtonEventArgs e)
    {
        _calMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        BuildCalendar();
        e.Handled = true;
    }
}
