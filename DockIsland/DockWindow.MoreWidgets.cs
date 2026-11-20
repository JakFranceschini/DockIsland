using System;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DockIsland;

// Widgets do dock: Relógio + calendário, Último item copiado e Lixeira (ver DockWidgets.cs).
// O MainWindow empurra os dados (UpdateClip / UpdateTrash); o dock só desenha.
public partial class DockWindow
{
    /// Copia de novo o último item do histórico (o MainWindow faz); devolve se deu certo
    public Func<Task<bool>> ClipCopyRequested;

    /// Pedido para esvaziar a lixeira (o MainWindow faz; a confirmação já foi feita no próprio widget)
    public event Action TrashEmptyRequested;

    /// Liga o que precisa de relógio próprio (a hora do widget de relógio)
    public void StartWidgets()
    {
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, __) => UpdateClock();
        _clockTimer.Start();
        UpdateClock();
    }

    // ---------------------------------------------------------------- relógio + calendário
    DispatcherTimer _clockTimer;
    string _clockKey;

    static readonly string[] Meses = { "JAN", "FEV", "MAR", "ABR", "MAI", "JUN", "JUL", "AGO", "SET", "OUT", "NOV", "DEZ" };

    void UpdateClock()
    {
        if (!On("clock")) return;
        var n = DateTime.Now;
        string key = n.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        if (key == _clockKey) return;   // só muda de minuto em minuto
        _clockKey = key;

        ClockTime.Text = n.ToString("HH:mm", CultureInfo.InvariantCulture);
        ClockMonth.Text = Meses[n.Month - 1];
        ClockDay.Text = n.Day.ToString(CultureInfo.InvariantCulture);
        string wd = PtBr.DateTimeFormat.GetDayName(n.DayOfWeek).Split('-')[0];   // "segunda-feira" -> "segunda"
        ClockWeekday.Text = wd;
        ClockWidget.ToolTip = n.ToString("dddd, d 'de' MMMM 'de' yyyy", PtBr);
    }

    // ---------------------------------------------------------------- último item copiado
    bool _clipHas;
    long _clipFlashAt;

    /// Chamado pelo MainWindow sempre que o histórico da área de transferência muda
    public void UpdateClip(bool has, string text, ImageSource thumb)
    {
        _clipHas = has;
        if (has)
        {
            ClipText.Text = text ?? "";
            ClipThumbBrush.ImageSource = thumb;
            ClipGlyph.Visibility = thumb == null ? Visibility.Visible : Visibility.Collapsed;
            RefreshClipMarquee();
        }
        ApplyWidgets();
    }

    // Letreiro do texto copiado: cabendo, fica parado; senão rola para o lado (mesma rotina do widget de música)
    bool _clipMarqueeHooked;

    void RefreshClipMarquee()
    {
        if (!_clipMarqueeHooked)
        {
            _clipMarqueeHooked = true;
            // o tamanho da área só existe depois do layout (e muda quando o widget aparece): recalcula nessas horas
            ClipTextHost.SizeChanged += (_, __) => Dispatcher.BeginInvoke(new Action(() =>
                Marquee.Update(ClipTextHost, ClipText, ClipTextShift)), DispatcherPriority.Loaded);
        }
        Dispatcher.BeginInvoke(new Action(() =>
            Marquee.Update(ClipTextHost, ClipText, ClipTextShift)), DispatcherPriority.Loaded);
    }

    async void Clip_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        var f = ClipCopyRequested;
        if (f == null) return;
        bool ok = false;
        try { ok = await f(); } catch { }

        // aviso rápido no lugar do rótulo
        long stamp = _clipFlashAt = Environment.TickCount64;
        ClipLabel.Text = ok ? "Copiado ✓" : "Não foi possível copiar";
        await Task.Delay(1200);
        if (stamp == _clipFlashAt) ClipLabel.Text = "Último copiado";
    }

    // ---------------------------------------------------------------- lixeira
    bool _trashKnown;
    long _trashItems;
    string _trashInfo = "";
    bool _trashConfirm;
    DispatcherTimer _trashConfirmTimer;

    /// Chamado pelo MainWindow depois de consultar a lixeira (items < 0 = não foi possível ler)
    public void UpdateTrash(long items, string info)
    {
        _trashKnown = items >= 0;
        _trashItems = items;
        _trashInfo = info ?? "";
        if (items <= 0) ResetTrashConfirm();
        if (!_trashConfirm) TrashSub.Text = _trashInfo;
        TrashActBtn.IsEnabled = items > 0;
        ApplyWidgets();
    }

    void ResetTrashConfirm()
    {
        _trashConfirmTimer?.Stop();
        _trashConfirm = false;
        TrashActBtn.Tag = null;
        TrashActText.Text = "Esvaziar";
        TrashActText.Foreground = TryFindResource("FgBrush") as Brush ?? Brushes.White;
        TrashSub.Text = _trashInfo;
    }

    // 1º clique pede confirmação (botão vermelho por ~4 s); 2º clique esvazia de vez
    void TrashAct_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_trashItems <= 0) return;

        if (!_trashConfirm)
        {
            _trashConfirm = true;
            TrashActBtn.Tag = "confirm";
            TrashActText.Text = "Confirmar";
            TrashActText.Foreground = Brushes.White;
            TrashSub.Text = "Apagar de vez?";
            _trashConfirmTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
            _trashConfirmTimer.Tick -= TrashConfirmExpired;
            _trashConfirmTimer.Tick += TrashConfirmExpired;
            _trashConfirmTimer.Stop();
            _trashConfirmTimer.Start();
            return;
        }

        ResetTrashConfirm();
        TrashSub.Text = "Esvaziando…";
        TrashEmptyRequested?.Invoke();
    }

    void TrashConfirmExpired(object s, EventArgs e) => ResetTrashConfirm();
}
