using System;
using System.IO;
using System.IO.Pipes;

namespace Orbit.Services;

// Lado cliente: roda dentro de `Orbit.exe --claude-hook`, um processo curto
// disparado pelo próprio Claude Code a cada hook. Nunca pode lançar nem travar —
// se o Orbit não estiver aberto, ou o pipe não responder, só desiste em silêncio.
public static class ClaudeHookClient
{
    public static void SendFromStdin()
    {
        try
        {
            // ponytail: assume stdin sempre redirecionado pelo hook runner do Claude Code;
            // uma chamada manual/interativa travaria aqui.
            string json = Console.In.ReadToEnd();
            using var client = new NamedPipeClientStream(".", ClaudeHookServer.PipeName, PipeDirection.Out);
            client.Connect(300);
            using var writer = new StreamWriter(client);
            writer.Write(json);
            writer.Flush();
        }
        catch
        {
            // Sem Orbit rodando, timeout, pipe ocupado etc — o hook do Claude Code
            // não pode falhar/travar por causa disso.
        }
    }
}
