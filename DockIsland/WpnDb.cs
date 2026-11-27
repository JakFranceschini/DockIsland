using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Data.Sqlite;
using Microsoft.Win32;

namespace DockIsland;

// Leitura das notificações direto do banco do próprio Windows:
//   %LocalAppData%\Microsoft\Windows\Notifications\wpndatabase.db
// É onde o Windows guarda o conteúdo (XML) de todo toast que chega, de qualquer app (inclusive
// sites/PWAs do Edge e do Chrome). Não exige permissão nem pacote MSIX, ao contrário da API
// UserNotificationListener, que só funciona para apps empacotados.
// O banco é copiado (db + wal) para uma pasta temporária antes de ler, para nunca travar o do Windows.
internal static class WpnDb
{
    public class Row
    {
        public long Id;
        public string Aumid;
        public string Title, Body, Attribution, ImageSrc;
        public List<string> Texts = new();
        public DateTime Arrival;
        public long ArrivalRaw;
        // O Id da tabela é reaproveitado pelo Windows (INTEGER PRIMARY KEY sem AUTOINCREMENT): Id + hora de chegada é único
        public string Key => Id + ":" + ArrivalRaw;
    }

    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\Windows\Notifications");
    static readonly string DbPath = Path.Combine(Dir, "wpndatabase.db");
    static readonly string TmpDir = Path.Combine(Path.GetTempPath(), "DockIsland-wpn");

    static DateTime _stamp = DateTime.MinValue;

    static DateTime Stamp()
    {
        DateTime t = DateTime.MinValue;
        foreach (var f in new[] { DbPath, DbPath + "-wal" })
            try { var w = File.GetLastWriteTimeUtc(f); if (w > t) t = w; } catch { }
        return t;
    }

    static void CopyShared(string src, string dst)
    {
        using var i = new FileStream(src, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var o = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        i.CopyTo(o);
    }

    // null = nada mudou desde a última leitura (a menos que force). Lança exceção se não conseguir ler.
    public static List<Row> Read(bool force)
    {
        if (!File.Exists(DbPath)) throw new FileNotFoundException("wpndatabase.db não encontrado em " + Dir);
        var st = Stamp();
        if (!force && st == _stamp) return null;

        Directory.CreateDirectory(TmpDir);
        var copy = Path.Combine(TmpDir, "wpn.db");
        foreach (var f in new[] { copy, copy + "-wal", copy + "-shm" }) try { File.Delete(f); } catch { }
        CopyShared(DbPath, copy);
        if (File.Exists(DbPath + "-wal")) try { CopyShared(DbPath + "-wal", copy + "-wal"); } catch { }

        var rows = new List<Row>();
        var cs = new SqliteConnectionStringBuilder { DataSource = copy, Pooling = false }.ToString();
        using (var con = new SqliteConnection(cs))
        {
            con.Open();
            using var cmd = con.CreateCommand();
            cmd.CommandText =
                "SELECT n.Id, h.PrimaryId, n.Payload, n.ArrivalTime " +
                "FROM Notification n JOIN NotificationHandler h ON n.HandlerId = h.RecordId " +
                "WHERE n.Type = 'toast' ORDER BY n.ArrivalTime";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                try
                {
                    var row = new Row { Id = r.GetInt64(0), Aumid = r.IsDBNull(1) ? "" : r.GetString(1) };
                    try { row.ArrivalRaw = r.IsDBNull(3) ? 0 : r.GetInt64(3); } catch { }
                    try { row.Arrival = r.IsDBNull(3) ? DateTime.Now : DateTime.FromFileTimeUtc(r.GetInt64(3)).ToLocalTime(); }
                    catch { row.Arrival = DateTime.Now; }
                    string xml = null;
                    if (!r.IsDBNull(2))
                    {
                        var v = r.GetValue(2);
                        xml = v is byte[] b ? DecodeXml(b) : v?.ToString();
                    }
                    ParsePayload(row, xml);
                    rows.Add(row);
                }
                catch { }
            }
        }
        foreach (var f in new[] { copy, copy + "-wal", copy + "-shm" }) try { File.Delete(f); } catch { }
        _stamp = st;
        return rows;
    }

