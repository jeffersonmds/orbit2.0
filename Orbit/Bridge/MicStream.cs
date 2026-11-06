using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Serilog;

namespace Orbit.Bridge;

public sealed record MicOpenRequest(string? DeviceId, string? Name, string? Resume);

// Microfone do PC no aparelho: WebSocket por tíquete (o browser não manda cabeçalho no WS).
// 1ª mensagem (texto) = {"rate":N}; depois binárias com PCM 16 bits mono. Independe da câmera.
public static class MicStream
{
    private static readonly SemaphoreSlim CaptureGate = new(1, 1);

    // Na 1ª vez o Windows pergunta NA TELA DO PC se o Orbit pode usar o microfone, e abrir fica
    // parado até alguém responder. Quem está no sofá não vê: depois deste prazo o aparelho é avisado.
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
            h.Notify(new BridgeEvent("Microfone aberto", string.IsNullOrWhiteSpace(req.Name) ? "Microfone" : req.Name, Device: BridgeServer.DeviceOf(ctx).Name));
            var owner = BridgeServer.DeviceOf(ctx);
            var ticket = tickets.Issue(owner.Hash, id, DateTime.Now);
            if (!sessions.PrepareMedia("mic", ticket, RemoteScreen.Who(tokens, owner.Hash), req.Resume)) return Results.Conflict();
            return Results.Ok(new { ticket });
        });

        api.MapPost("/mic/close", (HttpContext ctx, string? id) => { sessions.StopMedia("mic", BridgeServer.DeviceOf(ctx).Hash, id); return Results.NoContent(); });
        api.MapGet("/mic/status", (HttpContext ctx, string id) =>
        {
            var state = sessions.MediaStatus("mic", BridgeServer.DeviceOf(ctx).Hash, id);
            return Results.Ok(new { allowed = state.Allowed, active = state.Active, name = state.Name });
        });

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
            ctx.Items["device"] = tokens.Find(owner); // a auditoria mostra o aparelho do tíquete
            if (!BridgeServer.TicketOwnerAllowed(tokens, owner, ctx.Request.Path))
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            var session = sessions.BeginMedia("mic", ticket!, ctx.RequestAborted, RemoteScreen.Who(tokens, owner));
            if (session is null) { ctx.Response.StatusCode = StatusCodes.Status409Conflict; return; }
            WebSocket socket;
            try { socket = await ctx.WebSockets.AcceptWebSocketAsync(); }
            catch { sessions.End(session); throw; }
            using var ws = socket;
            // revogado entre a checagem e a vaga (o await acima): sem vaga a revogação não achou nada pra cortar
            if (!BridgeServer.TicketOwnerAllowed(tokens, owner, ctx.Request.Path)) session.Cancel();
            var ct = session.Token;
            var queue = new PcmQueue();
            var signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
            int captureRate = Orbit.Services.MicCapture.TargetRate;
            int ended = 0;
            IDisposable? mic = null;
            try
            {
                var opening = Task.Run(async () =>
                {
                    await CaptureGate.WaitAsync(ct);
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        return h.OpenMic(deviceId, chunk =>
                        {
                            if (ct.IsCancellationRequested || chunk.Length == 0) return;
                            queue.Push(chunk, captureRate, Environment.TickCount64);
                            signal.Writer.TryWrite(true);
                        }, error => { Interlocked.Exchange(ref ended, 1); signal.Writer.TryComplete(error); });
                    }
                    catch { CaptureGate.Release(); throw; }
                });
                if (await Task.WhenAny(opening, Task.Delay(ConsentTimeout, ct)) != opening)
                {
                    // outro aparelho assumiu (ou parou) enquanto esperava: não é falta de consentimento
                    if (ct.IsCancellationRequested)
                    {
                        _ = opening.ContinueWith(t => { if (t.IsCompletedSuccessfully) { try { t.Result.Stop.Dispose(); } finally { CaptureGate.Release(); } } else _ = t.Exception; }, TaskScheduler.Default);
                        return;
                    }
                    sessions.StopMedia("mic", owner, ticket);
                    h.Notify(new BridgeEvent("Autorize o microfone pro Orbit", "O Windows está perguntando nesta tela", Device: tokens.Find(owner)?.Name));
                    // se autorizarem depois, solta na hora: ninguém está ouvindo
                    _ = opening.ContinueWith(t => { if (t.IsCompletedSuccessfully) { try { t.Result.Stop.Dispose(); } finally { CaptureGate.Release(); } } else _ = t.Exception; }, TaskScheduler.Default);
                    await ws.CloseAsync(WebSocketCloseStatus.PolicyViolation, ConsentReason, CancellationToken.None);
                    return;
                }
                int rate;
                try { (rate, mic) = await opening; }
                catch (Exception ex)
                {
                    sessions.StopMedia("mic", owner, ticket);
                    Log.Warning(ex, "Microfone: não abriu");
                    await ws.CloseAsync(WebSocketCloseStatus.InternalServerError, "microfone indisponível", CancellationToken.None);
                    return;
                }
                captureRate = rate;
                ct.ThrowIfCancellationRequested();
                await ws.SendAsync(Encoding.UTF8.GetBytes($"{{\"rate\":{rate}}}"), WebSocketMessageType.Text, true, ct);
                // Se o aparelho fechar, o ReceiveAsync termina e cancela o envio (sem token: cancelar aborta o socket e o 4001 não sairia).
                _ = Task.Run(async () =>
                {
                    try { while (ws.State == WebSocketState.Open && (await ws.ReceiveAsync(new byte[64], CancellationToken.None)).MessageType != WebSocketMessageType.Close) { } }
                    catch { }
                    try { session.Cancel(); } catch (ObjectDisposedException) { }
                });
                while (await signal.Reader.WaitToReadAsync(ct).AsTask().WaitAsync(TimeSpan.FromSeconds(8), ct))
                {
                    signal.Reader.TryRead(out _);
                    while (queue.Take(Environment.TickCount64) is { } packet)
                    {
                        using var send = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        send.CancelAfter(TimeSpan.FromSeconds(3));
                        await ws.SendAsync(packet.Wire(Environment.TickCount64), WebSocketMessageType.Binary, true, send.Token);
                    }
                }
                sessions.StopMedia("mic", owner, ticket);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            catch (Exception ex)
            {
                Log.Warning(ex, "Microfone: sem sinal");
                sessions.StopMedia("mic", owner, ticket);
            }
            finally
            {
                if (Volatile.Read(ref ended) != 0) sessions.StopMedia("mic", owner, ticket);
                if (mic is not null) { try { mic.Dispose(); } finally { CaptureGate.Release(); } }
                await RemoteScreen.CloseAsync(ws, sessions, session);
            }
        });
    }
}

