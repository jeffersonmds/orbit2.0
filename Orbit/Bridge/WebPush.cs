using System;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Serilog;

namespace Orbit.Bridge;

// TokenName: o Hash do dispositivo que fez a inscrição (nome do campo mantido no JSON). Revogado = não recebe mais nada.
public sealed record PushSubscription(string Endpoint, string P256dh, string Auth, string TokenName, DateTime CreatedAt);

// Web Push pros aparelhos inscritos (iOS 16.4+, PWA na Tela de Início): RFC 8291 (payload cifrado,
// aes128gcm) + RFC 8292 (VAPID, ES256), só com a cripto nativa do .NET — sem pacote.
// Chave VAPID e inscrições ficam em %AppData%\Orbit2 (vapid.json, push.json); nunca lança por I/O.
public sealed class WebPush
{
    public static readonly string KeyPath = Path.Combine(Orbit.Services.SettingsStore.Dir, "vapid.json");
    public static readonly string SubsPath = Path.Combine(Orbit.Services.SettingsStore.Dir, "push.json");

    // Só os serviços de push conhecidos: senão um token faria o PC mandar POST pra qualquer endereço (SSRF).
    private static readonly string[] AllowedHosts =
        { "push.apple.com", "fcm.googleapis.com", "push.services.mozilla.com", "notify.windows.com" };

    // Apple exige um "sub" de contato; ninguém escreve pra cá.
    private const string Subject = "mailto:orbit2@example.com";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    private readonly string _subsPath;
    private readonly ECDsa _vapid;
    private readonly object _gate = new();
    private readonly List<PushSubscription> _subs;

    public string PublicKey { get; } // base64url do ponto não comprimido (65 bytes): a applicationServerKey do PWA

    public WebPush(string keyPath, string subsPath)
    {
        _subsPath = subsPath;
        _vapid = LoadOrCreateKey(keyPath);
        PublicKey = Base64Url.EncodeToString(Uncompressed(_vapid.ExportParameters(false).Q));
        _subs = LoadSubs(subsPath);
    }

    public int CountFor(string hash)
    {
        lock (_gate) return _subs.Count(s => s.TokenName == hash);
    }

    // push.json antigo guarda o NOME do token: vira o Hash do token mais recente com esse nome;
    // sem token correspondente, a inscrição sai. Quem já é Hash (64 hex) fica como está.
    public static List<PushSubscription> MigrateTokenRefs(IEnumerable<PushSubscription> subs, IReadOnlyList<TokenInfo> tokens)
    {
        // tokens.json ilegível volta lista vazia: migrar agora apagaria as inscrições por nome pra sempre.
        if (tokens.Count == 0) return subs.ToList();
        var result = new List<PushSubscription>();
        foreach (var s in subs)
        {
            if (s.TokenName is { Length: 64 } && s.TokenName.All(char.IsAsciiHexDigit)) { result.Add(s); continue; }
            var t = tokens.Where(x => x.Name == s.TokenName).OrderByDescending(x => x.CreatedAt).FirstOrDefault();
            if (t is not null) result.Add(s with { TokenName = t.Hash });
        }
        return result;
    }

    public void MigrateTokenRefs(IReadOnlyList<TokenInfo> tokens)
    {
        lock (_gate)
        {
            var migrated = MigrateTokenRefs(_subs, tokens);
            if (migrated.SequenceEqual(_subs)) return;
            _subs.Clear();
            _subs.AddRange(migrated);
            SaveSubs();
        }
    }

    // false = inscrição inválida (endpoint fora da lista, chaves com tamanho errado).
    public bool Subscribe(string? endpoint, string? p256dh, string? auth, string tokenName)
    {
        if (!IsAllowedEndpoint(endpoint) || !TryKeys(p256dh, auth, out _, out _)) return false;
        lock (_gate)
        {
            _subs.RemoveAll(s => s.Endpoint == endpoint);
            _subs.Add(new PushSubscription(endpoint!, p256dh!, auth!, tokenName, DateTime.Now));
            SaveSubs();
        }
        return true;
    }

    public void Unsubscribe(string? endpoint)
    {
        lock (_gate)
            if (_subs.RemoveAll(s => s.Endpoint == endpoint) > 0) SaveSubs();
    }

