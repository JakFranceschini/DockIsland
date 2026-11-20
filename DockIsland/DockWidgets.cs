namespace DockIsland;

// Lista dos widgets do dock. Cada widget aparece sozinho em Ajustes > Dock > Widgets, com um interruptor.
//
// Para criar um widget novo:
//   1) adicione uma linha em DockWidgets.All (id curto, nome e descrição);
//   2) coloque o visual dele em DockWindow.xaml (ao lado do MusicWidget);
//   3) registre-o em DockWindow.AllWidgets e Available (DockWindow.Widgets.cs). O dock mostra um widget por vez e a
//      roda do mouse sobre o dock de widgets troca entre eles.
// O estado ligado/desligado é salvo sozinho em settings.json (AppSettings.Widgets).
public sealed record WidgetInfo(string Id, string Name, string Description, bool DefaultOn = true);

public static class DockWidgets
{
    public static readonly WidgetInfo[] All =
    {
        new("music", "Música", "Capa, nome e controles da música no dock quando o Spotify (ou outro player) estiver aberto"),
        new("weather", "Previsão do tempo", "Cartão com a temperatura e o tempo de agora, pela sua localização (cidade fixa: WeatherCity no settings.json)"),
        new("clock", "Relógio e calendário", "Hora, dia da semana e o dia do mês num quadradinho de calendário"),
        new("clip", "Último item copiado", "Mostra o texto (ou a captura de tela) que você copiou por último; clicar copia de novo"),
        new("trash", "Lixeira", "Quantos itens e quanto espaço a lixeira ocupa, com botão para esvaziar (pede confirmação)"),
    };

    public static WidgetInfo Find(string id) => System.Array.Find(All, w => w.Id == id);
}
