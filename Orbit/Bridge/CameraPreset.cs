using System;

namespace Orbit.Bridge;

// Qualidade do stream ao vivo. Largura fixa em 2 opções; altura o encoder deriva do aspecto.
public sealed record CameraPreset(int Width, int Fps)
{
    public static readonly CameraPreset Default = new(1280, 24);

    // quality: "720"|"480"; fps: 5..30. Qualquer coisa fora disso cai no padrão/limite.
    public static CameraPreset Parse(string? quality, string? fps)
    {
        int width = quality switch { "720" => 1280, "480" => 640, _ => 0 };
        if (width == 0) return Default;
        if (!int.TryParse(fps, out var f)) return new CameraPreset(width, Default.Fps);
        return new CameraPreset(width, Math.Clamp(f, 5, 30));
    }
}