    static string DecodeXml(byte[] b)
    {
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE) return Encoding.Unicode.GetString(b, 2, b.Length - 2);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b, 3, b.Length - 3);
        // UTF-16 sem BOM (muitos zeros)?
        int zeros = 0; for (int i = 1; i < Math.Min(b.Length, 40); i += 2) if (b[i] == 0) zeros++;
        return zeros > 5 ? Encoding.Unicode.GetString(b) : Encoding.UTF8.GetString(b);
    }

    static void ParsePayload(Row row, string xml)
    {
        if (string.IsNullOrWhiteSpace(xml)) return;
        xml = xml.Trim('\0', '﻿', ' ', '\r', '\n');
        var doc = XDocument.Parse(xml);
        var binding = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "binding"
                          && (string)e.Attribute("template") == "ToastGeneric")
                      ?? doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "binding");
        if (binding == null) return;
        foreach (var t in binding.Elements().Where(e => e.Name.LocalName == "text"))
        {
            var text = (t.Value ?? "").Trim();
            if (text.Length == 0) continue;
            if (string.Equals((string)t.Attribute("placement"), "attribution", StringComparison.OrdinalIgnoreCase))
                row.Attribution = text;   // "via Microsoft Edge"
            else row.Texts.Add(text);
        }
        // ícone que o toast traz (o Vincular ao celular manda o ícone do app do celular aqui)
        foreach (var im in doc.Descendants().Where(e => e.Name.LocalName == "image"))
        {
            var pl = (string)im.Attribute("placement");
            if (string.Equals(pl, "hero", StringComparison.OrdinalIgnoreCase)) continue;
            var src = (string)im.Attribute("src");
            if (string.IsNullOrWhiteSpace(src)) continue;
            if (pl == null && row.ImageSrc != null) continue;
            row.ImageSrc = src;
            if (string.Equals(pl, "appLogoOverride", StringComparison.OrdinalIgnoreCase)) break;
        }
        row.Title = row.Texts.FirstOrDefault() ?? "";
        row.Body = string.Join(" ", row.Texts.Skip(1));
    }

    // Nome amigável do app a partir do AppUserModelID (apps de área de trabalho, PWAs e sites registram aqui)
    public static string DisplayName(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return null;
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var k = hive.OpenSubKey(@"Software\Classes\AppUserModelId\" + aumid);
                var n = k?.GetValue("DisplayName") as string;
                if (!string.IsNullOrWhiteSpace(n) && !n.StartsWith("@")) return n.Trim();
            }
            catch { }
        }
        return null;
    }

    // ---- notificações já limpas na ilha (para não voltarem quando o programa reabrir) ----
    static readonly string DismissPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "notif-dismissed.json");

    // Formato novo: lista de chaves "Id:ArrivalTime". O arquivo antigo (só Ids) é lido à parte, só para
    // as notificações que já estavam no banco na abertura: Ids reaproveitados escondiam notificações novas.
    static readonly string DismissPathV2 = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DockIsland", "notif-dismissed2.json");

    public static HashSet<string> LoadDismissed()
    {
        try { return JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(DismissPathV2)) ?? new(); }
        catch { return new(); }
    }

    public static HashSet<long> LoadDismissedLegacy()
    {
        try { return JsonSerializer.Deserialize<HashSet<long>>(File.ReadAllText(DismissPath)) ?? new(); }
        catch { return new(); }
    }

    public static void SaveDismissed(HashSet<string> set)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DismissPathV2)!);
            var keep = set.OrderByDescending(x => x.Substring(x.IndexOf(':') + 1).PadLeft(20, '0')).Take(500).ToList();
            AtomicFile.WriteAllText(DismissPathV2, JsonSerializer.Serialize(keep));
        }
        catch { }
    }
}
