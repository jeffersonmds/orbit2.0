using System;
using System.Security.Cryptography;

namespace Orbit.Bridge;

// Um tíquete ativo por vez, uso único, 30 s para o stream conectar. Guarda a câmera/preset
// escolhidos em /open para o GET /stream (que só carrega o tíquete na URL) saber o que abrir.
// ponytail: um por vez basta — só existe um stream. Só em memória: reiniciar o Orbit zera.
public sealed class StreamTickets
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private string? _ticket;
    private string _owner = "";
    private string _deviceId = "";
    private CameraPreset _preset = CameraPreset.Default;
    private DateTime _expires;

    public string Issue(string owner, string deviceId, CameraPreset preset, DateTime now)
    {
        var tk = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            _ticket = tk;
            _owner = owner;
            _deviceId = deviceId;
            _preset = preset;
            _expires = now + Lifetime;
        }
        return tk;
    }

    public (string Owner, string DeviceId, CameraPreset Preset)? Claim(string? ticket, DateTime now)
    {
        lock (_gate)
        {
            if (_ticket is null || ticket != _ticket) return null;
            var expired = now > _expires;
            var owner = _owner;
            var device = _deviceId;
            var preset = _preset;
            _ticket = null; // uso único, mesmo expirado
            return expired ? null : (owner, device, preset);
        }
    }
}
