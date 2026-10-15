using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Services;

namespace Orbit.Tabs.Controles;

// Portado do NowPlayingTileViewModel do 1.0. Diferença: fica ligado o tempo todo
// (SMTC é orientado a evento, custo ~0) pra alimentar a pílula com a ilha recolhida.
public sealed partial class NowPlayingViewModel : ObservableObject
{
    private readonly NowPlayingWatcher _watcher = new();

    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private BitmapImage? _thumbnail;
    [ObservableProperty] private string _playPauseGlyph = "";
    [ObservableProperty] private string? _activityText;

    public NowPlayingViewModel()
    {
        // O evento vem de thread do WinRT: volta pro dispatcher antes de mexer em propriedades.
        _watcher.SessionChanged += info => Application.Current?.Dispatcher.BeginInvoke(() => Apply(info));
    }

    public void Start() => _watcher.Start();

    private void Apply(NowPlayingInfo? info)
    {
        HasSession = info != null;
        ActivityText = BuildActivity(info);
        if (info == null)
        {
            Title = "";
            Subtitle = "";
            PlayPauseGlyph = PlayPauseGlyphFor(false);
            Thumbnail = null;
            return;
        }
        Title = DisplayTitle(info.Title);
        Subtitle = info.Artist ?? "";
        PlayPauseGlyph = PlayPauseGlyphFor(info.IsPlaying);
        Thumbnail = info.Thumbnail == null ? null : BuildThumbnail(info.Thumbnail);
    }

    private static BitmapImage? BuildThumbnail(byte[] bytes)
    {
        try
        {
            using var ms = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.DecodePixelWidth = 128; // capa é exibida em 64px: não guarda a imagem cheia na memória
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // formato não suportado: a view mostra o glifo de música
        }
    }

    public static string DisplayTitle(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "Tocando…" : title;

    // Pause (E769) enquanto toca, Play (E768) quando pausado.
    public static string PlayPauseGlyphFor(bool isPlaying) => isPlaying ? "" : "";

    // Texto da pílula: só enquanto toca (pausado não ocupa a pílula).
    public static string? BuildActivity(NowPlayingInfo? info)
    {
        if (info is not { IsPlaying: true }) return null;
        var title = DisplayTitle(info.Title);
        return string.IsNullOrWhiteSpace(info.Artist) ? $"▶  {title}" : $"▶  {title} · {info.Artist}";
    }

    [RelayCommand] private Task PlayPause() => _watcher.PlayPauseAsync();
    [RelayCommand] private Task Next() => _watcher.NextAsync();
    [RelayCommand] private Task Previous() => _watcher.PreviousAsync();
}
