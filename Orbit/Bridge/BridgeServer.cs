using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
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

// Kestrel dentro do processo do Orbit, só em 127.0.0.1 (o `tailscale serve` é quem expõe).
// Toda rota /api exige token e passa por rate limit; /health e o PWA (arquivos estáticos) não.
public sealed class BridgeServer : IAsyncDisposable
{
    private WebApplication? _app;

    public int Port { get; private set; }
    public bool Running => _app is not null;

    // port 0 = porta efêmera (testes).
    public async Task StartAsync(int port, string webRoot, TokenStore tokens, AuditLog audit, BridgeHandlers h)
    {
        if (_app is not null) return;
        bool hasWeb = Directory.Exists(webRoot);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            WebRootPath = hasWeb ? webRoot : null,
        });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.Listen(IPAddress.Loopback, port); // nunca 0.0.0.0 (RNF01)
            k.Limits.MaxRequestBodySize = BridgeRules.MaxUploadBytes;
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
        });

        var app = builder.Build();
        app.UseRateLimiter();
        if (hasWeb)
        {
            app.UseDefaultFiles();
            app.UseStaticFiles();
        }
        app.MapGet("/health", () => Results.Ok(new { ok = true }));

        var api = app.MapGroup("/api").RequireRateLimiting("api");
        api.AddEndpointFilter(async (ctx, next) =>
        {
            var req = ctx.HttpContext.Request;
            var action = $"{req.Method} {req.Path}";
            var name = tokens.Verify(BridgeRules.BearerToken(req.Headers.Authorization.ToString()));
            if (name is null)
            {
                audit.Write("?", action, "401");
                return Results.Unauthorized();
            }
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

        await app.StartAsync();
        _app = app;
        Port = ActualPort(app) ?? port;
        Log.Information("Ponte ouvindo em 127.0.0.1:{Porta}", Port);
    }

    private static void MapApi(RouteGroupBuilder api, BridgeHandlers h)
    {
        api.MapGet("/stats", () => h.GetStats());

        api.MapPost("/links/open", async (LinkRequest req) =>
        {
            if (!BridgeRules.IsOpenableUrl(req.Url)) return Results.BadRequest(new { erro = "Só links http/https." });
            var url = req.Url!.Trim();
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
            var form = await request.ReadFormAsync();
            if (form.Files.Count == 0) return Results.BadRequest(new { erro = "Nenhum arquivo." });
            var dir = h.UploadFolder();
            Directory.CreateDirectory(dir);
            var saved = new List<string>();
            foreach (var file in form.Files)
            {
                var name = BridgeRules.UniqueName(dir, BridgeRules.SafeFileName(file.FileName), File.Exists);
                await using (var target = File.Create(Path.Combine(dir, name)))
                    await file.CopyToAsync(target);
                saved.Add(name);
            }
            h.Notify(new BridgeEvent(saved.Count == 1 ? "Arquivo do iPhone" : $"{saved.Count} arquivos do iPhone",
                string.Join(", ", saved)));
            return Results.Ok(new { saved });
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
