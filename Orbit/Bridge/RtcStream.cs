using System;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Orbit.Services;
using Serilog;

namespace Orbit.Bridge;

public sealed record RtcOpenRequest(string? DeviceId, string? Quality, string? Resume);

// O PC oferece; o aparelho só responde SDP/ICE. Nada de scripts/comandos vindos do socket.
public static class RtcSignal
{
    public const int MaxBytes = 65536;
    public static bool HostCandidate(string? value) => value is { Length: > 0 and <= 2048 }
        && !value.Contains('\r') && !value.Contains('\n') && value.StartsWith("candidate:", StringComparison.Ordinal)
        && value.Split(' ', StringSplitOptions.RemoveEmptyEntries) is { Length: >= 8 } parts && parts[6] == "typ" && parts[7] == "host";

    public static bool Valid(string json, bool fromPc)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes) return false;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String) return false;
            var type = t.GetString();
            if (type == "error") return fromPc && root.EnumerateObject().Count() == 1;
            if (type == (fromPc ? "offer" : "answer"))
            {
                if (root.EnumerateObject().Count() != 2 || !root.TryGetProperty("sdp", out var s) || s.ValueKind != JsonValueKind.String) return false;
                var sdp = s.GetString()!;
                return sdp.StartsWith("v=0\r\n", StringComparison.Ordinal) && sdp.Length < MaxBytes
                    && sdp.Split('\n').Where(l => l.StartsWith("a=candidate:", StringComparison.Ordinal)).All(l => HostCandidate(l.TrimEnd('\r')[2..]));
            }
            if (type != "ice" || root.EnumerateObject().Count() != 2 || !root.TryGetProperty("candidate", out var c) || c.ValueKind != JsonValueKind.Object) return false;
            if (!c.TryGetProperty("candidate", out var candidate) || candidate.ValueKind != JsonValueKind.String || !HostCandidate(candidate.GetString())) return false;
            foreach (var p in c.EnumerateObject())
            {
                if (p.Name == "candidate") continue;
                if (p.Name is "sdpMid" or "usernameFragment") { if (p.Value.ValueKind != JsonValueKind.Null && (p.Value.ValueKind != JsonValueKind.String || p.Value.GetString()!.Length > 256)) return false; }
                else if (p.Name == "sdpMLineIndex") { if (p.Value.ValueKind != JsonValueKind.Null && (!p.Value.TryGetInt32(out int index) || index < 0 || index > 8)) return false; }
                else return false;
            }
            return true;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return false; }
    }
}

