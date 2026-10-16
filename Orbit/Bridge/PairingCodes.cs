using System;
using System.Security.Cryptography;
using System.Text;

namespace Orbit.Bridge;

// Código de 6 dígitos que troca por um token no pareamento (digitar no iPhone em vez de copiar link).
// Um código ativo por vez; vale 5 minutos, uso único, invalida após 5 tentativas erradas.
// Em memória só: reiniciar o Orbit invalida o código pendente (o token já criado continua).
public sealed class PairingCodes
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaxAttempts = 5;

    private readonly object _gate = new();
    private string? _code;
    private string? _token;
    private DateTime _expires;
    private int _attempts;

    // Gera um código novo pro token (substitui o anterior, se houver).
    public string Issue(string token, DateTime now)
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        lock (_gate)
        {
            _code = code;
            _token = token;
            _expires = now + Lifetime;
            _attempts = 0;
        }
        return code;
    }

    // Token se o código estiver certo e válido; senão null. Acerto ou excesso de erros invalida.
    public string? Claim(string? input, DateTime now)
    {
        var typed = Normalize(input);
        lock (_gate)
        {
            if (_code is null || _token is null) return null;
            if (now > _expires) { Clear(); return null; }
            if (typed.Length == 6 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(typed), Encoding.ASCII.GetBytes(_code)))
            {
                var token = _token;
                Clear();
                return token;
            }
            if (++_attempts >= MaxAttempts) Clear();
            return null;
        }
    }

    public void Cancel()
    {
        lock (_gate) Clear();
    }

    // "482 913" / "482-913" → "482913".
    public static string Normalize(string? input)
    {
        var sb = new StringBuilder(6);
        foreach (var c in input ?? "") if (char.IsAsciiDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public static string Format(string code) => code.Length == 6 ? code[..3] + " " + code[3..] : code;

    private void Clear()
    {
        _code = null;
        _token = null;
        _attempts = 0;
    }
}
