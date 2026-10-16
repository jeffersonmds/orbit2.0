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

public sealed record TokenInfo(string Name, string Hash, DateTime CreatedAt);

// Tokens da ponte. O arquivo guarda só o SHA-256 (o token em si aparece uma vez, no pareamento).
// Verify roda em threads do Kestrel; Create/Revoke na UI: tudo sob lock.
public sealed class TokenStore
{
    public static readonly string FilePath = Path.Combine(SettingsStore.Dir, "tokens.json");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };
    private readonly string _path;
    private readonly object _gate = new();
    private readonly List<TokenInfo> _tokens;

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
            _tokens.Add(new TokenInfo(name, Hash(token), DateTime.Now));
            Save();
        }
        return token;
    }

    // Nome do token, ou null. Comparação em tempo constante.
    public string? Verify(string? token)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var candidate = Convert.FromHexString(Hash(token));
        lock (_gate)
        {
            foreach (var t in _tokens)
                if (CryptographicOperations.FixedTimeEquals(candidate, Convert.FromHexString(t.Hash)))
                    return t.Name;
        }
        return null;
    }

    public void Revoke(string hash)
    {
        lock (_gate)
        {
            _tokens.RemoveAll(t => t.Hash == hash);
            Save();
        }
    }

    public static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static List<TokenInfo> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<List<TokenInfo>>(File.ReadAllText(path))?
                .Where(t => t.Hash is { Length: 64 })
                .ToList() ?? new();
        }
        catch
        {
            return new(); // corrompido → nenhum token (o usuário pareia de novo)
        }
    }

    private void Save()
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
            // sem disco: o token vale até fechar o app
        }
    }
}
