using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Orbit.Services;

namespace Orbit.Tabs.Clips;

// clipboard.json + pasta clipboard-img\. Mesma filosofia do SettingsStore: nunca lança.
public static class ClipboardStore
{
    public static readonly string FilePath = Path.Combine(SettingsStore.Dir, "clipboard.json");
    public static readonly string ImagesDir = Path.Combine(SettingsStore.Dir, "clipboard-img");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    private sealed record ClipDto(ClipKind Kind, string? Text, string? ImageFile, string? Key, DateTime CopiedAt,
        bool Pinned, int Width, int Height);

    public static List<ClipItem> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var dtos = JsonSerializer.Deserialize<List<ClipDto>>(File.ReadAllText(path));
            return dtos?.Where(d => d.Key is not null)
                .Select(d => new ClipItem(d.Kind, d.Text, d.ImageFile, d.Key!, d.CopiedAt, d.Pinned, d.Width, d.Height))
                .ToList() ?? new();
        }
        catch
        {
            return new(); // corrompido/I/O → histórico vazio
        }
    }

    public static void Save(IEnumerable<ClipItem> items, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dtos = items.Select(i => new ClipDto(i.Kind, i.Text, i.ImageFile, i.Key, i.CopiedAt, i.Pinned, i.Width, i.Height));
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(dtos, WriteOptions));
            File.Move(tmp, path, overwrite: true); // atômico: crash no meio não corrompe o histórico
        }
        catch
        {
            // disco cheio / sem permissão: não vale derrubar a ilha.
        }
    }
}
