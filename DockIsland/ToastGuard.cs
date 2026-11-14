using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Win32;

namespace DockIsland;

// Esconde o balão (toast) do Windows para que a notificação apareça só na ilha.
// Usa a mesma opção de Configurações > Sistema > Notificações > (app) > "Mostrar banners de notificação":
// HKCU\...\Notifications\Settings\<app>\ShowBanner = 0.
// A notificação continua indo para a Central de Notificações e a ilha continua lendo ela normalmente.
// Ao sair, os valores originais são restaurados (e, se o programa fechar de forma inesperada,
// são restaurados na próxima abertura).
public static class ToastGuard
{
    const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    const string ValueName = "ShowBanner";

    static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "toast-restore.json");

    // app (AUMID) -> valor original de ShowBanner (null = não existia)
    static readonly Dictionary<string, int?> _originals = new();
    static readonly HashSet<string> _handled = new();   // apps já verificados nesta sessão
    static bool _active;

    public static void Start()
    {
        RestoreFromFile();   // sobra de uma sessão que fechou sem restaurar
        _active = true;
        Scan();
    }

    // Chame de tempos em tempos: apps novos ganham a chave só depois do primeiro toast
    public static void Scan()
    {
        if (!_active) return;
        try
        {
            using var root = Registry.CurrentUser.OpenSubKey(BaseKey);
            if (root == null) return;
            foreach (var name in root.GetSubKeyNames()) Suppress(name);
        }
        catch { }
    }

    public static void Suppress(string aumid)
    {
        if (!_active || string.IsNullOrEmpty(aumid) || !_handled.Add(aumid)) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey($@"{BaseKey}\{aumid}");
            if (key == null) return;
            var cur = key.GetValue(ValueName);
            int? original = cur is int i ? i : null;
            if (original == 0) return;   // já estava sem banner: não mexe
            _originals[aumid] = original;
            Save();
            key.SetValue(ValueName, 0, RegistryValueKind.DWord);
        }
        catch { }
    }

    public static void Stop()
    {
        _active = false;
        Restore(_originals);
        _originals.Clear();
        _handled.Clear();
        try { File.Delete(StatePath); } catch { }
    }

    static void Restore(Dictionary<string, int?> map)
    {
        foreach (var (aumid, original) in map)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"{BaseKey}\{aumid}", true);
                if (key == null) continue;
                if (original == null) key.DeleteValue(ValueName, false);
                else key.SetValue(ValueName, original.Value, RegistryValueKind.DWord);
            }
            catch { }
        }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            var dto = _originals.ToDictionary(k => k.Key, k => k.Value ?? -1);
            AtomicFile.WriteAllText(StatePath, JsonSerializer.Serialize(dto));
        }
        catch { }
    }

    static void RestoreFromFile()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var dto = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(StatePath));
            if (dto != null) Restore(dto.ToDictionary(k => k.Key, k => k.Value < 0 ? (int?)null : k.Value));
            File.Delete(StatePath);
        }
        catch { }
    }
}
