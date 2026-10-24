using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Orbit.Services;

// Rede Wi-Fi atual pra compartilhar por QR (a visita aponta a câmera e entra). Password null = rede aberta.
public sealed record WifiInfo(string Ssid, string? Password, bool Open);

// Lê a rede e a senha do perfil salvo pelo netsh (não precisa de administrador pros perfis do usuário)
// e monta o texto do QR padrão "WIFI:" que iPhone e Android reconhecem.
public static class WifiShare
{
    public static WifiInfo? Current()
    {
        if (SystemSignals.WifiSsid() is not { } ssid) return null;
        var profile = SystemSignals.Run("netsh", $"wlan show profile name=\"{ssid.Replace("\"", "")}\" key=clear");
        return Parse(ssid, profile);
    }

    // Saída do netsh em português ou inglês: "Conteúdo da Chave"/"Key Content", "Autenticação"/"Authentication".
    public static WifiInfo Parse(string ssid, string profile)
    {
        string? Field(string pattern) => profile.Split('\n').Select(l => Regex.Match(l.Trim(), $@"^(?:{pattern})\s*:\s*(.+)$"))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value.Trim();
        var auth = Field("Autenticação|Authentication") ?? "";
        bool open = auth.StartsWith("Abert", StringComparison.OrdinalIgnoreCase) || auth.StartsWith("Open", StringComparison.OrdinalIgnoreCase);
        return new WifiInfo(ssid, open ? null : Field("Conteúdo da Chave|Key Content"), open);
    }

    // "WIFI:T:WPA;S:<rede>;P:<senha>;;" — \ ; , : " escapados com barra (padrão do QR de Wi-Fi).
    // T:WPA cobre WPA/WPA2/WPA3 pessoal; rede aberta vai como nopass.
    public static string QrPayload(WifiInfo w)
    {
        static string E(string s)
        {
            var b = new StringBuilder();
            foreach (var c in s) { if (c is '\\' or ';' or ',' or ':' or '"') b.Append('\\'); b.Append(c); }
            return b.ToString();
        }
        return w.Open || w.Password is null
            ? $"WIFI:T:nopass;S:{E(w.Ssid)};;"
            : $"WIFI:T:WPA;S:{E(w.Ssid)};P:{E(w.Password)};;";
    }
}
