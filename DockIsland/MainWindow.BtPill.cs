using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Bt = Windows.Devices.Bluetooth;
using DevEnum = Windows.Devices.Enumeration;

namespace DockIsland;

// =========================================================
//  Bluetooth na pílula: ao conectar um dispositivo, a pílula mostra o nome dele
//  (mesma faixa do pendrive) com um botão que abre os Ajustes de Bluetooth.
//  Varredura a cada 4 s; só dispositivos que conectam depois da abertura do programa aparecem.
// =========================================================
public partial class MainWindow
{
    readonly DispatcherTimer _btPoll = new() { Interval = TimeSpan.FromSeconds(4) };
    readonly HashSet<string> _btKnownNames = new(StringComparer.OrdinalIgnoreCase);
    bool _btPillBusy, _btPillFirst = true;
    bool _pillIsBt;   // a faixa do pendrive está mostrando um dispositivo Bluetooth

    void InitBtPill()
    {
        _btPoll.Tick += (_, __) => ScanBtPill();
        _btPoll.Start();
        ScanBtPill();
    }

    // null = a leitura falhou (não mexe no estado conhecido)
    static async Task<HashSet<string>> ReadConnectedBtNamesAsync()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool anyOk = false;
        var selectors = new Func<string>[]
        {
            () => Bt.BluetoothDevice.GetDeviceSelectorFromConnectionStatus(Bt.BluetoothConnectionStatus.Connected),
            () => Bt.BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(Bt.BluetoothConnectionStatus.Connected)
        };
        foreach (var sel in selectors)
        {
            try
            {
                var infos = await DevEnum.DeviceInformation.FindAllAsync(sel());
                anyOk = true;
                foreach (var info in infos)
                    if (!string.IsNullOrWhiteSpace(info.Name)) names.Add(info.Name.Trim());
            }
            catch { }
        }
        return anyOk ? names : null;
    }

    async void ScanBtPill()
    {
        if (_btPillBusy) return;
        _btPillBusy = true;
        HashSet<string> now = null;
        try { now = await ReadConnectedBtNamesAsync(); }
        catch { }
        _btPillBusy = false;
        if (now == null) return;

        string fresh = null;
        if (!_btPillFirst)
            foreach (var n in now)
                if (!_btKnownNames.Contains(n)) fresh = n;

        _btKnownNames.Clear();
        foreach (var n in now) _btKnownNames.Add(n);
        _btPillFirst = false;

        if (fresh == null) return;
        _ = RefreshBluetoothAsync();                   // aba Sistema atualiza a lista de conectados
        if (Progress > 0.05 || _usbEjecting) return;   // ilha aberta (ou ejetando pendrive): não mexe na pílula
        ShowBtPill(fresh);
    }

    void ShowBtPill(string name)
    {
        _pillIsBt = true;
        _usbCur = null;
        _usbDone = false;
        _usbEjecting = false;
        UsbIcon.Text = "\uE702";
        UsbIcon.Foreground = SysBlue;
        UsbName.Text = name;
        UsbSub.Text = "Bluetooth conectado";
        UsbEjectGlyph.Visibility = Visibility.Collapsed;
        UsbSettingsGlyph.Visibility = Visibility.Visible;
        UsbEjectBtn.ToolTip = "Ajustes de Bluetooth";
        UsbEjectBtn.Visibility = Visibility.Visible;
        UsbEjectBtn.Opacity = 1;
        PresentUsb(6);
    }

    void OpenBluetoothSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); } catch { }
    }
}
