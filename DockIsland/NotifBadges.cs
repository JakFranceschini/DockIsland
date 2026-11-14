using System;
using System.Collections.Generic;
using System.Linq;

namespace DockIsland;

// Selo com a quantidade de notificações no ícone do app (dock e lançador).
// A contagem vem da mesma lista da aba Notificações (menos as de música): o que você limpa lá
// some do selo, e o que chega acende o selo do app correspondente.
internal static class NotifBadges
{
    static NotifItem[] _items = Array.Empty<NotifItem>();

    // Disparado a cada mudança na lista de notificações: dock e lançador atualizam os selos
    public static event Action Changed;

    public static void Update(IEnumerable<NotifItem> notifs)
    {
        _items = notifs.Where(n => !n.IsMusic).ToArray();
        try { Changed?.Invoke(); } catch { }
    }

    static bool Eq(string x, string y) =>
        !string.IsNullOrEmpty(x) && !string.IsNullOrEmpty(y) && string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    // "shell:AppsFolder\Microsoft.Foo_abc!App" -> "Microsoft.Foo_abc!App"
    static string AumidOf(string target)
    {
        const string p = @"shell:AppsFolder\";
        return !string.IsNullOrEmpty(target) && target.StartsWith(p, StringComparison.OrdinalIgnoreCase)
            ? target.Substring(p.Length) : null;
    }

    // A notificação é deste app? Casa por AppUserModelID, por caminho do programa ou pelo nome.
    public static bool Matches(NotifItem n, AppEntry a)
    {
        if (n == null || a == null || a.IsDivider) return false;
        var t = n.Target;
        if (!string.IsNullOrEmpty(t) &&
            (Eq(t, a.Aumid) || Eq(t, AumidOf(a.Target)) || Eq(t, a.Target) || Eq(t, a.ResolvedExe))) return true;
        return Eq(n.App, a.Name);
    }

    public static int CountFor(AppEntry a)
    {
        if (a == null || a.IsDivider) return 0;
        int c = 0;
        foreach (var n in _items) if (Matches(n, a)) c++;
        return c;
    }
}
