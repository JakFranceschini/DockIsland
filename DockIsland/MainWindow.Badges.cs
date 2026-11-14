using System.Linq;

namespace DockIsland;

public partial class MainWindow
{
    // Abriu o app pelo dock ou pelo lançador: tira as notificações dele da lista (e o selo some)
    public void ClearNotifsFor(AppEntry a)
    {
        try
        {
            var mine = _notifs.Where(n => !n.IsMusic && NotifBadges.Matches(n, a)).ToList();
            RemoveFromWindows(mine);   // senão voltariam na próxima vez que o programa abrisse
            foreach (var n in mine) _notifs.Remove(n);
        }
        catch { }
    }
}
