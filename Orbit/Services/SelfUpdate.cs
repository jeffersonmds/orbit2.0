using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Serilog;

namespace Orbit.Services;

// Estado da versão em relação ao código. Behind = commits novos no repo; Dirty = código com
// alterações não commitadas (o build pode estar diferente mesmo com Behind = 0).
// DownloadUrl = modo GitHub (sem o código nesta máquina): zip do build automático da última versão.
public sealed record UpdateStatus(string? Commit, int Behind, bool Dirty, bool CanUpdate, string Reason, string? DownloadUrl = null);

// Autoatualização. Com o código nesta máquina: o build grava a pasta do repo e o commit (Orbit.csproj),
// o Orbit instalado compara com o repo e republica (dotnet publish). Sem o código (outro notebook):
// compara com a última release do GitHub (build automático, .github/workflows/release.yml) e baixa o zip.
// Nos dois casos troca os próprios arquivos com um script que espera ele fechar, guarda backup e volta
// atrás se a versão nova não ficar de pé (ex.: Smart App Control barrando binário recém-compilado).
public static class SelfUpdate
{
    public static string? RepoRoot =>
        typeof(SelfUpdate).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "RepoRoot")?.Value;

    // "2.0.0+6ac9043-dirty" → ("6ac9043", true). Sem "+…" → (null, false).
    public static (string? Commit, bool Dirty) ParseVersion(string? informational)
    {
        var at = informational?.IndexOf('+') ?? -1;
        if (at < 0) return (null, false);
        var rev = informational![(at + 1)..];
        bool dirty = rev.EndsWith("-dirty", StringComparison.Ordinal);
        return (dirty ? rev[..^6] : rev, dirty);
    }

    public static (string? Commit, bool Dirty) Current =>
        ParseVersion(typeof(SelfUpdate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit2", "update");

    // Rodando de dentro do repo (bin\Debug): aí a atualização é `dotnet run`, não trocar arquivos.
    public static bool IsInside(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    public static async Task<UpdateStatus> CheckAsync()
    {
        var (commit, builtDirty) = Current;
        var repo = RepoRoot;
        var exeDir = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        if (repo is null || !Directory.Exists(Path.Combine(repo, ".git")))
            return await CheckGitHubAsync(commit);
        if (IsInside(exeDir, repo))
            return new(commit, 0, false, false, "Rodando do código (Debug): use dotnet run");
        int behind = 0;
        if (commit is not null && int.TryParse((await Git(repo, $"rev-list --count {commit}..HEAD")).Out.Trim(), out var n)) behind = n;
        bool dirty = (await Git(repo, "status --porcelain")).Out.Trim().Length > 0;
        return new(commit, behind, dirty || builtDirty, true, "");
    }

    public const string GitHubRepo = "jeffersonmds/orbit2.0";
    public const string ReleaseAsset = "Orbit-win-x64.zip";
    private static readonly System.Net.Http.HttpClient Http = CreateHttp();

    private static System.Net.Http.HttpClient CreateHttp()
    {
        var h = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Orbit2-SelfUpdate");
        h.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return h;
    }

    // Tag da release = "build-<commit de 7>" (ver o workflow). Devolve o commit, ou null se não for desse formato.
    public static string? CommitFromTag(string? tag) =>
        tag is { Length: 13 } && tag.StartsWith("build-", StringComparison.Ordinal) ? tag[6..] : null;

    private static async Task<UpdateStatus> CheckGitHubAsync(string? commit)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest"));
            var latest = CommitFromTag(doc.RootElement.GetProperty("tag_name").GetString());
            string? url = null;
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
                if (a.GetProperty("name").GetString() == ReleaseAsset) url = a.GetProperty("browser_download_url").GetString();
            if (latest is null || url is null) return new(commit, 0, false, false, "Release do GitHub sem o pacote");
            int behind = 0;
            if (!string.Equals(latest, commit, StringComparison.OrdinalIgnoreCase))
            {
                behind = 1; // sem o compare (commit local que nunca subiu, cota da API): ao menos "tem versão nova"
                try
                {
                    using var cmp = System.Text.Json.JsonDocument.Parse(await Http.GetStringAsync($"https://api.github.com/repos/{GitHubRepo}/compare/{commit}...{latest}"));
                    behind = cmp.RootElement.GetProperty("ahead_by").GetInt32();
                }
                catch (Exception ex) { Log.Information(ex, "Autoatualização: compare do GitHub falhou"); }
            }
            return new(commit, behind, false, true, "", url);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Autoatualização: GitHub indisponível");
            return new(commit, 0, false, false, "Sem acesso ao GitHub");
        }
    }

    // Prepara a versão nova numa pasta: baixa do GitHub ou publica do código local.
    public static Task<string> PrepareAsync(UpdateStatus s) => s.DownloadUrl is { } url ? DownloadAsync(url) : PublishAsync();

    private static async Task<string> DownloadAsync(string url)
    {
        Directory.CreateDirectory(LocalDir);
        var zip = Path.Combine(LocalDir, ReleaseAsset);
        var staging = Path.Combine(LocalDir, "publish");
        await using (var src = await Http.GetStreamAsync(url))
        await using (var dst = File.Create(zip))
            await src.CopyToAsync(dst);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        System.IO.Compression.ZipFile.ExtractToDirectory(zip, staging);
        File.Delete(zip);
        if (!File.Exists(Path.Combine(staging, "Orbit.exe"))) throw new InvalidOperationException("pacote do GitHub sem Orbit.exe");
        return staging;
    }

    // dotnet publish do código pra pasta de preparo. Devolve a pasta, ou lança com o fim do log.
    public static async Task<string> PublishAsync()
    {
        var repo = RepoRoot ?? throw new InvalidOperationException("Sem pasta do código");
        var staging = Path.Combine(LocalDir, "publish");
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        var r = await Run("dotnet", $"publish \"{Path.Combine(repo, "Orbit", "Orbit.csproj")}\" -c Release -r win-x64 --self-contained -o \"{staging}\" -nologo", repo);
        if (r.Exit != 0 || !File.Exists(Path.Combine(staging, "Orbit.exe")))
            throw new InvalidOperationException("dotnet publish falhou: " + string.Join(" | ", r.Out.Split('\n').Where(l => l.Contains("error", StringComparison.OrdinalIgnoreCase)).Take(3)));
        return staging;
    }

    // Sinal de "a versão nova abriu de verdade": a ilha já está na tela. O script de troca espera
    // por ele (só "o processo não morreu em 8 s" deixava passar um Orbit travado na abertura).
    public static string ReadyFile => Path.Combine(LocalDir, "pronto");

    public static void MarkReady()
    {
        try { if (Directory.Exists(LocalDir)) File.WriteAllText(ReadyFile, Environment.ProcessId.ToString()); }
        catch (Exception ex) { Log.Warning(ex, "Autoatualização: não gravou o sinal de pronto"); }
    }

    // Script que troca os arquivos depois que este Orbit fechar. Aspas simples dobradas (PowerShell).
    public static string SwapScript(int pid, string install, string staging, string log, string? ready = null)
    {
        ready ??= ReadyFile;
        static string Q(string s) => "'" + s.Replace("'", "''") + "'";
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(install));
        return $$"""
            $ErrorActionPreference = 'Stop'
            # Fora da pasta do Orbit: diretório atual dentro dela impede renomeá-la ("em uso").
            Set-Location -LiteralPath $env:TEMP; [Environment]::CurrentDirectory = $env:TEMP  # o 2º muda a pasta do processo pro Windows
            $install = {{Q(install)}}; $staging = {{Q(staging)}}; $log = {{Q(log)}}; $leaf = {{Q(leaf)}}; $ready = {{Q(ready)}}
            $exe = Join-Path $install 'Orbit.exe'; $new = "$install.new"; $old = "$install.old"
            function L($m) { Add-Content -Encoding UTF8 $log ("{0:yyyy-MM-dd HH:mm:ss} $m" -f (Get-Date)) }
            # Antivírus examinando os arquivos novos segura a pasta por instantes: tenta por até 10 s.
            function Retry([scriptblock]$a) { for ($i = 0; $i -lt 20; $i++) { try { & $a; return } catch { Start-Sleep -Milliseconds 500 } }; & $a }
            try {
              Wait-Process -Id {{pid}} -Timeout 30 -ErrorAction SilentlyContinue
              Remove-Item $new, $old -Recurse -Force -ErrorAction SilentlyContinue
              Copy-Item $staging $new -Recurse
              Get-ChildItem $install -Filter 'unins*' | Copy-Item -Destination $new   # desinstalador do Inno
              Retry { Rename-Item $install "$leaf.old" }
              Retry { Rename-Item $new $leaf }
              Remove-Item $ready -Force -ErrorAction SilentlyContinue
              $p = Start-Process $exe -PassThru
              # Espera a ilha abrir (o Orbit novo grava $ready) por até 45 s; fechou ou travou = volta atrás.
              for ($i = 0; $i -lt 90 -and -not (Test-Path $ready); $i++) {
                if ($p.HasExited) { throw "a versão nova fechou sozinha (código $($p.ExitCode))" }
                Start-Sleep -Milliseconds 500
              }
              if (-not (Test-Path $ready)) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue; throw 'a versão nova não abriu a ilha em 45 s' }
              Start-Sleep 3
              if ($p.HasExited) { throw "a versão nova fechou logo depois de abrir (código $($p.ExitCode))" }
              Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
              L 'atualizado'
            } catch {
              L "falhou: $_ — voltando pra versão anterior"
              if (Test-Path $old) { Remove-Item $install -Recurse -Force -ErrorAction SilentlyContinue; Retry { Rename-Item $old $leaf } }
              Remove-Item $new -Recurse -Force -ErrorAction SilentlyContinue
              Start-Process $exe
            }
            """;
    }

    // Grava e dispara o script (oculto). Quem chama fecha o Orbit logo em seguida.
    public static void LaunchSwap(string staging)
    {
        var install = Path.GetDirectoryName(Environment.ProcessPath!)!;
        Directory.CreateDirectory(LocalDir);
        var script = Path.Combine(LocalDir, "trocar.ps1");
        File.WriteAllText(script, SwapScript(Environment.ProcessId, install, staging, Path.Combine(LocalDir, "update.log")), new System.Text.UTF8Encoding(true));
        Process.Start(new ProcessStartInfo("powershell.exe", $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{script}\"")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = LocalDir }); // nunca a pasta do Orbit
        Log.Information("Autoatualização: troca agendada de {Pasta}", install);
    }

    // Texto de Ajustes → Versão.
    public static string Describe(UpdateStatus s)
    {
        var version = s.Commit is null ? "Versão sem commit gravado" : $"Versão {s.Commit}";
        if (!s.CanUpdate) return $"{version} · {s.Reason}";
        var parts = new System.Collections.Generic.List<string>();
        var where = s.DownloadUrl is null ? "no código" : "no GitHub";
        if (s.Behind > 0) parts.Add(s.Behind == 1 ? $"1 commit novo {where}" : $"{s.Behind} commits novos {where}");
        if (s.Dirty) parts.Add("código com alterações não commitadas");
        return $"{version} · " + (parts.Count == 0 ? (s.DownloadUrl is null ? "em dia com o código" : "em dia com o GitHub") : string.Join(", ", parts));
    }

    // Resultado da última troca (linha do update.log), se foi agora há pouco: o Orbit novo avisa ao abrir.
    public static (bool Ok, string Message)? RecentOutcome(string? lastLogLine, DateTime now)
    {
        if (string.IsNullOrWhiteSpace(lastLogLine) || lastLogLine.Length < 20) return null;
        if (!DateTime.TryParseExact(lastLogLine[..19], "yyyy-MM-dd HH:mm:ss", null, System.Globalization.DateTimeStyles.None, out var at)) return null;
        if (now - at > TimeSpan.FromMinutes(3) || now < at) return null;
        var msg = lastLogLine[20..].Trim();
        return (msg.StartsWith("atualizado", StringComparison.Ordinal), msg);
    }

    public static string? LastLogLine()
    {
        try
        {
            var log = Path.Combine(LocalDir, "update.log");
            return File.Exists(log) ? File.ReadLines(log).LastOrDefault() : null;
        }
        catch { return null; }
    }

    private static Task<(int Exit, string Out)> Git(string repo, string args) => Run("git", $"-C \"{repo}\" {args}", repo);

    private static async Task<(int Exit, string Out)> Run(string file, string args, string cwd)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(file, args)
            {
                WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            })!;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode, await outTask + await errTask);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Autoatualização: falhou ao rodar {Arquivo}", file);
            return (-1, ex.Message);
        }
    }
}
