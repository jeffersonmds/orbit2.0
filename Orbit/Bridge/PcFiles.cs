using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace Orbit.Bridge;

public sealed record PcRoot(string Id, string Name, string Path);
public sealed record PcEntry(string Name, bool Dir, long Size, DateTime Modified);

// Arquivos do PC pelo iPhone: só leitura e só dentro das raízes (pastas do usuário).
// Todo caminho vem como raiz + relativo e é validado aqui — nunca um caminho absoluto do celular.
public static class PcFiles
{
    public const int MaxEntries = 2000;

    // ponytail: Downloads = %UserProfile%\Downloads; SHGetKnownFolderPath se alguém mover a pasta.
    public static List<PcRoot> DefaultRoots(string uploadFolder)
    {
        string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var roots = new List<PcRoot>
        {
            new("desktop", "Área de Trabalho", F(Environment.SpecialFolder.DesktopDirectory)),
            new("downloads", "Downloads", Path.Combine(F(Environment.SpecialFolder.UserProfile), "Downloads")),
            new("documentos", "Documentos", F(Environment.SpecialFolder.MyDocuments)),
            new("imagens", "Imagens", F(Environment.SpecialFolder.MyPictures)),
            new("videos", "Vídeos", F(Environment.SpecialFolder.MyVideos)),
            new("musicas", "Músicas", F(Environment.SpecialFolder.MyMusic)),
            new("orbit", "Recebidos do iPhone", uploadFolder),
        };
        return roots.Where(r => r.Path.Length > 0 && Directory.Exists(r.Path)).ToList();
    }

    // Caminho completo dentro da raiz, ou null. Recusa: raiz desconhecida, ':' (unidade/fluxo
    // alternativo), qualquer coisa que normalize pra fora da raiz, e — abaixo da raiz — atalhos
    // simbólicos/junções (escapariam dela) e itens ocultos/de sistema.
    public static string? Resolve(IReadOnlyList<PcRoot> roots, string? rootId, string? relative)
    {
        var root = roots.FirstOrDefault(r => r.Id == rootId);
        if (root is null) return null;
        var rel = (relative ?? "").Replace('/', '\\').Trim('\\');
        if (rel.Contains(':')) return null;
        var rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.Path)) + "\\";
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.Combine(rootFull, rel)));
        if (!(full + "\\").StartsWith(rootFull, StringComparison.OrdinalIgnoreCase)) return null;
        for (var cur = full; cur.Length >= rootFull.Length; cur = Path.GetDirectoryName(cur)!)
        {
            FileSystemInfo fi = Directory.Exists(cur) ? new DirectoryInfo(cur) : new FileInfo(cur);
            if (fi.Exists && (Hidden(fi) || IsLink(fi))) return null;
        }
        return full;
    }

    // Pastas primeiro (por nome), arquivos do mais novo pro mais antigo (o que acabou de baixar no topo).
    public static List<PcEntry> List(string dir) =>
        new DirectoryInfo(dir).EnumerateFileSystemInfos()
            .Where(fi => !Hidden(fi) && !IsLink(fi))
            .Take(MaxEntries)
            .Select(fi => new PcEntry(fi.Name, fi is DirectoryInfo, fi is FileInfo f ? f.Length : 0, fi.LastWriteTime))
            .OrderByDescending(e => e.Dir)
            .ThenBy(e => e.Dir ? e.Name : "", StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(e => e.Modified)
            .ToList();

    private static bool Hidden(FileSystemInfo fi) => (fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    // Só links de verdade (simbólico/junção). Arquivo do OneDrive também é reparse point, mas sem
    // LinkTarget — esses continuam visíveis.
    private static bool IsLink(FileSystemInfo fi) =>
        (fi.Attributes & FileAttributes.ReparsePoint) != 0 && fi.LinkTarget is not null;
}

// Tíquete de download: a navegação do iPhone (window.open) não manda o cabeçalho com o token.
// Vale 10 min pra um arquivo só e aguenta várias requisições (o Safari pede por faixas).
// ponytail: em memória, limpa vencidos ao emitir; teto de 50 ativos.
public sealed class FileTickets
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int Max = 50;

    private readonly object _gate = new();
    private readonly Dictionary<string, (string Path, DateTime Expires)> _tickets = new();

    public string Issue(string path, DateTime now)
    {
        var tk = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            foreach (var k in _tickets.Where(t => t.Value.Expires < now).Select(t => t.Key).ToList()) _tickets.Remove(k);
            if (_tickets.Count >= Max) _tickets.Remove(_tickets.MinBy(t => t.Value.Expires).Key);
            _tickets[tk] = (path, now + Lifetime);
        }
        return tk;
    }

    public string? Path(string? ticket, DateTime now)
    {
        if (ticket is null) return null;
        lock (_gate)
            return _tickets.TryGetValue(ticket, out var t) && t.Expires >= now ? t.Path : null;
    }
}
