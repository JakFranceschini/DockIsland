using System.Linq;

namespace DockIsland;

public partial class MainWindow
{
    // Abriu o app (pelo dock, pelo lançador ou clicando na notificação): as notificações dele já foram vistas,
    // então somem da lista (e do Windows) e o selo do ícone desaparece.
    public void ClearNotifsFor(AppEntry a)
    {
        if (a == null || a.IsDivider) return;
        var list = _notifs.Where(n => !n.IsMusic && NotifBadges.Matches(n, a)).ToList();
        if (list.Count == 0) return;
        RemoveFromWindows(list);
        foreach (var n in list) _notifs.Remove(n);
    }

    // Abriu o app clicando na notificação da ilha: remove as notificações desse app
    void ClearNotifsByTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return;
        var list = _notifs.Where(n => !n.IsMusic && string.Equals(n.Target, target, System.StringComparison.OrdinalIgnoreCase)).ToList();
        if (list.Count == 0) return;
        RemoveFromWindows(list);
        foreach (var n in list) _notifs.Remove(n);
    }
}
