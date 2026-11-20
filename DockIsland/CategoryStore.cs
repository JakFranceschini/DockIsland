using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DockIsland;

// Categorias do lançador (nomes digitados pelo usuário) e em qual categoria cada app está.
// Salvo em %AppData%\DockIsland\categories.json
static class CategoryStore
{
    class Data
    {
        public List<string> Categories { get; set; } = new();
        public Dictionary<string, string> Assign { get; set; } = new();   // destino do app -> categoria
        public List<string> Hidden { get; set; } = new();                  // apps removidos do lançador
        public List<string> Order { get; set; } = new();                   // ordem manual dos apps (arrastar e soltar)
    }

    public const int MaxLength = 30;
    static readonly string[] Reserved = { "Todos", "Todos os aplicativos", "Sem categoria" };

    static Data _d = new();
    static HashSet<string> _hidden = new(StringComparer.OrdinalIgnoreCase);
    static Dictionary<string, int> _orderIndex = new(StringComparer.OrdinalIgnoreCase);

    static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "categories.json");

    static CategoryStore()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(FilePath));
                if (d != null) _d = d;
            }
        }
        catch { }
        _d.Categories ??= new List<string>();
        _d.Assign = new Dictionary<string, string>(_d.Assign ?? new(), StringComparer.OrdinalIgnoreCase);
        _hidden = new HashSet<string>(_d.Hidden ?? new(), StringComparer.OrdinalIgnoreCase);
        _d.Order ??= new List<string>();
        RebuildOrderIndex();
    }

    static void RebuildOrderIndex()
    {
        _orderIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < _d.Order.Count; i++)
            if (!string.IsNullOrEmpty(_d.Order[i])) _orderIndex[_d.Order[i]] = i;
    }

    // Posição manual do app (int.MaxValue = sem posição: fica no fim, em ordem alfabética)
    public static int OrderOf(string target) =>
        !string.IsNullOrEmpty(target) && _orderIndex.TryGetValue(target, out var i) ? i : int.MaxValue;

    // Guarda a ordem em que os apps (de uma mesma categoria) devem aparecer
    public static void SetOrder(IEnumerable<string> targetsInOrder)
    {
        var list = targetsInOrder.Where(t => !string.IsNullOrEmpty(t)).ToList();
        var set = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        _d.Order.RemoveAll(t => set.Contains(t));
        _d.Order.AddRange(list);
        RebuildOrderIndex();
        Save();
    }

    static void Save()
    {
        try
        {
            _d.Hidden = _hidden.ToList();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            AtomicFile.WriteAllText(FilePath, JsonSerializer.Serialize(_d, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public static IReadOnlyList<string> All => _d.Categories;

    // Apps removidos do lançador (continuam instalados; só deixam de aparecer na lista)
    public static bool IsHidden(string target) => !string.IsNullOrEmpty(target) && _hidden.Contains(target);

    public static void Hide(string target)
    {
        if (string.IsNullOrEmpty(target)) return;
        _hidden.Add(target);
        Save();
    }

    public static void Unhide(string target)
    {
        if (!string.IsNullOrEmpty(target) && _hidden.Remove(target)) Save();
    }

    public static int IndexOf(string name) =>
        string.IsNullOrEmpty(name) ? -1 : _d.Categories.FindIndex(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase));

    // Categoria do app (ou null se não tiver)
    public static string Get(string target)
    {
        if (string.IsNullOrEmpty(target) || !_d.Assign.TryGetValue(target, out var c)) return null;
        var i = IndexOf(c);
        return i >= 0 ? _d.Categories[i] : null;
    }

    // Devolve a mensagem de erro, ou null se o nome é válido
    static string Validate(string name, string ignore = null)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Digite um nome para a categoria.";
        if (name.Length > MaxLength) return $"Use no máximo {MaxLength} caracteres.";
        if (Reserved.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase))) return "Esse nome é reservado.";
        var i = IndexOf(name);
        if (i >= 0 && !(ignore != null && string.Equals(_d.Categories[i], ignore, StringComparison.OrdinalIgnoreCase)))
            return "Já existe uma categoria com esse nome.";
        return null;
    }

    public static string Add(string name)
    {
        name = name?.Trim();
        var err = Validate(name);
        if (err != null) return err;
        _d.Categories.Add(name);
        Save();
        return null;
    }

    public static string Rename(string old, string name)
    {
        name = name?.Trim();
        var i = IndexOf(old);
        if (i < 0) return "Categoria não encontrada.";
        var err = Validate(name, old);
        if (err != null) return err;

        var oldName = _d.Categories[i];
        _d.Categories[i] = name;
        foreach (var k in _d.Assign.Where(kv => string.Equals(kv.Value, oldName, StringComparison.OrdinalIgnoreCase))
                                   .Select(kv => kv.Key).ToList())
            _d.Assign[k] = name;
        Save();
        return null;
    }

    // Os apps dessa categoria voltam para "Sem categoria"
    public static void Delete(string name)
    {
        var i = IndexOf(name);
        if (i < 0) return;
        var real = _d.Categories[i];
        _d.Categories.RemoveAt(i);
        foreach (var k in _d.Assign.Where(kv => string.Equals(kv.Value, real, StringComparison.OrdinalIgnoreCase))
                                   .Select(kv => kv.Key).ToList())
            _d.Assign.Remove(k);
        Save();
    }

    // category == null tira o app da categoria
    public static void Assign(string target, string category)
    {
        if (string.IsNullOrEmpty(target)) return;
        var i = IndexOf(category);
        if (i < 0) _d.Assign.Remove(target);
        else _d.Assign[target] = _d.Categories[i];
        Save();
    }
}
