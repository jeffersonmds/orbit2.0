using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Orbit.Tabs.Apps;

// Id = nome de parsing do shell:AppsFolder (AUMID de app da Store, ou caminho/GUID de desktop).
public sealed record AppEntry(string Name, string Id);

// Regras puras da aba Apps — testadas no harness.
public static class AppSearch
{
    public const int MaxFavorites = 12;

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // 3 = nome começa com a busca; 2 = alguma palavra começa; 1 = contém; 0 = não casa.
    public static List<AppEntry> Search(IEnumerable<AppEntry> apps, string query, int max)
    {
        var q = Normalize(query.Trim());
        if (q.Length == 0) return new();
        return apps
            .Select(a => (App: a, Score: Score(Normalize(a.Name), q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.App)
            .ToList();
    }

    private static int Score(string name, string q) =>
        name.StartsWith(q, StringComparison.Ordinal) ? 3
        : name.Split(' ', '-', '.', '(').Any(w => w.StartsWith(q, StringComparison.Ordinal)) ? 2
        : name.Contains(q, StringComparison.Ordinal) ? 1
        : 0;

    public static List<string> ToggleFavorite(IReadOnlyList<string> favorites, string id) =>
        favorites.Contains(id) ? favorites.Where(f => f != id).ToList()
        : favorites.Count >= MaxFavorites ? favorites.ToList()
        : favorites.Append(id).ToList();

    // Desinstaladores e afins poluem a busca.
    public static bool IsNoise(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("desinstalar", StringComparison.OrdinalIgnoreCase);

    public static string IconFileName(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16] + ".png";
}
