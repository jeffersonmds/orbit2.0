using System;
using System.IO;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Orbit.Services;

namespace Orbit.Bridge;

public enum MediaCommand { PlayPause, Next, Prev }

// Regras puras da ponte (sem rede nem disco) — testadas no harness.
public static class BridgeRules
{
    public const long MaxUploadBytes = 500L * 1024 * 1024;
    public const long MaxBodyBytes = 1 * 1024 * 1024;
    public const int MaxClipboardChars = 100_000;
    private const int MaxFileName = 120;

    private static readonly Regex Spaces = new(@"\s+");
    private static readonly string[] Reserved =
        { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
          "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" };

    // Só http/https: nada de javascript:, file:, ms-settings: etc. vindo do celular.
    public static bool IsOpenableUrl(string? url) =>
        Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    // Aceita "youtube.com" / "www.x.com/a" digitado no iPhone: completa com https://.
    // Devolve a URL normalizada, ou null se não for link http/https.
    public static string? NormalizeOpenUrl(string? url)
    {
        var t = (url ?? "").Trim();
        if (t.Length == 0) return null;
        if (!t.Contains("://", StringComparison.Ordinal) && t.Contains('.') && !t.Any(char.IsWhiteSpace))
            t = "https://" + t;
        return IsOpenableUrl(t) ? new Uri(t).AbsoluteUri : null;
    }

    public static string UploadFolder(string? configured) =>
        string.IsNullOrWhiteSpace(configured)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Orbit")
            : configured;

    public static string Host(string url) => Uri.TryCreate(url.Trim(), UriKind.Absolute, out var u) ? u.Host : "";

    public static string? BearerToken(string? header) =>
        header is not null && header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header[7..].Trim()
            : null;

    // Nome de arquivo vindo do celular: sem pastas (path traversal), sem caracteres inválidos,
    // sem nomes reservados, com tamanho limitado.
    public static string SafeFileName(string? name)
    {
        // Path.GetFileName trata ':' como separador de volume ("a:b" vira "b"); cortamos só em / e \.
        var raw = name ?? "";
        var n = raw[(raw.LastIndexOfAny(new[] { '/', '\\' }) + 1)..];
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        // Caracteres de formatação (bidi U+202E etc.) disfarçam extensões.
        n = string.Concat(n.Select(c => char.GetUnicodeCategory(c) == UnicodeCategory.Format ? '_' : c));
        n = n.Trim().TrimEnd('.', ' ');
        if (n.Length == 0 || n.All(ch => ch == '.')) n = "arquivo";
        if (Reserved.Contains(Path.GetFileNameWithoutExtension(n).ToUpperInvariant())) n = "_" + n;
        if (n.Length > MaxFileName)
        {
            var ext = Path.GetExtension(n);
            if (ext.Length > 20) ext = "";
            n = (ext.Length == 0 ? n[..MaxFileName] : n[..(MaxFileName - ext.Length)]).TrimEnd('.', ' ') + ext;
            n = n.TrimEnd('.', ' ');
            if (n.Length == 0) n = "arquivo";
        }
        return n;
    }

    // Não sobrescreve: "foto.jpg" → "foto (1).jpg" → "foto (2).jpg"…
    public static string UniqueName(string dir, string name, Func<string, bool> exists)
    {
        if (!exists(Path.Combine(dir, name))) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (int i = 1; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (!exists(Path.Combine(dir, candidate))) return candidate;
        }
    }

    public static PowerAction? ParsePower(string action) => action.ToLowerInvariant() switch
    {
        "lock" => PowerAction.Bloquear,
        "sleep" => PowerAction.Suspender,
        "restart" => PowerAction.Reiniciar,
        "shutdown" => PowerAction.Desligar,
        _ => null,
    };

    public static MediaCommand? ParseMedia(string command) => command.ToLowerInvariant() switch
    {
        "playpause" => MediaCommand.PlayPause,
        "next" => MediaCommand.Next,
        "prev" => MediaCommand.Prev,
        _ => null,
    };

    // Leituras que o PWA faz em polling (stats, mídia) não poluem a auditoria.
    public static bool ShouldAudit(string method, string path) =>
        !(string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
          && (path.EndsWith("/stats", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/media", StringComparison.OrdinalIgnoreCase)
              || path.EndsWith("/media/cover", StringComparison.OrdinalIgnoreCase)));

    public static string Preview(string text)
    {
        var s = Spaces.Replace(text, " ").Trim();
        return s.Length > 60 ? s[..60] + "…" : s;
    }

    public static string NormalizeBaseUrl(string? url)
    {
        var s = (url ?? "").Trim().TrimEnd('/');
        if (s.Length == 0) return "";
        return s.Contains("://", StringComparison.Ordinal) ? s : "https://" + s;
    }

    // O token vai no fragmento (#): o navegador não o manda em nenhuma requisição.
    public static string PairingLink(string baseUrl, string token) =>
        NormalizeBaseUrl(baseUrl) + "/#token=" + Uri.EscapeDataString(token);
}
