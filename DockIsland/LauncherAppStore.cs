using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DockIsland;

// Apps que o usuário adicionou ao lançador à mão (programas portáteis, atalhos, scripts...).
// Salvo em %AppData%\DockIsland\launcher-apps.json
static class LauncherAppStore
{
    public class Item
    {
        public string Name { get; set; }
        public string Target { get; set; }   // caminho do .exe / .lnk / ...
    }

    static List<Item> _items = new();

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "launcher-apps.json");

    static LauncherAppStore()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<List<Item>>(File.ReadAllText(FilePath));
                if (d != null) _items = d.Where(i => !string.IsNullOrWhiteSpace(i?.Target)).ToList();
            }
        }
        catch { }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static IReadOnlyList<Item> All => _items;

    public static bool Contains(string target) =>
        _items.Any(i => string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase));

    public static bool Add(string name, string target)
    {
        if (string.IsNullOrWhiteSpace(target) || Contains(target)) return false;
        _items.Add(new Item { Name = name, Target = target });
        Save();
        return true;
    }

    public static void Remove(string target)
    {
        if (_items.RemoveAll(i => string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase)) > 0) Save();
    }
}