public static class RtcStream
{
    internal static readonly SemaphoreSlim CameraGate = new(1, 1);
    private sealed record Source(string Kind, string Id, string Label, CameraPreset Preset);
    public static void Map(RouteGroupBuilder api, BridgeHandlers h, TokenStore tokens, RemoteSessions sessions)
    {
        foreach (var kind in new[] { "camera", "mic" })
        {
            var tickets = new OneShotTickets<Source>();
            api.MapPost("/" + kind + "/rtc/open", async (RtcOpenRequest req, HttpContext ctx) =>
            {
                var owner = BridgeServer.DeviceOf(ctx);
                var guard = sessions.TransferToken(owner.Hash);
                var label = kind == "camera"
                    ? (await Camera.DevicesAsync()).FirstOrDefault(d => d.Id == req.DeviceId)?.Name
                    : h.MicDevices().FirstOrDefault(d => d.Id == req.DeviceId)?.Name;
                if (label is null || req.DeviceId is null) return Results.NotFound();
                if (!BridgeServer.TicketOwnerAllowed(tokens, owner.Hash, ctx.Request.Path)) return Results.StatusCode(403);
                var tk = tickets.Issue(owner.Hash, new(kind, req.DeviceId, label, CameraPreset.Parse(req.Quality, null)), DateTime.Now);
                if (!sessions.PrepareMedia(kind, tk, RemoteScreen.Who(tokens, owner.Hash), req.Resume, guard)) return Results.Conflict();
                h.Notify(new BridgeEvent(kind == "camera" ? "Câmera aberta" : "Microfone aberto", label, Device: owner.Name));
                return Results.Ok(new { ticket = tk });
            });
            api.MapGet("/" + kind + "/rtc/signal", async (HttpContext ctx, string? ticket) =>
            {
                if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
                if (!tickets.TryClaim(ticket, DateTime.Now, out var source, out var owner)) { ctx.Response.StatusCode = 401; return; }
                ctx.Items["device"] = tokens.Find(owner);
                if (!BridgeServer.TicketOwnerAllowed(tokens, owner, ctx.Request.Path)) { ctx.Response.StatusCode = 403; return; }
                var session = sessions.BeginMedia(kind, ticket!, ctx.RequestAborted, RemoteScreen.Who(tokens, owner));
                if (session is null) { ctx.Response.StatusCode = 409; return; }
                WebSocket socket;
                try { socket = await ctx.WebSockets.AcceptWebSocketAsync(); }
                catch { sessions.End(session); throw; }
                using var ws = socket;
                if (!BridgeServer.TicketOwnerAllowed(tokens, owner, ctx.Request.Path)) session.Cancel();
                var ct = session.Token;
                var capture = new WebRtcCapture();
                // Cancela o documento sem esperar Initialize/getUserMedia retornarem.
                using var cut = ct.Register(() => { _ = capture.CloseAsync(); });
                Task? receive = null, send = null;
                var gate = kind == "camera" ? CameraGate : MicStream.CaptureGate;
                bool held = false;
                async Task Listen()
                {
                    try { await Receive(ws, capture, ct); }
                    finally { session.Cancel(); }
                }
                try
                {
                    receive = Listen();
                    await gate.WaitAsync(ct); held = true;
                    await capture.OpenAsync(source.Kind, source.Id, source.Label, source.Preset, ct).WaitAsync(ct);
                    send = Send(ws, capture, ct);
                    await Task.WhenAny(receive, send);
                }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
                catch (Exception ex) { Log.Warning(ex, "WebRTC: motor indisponível"); }
                finally
                {
                    session.Cancel();
                    await capture.CloseAsync();
                    if (held) gate.Release();
                    // Uma queda permite fallback com resume; StopMedia/StopAll já invalidaram a intenção.
                    if (send is not null) try { await send; } catch { }
                    await RemoteScreen.CloseAsync(ws, sessions, session);
                    if (receive is not null) try { await receive; } catch { }
                }
            });
        }
    }

    private static async Task Send(WebSocket ws, WebRtcCapture capture, CancellationToken ct)
    {
        await foreach (var json in capture.Messages.ReadAllAsync(ct))
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, limit.Token);
        }
    }

    private static async Task Receive(WebSocket ws, WebRtcCapture capture, CancellationToken ct)
    {
        var buf = new byte[RtcSignal.MaxBytes];
        var window = Environment.TickCount64;
        int count = 0;
        bool answered = false;
        while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            int length = 0;
            WebSocketReceiveResult r;
            do
            {
                // WaitAsync não aborta o socket: ainda permite avisar quem assumiu a vaga.
                r = await ws.ReceiveAsync(new ArraySegment<byte>(buf, length, buf.Length - length), CancellationToken.None).WaitAsync(ct);
                if (r.MessageType == WebSocketMessageType.Close) return;
                if (r.MessageType != WebSocketMessageType.Text) throw new InvalidOperationException("Sinalização inválida.");
                length += r.Count;
                if (length == buf.Length && !r.EndOfMessage) throw new InvalidOperationException("Sinalização grande demais.");
            } while (!r.EndOfMessage);
            if (Environment.TickCount64 - window >= 1000) { window = Environment.TickCount64; count = 0; }
            if (++count > 100) throw new InvalidOperationException("Sinalização em excesso.");
            var json = Encoding.UTF8.GetString(buf, 0, length);
            if (!RtcSignal.Valid(json, false)) throw new InvalidOperationException("Sinalização inválida.");
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.GetProperty("t").GetString() == "answer")
            {
                if (answered) throw new InvalidOperationException("Resposta repetida.");
                answered = true;
            }
            else if (!answered) throw new InvalidOperationException("ICE antes da resposta.");
            await capture.PostAsync(json, ct);
        }
    }
}
