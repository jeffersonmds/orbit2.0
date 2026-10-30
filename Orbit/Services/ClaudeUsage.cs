using System;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace Orbit.Services;

// Cota do plano, como o `claude` oficial informa (ver ClaudeUsageClient).
public sealed record ClaudeWindow(double UsedPct, DateTimeOffset ResetsAt);

public sealed record ClaudeUsage(ClaudeWindow? FiveHour, ClaudeWindow? SevenDay, ClaudeWindow? Opus, ClaudeWindow? Sonnet)
{
    // Uma linha do stdout do CLI; null se não é a resposta do pedido ou o plano não tem cota.
    public static ClaudeUsage? ParseResponse(string line, string requestId)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!IsString(root, "type", "control_response") || !root.TryGetProperty("response", out var outer)
                || !IsString(outer, "request_id", requestId) || !outer.TryGetProperty("response", out var inner)
                || !inner.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
                return null;
            return new ClaudeUsage(Window(limits, "five_hour"), Window(limits, "seven_day"),
                Window(limits, "seven_day_opus"), Window(limits, "seven_day_sonnet"));
        }
        catch (JsonException) { return null; }
    }

    private static bool IsString(JsonElement obj, string name, string value) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var v)
        && v.ValueKind == JsonValueKind.String && v.GetString() == value;

    private static ClaudeWindow? Window(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number) return null;
        if (!w.TryGetProperty("resets_at", out var resets) || resets.ValueKind != JsonValueKind.String) return null;
        return DateTimeOffset.TryParse(resets.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)
            ? new ClaudeWindow(used.GetDouble(), at) : null;
    }
}

public static class ClaudeContext
{
    // Fim do transcript da sessão → tokens de entrada da última resposta, que é o contexto em uso.
    // Sem % de propósito: a janela varia por modelo (200 mil ou 1 milhão) e o transcript não diz qual.
    public static long? LastTokens(string tail)
    {
        foreach (var line in tail.Split('\n').Reverse())
        {
            if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || type.GetString() != "assistant") continue;
                if (root.TryGetProperty("isSidechain", out var side) && side.ValueKind == JsonValueKind.True) continue;
                if (!root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object
                    || !msg.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) continue;
                return Tokens(usage, "input_tokens") + Tokens(usage, "cache_creation_input_tokens") + Tokens(usage, "cache_read_input_tokens");
            }
            catch (JsonException) { } // a 1ª linha do trecho vem cortada; registro corrompido idem
        }
        return null;
    }

    private static long Tokens(JsonElement usage, string name) =>
        usage.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}

public sealed record ClaudeUsageRow(string Label, int Percent, double Fraction, string Detail);

public static class ClaudeUsageMath
{
    public static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    public static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    private const int PaceTolerance = 5; // pontos
    private const double MinElapsed = 0.05; // no começo da janela o ritmo é só ruído
    private const double AlertPct = 97;

    // Janela vencida sem dado novo: o limite já zerou, mesmo que ainda não tenha buscado de novo.
    public static double UsedNow(ClaudeWindow w, DateTimeOffset now) => w.ResetsAt <= now ? 0 : w.UsedPct;

    public static ClaudeUsageRow Row(string label, ClaudeWindow w, TimeSpan length, DateTimeOffset now)
    {
        double used = UsedNow(w, now);
        return new ClaudeUsageRow(label, (int)Math.Round(used), Math.Clamp(used / 100, 0, 1), Describe(w, length, now));
    }

    public static string Describe(ClaudeWindow w, TimeSpan length, DateTimeOffset now)
    {
        var left = w.ResetsAt - now;
        if (left <= TimeSpan.Zero) return "janela reiniciada";
        string reset = $"reseta em {Span(left)}";
        if (w.UsedPct >= 100) return $"no limite · {reset}";

        var elapsed = length - left;
        if (elapsed.TotalSeconds < length.TotalSeconds * MinElapsed) return reset;
        int delta = (int)Math.Round(w.UsedPct - 100 * elapsed.TotalSeconds / length.TotalSeconds);
        if (Math.Abs(delta) <= PaceTolerance) return $"{reset} · no ritmo";
        if (delta < 0) return $"{reset} · {-delta} pts abaixo do ritmo";

        // Mantido o ritmo atual, o limite chega antes do reset?
        var toLimit = TimeSpan.FromSeconds(elapsed.TotalSeconds * (100 - w.UsedPct) / w.UsedPct);
        return toLimit < left ? $"{reset} · limite em {Span(toLimit)}" : $"{reset} · {delta} pts acima do ritmo";
    }

    public static string Span(TimeSpan t)
    {
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes:00}m";
        return $"{Math.Max(1, (int)t.TotalMinutes)}m";
    }

    // Um aviso por cruzamento: 0 = abaixo, 1 = perto, 2 = no limite. Baixar de nível rearma o aviso.
    public static (int Level, bool Notify) Crossing(int level, double usedPct)
    {
        int now = usedPct >= 100 ? 2 : usedPct >= AlertPct ? 1 : 0;
        return (now, now > level);
    }
}
