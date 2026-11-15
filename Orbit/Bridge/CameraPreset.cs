using System;

namespace Orbit.Bridge;

public sealed record CameraPreset(int Width, int Fps)
{
    public static readonly CameraPreset Default = new(1280, 24);

    public static CameraPreset Parse(string? quality, string? fps)
    {
        if (quality == "eco") return new(640, 12);
        int width = quality switch { "720" => 1280, "480" => 640, _ => 0 };
        if (width == 0) return Default;
        return new(width, int.TryParse(fps, out var f) ? Math.Clamp(f, 5, 30) : Default.Fps);
    }
}
