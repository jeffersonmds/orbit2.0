using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Serilog;

namespace Orbit.Services;

// Download terminado na pasta Downloads: a ilha oferece abrir, mostrar na pasta ou mandar pro aparelho.
// Navegadores baixam num temporário (.crdownload, .part…) e RENOMEIAM no fim; outros programas criam
// direto. Os dois caminhos chegam aqui; temporários e repetidos (mesmo arquivo em 10 s) são ignorados.
public sealed class DownloadWatcher : IDisposable
{
    private static readonly Guid DownloadsFolder = new("374de290-123f-4565-9164-39c4925e467b");
    private static readonly HashSet<string> TempExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".crdownload", ".part", ".partial", ".download", ".tmp", ".opdownload", ".!ut", ".ini",
    };
    private FileSystemWatcher? _fsw;
    private readonly Dictionary<string, DateTime> _recent = new(StringComparer.OrdinalIgnoreCase);

    public event Action<string>? Finished;

    public static string? FolderPath()
    {
        try
        {
            if (SHGetKnownFolderPath(DownloadsFolder, 0, IntPtr.Zero, out var p) == 0)
            {
                try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch { }
        return null;
    }

    // Nome final de um download de verdade (não temporário, não oculto do sistema como desktop.ini).
    public static bool IsFinalName(string path)
    {
        var name = Path.GetFileName(path);
        if (string.IsNullOrEmpty(name) || name.StartsWith('.') || name.StartsWith("~$")) return false;
        return !TempExtensions.Contains(Path.GetExtension(name));
    }

    // Mesmo arquivo avisado há menos de 10 s (criou + renomeou, ou dois eventos do watcher) não repete.
    public bool IsRepeat(string path, DateTime now)
    {
        lock (_recent)
        {
            var dup = _recent.TryGetValue(path, out var at) && now - at < TimeSpan.FromSeconds(10);
            _recent[path] = now;
            if (_recent.Count > 50) _recent.Clear();
            return dup;
        }
    }

    public void Start()
    {
        if (FolderPath() is not { } dir || !Directory.Exists(dir)) { Log.Information("Downloads: pasta não encontrada"); return; }
        _fsw = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
        _fsw.Created += (_, e) => Seen(e.FullPath);
        _fsw.Renamed += (_, e) => Seen(e.FullPath);
    }

    private void Seen(string path)
    {
        if (!IsFinalName(path) || Directory.Exists(path) || IsRepeat(path, DateTime.Now)) return;
        _ = Task.Run(() => FileSettle.WhenReady(path, () => Finished?.Invoke(path)));
    }

    public void Dispose() => _fsw?.Dispose();

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}

// Espera o arquivo terminar de ser gravado (quem criou ainda está escrevendo quando o aviso chega).
public static class FileSettle
{
    public static async Task WhenReady(string path, Action ready, int tries = 20)
    {
        for (int i = 0; i < tries; i++)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                if (new FileInfo(path).Length > 0) { ready(); return; }
            }
            catch (FileNotFoundException) { return; } // apagado/renomeado de novo: o próximo evento cuida
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(150);
        }
    }
}
