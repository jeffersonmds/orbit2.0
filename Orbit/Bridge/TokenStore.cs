using System;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Orbit.Services;

namespace Orbit.Bridge;

public enum Capability { Camera, PcFiles, Screen, Control, Mic, Keyboard }

public sealed record DevicePermissions(bool Camera, bool PcFiles, bool Screen, bool Control, bool Mic, bool Keyboard)
{
    public static readonly DevicePermissions None = new(false, false, false, false, false, false);

    public bool Has(Capability c) => c switch
    {
        Capability.Camera => Camera,
        Capability.PcFiles => PcFiles,
        Capability.Screen => Screen,
        Capability.Control => Control,
        Capability.Mic => Mic,
        Capability.Keyboard => Keyboard,
        _ => false,
    };
}

// Permissions nulo = tokens.json antigo, ainda não semeado (nega tudo até o SeedPermissions).
public sealed record TokenInfo(string Name, string Hash, DateTime CreatedAt, DevicePermissions? Permissions = null);

// Tokens da ponte. O arquivo guarda só o SHA-256 (o token em si aparece uma vez, no pareamento).
// Verify roda em threads do Kestrel; Create/Revoke na UI: tudo sob lock.
public sealed class TokenStore
{
    public static readonly string FilePath = Path.Combine(SettingsStore.Dir, "tokens.json");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<TokenInfo> _tokens;
    private readonly Dictionary<string, DateTime> _lastSeen = new(); // só em memória
    public const int MaxNameLength = 24;

    public TokenStore(string path)
    {
        _path = path;
        _tokens = Load(path);
    }

    public IReadOnlyList<TokenInfo> List()
    {
        lock (_gate) return _tokens.ToList();
    }

    // Devolve o token em texto (mostrar uma vez só).
    public string Create(string name)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            _tokens.Add(new TokenInfo(name, Hash(token), DateTime.Now, DevicePermissions.None));
            Save();
        }
        return token;
    }

    // Dispositivo do token, ou null. Comparação em tempo constante.
    public TokenInfo? Verify(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var candidate = Convert.FromHexString(Hash(token));
        lock (_gate)
        {
            foreach (var t in _tokens)
                if (CryptographicOperations.FixedTimeEquals(candidate, Convert.FromHexString(t.Hash)))
                {
                    _lastSeen[t.Hash] = DateTime.Now;
                    return t;
                }
        }
        return null;
    }

    public TokenInfo? Find(string hash)
    {
        lock (_gate) return _tokens.FirstOrDefault(t => t.Hash == hash);
    }

    public bool Allows(string hash, Capability c) => Find(hash)?.Permissions?.Has(c) == true;

    public DateTime? LastSeen(string hash)
    {
        lock (_gate) return _lastSeen.TryGetValue(hash, out var d) ? d : null;
    }

    // Nome aparado e cortado; vazio é recusado.
    public bool Rename(string hash, string name)
    {
        name = (name ?? "").Trim();
        if (name.Length > MaxNameLength) name = name[..MaxNameLength].TrimEnd();
        if (name.Length == 0) return false;
        return Update(hash, t => t with { Name = name });
    }

    public void SetPermissions(string hash, DevicePermissions p) => Update(hash, t => t with { Permissions = p });

    // Tokens antigos (sem Permissions) herdam as permissões globais de antes; os demais ficam como estão.
    public void SeedPermissions(DevicePermissions legacy)
    {
        lock (_gate)
        {
            var before = _tokens.ToList();
            for (var i = 0; i < _tokens.Count; i++)
                if (_tokens[i].Permissions is null) _tokens[i] = _tokens[i] with { Permissions = legacy };
            if (before.SequenceEqual(_tokens)) return;
            try { Save(rethrow: true); }
            catch
            {
                _tokens.Clear();
                _tokens.AddRange(before);
                throw;
            }
        }
    }

    private bool Update(string hash, Func<TokenInfo, TokenInfo> change)
    {
        lock (_gate)
        {
            var i = _tokens.FindIndex(t => t.Hash == hash);
            if (i < 0) return false;
            var old = _tokens[i];
            _tokens[i] = change(old);
            try { Save(rethrow: true); }
            catch
            {
                _tokens[i] = old;
                throw;
            }
            return true;
        }
    }

    public void Revoke(string hash)
    {
        lock (_gate)
        {
            var removed = _tokens.Where(t => t.Hash == hash).ToList();
            _tokens.RemoveAll(t => t.Hash == hash);
            try { Save(rethrow: true); }
            catch
            {
                _tokens.AddRange(removed); // memória e disco continuam iguais; a aba mostra o erro
                throw;
            }
        }
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static List<TokenInfo> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<TokenInfo>>(File.ReadAllText(path))?
                .Where(t => t.Hash is { Length: 64 } && t.Hash.All(char.IsAsciiHexDigit))
                .ToList() ?? new();
        }
        catch
        {
            return new(); // corrompido → nenhum token (o usuário pareia de novo)
        }
    }

    private void Save(bool rethrow = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_tokens, WriteOptions));
            File.Move(tmp, _path, overwrite: true);
        }
        catch
        {
            if (rethrow) throw;
            // sem disco: o token vale até fechar o app
        }
    }
}
