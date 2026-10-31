using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using Serilog;

namespace Orbit.Bridge;

// Item mandado do PC pro aparelho. Arquivo: File = nome na pasta da caixa de saída. Texto: Text.
// Recipients = hashes dos aparelhos que ainda veem o item; null (outbox.json antigo) = todos.
public sealed record OutboxItem(string Id, string Name, long Size, string? File, string? Text, DateTime CreatedAt, IReadOnlyList<string>? Recipients = null);

// "Recebidos do PC" no aparelho: o que o usuário mandou do PC (Enviar para, arrastar pra ilha, aba
// Clipboard). Arquivos são copiados pra pasta própria (mover/apagar o original não quebra o envio) e
// tudo vence em 24 h. Só entra o que o dono do PC escolheu — o aparelho não pede arquivo nenhum aqui.
public sealed class Outbox
{
    public static readonly string DefaultDir = Path.Combine(Orbit.Services.SettingsStore.Dir, "outbox");
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    public const long MaxFileBytes = 2L * 1024 * 1024 * 1024; // 2 GB por arquivo
    public const int MaxTextChars = 100_000;

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string _dir;
    private readonly string _index;
    private readonly object _gate = new();
    private readonly List<OutboxItem> _items;

    public Outbox(string dir)
    {
        _dir = dir;
        _index = Path.Combine(dir, "outbox.json");
        _items = Load();
    }

    private static bool Sees(OutboxItem i, string device) => i.Recipients is null || i.Recipients.Contains(device);

    public IReadOnlyList<OutboxItem> List(DateTime now, string device)
    {
        lock (_gate)
        {
            Expire(now);
            return _items.Where(i => Sees(i, device)).OrderByDescending(i => i.CreatedAt).ToList();
        }
    }

    // Caminho do arquivo de um item ainda válido e visível a esse aparelho, ou null.
    public string? PathOf(string id, DateTime now, string device)
    {
        lock (_gate)
        {
            Expire(now);
            return _items.FirstOrDefault(i => i.Id == id && Sees(i, device))?.File is { } f ? Path.Combine(_dir, f) : null;
        }
    }

    // Copia um arquivo do PC pra caixa de saída. null = não dá (pasta, sumiu, grande demais).
    public OutboxItem? AddFile(string path, DateTime now, IReadOnlyList<string> recipients, string? displayName = null)
    {
        if (recipients.Count == 0) return null;
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxFileBytes) return null;
        var id = NewId();
        var name = BridgeRules.SafeFileName(displayName ?? info.Name);
        var stored = id + Path.GetExtension(name);
        Directory.CreateDirectory(_dir);
        System.IO.File.Copy(path, Path.Combine(_dir, stored));
        return Add(new OutboxItem(id, name, info.Length, stored, null, now, recipients.ToList()), now);
    }

    public OutboxItem? AddText(string text, DateTime now, IReadOnlyList<string> recipients)
    {
        if (recipients.Count == 0 || string.IsNullOrWhiteSpace(text) || text.Length > MaxTextChars) return null;
        return Add(new OutboxItem(NewId(), BridgeRules.Preview(text), text.Length, null, text, now, recipients.ToList()), now);
    }

    // Apagar é só do aparelho: o item some pra ele; o arquivo (compartilhado) só quando ninguém mais vê.
    // Item antigo (sem destinatários) é de todos: apagar por um apaga de vez. false = não é desse aparelho.
    public bool Remove(string id, string device)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(i => i.Id == id && Sees(i, device));
            if (item is null) return false;
            Drop(item, device);
            Save();
            return true;
        }
    }

    // Aparelho revogado: sai de todos os destinatários; item que fica sem ninguém some com o arquivo.
    public void RemoveDevice(string device)
    {
        lock (_gate)
        {
            var mine = _items.Where(i => i.Recipients is not null && i.Recipients.Contains(device)).ToList();
            if (mine.Count == 0) return;
            foreach (var i in mine) Drop(i, device);
            Save();
        }
    }

    private void Drop(OutboxItem item, string device)
    {
        var left = item.Recipients?.Where(r => r != device).ToList();
        if (left is { Count: > 0 }) { _items[_items.IndexOf(item)] = item with { Recipients = left }; return; }
        _items.Remove(item);
        DeleteFile(item);
    }

    private OutboxItem Add(OutboxItem item, DateTime now)
    {
        lock (_gate)
        {
            Expire(now);
            _items.Add(item);
            Save();
        }
        return item;
    }

    private void Expire(DateTime now)
    {
        var old = _items.Where(i => now - i.CreatedAt > Lifetime).ToList();
        if (old.Count == 0) return;
        foreach (var i in old) { _items.Remove(i); DeleteFile(i); }
        Save();
    }

    private static string NewId() => Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    private void DeleteFile(OutboxItem i)
    {
        if (i.File is null) return;
        try { System.IO.File.Delete(Path.Combine(_dir, i.File)); } catch (Exception ex) { Log.Warning(ex, "Caixa de saída: não apagou {Arquivo}", i.File); }
    }

    private List<OutboxItem> Load()
    {
        try { return System.IO.File.Exists(_index) ? JsonSerializer.Deserialize<List<OutboxItem>>(System.IO.File.ReadAllText(_index)) ?? new() : new(); }
        catch { return new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(_dir);
            System.IO.File.WriteAllText(_index, JsonSerializer.Serialize(_items, WriteOptions));
        }
        catch (Exception ex) { Log.Warning(ex, "Caixa de saída: não salvou o índice"); }
    }
}
