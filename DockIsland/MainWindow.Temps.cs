using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace DockIsland;

// =========================================================
//  Cartão de temperaturas da aba Sistema: CPU, GPU, Memória e Placa-mãe.
//  GPU é lida aqui mesmo; CPU, memória e placa-mãe vêm do auxiliar elevado (ver HwSensors.cs).
//  Sem sensor ou sem permissão, o valor fica "—".
// =========================================================
public partial class MainWindow
{
    static readonly SolidColorBrush SysOrange = Frozen(0xFF, 0xFF, 0x9F, 0x0A);

    HwReader _gpuReader;
    bool _gpuFailed, _tempBusy, _tempHelperOn;
    TempSnapshot _lastSnap;
    int _lastSnapTick, _lastHelperKick;
    bool _setupAutoTried;

    // Chamado junto com o resto da aba Sistema (a cada 2 s enquanto ela está aberta)
    void RefreshTemps()
    {
        if (_tempBusy) return;
        _tempBusy = true;
        bool needGpu = !_gpuFailed;

        Task.Run(() =>
        {
            float? gpu = null;
            if (needGpu)
            {
                try
                {
                    _gpuReader ??= new HwReader(cpu: false, gpu: true, memory: false, board: false);
                    gpu = _gpuReader.Read().Gpu;
                }
                catch { _gpuFailed = true; }   // biblioteca/driver indisponível: não tenta de novo
            }
            var snap = SensorHelper.ReadSnapshot();

            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // o arquivo pode estar sendo trocado naquele instante: mantém a última leitura por alguns segundos
                    if (snap != null) { _lastSnap = snap; _lastSnapTick = Environment.TickCount; }
                    else if (_lastSnap != null && unchecked(Environment.TickCount - _lastSnapTick) < 5000) snap = _lastSnap;
                    ApplyTemps(snap, gpu);
                }
                finally { _tempBusy = false; }
            }));
        });
    }

    // Deixa as temperaturas sempre ativas, sem clicar:
    //  - leitura já ativada, mas o auxiliar não está respondendo: reinicia a tarefa agendada (a cada 15 s);
    //    se a tarefa sumiu, pede a permissão de administrador uma única vez para recriá-la;
    //  - nunca ativada: mostra a pergunta de ativação uma única vez (depois disso só pelo clique / Ajustes).
    void EnsureSensorHelper(bool helperOn)
    {
        if (helperOn || _confirmShown) return;
        try
        {
            if (SensorHelper.Enabled)
            {
                if (unchecked(Environment.TickCount - _lastHelperKick) < 15000 && _lastHelperKick != 0) return;
                _lastHelperKick = Environment.TickCount;
                Task.Run(() =>
                {
                    int code = SensorHelper.RunTask();
                    if (code != 0 && !_setupAutoTried)
                    {
                        _setupAutoTried = true;   // tarefa ausente: recria uma vez nesta execução
                        Dispatcher.BeginInvoke(new Action(StartSensorSetup));
                    }
                });
            }
            else if (!_setupAutoTried)
            {
                _setupAutoTried = true;
                string flag = System.IO.Path.Combine(SensorHelper.Dir, "sensors.asked");
                if (System.IO.File.Exists(flag)) return;
                System.IO.Directory.CreateDirectory(SensorHelper.Dir);
                System.IO.File.WriteAllText(flag, "1");
                ShowConfirm("sensors");
            }
        }
        catch { }
    }

    void ApplyTemps(TempSnapshot snap, float? gpu)
    {
        bool helper = snap != null;
        EnsureSensorHelper(helper);
        _tempHelperOn = helper;
        SetTemp(TempCpu, TempCpuBox, snap?.Cpu, 70, 85, helper, needsAdmin: true);
        SetTemp(TempGpu, TempGpuBox, gpu ?? snap?.Gpu, 75, 88, helper, needsAdmin: false);
        SetTemp(TempMem, TempMemBox, snap?.Mem, 55, 75, helper, needsAdmin: true);
        SetTemp(TempBoard, TempBoardBox, snap?.Board, 55, 70, helper, needsAdmin: true);
    }

    static void SetTemp(TextBlock t, Border box, float? v, double warm, double hot, bool helper, bool needsAdmin)
    {
        if (v.HasValue)
        {
            t.Text = $"{v.Value:0}°C";
            t.Foreground = v.Value >= hot ? SysRed : v.Value >= warm ? SysOrange : SysWhite;
            box.ToolTip = null;
            box.Cursor = Cursors.Arrow;
            return;
        }

        t.Text = "—";
        t.Foreground = SysDim;
        if (needsAdmin && !helper)
        {
            box.ToolTip = "Clique para ativar a leitura (pede permissão de administrador)";
            box.Cursor = Cursors.Hand;
        }
        else
        {
            box.ToolTip = "Este PC não informa a temperatura deste item";
            box.Cursor = Cursors.Arrow;
        }
    }

    // Clique num valor "—" de CPU / Memória / Placa: oferece ativar a leitura completa
    void TempCell_Click(object sender, MouseButtonEventArgs e)
    {
        if (_tempHelperOn || _confirmShown) return;
        if (sender is FrameworkElement fe && fe.Tag is string tag && tag != "gpu")
            ShowConfirm("sensors");
    }

    // ---------- Ajustes: "Temperaturas completas" ----------
    void SwTemp_Click(object sender, RoutedEventArgs e)
    {
        if (SwTemp.IsChecked == true) ShowConfirm("sensors");   // confirma antes de pedir administrador
        else SensorHelper.Disable();
    }

    void SyncTempSwitch() => SwTemp.IsChecked = SensorHelper.Enabled;

    void StartSensorSetup() => SensorHelper.StartElevatedSetup();

    void ShutdownTemps()
    {
        _gpuFailed = true;   // nenhuma nova leitura depois de fechar
        try { _gpuReader?.Dispose(); } catch { }
    }
}
