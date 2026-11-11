using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Orbit.Services;

// Escuta eventos de hook do Claude Code via named pipe. Um `Orbit.exe --claude-hook`
// por conexão; sempre re-arma pra aceitar a próxima.
public sealed class ClaudeHookServer : IDisposable
{
    // Nome próprio do 2.0: o 1.0 usa "OrbitClaudeHooks" com 1 instância; dividir o nome faria os dois
    // disputarem o pipe (laço de IOException a 100% de CPU) e eventos caírem no app errado.
    public const string PipeName = "Orbit2ClaudeHooks";
    private static readonly ILogger _log = Log.ForContext<ClaudeHookServer>();

    public event Action<ClaudeHookEvent>? HookReceived;

    // PermissionRequest: o hook espera a resposta no mesmo pipe. null = sem decisão (diálogo no terminal).
    public Func<ClaudeHookEvent, Task<PermissionAnswer?>>? PermissionRequested { get; set; }

    private readonly string _pipeName;
    private CancellationTokenSource? _cts;

    // pipeName só muda nos testes (o real pode estar ocupado pelo Orbit aberto).
    public ClaudeHookServer(string pipeName = PipeName) => _pipeName = pipeName;

    public void Start()
    {
        if (_cts != null)
        {
            _log.Warning("ClaudeHookServer: Start() chamado mais de uma vez, ignorando");
            return;
        }
        _cts = new CancellationTokenSource();
        _ = ListenLoop(_cts.Token);
    }

    // Protocolo: o cliente manda o JSON numa linha. Eventos comuns são tratados aqui, em ordem (Stop +
    // Notification colados não trocam de lugar); PermissionRequest fica com a conexão aberta até a resposta,
    // por isso o pipe aceita várias instâncias.
    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // ponytail: ACL padrão do pipe (sem PipeSecurity/CurrentUserOnly) — na prática só o
                // dono do processo acessa em máquina de usuário único; numa máquina compartilhada,
                // outra conta local poderia squatar o nome antes do Orbit subir. Risco aceito para
                // widget desktop pessoal; upgrade path é PipeSecurity restrita se isso um dia importar.
                server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                string? line = await new StreamReader(server).ReadLineAsync(ct);
                var evt = ClaudeHookEvent.Parse(line ?? "");
                if (evt.HookEventName == "PermissionRequest")
                {
                    _ = AnswerAsync(server, evt);
                    server = null; // a conexão agora é do AnswerAsync
                }
                else HookReceived?.Invoke(evt);
            }
            catch (OperationCanceledException)
            {
                // Dispose() cancelou: sai do loop na próxima checagem do while.
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "ClaudeHookServer: falha ao processar evento de hook");
                // Erro repetido (ex.: pipe ocupado) não pode virar laço quente de CPU e log.
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
            }
            finally { server?.Dispose(); }
        }
    }

    private async Task AnswerAsync(NamedPipeServerStream server, ClaudeHookEvent evt)
    {
        try
        {
            var answer = PermissionRequested is { } ask ? await ask(evt) : null;
            using var writer = new StreamWriter(server); // fecha o pipe junto
            await writer.WriteLineAsync(ClaudePermission.Response(answer, evt.Suggestions));
            await writer.FlushAsync();
        }
        catch (Exception ex)
        {
            // O Claude desistiu do hook (Esc, timeout): o pipe já fechou.
            _log.Information(ex, "ClaudeHookServer: resposta de permissão não entregue");
        }
        finally { server.Dispose(); }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
