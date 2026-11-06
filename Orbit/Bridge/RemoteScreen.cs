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
// O que o aparelho está fazendo agora — vira o indicador de privacidade na pílula.
public sealed record RemoteState(bool Viewing, bool Controlling, bool Listening,
    string? Viewer = null, string? Controller = null, string? Listener = null,
    string? Camera = null); // aparelho vendo a câmera do PC (null = ninguém)
// Quem segura uma vaga: Hash identifica o aparelho, Name é o que aparece pra pessoa.
public sealed record SessionOwner(string Hash, string Name);
public sealed record RemoteTicketRequest(string? Screen);
public sealed record FocusRequest(long Id);

// Tíquete de uso único (30 s pra conectar) com um valor junto. Vários em aberto (vídeo + controle).
public sealed class OneShotTickets<T>
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);
    private readonly object _gate = new();
    private readonly Dictionary<string, (T Value, string Owner, DateTime Expires)> _tickets = new();

    // owner = Hash do aparelho que pediu: quem usa o tíquete herda as permissões dele.
    public string Issue(string owner, T value, DateTime now)
    {
        var tk = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            foreach (var k in _tickets.Where(t => t.Value.Expires < now).Select(t => t.Key).ToList()) _tickets.Remove(k);
            _tickets[tk] = (value, owner, now + Lifetime);
        }
        return tk;
    }

    // Uso único mesmo expirado. Tíquete inexistente/vencido = false.
    public bool TryClaim(string? ticket, DateTime now, out T value, out string owner)
    {
        value = default!;
        owner = "";
        if (ticket is null) return false;
        lock (_gate)
        {
            if (!_tickets.Remove(ticket, out var t) || now > t.Expires) return false;
            value = t.Value;
            owner = t.Owner;
            return true;
        }
    }
}

// Uma sessão de cada tipo por vez — vídeo da tela, controle, microfone (uma nova derruba a anterior).
// Cada vaga tem dono: quem perde a vaga pra outro aparelho fica sabendo quem assumiu (DisplacedBy).
// Avisa a ilha a cada mudança pra pílula mostrar o indicador; StopAll é o "Encerrar" da aba Dispositivos.
public sealed class RemoteSessions
{
    private readonly object _gate = new();
    private readonly Action<RemoteState> _changed;
    private CancellationTokenSource? _video, _input, _audio, _camera;
    private readonly Dictionary<CancellationTokenSource, SessionOwner> _owners = new();
    private readonly Dictionary<CancellationTokenSource, string> _displaced = new();
    private readonly Dictionary<string, CancellationTokenSource> _transfers = new(); // por aparelho

    public RemoteSessions(Action<RemoteState> changed) => _changed = changed;

    // Uploads/downloads do aparelho: revogar (StopDevice) ou desligar a ponte (StopAll) corta no meio.
    public CancellationToken TransferToken(string hash)
    {
        lock (_gate)
        {
            if (!_transfers.TryGetValue(hash, out var c)) _transfers[hash] = c = new CancellationTokenSource();
            return c.Token;
        }
    }

    public CancellationTokenSource BeginVideo(CancellationToken aborted, SessionOwner owner) => Begin(ref _video, aborted, owner);
    public CancellationTokenSource BeginInput(CancellationToken aborted, SessionOwner owner) => Begin(ref _input, aborted, owner);
    public CancellationTokenSource BeginAudio(CancellationToken aborted, SessionOwner owner) => Begin(ref _audio, aborted, owner);
    // Câmera: a vaga serve pro indicador e pra revogação/Encerrar alcançarem o stream (que pode estar
    // parado esperando um quadro que nunca vem).
    public CancellationTokenSource BeginCamera(CancellationToken aborted, SessionOwner owner) => Begin(ref _camera, aborted, owner);

    private CancellationTokenSource Begin(ref CancellationTokenSource? slot, CancellationToken aborted, SessionOwner owner)
    {
        var s = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        lock (_gate)
        {
            if (slot is not null)
            {
                // mesmo aparelho reconectando não é "assumiu"
                if (_owners.TryGetValue(slot, out var old) && old.Hash != owner.Hash) _displaced[slot] = owner.Name;
                slot.Cancel();
            }
            slot = s;
            _owners[s] = owner;
        }
        Notify();
        return s;
    }

