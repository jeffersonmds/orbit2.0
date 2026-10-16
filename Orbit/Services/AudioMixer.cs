using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using NAudio.CoreAudioApi;
using Serilog;

namespace Orbit.Services;

// Mixer por app + lista/troca de saída. Vive só com o mixer aberto na aba Controles (Dispose ao fechar).
// Sessões do Windows agrupadas por executável (o Chrome abre várias): volume/mudo valem pro grupo.
public sealed class AudioMixer : IDisposable
{
    public sealed record Output(string Id, string Name, bool IsDefault);
    public sealed record App(string Key, string Name, string? ExePath, double Percent, bool Muted);

    public const string SystemKey = "system";

    private readonly MMDeviceEnumerator _enumerator = new();
    private MMDevice? _device;
    private Dictionary<string, List<AudioSessionControl>> _groups = new();
    private readonly Dictionary<uint, (string Key, string Name, string? Path)> _procs = new(); // pid → identidade

    public IReadOnlyList<Output> Outputs()
    {
        try
        {
            string? def = null;
            try { using var d = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); def = d.ID; } catch { }
            var list = new List<Output>();
            foreach (var d in _enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
                using (d) list.Add(new Output(d.ID, d.FriendlyName, d.ID == def));
            return list.OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Mixer: não listei as saídas");
            return [];
        }
    }

    // Relê o dispositivo padrão (segue troca feita fora) e as sessões. Barato: dá pra chamar a cada 2 s.
    public IReadOnlyList<App> Refresh()
    {
        try
        {
            var def = _enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (_device?.ID != def.ID)
            {
                _device?.Dispose();
                _device = def;
            }
            else def.Dispose();
            var mgr = _device.AudioSessionManager;
            mgr.RefreshSessions();
            var sessions = mgr.Sessions;
            var groups = new Dictionary<string, List<AudioSessionControl>>();
            var names = new Dictionary<string, (string Name, string? Path)>();
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                if (s.State == NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateExpired) continue;
                var who = Identify(s);
                if (who is null) continue;
                if (!groups.TryGetValue(who.Value.Key, out var list)) groups[who.Value.Key] = list = new();
                list.Add(s);
                names[who.Value.Key] = (who.Value.Name, who.Value.Path);
            }
            _groups = groups;
            return Order(groups.Select(g =>
            {
                var v = g.Value[0].SimpleAudioVolume;
                return new App(g.Key, names[g.Key].Name, names[g.Key].Path, Volume.ToPercent(v.Volume), v.Mute);
            }));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Mixer: não li as sessões");
            _groups = new();
            return [];
        }
    }

    // Pico atual (0..1) do app, pro medidor.
    public double Peak(string key)
    {
        if (!_groups.TryGetValue(key, out var list)) return 0;
        double max = 0;
        foreach (var s in list)
        {
            try { max = Math.Max(max, s.AudioMeterInformation.MasterPeakValue); } catch { }
        }
        return max;
    }

    public void SetVolume(string key, double percent) => ForGroup(key, s => s.SimpleAudioVolume.Volume = Volume.ToScalar(percent));
    public void SetMute(string key, bool mute) => ForGroup(key, s => s.SimpleAudioVolume.Mute = mute);

    private void ForGroup(string key, Action<AudioSessionControl> act)
    {
        if (!_groups.TryGetValue(key, out var list)) return;
        foreach (var s in list)
        {
            try { act(s); } catch (Exception ex) { Log.Debug(ex, "Mixer: sessão sumiu"); }
        }
    }

    private (string Key, string Name, string? Path)? Identify(AudioSessionControl s)
    {
        if (s.IsSystemSoundsSession) return (SystemKey, "Sons do sistema", null);
        uint pid = s.GetProcessID;
        if (_procs.TryGetValue(pid, out var known)) return known;
        var path = ExePath(pid);
        if (path is null && !ProcessAlive(pid)) return null;
        string? desc = null;
        try { if (path is not null) desc = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
        var id = (path?.ToLowerInvariant() ?? "pid:" + pid, DisplayName(desc, path, s.DisplayName), path);
        _procs[pid] = id;
        return id;
    }

    // --- Lógica pura testável ---

    // Descrição do exe ("Spotify") > nome da sessão > nome do arquivo ("spotify") > "App".
    public static string DisplayName(string? fileDescription, string? exePath, string? sessionName)
    {
        if (!string.IsNullOrWhiteSpace(fileDescription)) return fileDescription.Trim();
        if (!string.IsNullOrWhiteSpace(sessionName) && !sessionName.StartsWith('@')) return sessionName.Trim();
        if (!string.IsNullOrWhiteSpace(exePath)) return Path.GetFileNameWithoutExtension(exePath);
        return "App";
    }

    // Apps por nome; "Sons do sistema" por último.
    public static IReadOnlyList<App> Order(IEnumerable<App> apps) =>
        apps.OrderBy(a => a.Key == SystemKey)
            .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

    // --- Troca da saída padrão ---

    // IPolicyConfig NÃO é documentada (a mesma que SoundSwitch/EarTrumpet usam). Pode quebrar num
    // update do Windows: quem chama trata false abrindo as Configurações de som.
    public static bool SetDefaultOutput(string deviceId)
    {
        object? obj = null;
        try
        {
            obj = new CPolicyConfigClient();
            var policy = (IPolicyConfig)obj;
            foreach (var role in new[] { 0, 1, 2 }) // eConsole, eMultimedia, eCommunications
                if (policy.SetDefaultEndpoint(deviceId, role) != 0) return false;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Mixer: IPolicyConfig falhou");
            return false;
        }
        finally
        {
            if (obj is not null) Marshal.ReleaseComObject(obj);
        }
    }

    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")]
    private class CPolicyConfigClient { }

    // Só a ordem da vtable importa; os métodos que não usamos ficam com IntPtr.
    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }

    // --- Processo ---

    private static string? ExePath(uint pid)
    {
        var h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    private static bool ProcessAlive(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return !p.HasExited; }
        catch { return false; }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    public void Dispose()
    {
        _groups = new();
        _device?.Dispose();
        _enumerator.Dispose();
    }
}
