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

// Kestrel dentro do processo do Orbit, só em 127.0.0.1 (o `tailscale serve` é quem expõe).
// Toda rota /api exige token e passa por rate limit; /health e o PWA (arquivos estáticos) não.
public sealed class BridgeServer : IAsyncDisposable
{
    private WebApplication? _app;

    public int Port { get; private set; }
    private long _lastUnauthorizedAuditTicks = long.MinValue / 2;

    public bool Running => _app is not null;

    // port 0 = porta efêmera (testes).
    // codes: pareamento por código de 6 dígitos (POST /pair, sem token). null = rota desligada.
    public async Task StartAsync(int port, string webRoot, TokenStore tokens, AuditLog audit, BridgeHandlers h,
        PairingCodes? codes = null)
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
            if (ctx.Request.Path.StartsWithSegments("/api"))
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
        MapApi(api, h);

        try { await app.StartAsync(); }
        catch { await app.DisposeAsync(); throw; }
        _app = app;
        Port = ActualPort(app) ?? port;
        Log.Information("Ponte ouvindo em 127.0.0.1:{Porta}", Port);
    }

    private static void MapApi(RouteGroupBuilder api, BridgeHandlers h)
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

        api.MapPost("/power/{action}", async (string action) =>
        {
            if (BridgeRules.ParsePower(action) is not { } a) return Results.NotFound();
            await h.Power(a);
            return Results.NoContent();
        });

        api.MapGet("/media", async () => Results.Ok(await h.GetMedia()));
        api.MapGet("/media/cover", async () => await h.GetCover() is { } png ? Results.File(png, "image/png") : Results.NoContent());
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
