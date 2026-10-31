using System;
using System.IO;
using System.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Orbit.Bridge;

public sealed record OutboxTicketRequest(string? Id);

// Troca de arquivos aparelho ↔ PC além do "Enviar": foto direto pro clipboard do PC e a caixa de
// saída "Recebidos do PC" (download por tíquete — o compartilhar do iOS busca o arquivo sem cabeçalho).
public static class OutboxRoutes
{
    public const long MaxPhotoBytes = 40L * 1024 * 1024;

    public static void Map(RouteGroupBuilder api, BridgeHandlers h, TokenStore tokens)
    {
        api.MapPost("/clipboard/image", async (HttpRequest request) =>
        {
            if (!request.HasFormContentType) return Results.BadRequest(new { erro = "Envie multipart/form-data." });
            request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = MaxPhotoBytes;
            var form = await request.ReadFormAsync();
            if (form.Files.Count != 1 || form.Files[0].Length is 0 or > MaxPhotoBytes)
                return Results.BadRequest(new { erro = "Mande uma foto (até 40 MB)." });
            using var ms = new MemoryStream();
            await form.Files[0].CopyToAsync(ms);
            if (!await h.SetClipboardImage(ms.ToArray())) return Results.BadRequest(new { erro = "Não parece uma imagem, ou o clipboard do PC está ocupado." });
            h.Notify(new BridgeEvent("Foto no clipboard", "É só colar (Ctrl+V)", Device: BridgeServer.DeviceOf(request.HttpContext).Name));
            return Results.NoContent();
        });

        if (h.Outbox is not { } box) return;
        var tickets = new FileTickets();

        api.MapGet("/outbox", (HttpContext ctx) => Results.Ok(box.List(DateTime.Now, BridgeServer.DeviceOf(ctx).Hash).Select(i => new
        {
            id = i.Id, name = i.Name, size = i.Size, created = i.CreatedAt, text = i.Text, file = i.File is not null,
        })));

        api.MapPost("/outbox/ticket", (OutboxTicketRequest req, HttpContext ctx) =>
            box.PathOf(req.Id ?? "", DateTime.Now, BridgeServer.DeviceOf(ctx).Hash) is { } path && File.Exists(path)
                ? Results.Ok(new { ticket = tickets.Issue(BridgeServer.DeviceOf(ctx).Hash, path, DateTime.Now) })
                : Results.NotFound());

        api.MapGet("/outbox/download", (HttpContext ctx, string? ticket) =>
        {
            if (tickets.Find(ticket, DateTime.Now) is not { } found) return Results.StatusCode(StatusCodes.Status401Unauthorized);
            if (!BridgeServer.TicketOwnerAllowed(tokens, found.Owner, ctx.Request.Path)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            var path = found.Path;
            // nome bonito do item (o arquivo guardado é id.ext); dono que já não é destinatário = 404
            var id = Path.GetFileNameWithoutExtension(path);
            if (box.List(DateTime.Now, found.Owner).FirstOrDefault(i => i.Id == id) is not { } item || !File.Exists(path)) return Results.NotFound();
            var name = item.Name;
            new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider().TryGetContentType(name, out var type);
            return Results.File(path, type ?? "application/octet-stream", name, enableRangeProcessing: true);
        });

        api.MapDelete("/outbox/{id}", (string id, HttpContext ctx) => box.Remove(id, BridgeServer.DeviceOf(ctx).Hash) ? Results.NoContent() : Results.NotFound());
    }
}