    // Nome de quem tomou a vaga desta sessão, ou null (consultar ANTES de End).
    public string? DisplacedBy(CancellationTokenSource s)
    {
        lock (_gate) return _displaced.GetValueOrDefault(s);
    }

    // Captura falhando há um tempo (PC bloqueado, UAC, tela protegida): a imagem no aparelho está parada.
    private volatile bool _videoFrozen;
    public bool VideoFrozen { get => _videoFrozen; set => _videoFrozen = value; }
    public static readonly TimeSpan FrozenAfter = TimeSpan.FromSeconds(2);

    public SessionOwner? Viewer
    {
        get { lock (_gate) return Owner(_video); }
    }

    private SessionOwner? Owner(CancellationTokenSource? s) => s is not null && _owners.TryGetValue(s, out var o) ? o : null;

    public void End(CancellationTokenSource s)
    {
        lock (_gate)
        {
            if (_video == s) _video = null;
            if (_input == s) _input = null;
            if (_audio == s) _audio = null;
            if (_camera == s) _camera = null;
            _owners.Remove(s);
            _displaced.Remove(s);
        }
        s.Dispose();
        Notify();
    }

    // "Parar" do aparelho: o Safari não derruba a conexão MJPEG só trocando a imagem.
    // Só cancela se o aparelho que pediu é o dono da vaga (senão encerraria a sessão de outro).
    public void StopVideo(string hash)
    {
        lock (_gate) if (Owner(_video)?.Hash == hash) _video!.Cancel();
    }

    public void StopAudio(string hash)
    {
        lock (_gate) if (Owner(_audio)?.Hash == hash) _audio!.Cancel();
    }

    public void StopAll()
    {
        lock (_gate)
        {
            _video?.Cancel();
            _input?.Cancel();
            _audio?.Cancel();
            _camera?.Cancel();
            foreach (var c in _transfers.Values) c.Cancel();
            _transfers.Clear();
        }
    }

    // Revogar ou desligar permissão de um aparelho derruba só as sessões dele.
    public void StopDevice(string hash)
    {
        lock (_gate)
        {
            foreach (var s in new[] { _video, _input, _audio, _camera })
                if (s is not null && Owner(s)?.Hash == hash) s.Cancel();
            if (_transfers.Remove(hash, out var t)) t.Cancel();
        }
    }

    private void Notify()
    {
        RemoteState state;
        lock (_gate) state = new(_video is not null, _input is not null, _audio is not null,
            Owner(_video)?.Name, Owner(_input)?.Name, Owner(_audio)?.Name,
            _camera is null ? null : Owner(_camera)?.Name ?? "Aparelho");
        try { _changed(state); } catch (Exception ex) { Log.Warning(ex, "Tela remota: falha ao avisar a ilha"); }
    }
}

public static class RemoteScreen
{
    public const int TakenCloseCode = 4001; // WebSocket fechado porque outro aparelho assumiu (reason = nome dele)
    public const int MaxMessagesPerSecond = 300;
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(1); // reenvia o quadro parado (o Safari só mostra ao chegar o próximo)

