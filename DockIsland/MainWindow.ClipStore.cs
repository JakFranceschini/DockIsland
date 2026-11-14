using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DockIsland;

// Guarda o histórico da área de transferência (últimos MaxClips itens, textos e capturas de tela)
// para que continue ali depois de fechar e abrir o programa.
//   %AppData%\DockIsland\clipboard.json   -> lista (mais recente primeiro)
//   %AppData%\DockIsland\clips\<id>.png   -> imagens
public partial class MainWindow
{
    class ClipEntry
    {
        public string Text { get; set; }    // texto (null se for imagem)
        public string Image { get; set; }   // nome do .png em clips\ (null se for texto)
        public int W { get; set; }
        public int H { get; set; }
        public string Time { get; set; }
    }

    static string ClipRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland");
    static string ClipFile => Path.Combine(ClipRoot, "clipboard.json");
    static string ClipImgDir => Path.Combine(ClipRoot, "clips");

    readonly DispatcherTimer _clipSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    bool _clipDirty;

    void InitClipStore()
    {
        LoadClips();
        _clipSaveTimer.Tick += (_, __) => { _clipSaveTimer.Stop(); SaveClips(); };
        _clips.CollectionChanged += (_, __) => { _clipDirty = true; _clipSaveTimer.Stop(); _clipSaveTimer.Start(); };
    }

    // Grava na hora o que ainda estiver pendente (usado ao fechar o programa)
    void FlushClips()
    {
        _clipSaveTimer.Stop();
        if (_clipDirty) SaveClips();
    }

    void LoadClips()
    {
        try
        {
            if (!File.Exists(ClipFile)) return;
            var list = JsonSerializer.Deserialize<List<ClipEntry>>(File.ReadAllText(ClipFile)) ?? new();
            foreach (var e in list.Take(MaxClips))
            {
                try
                {
                    if (!string.IsNullOrEmpty(e.Text))
                    {
                        _clips.Add(new ClipItem { Hash = "T:" + e.Text, Text = e.Text, Time = e.Time });
                    }
                    else if (!string.IsNullOrEmpty(e.Image))
                    {
                        var path = Path.Combine(ClipImgDir, Path.GetFileName(e.Image));
                        if (!File.Exists(path)) continue;
                        var bytes = File.ReadAllBytes(path);

                        var thumb = new BitmapImage();
                        using (var ms = new MemoryStream(bytes))
                        {
                            thumb.BeginInit();
                            thumb.CacheOption = BitmapCacheOption.OnLoad;
                            thumb.DecodePixelWidth = 180;
                            thumb.StreamSource = ms;
                            thumb.EndInit();
                        }
                        thumb.Freeze();

                        _clips.Add(new ClipItem
                        {
                            Hash = "I:" + Path.GetFileNameWithoutExtension(path).ToUpperInvariant(),
                            PngBytes = bytes,
                            Thumb = thumb,
                            Width = e.W,
                            Height = e.H,
                            Time = e.Time
                        });
                    }
                }
                catch { }   // item corrompido: pula
            }
        }
        catch { }
    }

    void SaveClips()
    {
        _clipDirty = false;
        try
        {
            Directory.CreateDirectory(ClipRoot);
            var entries = new List<ClipEntry>();
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in _clips)
            {
                if (c.IsImage)
                {
                    var name = c.Hash.Substring(2) + ".png";
                    keep.Add(name);
                    Directory.CreateDirectory(ClipImgDir);
                    var path = Path.Combine(ClipImgDir, name);
                    if (!File.Exists(path)) File.WriteAllBytes(path, c.PngBytes);
                    entries.Add(new ClipEntry { Image = name, W = c.Width, H = c.Height, Time = c.Time });
                }
                else
                {
                    entries.Add(new ClipEntry { Text = c.Text, Time = c.Time });
                }
            }

            var tmp = ClipFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entries));
            File.Move(tmp, ClipFile, true);

            // apaga as imagens de itens que saíram do histórico
            if (Directory.Exists(ClipImgDir))
                foreach (var f in Directory.GetFiles(ClipImgDir, "*.png"))
                    if (!keep.Contains(Path.GetFileName(f)))
                        try { File.Delete(f); } catch { }
        }
        catch { }
    }
}
