using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Serilog;

namespace Orbit.Services;

// Print novo (Win+Shift+S / PrintScreen salvam sozinhos na pasta Capturas de tela do Windows 11):
// avisa com o caminho pra ilha oferecer "mandar pro aparelho". Pasta conhecida do Windows (pode estar no
// OneDrive). Sem polling: FileSystemWatcher.
public sealed class ScreenshotWatcher : IDisposable
{
    private static readonly Guid ScreenshotsFolder = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    private FileSystemWatcher? _fsw;

    public event Action<string>? Captured;

    public static string? FolderPath()
    {
        try
        {
            if (SHGetKnownFolderPath(ScreenshotsFolder, 0, IntPtr.Zero, out var p) == 0)
            {
                try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
            }
        }
        catch { }
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        return Directory.Exists(fallback) ? fallback : null;
    }

    public void Start()
    {
        if (FolderPath() is not { } dir || !Directory.Exists(dir)) { Log.Information("Prints: pasta de capturas não encontrada"); return; }
        _fsw = new FileSystemWatcher(dir) { Filter = "*.png", NotifyFilter = NotifyFilters.FileName, EnableRaisingEvents = true };
        _fsw.Created += (_, e) => _ = Task.Run(() => Settle(e.FullPath));
    }

    // Espera o arquivo terminar de ser gravado (o Windows/OneDrive ainda está escrevendo quando o aviso chega).
    private async Task Settle(string path)
    {
        for (int i = 0; i < 20; i++)
        {
            try
            {
                using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                if (new FileInfo(path).Length > 0) { Captured?.Invoke(path); return; }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            await Task.Delay(150);
        }
    }

    public void Dispose() => _fsw?.Dispose();

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
}
