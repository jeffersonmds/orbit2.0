using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Orbit.Services;

// Pede a cota ao `claude` oficial, o mesmo pedido do painel do VS Code: o CLI fala com a Anthropic e o Orbit
// não encosta em token. Nenhum prompt é enviado, logo não gasta cota. Hooks desligados: senão o processo
// apareceria na aba como sessão nova. Sem persistência: não cria transcript a cada consulta.
public static class ClaudeUsageClient
{
    private const string RequestId = "orbit-usage";
    private const string Request = "{\"type\":\"control_request\",\"request_id\":\"" + RequestId + "\",\"request\":{\"subtype\":\"get_usage\"}}";
    private const string Arguments = "/c claude -p --no-session-persistence --input-format stream-json --output-format stream-json"
        + " --verbose --settings \"{\\\"disableAllHooks\\\":true}\"";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);
    private const int TailBytes = 256 * 1024;

    // ponytail: get_usage é experimental no SDK do Claude; se mudar, a cota some e a aba diz "indisponível".
    public static async Task<ClaudeUsage?> FetchAsync()
    {
        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo("cmd.exe", Arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                StandardOutputEncoding = Encoding.UTF8,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
            if (p is null) return null;
            using var cts = new CancellationTokenSource(Timeout);
            await p.StandardInput.WriteLineAsync(Request);
            await p.StandardInput.FlushAsync();
            while (await p.StandardOutput.ReadLineAsync(cts.Token) is { } line)
                if (line.Contains(RequestId, StringComparison.Ordinal))
                    return ClaudeUsage.ParseResponse(line, RequestId);
            return null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClaudeUsageClient: falha ao pedir a cota ao claude");
            return null;
        }
        finally
        {
            if (p is not null) await CloseAsync(p);
        }
    }

    // Fechar o stdin encerra o CLI; a árvore cai à força só se ele não sair.
    private static async Task CloseAsync(Process p)
    {
        try
        {
            p.StandardInput.Close();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await p.WaitForExitAsync(cts.Token);
        }
        catch (Exception)
        {
            try { p.Kill(entireProcessTree: true); }
            catch (Exception ex) { Log.Warning(ex, "ClaudeUsageClient: não encerrou o claude"); }
        }
        p.Dispose();
    }

    // I/O: lê só o fim do transcript, que o Claude Code segue gravando. Nunca lança.
    public static long? ReadContextTokens(string transcriptPath)
    {
        try
        {
            using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            int size = (int)Math.Min(TailBytes, fs.Length);
            fs.Seek(-size, SeekOrigin.End);
            var buffer = new byte[size];
            fs.ReadExactly(buffer);
            return ClaudeContext.LastTokens(Encoding.UTF8.GetString(buffer));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ClaudeUsageClient: transcript ilegível");
            return null;
        }
    }
}