    public static void Map(RouteGroupBuilder api, BridgeHandlers h, TokenStore tokens, RemoteSessions sessions)
    {
        var video = new OneShotTickets<(string Screen, ScreenQuality Quality)>();
        var input = new OneShotTickets<string>();

        // Sem id: o primeiro monitor. Id que sumiu (monitor desconectado entre o /open e o stream) NÃO cai
        // em outro: transmitiria um conteúdo que ninguém escolheu.
        ScreenInfo? Find(string? id)
        {
            var all = h.Screens();
            return string.IsNullOrEmpty(id) ? all.FirstOrDefault() : all.FirstOrDefault(s => s.Id == id);
        }

        api.MapGet("/screen/list", () => Results.Ok(h.Screens().Select(s => new { id = s.Id, name = s.Name })));

        api.MapPost("/screen/open", (ScreenOpenRequest req, HttpContext ctx) =>
        {
            if (Find(req.Screen) is not { } screen) return Results.NotFound();
            h.Notify(new BridgeEvent("Tela compartilhada", screen.Name, Device: BridgeServer.DeviceOf(ctx).Name));
            return Results.Ok(new { ticket = video.Issue(BridgeServer.DeviceOf(ctx).Hash, (screen.Id, ScreenQuality.Parse(req.Quality)), DateTime.Now) });
        });

        api.MapGet("/screen/stream", async (HttpContext ctx, string? ticket) =>
        {
            if (!video.TryClaim(ticket, DateTime.Now, out var req, out var owner))
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
            if (Find(req.Screen) is not { } screen)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            var session = sessions.BeginVideo(ctx.RequestAborted, Who(tokens, owner));
            // revogado entre a checagem e a vaga (o await acima): sem vaga a revogação não achou nada pra cortar
            if (!BridgeServer.TicketOwnerAllowed(tokens, owner, ctx.Request.Path)) session.Cancel();
            var ct = session.Token;
            try
            {
                ctx.Response.ContentType = Mjpeg.ContentType;
                ctx.Response.Headers.CacheControl = "no-cache, no-store";
                var body = ctx.Response.BodyWriter;
                byte[]? last = null;
                var sent = Stopwatch.StartNew();
                Stopwatch? failing = null; // desde a 1ª captura falha seguida
                var recheck = Stopwatch.StartNew();
                while (!ct.IsCancellationRequested)
                {
                    // Monitor desconectado ou rearranjado no meio: os mesmos Bounds passariam a pegar OUTRO
                    // monitor (o Windows reposiciona). Confere a cada segundo e encerra se mudou.
                    if (recheck.Elapsed >= TimeSpan.FromSeconds(1))
                    {
                        recheck.Restart();
                        if (!h.Screens().Any(s => s.Id == screen.Id && s.Bounds == screen.Bounds)) break;
                    }
                    var t0 = Stopwatch.GetTimestamp();
                    var jpeg = await Task.Run(() => h.CaptureScreen(screen.Bounds, req.Quality), ct);
                    if (jpeg is null) failing ??= Stopwatch.StartNew(); else failing = null;
                    sessions.VideoFrozen = failing is not null && failing.Elapsed >= RemoteSessions.FrozenAfter;
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
            catch (OperationCanceledException) { /* parar, troca, encerrar ou aparelho fechou */ }
            finally
            {
                sessions.End(session);
                if (sessions.Viewer is null) sessions.VideoFrozen = false;
            }
        });

        // Quem está vendo a tela agora (o MJPEG não avisa o Safari quando outro aparelho assume).
        api.MapGet("/screen/owner", (HttpContext ctx) =>
        {
            var viewer = sessions.Viewer;
            return Results.Ok(new { name = viewer?.Name, mine = viewer?.Hash == BridgeServer.DeviceOf(ctx).Hash,
                frozen = viewer is not null && sessions.VideoFrozen });
        });

        api.MapPost("/screen/close", (HttpContext ctx) => { sessions.StopVideo(BridgeServer.DeviceOf(ctx).Hash); return Results.NoContent(); });

        // Tíquete do WebSocket de controle, preso ao monitor que o aparelho está vendo.
        api.MapPost("/remote/ticket", (RemoteTicketRequest req, HttpContext ctx) =>
            Find(req.Screen) is { } screen
                ? Results.Ok(new { ticket = input.Issue(BridgeServer.DeviceOf(ctx).Hash, screen.Id, DateTime.Now) })
                : Results.NotFound());

        // Teclado remoto (ditado): mesmo canal da tela, mas só texto e teclas — mouse é descartado.
        var keyboard = new OneShotTickets<bool>();

        // Trocar de janela pelo aparelho (exige Tela + Controle, como o resto de /remote).
        api.MapGet("/remote/windows", () => Results.Ok(h.Windows().Select(w => new { id = w.Id, title = w.Title, app = w.App })));
        api.MapPost("/remote/focus", (FocusRequest req) => h.FocusWindow(req.Id) ? Results.NoContent() : Results.NotFound());
        api.MapPost("/keyboard/ticket", (HttpContext ctx) => Results.Ok(new { ticket = keyboard.Issue(BridgeServer.DeviceOf(ctx).Hash, true, DateTime.Now) }));
        api.MapGet("/keyboard/input", async (HttpContext ctx, string? ticket) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            if (!keyboard.TryClaim(ticket, DateTime.Now, out _, out var owner)) { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return; }
            ctx.Items["device"] = tokens.Find(owner); // a auditoria mostra o aparelho do tíquete
            if (!BridgeServer.TicketOwnerAllowed(tokens, owner, ctx.Request.Path)) { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var session = sessions.BeginInput(ctx.RequestAborted, Who(tokens, owner));
            // revogado entre a checagem e a vaga (o await acima): sem vaga a revogação não achou nada pra cortar
            if (!BridgeServer.TicketOwnerAllowed(tokens, owner, ctx.Request.Path)) session.Cancel();
            try { await ReceiveLoop(ws, default, h, session.Token, KeyboardOnly); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            finally { await CloseAsync(ws, sessions, session); }
        });

        api.MapGet("/remote/input", async (HttpContext ctx, string? ticket) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            if (!input.TryClaim(ticket, DateTime.Now, out var screenId, out var owner))
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
            if (Find(screenId) is not { } screen)
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var session = sessions.BeginInput(ctx.RequestAborted, Who(tokens, owner));
            // revogado entre a checagem e a vaga (o await acima): sem vaga a revogação não achou nada pra cortar
            if (!BridgeServer.TicketOwnerAllowed(tokens, owner, ctx.Request.Path)) session.Cancel();
            // Monitor rearranjado/desconectado: os Bounds velhos clicariam em OUTRO monitor (o vídeo já encerra).
            // Vigia à parte, de 1 em 1 s: encerra mesmo sem comando chegando (botão segurado, mensagem pela metade).
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!session.IsCancellationRequested)
                    {
                        await Task.Delay(1000, session.Token);
                        if (!h.Screens().Any(s => s.Id == screen.Id && s.Bounds == screen.Bounds)) { session.Cancel(); return; }
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            });
            try { await ReceiveLoop(ws, screen.Bounds, h, session.Token, _ => true); }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            finally { await CloseAsync(ws, sessions, session); }
        });
    }

