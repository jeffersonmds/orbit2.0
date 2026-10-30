using System;
using System.Text.Json;

namespace Orbit.Services;

// Cota do plano e contexto da sessão, vindos do JSON da status line do Claude Code (sem token nem rede).
// Só chega com sessão ativa: entre uma resposta e outra o valor é o último recebido.
public sealed record ClaudeWindow(double UsedPct, DateTimeOffset ResetsAt);

public sealed record ClaudeUsage(ClaudeWindow? FiveHour, ClaudeWindow? SevenDay, double? ContextPct)
{
    public static ClaudeUsage Parse(JsonElement root)
    {
        ClaudeWindow? five = null, week = null;
        if (root.TryGetProperty("rate_limits", out var limits) && limits.ValueKind == JsonValueKind.Object)
        {
            five = Window(limits, "five_hour");
            week = Window(limits, "seven_day");
        }
        double? context = root.TryGetProperty("context_window", out var ctx) && ctx.ValueKind == JsonValueKind.Object
            ? Number(ctx, "used_percentage") : null;
        return new ClaudeUsage(five, week, context);
    }

    // A status line omite janelas (início de sessão, reset passado): mantém o que já se sabia.
    public ClaudeUsage Merge(ClaudeUsage? older) =>
        new(FiveHour ?? older?.FiveHour, SevenDay ?? older?.SevenDay, ContextPct);

    private static ClaudeWindow? Window(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (Number(w, "used_percentage") is not { } used || Number(w, "resets_at") is not { } resets) return null;
        try { return new ClaudeWindow(used, DateTimeOffset.FromUnixTimeSeconds((long)resets)); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static double? Number(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}

public sealed record ClaudeUsageRow(string Label, int Percent, double Fraction, string Detail);

public static class ClaudeUsageMath
{
    public static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    public static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    private const int PaceTolerance = 5; // pontos
    private const double MinElapsed = 0.05; // no começo da janela o ritmo é só ruído
    private const double AlertPct = 97;

    // Janela vencida sem dado novo: o limite já zerou, mesmo que nenhuma sessão tenha falado ainda.
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
