using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using Serilog;

namespace Orbit.Services;

// Um app usando câmera e/ou microfone agora. Since = desde quando (o mais antigo dos dois).
public sealed record CameraMicUse(string App, bool Camera, bool Mic, DateTime Since);

// O que a pílula desenha: bolinha (verde se câmera, laranja se só microfone), nome, ícones e tempo.
public sealed record CameraMicDisplay(string App, bool Camera, bool Mic, string Time);

// "Quem está usando sua câmera e seu microfone" — as bolinhas verde/laranja do iPhone, no Windows.
// Microfone: sessões de captura ativas no serviço de áudio (vale pra qualquer app). Câmera: o registro
// de privacidade do Windows (ConsentStore; Fim = 0 é uso em andamento) — nesta máquina ele só anota
// apps assinados, então app sem assinatura usando a câmera pode não aparecer.
// ponytail: sessões de áudio conferidas a cada 3 s; IAudioSessionNotification se um dia pesar.
public sealed class CameraMicWatcher : IDisposable
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    public event Action<IReadOnlyList<CameraMicUse>>? Changed;

    private readonly object _gate = new();
    private readonly Dictionary<string, DateTime> _micSince = new(StringComparer.OrdinalIgnoreCase); // início visto das capturas
    private Timer? _poll;
    private string _last = "";
    private volatile bool _stop;

    public void Start()
    {
        new Thread(RegistryLoop) { IsBackground = true, Name = "CameraMicWatcher" }.Start();
        _poll = new Timer(_ => Refresh(), null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    // Avisa só quando o conjunto muda (o timer roda a cada 3 s, mas a pílula não precisa saber).
    private void Refresh()
    {
        if (_stop) return;
        try
        {
            var uses = Merge(ReadCamera(Environment.ProcessPath), ReadMicSessions(Environment.ProcessId));
            var key = string.Join("|", uses.Select(u => $"{u.App}:{u.Camera}:{u.Mic}"));
            lock (_gate)
            {
                if (key == _last) return;
                _last = key;
            }
            Changed?.Invoke(uses);
        }
        catch (Exception ex) { Log.Warning(ex, "Câmera/microfone em uso: falha ao ler"); }
    }

    // Registro muda (início/fim de uso da câmera): relê na hora em vez de esperar o timer.
    private void RegistryLoop()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ConsentStore);
            if (key is null) return;
            while (!_stop)
            {
                if (RegNotifyChangeKeyValue(key.Handle.DangerousGetHandle(), true, REG_NOTIFY_CHANGE_NAME | REG_NOTIFY_CHANGE_LAST_SET, IntPtr.Zero, false) != 0) return;
                Thread.Sleep(250); // início/fim costumam vir em sequência: lê uma vez só
                Refresh();
            }
        }
        catch (Exception ex) { Log.Warning(ex, "Câmera em uso: parou de observar o registro"); }
    }

    // --- Câmera (registro de privacidade) ---

    public static IReadOnlyList<CameraMicUse> ReadCamera(string? self)
    {
        var raw = new List<(string Cap, string App, long Start, long Stop)>();
        using var capKey = Registry.CurrentUser.OpenSubKey($@"{ConsentStore}\webcam");
        if (capKey is null) return Array.Empty<CameraMicUse>();
        foreach (var name in capKey.GetSubKeyNames())
        {
            if (name == "NonPackaged")
            {
                using var np = capKey.OpenSubKey(name);
                foreach (var app in np?.GetSubKeyNames() ?? Array.Empty<string>())
                    using (var k = np!.OpenSubKey(app)) raw.Add(("webcam", app.Replace('#', '\\'), Long(k, "LastUsedTimeStart"), Long(k, "LastUsedTimeStop")));
            }
            else
                using (var k = capKey.OpenSubKey(name)) raw.Add(("webcam", name, Long(k, "LastUsedTimeStart"), Long(k, "LastUsedTimeStop")));
        }
        return Active(raw, self);
    }

    private static long Long(RegistryKey? k, string name) => k?.GetValue(name) is long v ? v : 0;

    // Em uso = começou e não terminou. Junta por app; ignora o próprio Orbit.
    public static IReadOnlyList<CameraMicUse> Active(IEnumerable<(string Cap, string App, long Start, long Stop)> raw, string? self) =>
        raw.Where(r => r.Start > 0 && r.Stop == 0 && !string.Equals(r.App, self, StringComparison.OrdinalIgnoreCase))
            .GroupBy(r => r.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CameraMicUse(FriendlyName(g.Key), g.Any(r => r.Cap == "webcam"), g.Any(r => r.Cap == "microphone"),
                DateTime.FromFileTime(g.Min(r => r.Start))))
            .OrderBy(u => u.Since)
            .ToList();

    // --- Microfone (sessões de captura do serviço de áudio) ---

    private IReadOnlyList<CameraMicUse> ReadMicSessions(int selfPid)
    {
        var now = DateTime.Now;
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var en = new MMDeviceEnumerator())
            foreach (var dev in en.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                using (dev)
                {
                    var sessions = dev.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++)
                    {
                        using var s = sessions[i];
                        if (s.State != AudioSessionState.AudioSessionStateActive) continue;
                        var pid = (int)s.GetProcessID;
                        if (pid == 0 || pid == selfPid) continue;
                        apps.Add(AppOf(pid));
                    }
                }
        lock (_gate)
        {
            foreach (var gone in _micSince.Keys.Where(k => !apps.Contains(k)).ToList()) _micSince.Remove(gone);
            foreach (var a in apps) _micSince.TryAdd(a, now);
            return apps.Select(a => new CameraMicUse(FriendlyName(a), false, true, _micSince[a])).ToList();
        }
    }

    private static string AppOf(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            try { return p.MainModule?.FileName ?? p.ProcessName; } catch { return p.ProcessName; } // processo elevado: só o nome
        }
        catch { return $"processo {pid}"; }
    }

    // Câmera (registro) + microfone (sessões): mesmo app vira uma linha só com os dois.
    public static IReadOnlyList<CameraMicUse> Merge(IEnumerable<CameraMicUse> camera, IEnumerable<CameraMicUse> mic) =>
        camera.Concat(mic)
            .GroupBy(u => u.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => new CameraMicUse(g.Key, g.Any(u => u.Camera), g.Any(u => u.Mic), g.Min(u => u.Since)))
            .OrderBy(u => u.Since)
            .ToList();

    // "C:\…\Discord.exe" → descrição do executável ("Discord"); pacote da loja
    // "Microsoft.WindowsCamera_8wekyb3d8bbwe" → "WindowsCamera".
    public static string FriendlyName(string app)
    {
        if (app.Contains('\\'))
        {
            try
            {
                if (File.Exists(app) && FileVersionInfo.GetVersionInfo(app).FileDescription is { Length: > 0 } d) return d.Trim();
            }
            catch { }
            return Path.GetFileNameWithoutExtension(app);
        }
        var family = app.Split('_')[0];
        var dot = family.IndexOf('.');
        return dot >= 0 && dot < family.Length - 1 ? family[(dot + 1)..] : family;
    }

    // Pílula: nome(s) do(s) app(s), ícones do que está em uso e há quanto tempo (do mais antigo).
    public static CameraMicDisplay? Display(IReadOnlyList<CameraMicUse> uses, DateTime now)
    {
        if (uses.Count == 0) return null;
        var mins = (int)(now - uses.Min(u => u.Since)).TotalMinutes;
        var time = mins < 1 ? "agora" : mins < 60 ? $"{mins} min" : $"{mins / 60} h {mins % 60:00}";
        return new CameraMicDisplay(string.Join(", ", uses.Select(u => u.App)), uses.Any(u => u.Camera), uses.Any(u => u.Mic), time);
    }

    // Texto (toast/acessibilidade): "Discord · câmera e microfone · 12 min"; vários: "Microfone: Discord, Brave".
    public static string? Describe(IReadOnlyList<CameraMicUse> uses, DateTime now)
    {
        if (uses.Count == 0) return null;
        static string What(bool cam, bool mic) => cam && mic ? "câmera e microfone" : cam ? "câmera" : "microfone";
        if (uses.Count == 1)
        {
            var u = uses[0];
            var mins = (int)(now - u.Since).TotalMinutes;
            return $"{u.App} · {What(u.Camera, u.Mic)}" + (mins >= 1 ? $" · {mins} min" : "");
        }
        var label = What(uses.Any(u => u.Camera), uses.Any(u => u.Mic));
        return char.ToUpper(label[0]) + label[1..] + ": " + string.Join(", ", uses.Select(u => u.App));
    }

    public void Dispose()
    {
        _stop = true;
        _poll?.Dispose();
    }

    private const int REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4;
    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(IntPtr hKey, bool watchSubtree, int notifyFilter, IntPtr hEvent, bool asynchronous);
}