    // Manda pra todas as inscrições de tokens ainda válidos (onlyTokens: só as desses hashes, pro "Testar").
    // Endpoint que responde 404/410 morreu (app removido, permissão retirada): sai da lista.
    // url: página do PWA que o toque no aviso abre (ex.: "./#recebidos").
    public async Task SendAsync(string title, string body, IReadOnlyCollection<string> validTokens, IReadOnlyCollection<string>? onlyTokens = null, string? url = null)
    {
        List<PushSubscription> targets;
        lock (_gate) targets = _subs.Where(s => validTokens.Contains(s.TokenName) && (onlyTokens is null || onlyTokens.Contains(s.TokenName))).ToList();
        if (targets.Count == 0) return;
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { title, body, url });
        foreach (var sub in targets)
        {
            try
            {
                var status = await SendOneAsync(sub, payload);
                if (status is HttpStatusCode.NotFound or HttpStatusCode.Gone) Unsubscribe(sub.Endpoint);
                else if ((int)status >= 400) Log.Warning("Push: {Host} respondeu {Status}", new Uri(sub.Endpoint).Host, (int)status);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Push: falha ao enviar");
            }
        }
    }

    private async Task<HttpStatusCode> SendOneAsync(PushSubscription sub, byte[] payload)
    {
        TryKeys(sub.P256dh, sub.Auth, out var uaPublic, out var auth);
        var uri = new Uri(sub.Endpoint);
        using var req = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(Encrypt(payload, uaPublic, auth)) };
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        req.Content.Headers.ContentEncoding.Add("aes128gcm");
        req.Headers.TryAddWithoutValidation("TTL", "86400"); // aviso vale 1 dia se o aparelho estiver offline
        req.Headers.TryAddWithoutValidation("Urgency", "high");
        req.Headers.TryAddWithoutValidation("Authorization",
            $"vapid t={VapidJwt(_vapid, uri.GetLeftPart(UriPartial.Authority), DateTime.UtcNow)}, k={PublicKey}");
        using var resp = await Http.SendAsync(req);
        return resp.StatusCode;
    }

    // --- Regras e cripto puras (testadas no harness) ---

    public static bool IsAllowedEndpoint(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort
        && AllowedHosts.Any(h => u.Host.Equals(h, StringComparison.OrdinalIgnoreCase)
                                 || u.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    // p256dh = ponto P-256 não comprimido (65 bytes, começa com 0x04); auth = 16 bytes.
    public static bool TryKeys(string? p256dh, string? auth, out byte[] uaPublic, out byte[] authSecret)
    {
        uaPublic = authSecret = Array.Empty<byte>();
        try
        {
            uaPublic = Base64Url.DecodeFromChars(p256dh ?? "");
            authSecret = Base64Url.DecodeFromChars(auth ?? "");
        }
        catch (FormatException) { return false; }
        return uaPublic.Length == 65 && uaPublic[0] == 0x04 && authSecret.Length == 16;
    }

    // RFC 8291 §3.4 + RFC 8188: um único registro aes128gcm. ephemeral/salt só pra teste.
    public static byte[] Encrypt(byte[] plaintext, byte[] uaPublic, byte[] authSecret, ECDiffieHellman? ephemeral = null, byte[]? salt = null)
    {
        using var own = ephemeral is null ? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256) : null;
        var asKey = ephemeral ?? own!;
        salt ??= RandomNumberGenerator.GetBytes(16);
        var asPublic = Uncompressed(asKey.ExportParameters(false).Q);
        using var ua = ECDiffieHellman.Create(PublicParams(uaPublic));
        var ecdhSecret = asKey.DeriveRawSecretAgreement(ua.PublicKey);

        byte[] keyInfo = [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic];
        var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdhSecret, 32, authSecret, keyInfo);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
        var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());

        byte[] padded = [.. plaintext, 0x02]; // delimitador do último registro, sem padding
        var cipher = new byte[padded.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(cek, 16)) gcm.Encrypt(nonce, padded, cipher, tag);

        var rs = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(rs, 4096);
        return [.. salt, .. rs, (byte)asPublic.Length, .. asPublic, .. cipher, .. tag];
    }

    public static string VapidJwt(ECDsa key, string audience, DateTime nowUtc)
    {
        var header = Base64Url.EncodeToString("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"u8);
        var exp = new DateTimeOffset(nowUtc, TimeSpan.Zero).AddHours(12).ToUnixTimeSeconds();
        var claims = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(new { aud = audience, exp, sub = Subject }));
        var sig = key.SignData(Encoding.ASCII.GetBytes($"{header}.{claims}"), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"{header}.{claims}.{Base64Url.EncodeToString(sig)}";
    }

    public static byte[] Uncompressed(ECPoint q) => [0x04, .. q.X!, .. q.Y!];

    public static ECParameters PublicParams(byte[] uncompressed) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..65] },
    };

    // --- Disco ---

    private sealed record KeyDto(string Pkcs8);

    // ponytail: chave privada VAPID em texto no %AppData% (como o resto do Orbit); DPAPI se precisar.
    private static ECDsa LoadOrCreateKey(string path)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            if (File.Exists(path) && JsonSerializer.Deserialize<KeyDto>(File.ReadAllText(path)) is { } dto)
            {
                key.ImportPkcs8PrivateKey(Convert.FromBase64String(dto.Pkcs8), out _);
                return key;
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Push: chave VAPID ilegível, gerando outra"); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new KeyDto(Convert.ToBase64String(key.ExportPkcs8PrivateKey()))));
        }
        catch (Exception ex) { Log.Warning(ex, "Push: não salvou a chave VAPID"); }
        return key;
    }

    private static List<PushSubscription> LoadSubs(string path)
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<List<PushSubscription>>(File.ReadAllText(path)) ?? new() : new(); }
        catch { return new(); }
    }

    private void SaveSubs()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_subsPath)!);
            File.WriteAllText(_subsPath, JsonSerializer.Serialize(_subs, WriteOptions));
        }
        catch (Exception ex) { Log.Warning(ex, "Push: não salvou as inscrições"); }
    }
}
