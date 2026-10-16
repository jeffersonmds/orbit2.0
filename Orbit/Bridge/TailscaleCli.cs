using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Orbit.Bridge;

// O que interessa do `tailscale status --json`.
// HttpsReady = a conta tem "HTTPS Certificates" ligado (CertDomains não vazio).
public sealed record TailscaleInfo(bool Installed, bool Running, string? DnsName, bool HttpsReady);

// Stdout separado: `status --json` precisa do JSON limpo; avisos vêm no Stderr.
public sealed record CliResult(int ExitCode, string Stdout, string Stderr, bool TimedOut)
{
    public string All => Stdout + Stderr;
}

// Integração com o Tailscale já instalado, pelo CLI dele (sem embutir nada no Orbit).
// Parsers e textos são puros (testados); RunAsync é o único I/O.
public static class TailscaleCli
{
    public const string AdminDnsUrl = "https://login.tailscale.com/admin/dns";
    private static readonly Regex EnableUrl = new(@"https://login\.tailscale\.com/\S+");

    public static string? FindExe()
    {
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
        if (File.Exists(installed)) return installed;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "tailscale.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // entrada inválida no PATH
            }
        }
        return null;
    }

    public static TailscaleInfo ParseStatus(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool running = root.TryGetProperty("BackendState", out var s) && s.GetString() == "Running";
            string? dns = root.TryGetProperty("Self", out var self) && self.TryGetProperty("DNSName", out var d)
                ? d.GetString()?.TrimEnd('.')
                : null;
            bool https = root.TryGetProperty("CertDomains", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0;
            return new TailscaleInfo(true, running, string.IsNullOrWhiteSpace(dns) ? null : dns, https);
        }
        catch
        {
            return new TailscaleInfo(true, false, null, false);
        }
    }

    // `tailscale serve status --json` tem algum handler "Proxy" apontando pra nossa porta?
    public static bool IsServing(string json, int port)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return ProxyTargets(doc.RootElement).Any(p =>
                p.EndsWith($":{port}", StringComparison.Ordinal) || p.EndsWith($":{port}/", StringComparison.Ordinal));
        }
        catch
        {
            return false;
        }
    }

    private static IEnumerable<string> ProxyTargets(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.Name == "Proxy" && p.Value.ValueKind == JsonValueKind.String) yield return p.Value.GetString()!;
                foreach (var inner in ProxyTargets(p.Value)) yield return inner;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                foreach (var inner in ProxyTargets(item)) yield return inner;
        }
    }

    // --yes: sem prompt interativo. Nunca funnel (internet pública): só serve (tailnet).
    public static string[] ServeArgs(int port) => new[] { "serve", "--bg", "--yes", "--https=443", $"http://127.0.0.1:{port}" };
    public static string[] ServeOffArgs() => new[] { "serve", "--https=443", "off" };

    // Quando o Serve/HTTPS não está liberado na conta, o CLI imprime um link do painel e espera.
    public static string? ExtractEnableUrl(string output) => EnableUrl.Match(output) is { Success: true } m ? m.Value.TrimEnd('.', ',') : null;

    public static string Describe(TailscaleInfo info, bool serving)
    {
        if (!info.Installed) return "Tailscale não instalado (tailscale.com/download).";
        if (!info.Running) return "Tailscale desconectado: abra o app do Tailscale e entre na conta.";
        if (!info.HttpsReady) return "Falta ligar \"HTTPS Certificates\" no painel do Tailscale (DNS).";
        return serving ? "Publicado no Tailscale ✓" : "Tailscale ok. Falta publicar a ponte.";
    }

    public static async Task<CliResult> RunAsync(string exe, IEnumerable<string> args, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("tailscale não iniciou");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        using var cts = new CancellationTokenSource(timeout);
        bool timedOut = false;
        try
        {
            await p.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            timedOut = true; // ex.: esperando o usuário liberar o Serve no painel
            try { p.Kill(entireProcessTree: true); } catch { }
        }
        return new CliResult(timedOut ? -1 : p.ExitCode, await stdout, await stderr, timedOut);
    }
}
