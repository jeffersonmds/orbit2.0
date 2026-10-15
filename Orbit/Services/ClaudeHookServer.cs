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

    private CancellationTokenSource? _cts;

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

    private async Task ListenLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // ponytail: ACL padrão do pipe (sem PipeSecurity/CurrentUserOnly) — na prática só o
                // dono do processo acessa em máquina de usuário único; numa máquina compartilhada,
                // outra conta local poderia squatar o nome antes do Orbit subir. Risco aceito para
                // widget desktop pessoal; upgrade path é PipeSecurity restrita se isso um dia importar.
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server);
                string json = await reader.ReadToEndAsync();
                var evt = ClaudeHookEvent.Parse(json);
                HookReceived?.Invoke(evt);
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
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
