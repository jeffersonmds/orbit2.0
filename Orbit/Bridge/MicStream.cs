using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serilog;

namespace Orbit.Bridge;

public sealed record MicOpenRequest(string? DeviceId, string? Name);

// Microfone do PC no iPhone: WebSocket por tíquete (o browser não manda cabeçalho no WS).
// 1ª mensagem (texto) = {"rate":N}; depois binárias com PCM 16 bits mono. Independe da câmera.
public static class MicStream
{
    // ~2 s de áudio na fila: se a rede engasgar, descarta o mais antigo (atraso não acumula).
    private const int QueueChunks = 40;

    // Na 1ª vez o Windows pergunta NA TELA DO PC se o Orbit pode usar o microfone, e abrir fica
    // parado até alguém responder. Quem está no sofá não vê: depois deste prazo o iPhone é avisado.
    public static TimeSpan ConsentTimeout { get; set; } = TimeSpan.FromSeconds(20);
    public const string ConsentReason = "autorize no PC";

    public static void Map(RouteGroupBuilder api, BridgeHandlers h, TokenStore tokens, RemoteSessions sessions)
    {
        var tickets = new OneShotTickets<string?>();

        api.MapGet("/mic/devices", () => Results.Ok(h.MicDevices().Select(d => new { id = d.Id, name = d.Name })));

        api.MapPost("/mic/open", (MicOpenRequest req, HttpContext ctx) =>
        {
            var id = string.IsNullOrWhiteSpace(req.DeviceId) ? null : req.DeviceId;
            if (id is not null && h.MicDevices().All(d => d.Id != id)) return Results.NotFound();
            h.Notify(new BridgeEvent("Microfone aberto pelo iPhone", string.IsNullOrWhiteSpace(req.Name) ? "Microfone" : req.Name));
            return Results.Ok(new { ticket = tickets.Issue(BridgeServer.DeviceOf(ctx).Hash, id, DateTime.Now) });
        });

        api.MapPost("/mic/close", () => { sessions.StopAudio(); return Results.NoContent(); });

        api.MapGet("/mic/stream", async (HttpContext ctx, string? ticket) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (!tickets.TryClaim(ticket, DateTime.Now, out var deviceId, out var owner))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            if (!BridgeServer.TicketOwnerAllowed(tokens, owner, ctx.Request.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var session = sessions.BeginAudio(ctx.RequestAborted);
            var ct = session.Token;
            var queue = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(QueueChunks)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            IDisposable? mic = null;
            try
            {
                var opening = Task.Run(() => h.OpenMic(deviceId, chunk => queue.Writer.TryWrite(chunk)));
                if (await Task.WhenAny(opening, Task.Delay(ConsentTimeout, ct)) != opening)
                {
                    h.Notify(new BridgeEvent("Autorize o microfone pro Orbit", "O Windows está perguntando nesta tela"));
                    // se autorizarem depois, solta na hora: ninguém está ouvindo
                    _ = opening.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Stop.Dispose(); }, TaskScheduler.Default);
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, ConsentReason, CancellationToken.None);
                    return;
                }
                int rate;
                try { (rate, mic) = await opening; }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Microfone: não abriu");
                    await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "microfone indisponível", CancellationToken.None);
                    return;
                }
                await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"rate\":{rate}}}"), WebSocketMessageType.Text, true, ct);
                // Se o iPhone fechar, o ReceiveAsync termina e cancela o envio.
                _ = Task.Run(async () =>
                {
                    try { while (ws.State == WebSocketState.Open && (await ws.ReceiveAsync(new byte[64], ct)).MessageType != WebSocketMessageType.Close) { } }
                    catch { }
                    try { session.Cancel(); } catch (ObjectDisposedException) { }
                });
                await foreach (var chunk in queue.Reader.ReadAllAsync(ct))
                    await ws.SendAsync(chunk, WebSocketMessageType.Binary, true, ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            finally
            {
                mic?.Dispose(); // solta o microfone na hora (o ícone do Windows apaga)
                sessions.End(session);
                if (ws.State == WebSocketState.Open)
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", CancellationToken.None); } catch { }
            }
        });
    }
}