// Limite em tempo, sem depender do tamanho dos pacotes do driver.
public sealed record PcmPacket(uint Sequence, long Captured, byte[] Pcm)
{
    public byte[] Wire(long now)
    {
        var wire = new byte[Pcm.Length + 16];
        BinaryPrimitives.WriteUInt32LittleEndian(wire, Sequence);
        BinaryPrimitives.WriteUInt32LittleEndian(wire.AsSpan(4), (uint)Math.Max(0, now - Captured));
        BinaryPrimitives.WriteInt64LittleEndian(wire.AsSpan(8), BitConverter.DoubleToInt64Bits(Captured));
        Pcm.CopyTo(wire, 16);
        return wire;
    }
}

public sealed class PcmQueue
{
    public const int MaxMs = 300;
    private readonly Queue<PcmPacket> _queue = new();
    private int _bytes;
    private uint _sequence;
    private readonly object _gate = new();

    public void Push(byte[] pcm, int rate, long now)
    {
        lock (_gate)
        {
            int limit = Math.Max(2, rate * 2 * MaxMs / 1000 / 2 * 2);
            int valid = pcm.Length / 2 * 2;
            int length = Math.Min(limit, valid);
            if (length == 0) return;
            while (_bytes + length > limit && _queue.Count > 0) _bytes -= _queue.Dequeue().Pcm.Length;
            var kept = pcm.AsSpan(valid - length, length).ToArray();
            _queue.Enqueue(new(++_sequence, now, kept));
            _bytes += length;
        }
    }

    public PcmPacket? Take(long now)
    {
        lock (_gate)
        {
            while (_queue.TryDequeue(out var p))
            {
                _bytes -= p.Pcm.Length;
                if (now - p.Captured <= MaxMs) return p;
            }
            return null;
        }
    }
}
