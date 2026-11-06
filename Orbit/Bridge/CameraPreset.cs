using System;

namespace Orbit.Bridge;

public sealed record CameraPreset(int Width, int Fps, float JpegQuality = .75f, bool Automatic = false)
{
    public static readonly CameraPreset Default = new(1280, 24);

    public static CameraPreset Parse(string? quality, string? fps)
    {
        if (quality == "auto") return Default with { Automatic = true };
        if (quality == "eco") return new(640, 12, .55f);
        if (quality == "fluid") return Default;
        int width = quality switch { "720" => 1280, "480" => 640, _ => 0 };
        if (width == 0) return Default;
        return new(width, int.TryParse(fps, out var f) ? Math.Clamp(f, 5, 30) : Default.Fps);
    }

    public CameraPreset Adapt(double sendMs, ref int slow, ref int fast, CameraPreset requested)
    {
        if (!requested.Automatic) return this;
        bool late = sendMs > 1000.0 / Fps;
        slow = late ? slow + 1 : 0;
        fast = !late && sendMs < 20 ? fast + 1 : 0;
        if (slow >= 3) { slow = 0; return requested.Reduced; }
        if (fast >= 120) { fast = 0; return requested; }
        return this;
    }

    public CameraPreset Reduced => new(640, 12, .55f, Automatic);
    public static double FormatCost(int width, double fps, CameraPreset wanted) =>
        Math.Abs(width - wanted.Width) / (double)wanted.Width + Math.Abs(fps - wanted.Fps) / wanted.Fps;
}

public sealed class FramePacer
{
    private double _next = double.NaN;
    public bool Accept(double nowMs, int fps)
    {
        double step = 1000.0 / Math.Max(1, fps);
        if (!double.IsNaN(_next) && nowMs + .001 < _next) return false;
        _next = double.IsNaN(_next) || nowMs - _next > step * 2 ? nowMs + step : _next + step;
        return true;
    }
}
