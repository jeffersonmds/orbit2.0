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
    // Foto do iPhone pro clipboard do PC. false = não é imagem / clipboard ocupado.
    public Func<byte[], Task<bool>> SetClipboardImage { get; init; } = _ => Task.FromResult(false);
    // "Recebidos do PC": o que o dono do PC mandou pro iPhone. null = rotas /api/outbox somem.
    public Outbox? Outbox { get; init; }
    // Texto da aba Notas (notes.txt).
    public Func<Task<string>> GetNotes { get; init; } = () => Task.FromResult("");
    public Func<string, Task> SetNotes { get; init; } = _ => Task.CompletedTask;
    public Func<PowerAction, Task> Power { get; init; } = _ => Task.CompletedTask;
    public Func<MediaCommand, Task> Media { get; init; } = _ => Task.CompletedTask;
    public Func<Task<MediaState>> GetMedia { get; init; } = () => Task.FromResult(new MediaState(null, null, false, 0));
    public Func<Task<byte[]?>> GetCover { get; init; } = () => Task.FromResult<byte[]?>(null); // capa original (JPEG/PNG), ou null
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

    // Avisos no iPhone (Web Push). null = rotas /api/push respondem 404.
    public WebPush? Push { get; init; }

    // Tela remota: ver (Tela) e controlar (Controle, só com Tela ligada). Captura e entrada vêm da
    // ilha; os testes injetam fakes (nenhum teste mexe no mouse de verdade).
    public Func<bool> ScreenEnabled { get; init; } = () => false;
    public Func<bool> ControlEnabled { get; init; } = () => false;
    // Teclado remoto (ditado do iPhone → app em foco no PC): só texto e teclas, permissão própria.
    public Func<bool> KeyboardEnabled { get; init; } = () => false;
    public Func<IReadOnlyList<ScreenInfo>> Screens { get; init; } = () => Array.Empty<ScreenInfo>();
    public Func<System.Drawing.Rectangle, ScreenQuality, byte[]?> CaptureScreen { get; init; } = (_, _) => null;
    public Action<RemoteCommand, System.Drawing.Rectangle> RemoteInput { get; init; } = (_, _) => { };
    // Microfone (permissão própria). OpenMic devolve a taxa do PCM e quem para a captura.
    public Func<bool> MicEnabled { get; init; } = () => false;
    public Func<IReadOnlyList<MicDevice>> MicDevices { get; init; } = () => Array.Empty<MicDevice>();
    public Func<string?, Action<byte[]>, (int Rate, IDisposable Stop)> OpenMic { get; init; } =
        (_, _) => throw new InvalidOperationException("Sem microfone");

    // Vendo/controlando/ouvindo — pro indicador na pílula. Chamado de thread do Kestrel.
    public Action<RemoteState> RemoteChanged { get; init; } = _ => { };
    public Func<IReadOnlyCollection<string>> ValidTokens { get; init; } = () => Array.Empty<string>();
}
