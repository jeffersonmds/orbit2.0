using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Orbit.Services;

// `Orbit.exe --notify "título" ["detalhe"] [--silent]`: qualquer script manda um alerta pra pílula
// (ex.: `dotnet build; Orbit.exe --notify "Build" "terminou"`). Processo curto → pipe → Orbit aberto.
public static class NotifyCommand
{
    public const string Arg = "--notify";
    public const string PipeName = "Orbit2Notify";
    public const int MaxTitle = 80, MaxDetail = 200;

    public sealed record Message(string Title, string? Detail, bool Sound);

    // null = nada pra avisar (sem título). Flags desconhecidas são ignoradas; textos são cortados.
    public static Message? Parse(IEnumerable<string> args)
    {
        var list = args.ToList();
        var texts = list.Where(a => !a.StartsWith("--", StringComparison.Ordinal))
            .Select(a => a.Trim()).Where(a => a.Length > 0).ToList();
        if (texts.Count == 0) return null;
        return new Message(Cut(texts[0], MaxTitle), texts.Count > 1 ? Cut(string.Join(" ", texts.Skip(1)), MaxDetail) : null,
            !list.Contains("--silent"));
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public static void SendToRunning(Message? msg)
    {
        if (msg is not null) UserPipe.Send(PipeName, JsonSerializer.Serialize(msg));
    }

    // A mensagem passa de novo pelo Parse: o pipe é do usuário, mas o Orbit não confia no tamanho.
    public static void Listen(Action<Message> onMessage, CancellationToken ct) => UserPipe.Listen(PipeName, text =>
    {
        var m = JsonSerializer.Deserialize<Message>(text);
        if (m is null) return;
        var clean = Parse(m.Detail is null ? new[] { m.Title } : new[] { m.Title, m.Detail });
        if (clean is not null) onMessage(clean with { Sound = m.Sound });
    }, ct);
}
