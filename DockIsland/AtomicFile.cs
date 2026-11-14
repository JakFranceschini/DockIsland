using System;
using System.IO;

namespace DockIsland;

// Grava o arquivo inteiro de uma vez (escreve num .tmp e troca): se o programa ou o Windows
// fechar no meio da gravação, o arquivo antigo continua intacto em vez de ficar corrompido/vazio.
static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;   // quem chamou já trata a falha como antes
        }
    }
}
