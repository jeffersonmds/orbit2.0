using System;
using System.Threading.Tasks;
using Serilog;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Orbit.Services;

// Estado de mídia lido de uma sessão SMTC — DTO puro, sem lógica.
public record NowPlayingInfo(string? Title, string? Artist, byte[]? Thumbnail, bool IsPlaying);

// Observa a sessão de mídia "atual" do Windows (SMTC) e dispara um evento a cada
// mudança de faixa/estado. Orientado a evento — sem DispatcherTimer batendo o SO.
public sealed class NowPlayingWatcher
{
    private static readonly ILogger _log = Log.ForContext<NowPlayingWatcher>();

    public event Action<NowPlayingInfo?>? SessionChanged;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _active;
    private int _generation;

    public void Start()
    {
        if (_active) return;
        _active = true;
        int generation = ++_generation;
        _ = InitAsync(generation);
    }

    public void Stop()
    {
        _active = false;
        _generation++; // invalida qualquer InitAsync pendente de um Start() anterior
        Detach();
        if (_manager != null) _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        _manager = null;
    }

    private async Task InitAsync(int generation)
    {
        GlobalSystemMediaTransportControlsSessionManager manager;
        try
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "NowPlayingWatcher: SMTC indisponível");
            return;
        }
        // Stop()/novo Start() chegou antes do RequestAsync terminar — descarta este ciclo.
        if (!_active || generation != _generation) return;
        _manager = manager;
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        try
        {
            Attach(_manager.GetCurrentSession());
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "NowPlayingWatcher: falha ao obter sessão atual");
        }
    }

    private void OnCurrentSessionChanged(
        GlobalSystemMediaTransportControlsSessionManager sender,
        CurrentSessionChangedEventArgs args)
    {
        try
        {
            Attach(sender.GetCurrentSession());
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "NowPlayingWatcher: falha ao obter sessão atual");
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        Detach();
        _session = session;
        if (_session == null) { SessionChanged?.Invoke(null); return; }
        _session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        _ = RefreshAsync();
    }

    private void Detach()
    {
        if (_session == null) return;
        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session = null;
    }

    private void OnMediaPropertiesChanged(
        GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
        => _ = RefreshAsync();

    private void OnPlaybackInfoChanged(
        GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
        => _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session == null) return;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            bool isPlaying = session.GetPlaybackInfo().PlaybackStatus
                == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            byte[]? thumb = props.Thumbnail == null ? null : await ReadThumbnailAsync(props.Thumbnail);
            SessionChanged?.Invoke(new NowPlayingInfo(props.Title, props.Artist, thumb, isPlaying));
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "NowPlayingWatcher: falha ao ler propriedades da sessão");
        }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference thumbnail)
    {
        try
        {
            using var stream = await thumbnail.OpenReadAsync();
            using var reader = new DataReader(stream);
            await reader.LoadAsync((uint)stream.Size);
            var bytes = new byte[stream.Size];
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "NowPlayingWatcher: falha ao ler capa");
            return null;
        }
    }

    public async Task PlayPauseAsync()
    {
        var session = _session;
        if (session == null) return;
        try { await session.TryTogglePlayPauseAsync(); } catch (Exception ex) { _log.Warning(ex, "NowPlayingWatcher: play/pause falhou"); }
    }

    public async Task NextAsync()
    {
        var session = _session;
        if (session == null) return;
        try { await session.TrySkipNextAsync(); } catch (Exception ex) { _log.Warning(ex, "NowPlayingWatcher: skip next falhou"); }
    }

    public async Task PreviousAsync()
    {
        var session = _session;
        if (session == null) return;
        try { await session.TrySkipPreviousAsync(); } catch (Exception ex) { _log.Warning(ex, "NowPlayingWatcher: skip previous falhou"); }
    }
}
