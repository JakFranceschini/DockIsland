using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DockIsland;

// Liga os widgets do dock "Último item copiado" e "Lixeira" aos dados da ilha
// (histórico da área de transferência e consulta da lixeira). Chamado pelo App depois de criar o dock.
public partial class MainWindow
{
    DispatcherTimer _trashTimer;

    public void StartDockWidgets()
    {
        var dock = (Application.Current as App)?.Dock;
        if (dock == null) return;

        dock.ClipCopyRequested = ClipWidgetCopyAsync;
        dock.TrashEmptyRequested += EmptyTrash;

        _clips.CollectionChanged += (_, __) => PushDockClip();
        PushDockClip();

        // a lixeira muda fora do programa: confere de tempos em tempos (só com o widget ligado)
        _trashTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _trashTimer.Tick += (_, __) => { if (WidgetOn("trash")) RefreshTrash(); };
        _trashTimer.Start();
        if (WidgetOn("trash")) RefreshTrash();
    }

    // Envia ao dock o item mais recente do histórico (texto ou miniatura da captura)
    void PushDockClip()
    {
        try
        {
            var dock = (Application.Current as App)?.Dock;
            if (dock == null) return;
            var c = _clips.FirstOrDefault();
            if (c == null) dock.UpdateClip(false, null, null);
            else if (c.IsImage) dock.UpdateClip(true, $"Captura de tela · {c.Width} × {c.Height}", c.Thumb);
            else dock.UpdateClip(true, c.Preview, null);
        }
        catch { }
    }

    // Clique no widget: copia de novo o último item do histórico
    async Task<bool> ClipWidgetCopyAsync()
    {
        var item = _clips.FirstOrDefault();
        if (item == null) return false;

        _ignoreUntil = DateTime.UtcNow.AddMilliseconds(900);   // fomos nós que copiamos: não vira item novo
        for (int i = 0; i < 6; i++)
        {
            try
            {
                if (item.IsImage)
                {
                    var img = new BitmapImage();
                    using (var ms = new MemoryStream(item.PngBytes))
                    {
                        img.BeginInit();
                        img.CacheOption = BitmapCacheOption.OnLoad;
                        img.StreamSource = ms;
                        img.EndInit();
                    }
                    img.Freeze();
                    Clipboard.SetImage(img);
                }
                else Clipboard.SetText(item.Text);
                return true;
            }
            catch { await Task.Delay(70); }
        }
        return false;
    }
}
