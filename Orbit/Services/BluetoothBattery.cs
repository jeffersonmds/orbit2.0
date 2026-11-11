using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.Devices.Enumeration;
using Windows.Devices.Enumeration.Pnp;

namespace Orbit.Services;

// Fone/controle Bluetooth pareado: avisa ao conectar/desconectar (com a bateria, se o aparelho informa) e
// quando a bateria cai a 20% e a 10%. Conexão vem por evento (DeviceWatcher dos pareados); a bateria é a
// mesma que o Windows mostra em Configurações (propriedade PnP do aparelho, vinda do HFP ou do GATT).
public sealed class BluetoothBattery : IDisposable
{
    public event Action<SystemSignal>? Raised;

    private const string Connected = "System.Devices.Aep.IsConnected", Container = "System.Devices.Aep.ContainerId";
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    // Bluetooth clássico OU LE, só os pareados.
    private const string Paired = "(System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR "
        + "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\") AND "
        + "System.Devices.Aep.IsPaired:=System.StructuredQueryType.Boolean#True";
    // ponytail: bateria lida a cada 5 min só com aparelho conectado; aviso de queda pode atrasar até 5 min.
    private static readonly TimeSpan BatteryEvery = TimeSpan.FromMinutes(5);

    private sealed class Device
    {
        public required string Name;
        public Guid? Container;
        public bool On;
        public int LowLevel;
    }

    private readonly Dictionary<string, Device> _devices = new();
    private readonly object _lock = new();
    private DeviceWatcher? _watcher;
    private Timer? _poll;
    private bool _ready; // pareados lidos na abertura: até aqui nada avisa

    public void Start()
    {
        try
        {
            _watcher = DeviceInformation.CreateWatcher(Paired, new[] { Connected, Container }, DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += (_, d) => { lock (_lock) _devices[d.Id] = new Device { Name = d.Name, Container = d.Properties.GetValueOrDefault(Container) as Guid?, On = d.Properties.GetValueOrDefault(Connected) is true }; };
            _watcher.Removed += (_, u) => { lock (_lock) _devices.Remove(u.Id); };
            _watcher.Updated += (_, u) => { if (u.Properties.TryGetValue(Connected, out var v)) OnConnection(u.Id, v is true); };
            _watcher.EnumerationCompleted += (_, _) => { _ready = true; Reschedule(); };
            _poll = new Timer(_ => _ = CheckLow());
            _watcher.Start();
        }
        catch (Exception ex) { Log.Warning(ex, "Bluetooth: sem aviso de fone"); }
    }

    private void OnConnection(string id, bool on)
    {
        Device? dev;
        lock (_lock)
        {
            if (!_devices.TryGetValue(id, out dev) || dev.On == on) return;
            dev.On = on;
            dev.LowLevel = 0;
        }
        Reschedule();
        if (!_ready) return;
        if (!on) { Raised?.Invoke(new SystemSignal(SignalKind.Bluetooth, "Desconectado", dev.Name)); return; }
        _ = Task.Run(async () =>
        {
            await Task.Delay(5000); // o fone só manda a bateria uns segundos depois de conectar
            var pct = await ReadBattery(dev.Container);
            if (pct is not null) lock (_lock) dev.LowLevel = LowLevel(pct.Value);
            Raised?.Invoke(ConnectedSignal(dev.Name, pct));
        });
    }

    public static SystemSignal ConnectedSignal(string name, int? pct) =>
        new(SignalKind.Bluetooth, $"{name} conectado", pct is { } p ? $"Bateria {p}%" : null, pct / 100.0);

    // 0 = ok, 1 = ≤20%, 2 = ≤10%. Avisa quando o nível sobe (piora); carregar volta a 0.
    public static int LowLevel(int pct) => pct <= 10 ? 2 : pct <= 20 ? 1 : 0;

    private async Task CheckLow()
    {
        List<Device> on;
        lock (_lock) on = _devices.Values.Where(d => d.On).ToList();
        foreach (var dev in on)
        {
            if (await ReadBattery(dev.Container) is not { } pct) continue;
            int level = LowLevel(pct), before;
            lock (_lock) { before = dev.LowLevel; dev.LowLevel = level; }
            if (level > before) Raised?.Invoke(new SystemSignal(SignalKind.Bluetooth, $"{dev.Name} com bateria fraca", $"{pct}%", pct / 100.0));
        }
    }

    private void Reschedule()
    {
        bool any;
        lock (_lock) any = _devices.Values.Any(d => d.On);
        _poll?.Change(any ? BatteryEvery : Timeout.InfiniteTimeSpan, any ? BatteryEvery : Timeout.InfiniteTimeSpan);
    }

    // Bateria de qualquer nó PnP do mesmo aparelho (container); null = o aparelho não informa.
    private static async Task<int?> ReadBattery(Guid? container)
    {
        if (container is null) return null;
        try
        {
            var nodes = await PnpObject.FindAllAsync(PnpObjectType.Device, new[] { BatteryKey }, $"System.Devices.ContainerId:=\"{{{container}}}\"");
            foreach (var n in nodes)
                if (n.Properties.GetValueOrDefault(BatteryKey) is IConvertible v) return Convert.ToInt32(v);
        }
        catch (Exception ex) { Log.Information(ex, "Bluetooth: bateria ilegível"); }
        return null;
    }

    public void Dispose()
    {
        _poll?.Dispose();
        try { _watcher?.Stop(); } catch { }
    }
}
