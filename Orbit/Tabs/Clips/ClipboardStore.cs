using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orbit.Services;

namespace Orbit.Tabs.Clips;

// clipboard.json + pasta clipboard-img\. Mesma filosofia do SettingsStore: nunca lança.
// O JSON é cifrado com DPAPI (conta do Windows): cópia do arquivo (backup, sync, outro usuário, disco
// fora da máquina) não abre. Não protege de programa rodando como você — esse lê o clipboard direto.
// ponytail: os PNGs de clipboard-img\ continuam abertos (miniaturas leem do disco); cifrar se pesar.
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
            var dtos = JsonSerializer.Deserialize<List<ClipDto>>(Decode(File.ReadAllBytes(path)));
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
            File.WriteAllBytes(tmp, Encode(JsonSerializer.Serialize(dtos, WriteOptions)));
            File.Move(tmp, path, overwrite: true); // atômico: crash no meio não corrompe o histórico
        }
        catch
        {
            // disco cheio / sem permissão: não vale derrubar a ilha.
        }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Orbit2.clipboard");

    public static byte[] Encode(string json) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(json), Entropy, DataProtectionScope.CurrentUser);

    // Histórico de antes da cifra (JSON puro, começa com '[') ainda abre; o próximo Save já grava cifrado.
    public static string Decode(byte[] bytes)
    {
        // O blob do DPAPI começa com 01 00 00 00: nunca é confundido com JSON.
        var plain = Encoding.UTF8.GetString(bytes);
        if (plain.TrimStart('﻿', ' ', '\r', '\n', '\t').StartsWith('[')) return plain;
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, Entropy, DataProtectionScope.CurrentUser));
    }
}
