using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orbit.Services;
using Serilog;

namespace Orbit.Bridge;

public sealed record ScreenOpenRequest(string? Screen, string? Quality);
public sealed record RemoteTicketRequest(string? Screen);

// Tíquete de uso único (30 s pra conectar) com um valor junto. Vários em aberto (vídeo + controle).
public sealed class OneShotTickets<T>
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Dictionary<string, (T Value, DateTime Expires)> _tickets = new();

    public string Issue(T value, DateTime now)
    {
        var tk = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            foreach (var k in _tickets.Where(t => t.Value.Expires < now).Select(t => t.Key).ToList()) _tickets.Remove(k);
            _tickets[tk] = (value, now + Lifetime);
        }
        return tk;
    }

    // Uso único mesmo expirado. Tíquete inexistente/vencido = false.
    public bool TryClaim(string? ticket, DateTime now, out T value)
    {
        value = default!;
        if (ticket is null) return false;
        lock (_gate)
        {
            if (!_tickets.Remove(ticket, out var t) || now > t.Expires) return false;
            value = t.Value;
            return true;
        }
    }
}

// Uma sessão de vídeo e uma de controle por vez (uma nova derruba a anterior). Avisa a ilha a cada
// mudança pra pílula mostrar o indicador; StopAll é o "Encerrar" da aba iPhone.
public sealed class RemoteSessions
{
    private readonly object _gate = new();
    private readonly Action<bool, bool> _changed;
    private CancellationTokenSource? _video, _input;

    public RemoteSessions(Action<bool, bool> changed) => _changed = changed;

    public CancellationTokenSource BeginVideo(CancellationToken aborted) => Begin(ref _video, aborted);
    public CancellationTokenSource BeginInput(CancellationToken aborted) => Begin(ref _input, aborted);

    private CancellationTokenSource Begin(ref CancellationTokenSource? slot, CancellationToken aborted)
    {
        var s = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        lock (_gate)
        {
            slot?.Cancel();
            slot = s;
        }
        Notify();
        return s;
    }

    public void End(CancellationTokenSource s)
    {
        lock (_gate)
        {
            if (_video == s) _video = null;
            if (_input == s) _input = null;
        }
        s.Dispose();
        Notify();
    }

    // "Parar" do iPhone: o Safari não derruba a conexão MJPEG só trocando a imagem.
    public void StopVideo()
    {
        lock (_gate) _video?.Cancel();
    }

    public void StopAll()
    {
        lock (_gate)
        {
            _video?.Cancel();
            _input?.Cancel();
        }
    }

    private void Notify()
    {
        bool v, i;
        lock (_gate) (v, i) = (_video is not null, _input is not null);
        try { _changed(v, i); } catch (Exception ex) { Log.Warning(ex, "Tela remota: falha ao avisar a ilha"); }
    }
}

public static class RemoteScreen
{
    public const int MaxMessagesPerSecond = 300;
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(1); // reenvia o quadro parado (o Safari só mostra ao chegar o próximo)

    public static void Map(RouteGroupBuilder api, BridgeHandlers h, RemoteSessions sessions)
    {
        var video = new OneShotTickets<(string Screen, ScreenQuality Quality)>();
        var input = new OneShotTickets<string>();

        ScreenInfo? Find(string? id) { var all = h.Screens(); return all.FirstOrDefault(s => s.Id == id) ?? all.FirstOrDefault(); }

        api.MapGet("/screen/list", () => Results.Ok(h.Screens().Select(s => new { id = s.Id, name = s.Name })));

        api.MapPost("/screen/open", (ScreenOpenRequest req) =>
        {
            if (Find(req.Screen) is not { } screen) return Results.NotFound();
            h.Notify(new BridgeEvent("Tela compartilhada com o iPhone", screen.Name));
            return Results.Ok(new { ticket = video.Issue((screen.Id, ScreenQuality.Parse(req.Quality)), DateTime.Now) });
        });

        api.MapGet("/screen/stream", async (HttpContext ctx, string? ticket) =>
        {
            if (!video.TryClaim(ticket, DateTime.Now, out var req) || Find(req.Screen) is not { } screen)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var session = sessions.BeginVideo(ctx.RequestAborted);
            var ct = session.Token;
            try
            {
                ctx.Response.ContentType = Mjpeg.ContentType;
                ctx.Response.Headers.CacheControl = "no-cache, no-store";
                var body = ctx.Response.BodyWriter;
                byte[]? last = null;
                var sent = Stopwatch.StartNew();
                while (!ct.IsCancellationRequested)
                {
                    var t0 = Stopwatch.GetTimestamp();
                    var jpeg = await Task.Run(() => h.CaptureScreen(screen.Bounds, req.Quality), ct);
                    if (jpeg is not null && (last is null || sent.Elapsed >= KeepAlive || !jpeg.AsSpan().SequenceEqual(last)))
                    {
                        await body.WriteAsync(Mjpeg.PartHeader(jpeg.Length), ct);
                        await body.WriteAsync(jpeg, ct);
                        await body.FlushAsync(ct);
                        last = jpeg;
                        sent.Restart();
                    }
                    var wait = req.Quality.FrameMs - (int)Stopwatch.GetElapsedTime(t0).TotalMilliseconds;
                    await Task.Delay(Math.Max(10, wait), ct);
                }
            }
            catch (OperationCanceledException) { /* parar, troca, encerrar ou iPhone fechou */ }
            finally { sessions.End(session); }
        });

        api.MapPost("/screen/close", () => { sessions.StopVideo(); return Results.NoContent(); });

        // Tíquete do WebSocket de controle, preso ao monitor que o iPhone está vendo.
        api.MapPost("/remote/ticket", (RemoteTicketRequest req) =>
            Find(req.Screen) is { } screen ? Results.Ok(new { ticket = input.Issue(screen.Id, DateTime.Now) }) : Results.NotFound());

        api.MapGet("/remote/input", async (HttpContext ctx, string? ticket) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (!input.TryClaim(ticket, DateTime.Now, out var screenId) || Find(screenId) is not { } screen)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var session = sessions.BeginInput(ctx.RequestAborted);
            try { await ReceiveLoop(ws, screen, h, session.Token); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            finally
            {
                sessions.End(session);
                if (ws.State == WebSocketState.Open)
                    try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", CancellationToken.None); } catch { }
            }
        });
    }

    private static async Task ReceiveLoop(WebSocket ws, ScreenInfo screen, BridgeHandlers h, CancellationToken ct)
    {
        var buf = new byte[8192];
        var window = Stopwatch.StartNew();
        int inWindow = 0;
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var res = await ws.ReceiveAsync(buf, ct);
            if (res.MessageType == WebSocketMessageType.Close) break;
            if (!res.EndOfMessage)
            {
                // mensagem grande demais: descarta até o fim dela
                while (!(await ws.ReceiveAsync(buf, ct)).EndOfMessage) { }
                continue;
            }
            if (window.ElapsedMilliseconds >= 1000) { window.Restart(); inWindow = 0; }
            if (++inWindow > MaxMessagesPerSecond) continue;
            if (RemoteInput.Parse(Encoding.UTF8.GetString(buf, 0, res.Count)) is { } cmd)
                h.RemoteInput(cmd, screen.Bounds);
        }
    }
}
