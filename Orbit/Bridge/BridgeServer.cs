using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.RateLimiting;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Orbit.Bridge;

public sealed record LinkRequest(string? Url);
public sealed record TextRequest(string? Text);
public sealed record VolumeRequest(double Percent);
public sealed record PairRequest(string? Code);
public sealed record CameraOpenRequest(string? DeviceId, string? Name, string? Quality, string? Fps);
public sealed record CameraPhotoRequest(string? DeviceId, string? Name);
public sealed record PcFileRequest(string? Root, string? Path);
public sealed record PushKeys(string? P256dh, string? Auth);
public sealed record PushSubRequest(string? Endpoint, PushKeys? Keys);

// Kestrel dentro do processo do Orbit, só em 127.0.0.1 (o `tailscale serve` é quem expõe).
// Toda rota /api exige token e passa por rate limit; /health e o PWA (arquivos estáticos) não.
public sealed class BridgeServer : IAsyncDisposable
{
    private WebApplication? _app;

    public int Port { get; private set; }
    private long _lastUnauthorizedAuditTicks = long.MinValue / 2;

    public bool Running => _app is not null;

    private RemoteSessions? _remote;

    // "Encerrar" da aba iPhone: derruba vídeo e controle da tela remota.
    public void StopRemote() => _remote?.StopAll();

