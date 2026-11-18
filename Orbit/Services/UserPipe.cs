using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Orbit.Services;

// Pipe só do usuário atual entre um processo curto (`Orbit.exe --algo …`) e o Orbit aberto:
// uma mensagem de texto por conexão.
public static class UserPipe
{
    // Lado do processo curto. Nunca lança: sem Orbit aberto, só desiste.
    public static void Send(string pipeName, string text)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1000);
            using var writer = new StreamWriter(client);
            writer.Write(text);
        }
        catch { }
    }

    // Lado do Orbit aberto: chama onText (thread do pool) a cada mensagem.
    public static void Listen(string pipeName, Action<string> onText, CancellationToken ct) => _ = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server);
                onText(await reader.ReadToEndAsync(ct));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warning(ex, "Pipe {Pipe}: falha", pipeName);
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
            }
        }
    }, ct);
}