    internal static SessionOwner Who(TokenStore tokens, string hash) => new(hash, tokens.Find(hash)?.Name ?? "Aparelho");

    // Fim do WebSocket: se outro aparelho tomou a vaga, avisa com 4001 + nome (ler antes de End).
    internal static async Task CloseAsync(WebSocket ws, RemoteSessions sessions, CancellationTokenSource session)
    {
        var taken = sessions.DisplacedBy(session);
        sessions.End(session);
        if (ws.State != WebSocketState.Open) return;
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2)); // cliente sumido não prende o handler
            if (taken is null) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "fim", limit.Token);
            else await ws.CloseAsync((WebSocketCloseStatus)TakenCloseCode, taken, limit.Token);
        }
        catch { }
    }

    public static bool KeyboardOnly(RemoteCommand c) => c.Kind is RemoteKind.Text or RemoteKind.Key;

    private static async Task ReceiveLoop(WebSocket ws, System.Drawing.Rectangle bounds, BridgeHandlers h, CancellationToken ct,
        Func<RemoteCommand, bool> allow)
    {
        var buf = new byte[8192];
        // Cancelar o ReceiveAsync aborta o socket e o 4001/"fim" nunca sairia: espera sem passar o token.
        async Task<WebSocketReceiveResult> Receive(ArraySegment<byte> b)
        {
            return await ws.ReceiveAsync(b, CancellationToken.None).WaitAsync(ct);
        }
        var window = Stopwatch.StartNew();
        int inWindow = 0;
        var held = new HeldButtons();
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                // A mensagem pode chegar em pedaços: junta até o fim; passou de 8 KiB, descarta inteira.
                int len = 0;
                bool tooBig = false;
                WebSocketReceiveResult res;
                do
                {
                    res = await Receive(tooBig ? new ArraySegment<byte>(buf) : new ArraySegment<byte>(buf, len, buf.Length - len));
                    if (res.MessageType == WebSocketMessageType.Close) return;
                    if (!tooBig) len += res.Count;
                    if (!res.EndOfMessage && len == buf.Length) tooBig = true;
                } while (!res.EndOfMessage);
                if (tooBig) continue;
                if (window.ElapsedMilliseconds >= 1000) { window.Restart(); inWindow = 0; }
                if (++inWindow > MaxMessagesPerSecond) continue;
                ct.ThrowIfCancellationRequested(); // sessão encerrada/assumida: nada de comando atrasado
                if (RemoteInput.Parse(Encoding.UTF8.GetString(buf, 0, len)) is { } cmd && allow(cmd))
                {
                    held.Track(cmd);
                    h.RemoteInput(cmd, bounds);
                }
            }
        }
        finally
        {
            foreach (var up in held.ReleaseAll())
                try { h.RemoteInput(up, bounds); } catch { }
        }
    }
}
