using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Orbit.Services;

namespace Orbit.Bridge;

public sealed record MediaState(string? Title, string? Artist, bool Playing, double Volume);
public sealed record StatsSnapshot(double Cpu, double RamUsedGb, double RamTotalGb, double DiskUsedGb, double DiskTotalGb,
    int? BatteryPercent, bool Charging);
// OpenPath: pasta/arquivo que o clique no alerta abre (ex.: onde o upload foi salvo).
public sealed record BridgeEvent(string Title, string Detail, string? OpenPath = null);

// O que o servidor pede pra ilha fazer. O BridgeServer não conhece WPF; a ilha implementa
// estes delegates (com Dispatcher quando precisa da UI thread) e os testes usam fakes.
public sealed class BridgeHandlers
{
    public Func<string, Task> OpenUrl { get; init; } = _ => Task.CompletedTask;
    public Func<Task<string>> GetClipboard { get; init; } = () => Task.FromResult("");
    public Func<string, Task> SetClipboard { get; init; } = _ => Task.CompletedTask;
    // Texto da aba Notas (notes.txt).
    public Func<Task<string>> GetNotes { get; init; } = () => Task.FromResult("");
    public Func<string, Task> SetNotes { get; init; } = _ => Task.CompletedTask;
    public Func<PowerAction, Task> Power { get; init; } = _ => Task.CompletedTask;
    public Func<MediaCommand, Task> Media { get; init; } = _ => Task.CompletedTask;
    public Func<Task<MediaState>> GetMedia { get; init; } = () => Task.FromResult(new MediaState(null, null, false, 0));
    public Func<Task<byte[]?>> GetCover { get; init; } = () => Task.FromResult<byte[]?>(null); // PNG da capa, ou null
    public Func<double, Task> SetVolume { get; init; } = _ => Task.CompletedTask;
    public Func<StatsSnapshot> GetStats { get; init; } = () => new StatsSnapshot(0, 0, 0, 0, 0, null, false);
    public Func<string> UploadFolder { get; init; } = () => Path.GetTempPath();
    // Pra pílula (link aberto, arquivo recebido…). Chamado de thread do Kestrel.
    public Action<BridgeEvent> Notify { get; init; } = _ => { };

    // true = a câmera pode responder. Lida a cada requisição /api/camera/*.
    public Func<bool> CameraEnabled { get; init; } = () => false;

    // Arquivos do PC (só leitura): capability à parte, lida a cada requisição /api/pcfiles/*.
    public Func<bool> PcFilesEnabled { get; init; } = () => false;
    public Func<IReadOnlyList<PcRoot>> PcRoots { get; init; } = () => Array.Empty<PcRoot>();
}