    // port 0 = porta efêmera (testes).
    // codes: pareamento por código de 6 dígitos (POST /pair, sem token). null = rota desligada.
    public async Task StartAsync(int port, string webRoot, TokenStore tokens, AuditLog audit, BridgeHandlers h,
        PairingCodes? codes = null, Orbit.Services.Camera? camera = null, StreamTickets? tickets = null)
    {
        if (_app is not null) return;
        bool hasWeb = Directory.Exists(webRoot);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = hasWeb ? webRoot : null,
            EnvironmentName = "Production", // em Development um 400 vazava stack trace
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port); // nunca 0.0.0.0 (RNF01)
            k.Limits.MaxRequestBodySize = BridgeRules.MaxBodyBytes; // /files eleva só a própria requisição
        });
        builder.Services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = BridgeRules.MaxUploadBytes);
        builder.Services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddFixedWindowLimiter("api", w =>
            {
                w.PermitLimit = 120;
                w.Window = TimeSpan.FromMinutes(1);
                w.QueueLimit = 0;
            });
            // Pareamento é a única rota aberta que aceita palpite: cota própria e baixa.
            o.AddFixedWindowLimiter("pair", w =>
            {
                w.PermitLimit = 10;
                w.Window = TimeSpan.FromMinutes(1);
                w.QueueLimit = 0;
            });
        });

        var app = builder.Build();
        // Auth ANTES do rate limit e do bind do body: sem token não lê corpo nem gasta a cota do dono.
        app.Use(async (ctx, next) =>
        {
            // Stream da câmera e download de arquivo do PC são as rotas /api autenticadas por tíquete
            // (<img>/window.open não mandam cabeçalho): o próprio handler valida. As demais exigem token.
            bool ticketRoute = HttpMethods.IsGet(ctx.Request.Method)
                && (ctx.Request.Path.Equals("/api/camera/stream", StringComparison.OrdinalIgnoreCase)
                    || ctx.Request.Path.Equals("/api/pcfiles/download", StringComparison.OrdinalIgnoreCase)
                    || ctx.Request.Path.Equals("/api/screen/stream", StringComparison.OrdinalIgnoreCase)
                    || ctx.Request.Path.Equals("/api/remote/input", StringComparison.OrdinalIgnoreCase));
            if (ctx.Request.Path.StartsWithSegments("/api") && !ticketRoute)
            {
                var name = tokens.Verify(BridgeRules.BearerToken(ctx.Request.Headers.Authorization.ToString()));
                if (name is null)
                {
                    // flood sem token não vira escrita em disco por requisição
                    var now = Environment.TickCount64;
                    var last = Interlocked.Read(ref _lastUnauthorizedAuditTicks);
                    if (now - last >= 1000 && Interlocked.CompareExchange(ref _lastUnauthorizedAuditTicks, now, last) == last)
                        audit.Write("?", $"{ctx.Request.Method} {ctx.Request.Path}", "401");
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                ctx.Items["token"] = name;
            }
            try { await next(); }
            catch (Exception ex) { Log.Warning(ex, "Ponte: erro fora das rotas"); throw; }
        });
        app.UseRateLimiter();
        app.UseWebSockets(); // controle da tela remota
        if (hasWeb)
        {
            app.UseDefaultFiles();
            // revalida sempre (304 barato): PWA atualizado chega no próximo abrir
            app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });
        }
        app.MapGet("/health", () => Results.Ok(new { ok = true }));
        if (codes is not null)
        {
            app.MapPost("/pair", (PairRequest req) =>
            {
                var token = codes.Claim(req.Code, DateTime.Now);
                audit.Write("?", "POST /pair", token is null ? "400" : "200");
                if (token is null) return Results.BadRequest(new { erro = "Código inválido ou expirado." });
                h.Notify(new BridgeEvent("Pareado", "O iPhone recebeu o acesso."));
                return Results.Ok(new { token });
            }).RequireRateLimiting("pair");
        }

        var api = app.MapGroup("/api").RequireRateLimiting("api");
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var req = ctx.HttpContext.Request;
            var action = $"{req.Method} {req.Path}";
            var name = (string)ctx.HttpContext.Items["token"]!;
            try
            {
                var result = await next(ctx);
                if (BridgeRules.ShouldAudit(req.Method, req.Path))
                    audit.Write(name, action, ((result as IStatusCodeHttpResult)?.StatusCode ?? 200).ToString());
                return result;
            }
            catch (Exception ex) when (ex is not BadHttpRequestException)
            {
                Log.Warning(ex, "Ponte: falha em {Acao}", action);
                audit.Write(name, action, "erro");
                return Results.Problem("Falha no PC.", statusCode: StatusCodes.Status500InternalServerError);
            }
        });
        // Câmera é capability à parte: 403 enquanto o usuário não liga "Câmera" na aba iPhone.
        api.AddEndpointFilter(async (ctx, next) =>
        {
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/api/camera") && !h.CameraEnabled())
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/api/pcfiles") && !h.PcFilesEnabled())
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/api/screen") && !h.ScreenEnabled())
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/api/remote") && !(h.ScreenEnabled() && h.ControlEnabled()))
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await next(ctx);
        });
        MapApi(api, h, camera, tickets);
        MapPcFiles(api, h, new FileTickets());
        if (h.Push is { } push) MapPush(api, h, push);
        _remote = new RemoteSessions(h.RemoteChanged);
        RemoteScreen.Map(api, h, _remote);

        try { await app.StartAsync(); }
        catch { await app.DisposeAsync(); throw; }
        _app = app;
        Port = ActualPort(app) ?? port;
        Log.Information("Ponte ouvindo em 127.0.0.1:{Porta}", Port);
    }

    private static void MapApi(RouteGroupBuilder api, BridgeHandlers h, Orbit.Services.Camera? camera, StreamTickets? tickets)
    {
        api.MapGet("/stats", () => h.GetStats());

        api.MapPost("/links/open", async (LinkRequest req) =>
        {
            if (BridgeRules.NormalizeOpenUrl(req.Url) is not { } url) return Results.BadRequest(new { erro = "Isso não parece um link." });
            await h.OpenUrl(url);
            h.Notify(new BridgeEvent("Link aberto no PC", BridgeRules.Host(url)));
            return Results.NoContent();
        });

        api.MapGet("/clipboard", async () => Results.Ok(new { text = await h.GetClipboard() }));
        api.MapPut("/clipboard", async (TextRequest req) =>
        {
            if (req.Text is null || req.Text.Length > BridgeRules.MaxClipboardChars)
                return Results.BadRequest(new { erro = "Texto vazio ou grande demais." });
            await h.SetClipboard(req.Text);
            h.Notify(new BridgeEvent("Texto no clipboard do PC", BridgeRules.Preview(req.Text)));
            return Results.NoContent();
        });

        // ponytail: última gravação vence; editar ao mesmo tempo no PC e no iPhone perde uma das versões.
        api.MapGet("/notes", async () => Results.Ok(new { text = await h.GetNotes() }));
        api.MapPut("/notes", async (TextRequest req) =>
        {
            if (req.Text is null || req.Text.Length > BridgeRules.MaxClipboardChars)
                return Results.BadRequest(new { erro = "Nota grande demais." });
            await h.SetNotes(req.Text);
            return Results.NoContent();
        });

        api.MapPost("/power/{action}", async (string action) =>
        {
            if (BridgeRules.ParsePower(action) is not { } a) return Results.NotFound();
            await h.Power(a);
            return Results.NoContent();
        });

        api.MapGet("/media", async () => Results.Ok(await h.GetMedia()));
        api.MapGet("/media/cover", async () => await h.GetCover() is { } img ? Results.File(img, BridgeRules.ImageType(img)) : Results.NoContent());
        api.MapPost("/media/{command}", async (string command) =>
        {
            if (BridgeRules.ParseMedia(command) is not { } m) return Results.NotFound();
            await h.Media(m);
            return Results.NoContent();
        });
        api.MapPut("/media/volume", async (VolumeRequest req) =>
        {
            await h.SetVolume(Math.Clamp(req.Percent, 0, 100));
            return Results.NoContent();
        });

        api.MapPost("/files", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { erro = "Envie multipart/form-data." });
            request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = BridgeRules.MaxUploadBytes;
            var form = await request.ReadFormAsync();
            if (form.Files.Count == 0) return Results.BadRequest(new { erro = "Nenhum arquivo." });
            if (form.Files.Count > 50) return Results.BadRequest(new { erro = "Máximo de 50 arquivos por envio." });
            var dir = h.UploadFolder();
            Directory.CreateDirectory(dir);
            var saved = new List<string>();
            foreach (var file in form.Files)
            {
                var wanted = BridgeRules.SafeFileName(file.FileName);
                for (int attempt = 0; ; attempt++)
                {
                    var name = BridgeRules.UniqueName(dir, wanted, File.Exists);
                    try
                    {
                        // CreateNew: nunca sobrescreve, mesmo com corrida entre dois envios.
                        await using var target = new FileStream(Path.Combine(dir, name), FileMode.CreateNew);
                        await file.CopyToAsync(target);
                        saved.Add(name);
                        break;
                    }
                    catch (IOException) when (attempt < 5 && File.Exists(Path.Combine(dir, name))) { }
                }
            }
            h.Notify(new BridgeEvent(saved.Count == 1 ? "Arquivo do iPhone" : $"{saved.Count} arquivos do iPhone",
                string.Join(", ", saved), OpenPath: dir));
            return Results.Ok(new { saved, folder = dir });
        });

        if (camera is not null && tickets is not null)
        {
            api.MapGet("/camera/devices", async () =>
                Results.Ok((await Orbit.Services.Camera.DevicesAsync()).Select(d => new { id = d.Id, name = d.Name })));

            api.MapPost("/camera/open", (CameraOpenRequest req) =>
            {
                if (string.IsNullOrWhiteSpace(req.DeviceId)) return Results.BadRequest(new { erro = "Sem câmera." });
                var preset = CameraPreset.Parse(req.Quality, req.Fps);
                var ticket = tickets.Issue(req.DeviceId, preset, DateTime.Now);
                h.Notify(new BridgeEvent("Câmera aberta pelo iPhone", string.IsNullOrWhiteSpace(req.Name) ? "Câmera" : req.Name));
                return Results.Ok(new { ticket });
            });

            api.MapGet("/camera/stream", async (HttpContext ctx, string? ticket) =>
            {
                if (tickets.Claim(ticket, DateTime.Now) is not { } claim)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                System.Threading.CancellationTokenSource session;
                System.Threading.CancellationToken sessionToken;
                try { (session, sessionToken) = await camera.OpenStreamAsync(claim.DeviceId, claim.Preset); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Câmera: não abriu");
                    ctx.Response.StatusCode = StatusCodes.Status409Conflict; // ocupada por outro app, etc.
                    return;
                }
                // Encerra por: conexão caiu (RequestAborted), parar explícito ou troca (sessionToken).
                using var link = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, sessionToken);
                try
                {
                    ctx.Response.ContentType = Mjpeg.ContentType;
                    ctx.Response.Headers.CacheControl = "no-cache, no-store";
                    var body = ctx.Response.BodyWriter;
                    while (!link.Token.IsCancellationRequested)
                    {
                        var jpeg = await camera.NextFrameAsync(link.Token);
                        await body.WriteAsync(Mjpeg.PartHeader(jpeg.Length), link.Token);
                        await body.WriteAsync(jpeg, link.Token);
                        await body.FlushAsync(link.Token);
                    }
                }
                catch (OperationCanceledException) { /* parar, troca ou iPhone fechou */ }
                finally { await camera.EndStream(session); }
            });

            api.MapPost("/camera/close", async () => { await camera.CloseAsync(); return Results.NoContent(); });

            api.MapPost("/camera/photo", async (CameraPhotoRequest req) =>
            {
                if (string.IsNullOrWhiteSpace(req.DeviceId)) return Results.BadRequest(new { erro = "Sem câmera." });
                var jpeg = await camera.PhotoAsync(req.DeviceId);
                h.Notify(new BridgeEvent("Foto capturada pelo iPhone", string.IsNullOrWhiteSpace(req.Name) ? "Câmera" : req.Name));
                return Results.File(jpeg, "image/jpeg");
            });
        }
    }

    private static void MapPcFiles(RouteGroupBuilder api, BridgeHandlers h, FileTickets tickets)
    {
        api.MapGet("/pcfiles/roots", () => Results.Ok(h.PcRoots().Select(r => new { id = r.Id, name = r.Name })));

        api.MapGet("/pcfiles/list", (string? root, string? path) =>
        {
            if (PcFiles.Resolve(h.PcRoots(), root, path) is not { } dir || !Directory.Exists(dir)) return Results.NotFound();
            return Results.Ok(PcFiles.List(dir));
        });

        // Emite o tíquete (com token) pro download que vem em seguida sem cabeçalho.
        api.MapPost("/pcfiles/ticket", (PcFileRequest req) =>
        {
            if (PcFiles.Resolve(h.PcRoots(), req.Root, req.Path) is not { } file || !File.Exists(file)) return Results.NotFound();
            return Results.Ok(new { ticket = tickets.Issue(file, DateTime.Now) });
        });

        api.MapGet("/pcfiles/download", (string? ticket) =>
        {
            if (tickets.Path(ticket, DateTime.Now) is not { } file) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            if (!File.Exists(file)) return Results.NotFound();
            new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider().TryGetContentType(file, out var type);
            return Results.File(file, type ?? "application/octet-stream", Path.GetFileName(file), enableRangeProcessing: true);
        });
    }

    // O iPhone se inscreve com o próprio token: revogar o token corta os avisos dele.
    private static void MapPush(RouteGroupBuilder api, BridgeHandlers h, WebPush push)
    {
        static string Token(HttpContext ctx) => (string)ctx.Items["token"]!;

        api.MapGet("/push/key", () => Results.Ok(new { publicKey = push.PublicKey }));
        api.MapGet("/push/status", (HttpContext ctx) => Results.Ok(new { subscriptions = push.CountFor(Token(ctx)) }));
        api.MapPost("/push/subscribe", (PushSubRequest req, HttpContext ctx) =>
            push.Subscribe(req.Endpoint, req.Keys?.P256dh, req.Keys?.Auth, Token(ctx))
                ? Results.NoContent()
                : Results.BadRequest(new { erro = "Inscrição inválida." }));
        api.MapPost("/push/unsubscribe", (PushSubRequest req) => { push.Unsubscribe(req.Endpoint); return Results.NoContent(); });
        api.MapPost("/push/test", async (HttpContext ctx) =>
        {
            await push.SendAsync("Orbit", "Avisos do PC funcionando.", h.ValidTokens(), onlyToken: Token(ctx));
            return Results.NoContent();
        });
    }

    private static int? ActualPort(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        return address is not null && Uri.TryCreate(address, UriKind.Absolute, out var u) ? u.Port : null;
    }

    public async Task StopAsync()
    {
        if (_app is null) return;
        var app = _app;
        _app = null;
        await app.StopAsync();
        await app.DisposeAsync();
        Log.Information("Ponte desligada");
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
