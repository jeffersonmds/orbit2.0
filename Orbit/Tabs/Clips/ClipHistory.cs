using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Orbit.Tabs.Clips;

public enum ClipFilter { Tudo, Texto, Imagens, Links }

// Regras puras do histórico (sem I/O nem clipboard) — testadas no harness.
public static class ClipHistory
{
    public const int MaxUnpinned = 200;
    public const int MaxTextChars = 100_000; // texto maior (log/dump colado sem querer) não entra

    private static readonly Regex Spaces = new(@"\s+");

    public static ClipKind ClassifyText(string text)
    {
        var t = text.Trim();
        bool single = t.Length > 0 && !t.Any(char.IsWhiteSpace);
        return single && Uri.TryCreate(t, UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? ClipKind.Link
            : ClipKind.Text;
    }

    // Gerenciadores de senha (e o próprio Windows) marcam conteúdo que não deve ir pra histórico.
    public static bool IsPrivate(IEnumerable<string> formats, int? canIncludeInHistory) =>
        formats.Contains("ExcludeClipboardContentFromMonitorProcessing") || canIncludeInHistory == 0;

    // Insere no topo. Conteúdo repetido (mesma Key) sobe pro topo mantendo o "fixado".
    // Devolve os itens descartados pelo limite (pra apagar os PNGs).
    public static List<ClipItem> Add(List<ClipItem> items, ClipItem item)
    {
        var existing = items.FirstOrDefault(i => i.Key == item.Key);
        if (existing is not null)
        {
            items.Remove(existing);
            existing.CopiedAt = item.CopiedAt;
            items.Insert(0, existing);
            return new List<ClipItem>();
        }
        items.Insert(0, item);
        var removed = items.Where(i => !i.Pinned).Skip(MaxUnpinned).ToList();
        foreach (var old in removed) items.Remove(old);
        return removed;
    }

    // Fixados primeiro, depois do mais recente pro mais antigo.
    public static List<ClipItem> View(IEnumerable<ClipItem> items, string? query, ClipFilter filter)
    {
        var q = query?.Trim() ?? "";
        return items
            .Where(i => filter switch
            {
                ClipFilter.Texto => i.Kind == ClipKind.Text,
                ClipFilter.Imagens => i.Kind == ClipKind.Image,
                ClipFilter.Links => i.Kind == ClipKind.Link,
                _ => true,
            })
            .Where(i => q.Length == 0 || (i.Text?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(i => i.Pinned)
            .ThenByDescending(i => i.CopiedAt)
            .ToList();
    }

    public static string RelativeTime(DateTime now, DateTime at)
    {
        var d = now - at;
        if (d.TotalMinutes < 1) return "agora";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (at.Date == now.Date) return $"{(int)d.TotalHours}h";
        if (at.Date == now.Date.AddDays(-1)) return "ontem";
        return $"{(int)(now.Date - at.Date).TotalDays}d";
    }

    public static string Describe(string text)
    {
        int chars = text.Length;
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int lines = text.Split('\n').Length;
        return $"{chars} {Plural(chars, "caractere", "caracteres")} · {words} {Plural(words, "palavra", "palavras")} · " +
               $"{lines} {Plural(lines, "linha", "linhas")}";
    }

    // Texto numa linha só (lista): espaços colapsados, no máximo 140 caracteres.
    public static string OneLine(string? text)
    {
        var s = Spaces.Replace(text ?? "", " ").Trim();
        return s.Length > 140 ? s[..140] + "…" : s;
    }

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;
}
