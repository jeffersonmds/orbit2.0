using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Services;
using Serilog;

namespace Orbit.Tabs.Clips;

// Aba Clipboard. A captura roda sempre (evento WM_CLIPBOARDUPDATE → CaptureAsync), mesmo com a
// ilha recolhida — a menos que o usuário esconda a aba em Ajustes (aí não grava nada).
public sealed partial class ClipsViewModel : TabViewModel
{
    private readonly string _storePath;
    private readonly string _imagesDir;
    private readonly Func<bool> _enabled;
    private readonly List<ClipItem> _items;

    public override string Id => "clipboard";
    public override string Title => "Clipboard";

    public ObservableCollection<ClipItem> Visible { get; } = new();
    public IReadOnlyList<ClipFilter> Filters { get; } = Enum.GetValues<ClipFilter>();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private ClipFilter _filter = ClipFilter.Tudo;
    [ObservableProperty] private ClipItem? _selected;
    [ObservableProperty] private string _searchHint = "Buscar…";
    [ObservableProperty] private string _detailType = "";
    [ObservableProperty] private string _detailSize = "";
    [ObservableProperty] private string _detailCopied = "";

    public bool HasQuery => Query.Length > 0;
    public bool ShowNothingCopied => _items.Count == 0;
    public bool ShowNothingFound => _items.Count > 0 && Visible.Count == 0;
    public string PinLabel => Selected?.Pinned == true ? "Desafixar" : "Fixar";

    // Item já está no clipboard: a ilha recolhe e manda Ctrl+V pro app anterior.
    public event Action? PasteRequested;

    public ClipsViewModel(string storePath, string imagesDir, Func<bool> enabled)
    {
        _storePath = storePath;
        _imagesDir = imagesDir;
        _enabled = enabled;
        _items = ClipboardStore.Load(storePath);
        Refresh();
    }

    public override void SetActive(bool active)
    {
        if (active) Refresh(); // atualiza os "4m", "ontem"
    }

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        Refresh();
    }

    partial void OnFilterChanged(ClipFilter value) => Refresh();

    partial void OnSelectedChanged(ClipItem? value)
    {
        OnPropertyChanged(nameof(PinLabel));
        DetailType = value?.Kind switch { ClipKind.Link => "Link", ClipKind.Image => "Imagem", ClipKind.Text => "Texto", _ => "" };
        DetailSize = value is null ? ""
            : value.Kind == ClipKind.Image ? $"{value.Width}×{value.Height}"
            : ClipHistory.Describe(value.Text ?? "");
        DetailCopied = value is null ? "" : value.CopiedAt.ToString("dd/MM 'às' HH:mm");
    }

    // Chamado pela ilha a cada WM_CLIPBOARDUPDATE (UI thread).
    public async Task CaptureAsync()
    {
        if (!_enabled()) return;
        if (ClipboardReader.OwnedByUs()) return; // eco do nosso próprio Colar
        var snap = ClipboardReader.Read();
        if (snap is not null) await IngestAsync(snap);
    }

    // Separado do Read pra ser testável sem clipboard de verdade.
    public async Task IngestAsync(ClipSnapshot snap)
    {
        if (!_enabled() || snap.IsPrivate) return;
        var at = DateTime.Now; // antes de qualquer await
        ClipItem item;
        if (snap.Text is { } text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > ClipHistory.MaxTextChars) return;
            item = new ClipItem(ClipHistory.ClassifyText(text), text, null, ClipHistory.TextKey(text), at, false, 0, 0);
        }
        else if (snap.Image is { } img)
        {
            try
            {
                // PNG + hash fora da UI thread: um print 4K leva ~100 ms pra codificar.
                var (png, hash) = await Task.Run(() => EncodePng(img));
                var file = hash + ".png";
                var path = Path.Combine(_imagesDir, file);
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(_imagesDir);
                    await File.WriteAllBytesAsync(path, png);
                }
                item = new ClipItem(ClipKind.Image, null, file, "i:" + hash, at, false, img.PixelWidth, img.PixelHeight);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Clipboard: falha ao salvar imagem");
                return;
            }
        }
        else
        {
            return;
        }

        foreach (var gone in ClipHistory.Add(_items, item)) DeleteImage(gone);
        Persist();
        Refresh();
    }

    [RelayCommand]
    private void Paste(ClipItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        bool ok = item.Kind == ClipKind.Image
            ? item.ImagePath is { } p && File.Exists(p) && ClipboardReader.SetImage(LoadFull(p))
            : ClipboardReader.SetText(item.Text ?? "");
        if (!ok) return;
        item.CopiedAt = DateTime.Now; // sobe pro topo
        Persist();
        Refresh();
        PasteRequested?.Invoke();
    }

    // A ilha manda pro iPhone (caixa de saída + aviso). Imagem vai como arquivo, texto como texto.
    public event Action<ClipItem>? SendToIPhoneRequested;

    [RelayCommand]
    private void SendToIPhone()
    {
        if (Selected is { } item) SendToIPhoneRequested?.Invoke(item);
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (Selected is not { } item) return;
        item.Pinned = !item.Pinned;
        Persist();
        Refresh();
        OnPropertyChanged(nameof(PinLabel));
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not { } item) return;
        _items.Remove(item);
        DeleteImage(item);
        Persist();
        Refresh();
    }

    // Limpa o histórico, preservando os fixados.
    [RelayCommand]
    private void Clear()
    {
        foreach (var item in _items.Where(i => !i.Pinned).ToList())
        {
            _items.Remove(item);
            DeleteImage(item);
        }
        Persist();
        Refresh();
    }

    private void Refresh()
    {
        var keep = Selected;
        var now = DateTime.Now;
        Visible.Clear();
        foreach (var item in ClipHistory.View(_items, Query, Filter))
        {
            item.When = ClipHistory.RelativeTime(now, item.CopiedAt);
            Visible.Add(item);
        }
        Selected = keep is not null && Visible.Contains(keep) ? keep : Visible.FirstOrDefault();
        SearchHint = $"Buscar em {_items.Count} {(_items.Count == 1 ? "item" : "itens")}…";
        OnPropertyChanged(nameof(ShowNothingCopied));
        OnPropertyChanged(nameof(ShowNothingFound));
    }

    private void Persist() => ClipboardStore.Save(_items, _storePath);

    private void DeleteImage(ClipItem item)
    {
        if (item.ImageFile is null) return;
        // Outro item pode apontar pro mesmo PNG (mesmo hash): só apaga se ninguém mais usa.
        if (_items.Any(i => i.ImageFile == item.ImageFile)) return;
        try { File.Delete(Path.Combine(_imagesDir, item.ImageFile)); }
        catch (Exception ex) { Log.Warning(ex, "Clipboard: não apaguei {Arquivo}", item.ImageFile); }
    }

    private static (byte[] Png, string Hash) EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var bytes = ms.ToArray();
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes))[..16]);
    }

    private static BitmapImage LoadFull(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
