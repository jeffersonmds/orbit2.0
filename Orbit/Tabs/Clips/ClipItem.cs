using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Orbit.Tabs.Clips;

public enum ClipKind { Text, Link, Image }

// Um item do histórico. Imagem fica num PNG em clipboard-img\ (ImageFile = nome do arquivo).
public sealed partial class ClipItem : ObservableObject
{
    public ClipItem(ClipKind kind, string? text, string? imageFile, string key, DateTime copiedAt, bool pinned,
        int width, int height)
    {
        Kind = kind;
        Text = text;
        ImageFile = imageFile;
        Key = key;
        Width = width;
        Height = height;
        _copiedAt = copiedAt;
        _pinned = pinned;
    }

    public ClipKind Kind { get; }
    public string? Text { get; }
    public string? ImageFile { get; }
    public string Key { get; }   // dedupe: "t:<texto>" ou "i:<hash do PNG>"
    public int Width { get; }
    public int Height { get; }

    [ObservableProperty] private DateTime _copiedAt;
    [ObservableProperty] private bool _pinned;
    [ObservableProperty] private string _when = ""; // "4m", "ontem"… (o VM preenche)

    public string? ImagePath => ImageFile is null ? null : Path.Combine(ClipboardStore.ImagesDir, ImageFile);

    public string Preview => Kind == ClipKind.Image ? $"Imagem · {Width}×{Height}" : ClipHistory.OneLine(Text);
}
