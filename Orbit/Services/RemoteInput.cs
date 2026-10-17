using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Orbit.Services;

public enum RemoteKind { Abs, Move, Down, Up, Click, Scroll, Text, Key }

// Um comando já validado. X/Y: Abs = 0..1 no monitor; Move = delta em px; Scroll = delta da roda.
public sealed record RemoteCommand(RemoteKind Kind, double X = 0, double Y = 0, bool Right = false, int Count = 1,
    string Text = "", ushort Vk = 0, ushort[]? Mods = null);

// Mouse e teclado vindos do iPhone (WebSocket da tela remota). Parse é puro e recusa tudo que não
// conhece; Execute injeta com SendInput/SetCursorPos. Apps elevados ignoram (UIPI) — limite do Windows.
public static class RemoteInput
{
    public const int MaxText = 500;
    private const double MaxDelta = 2000;

    private static readonly Dictionary<string, ushort> Keys = BuildKeys();
    private static readonly Dictionary<string, ushort> Modifiers = new()
    {
        ["ctrl"] = 0x11, ["shift"] = 0x10, ["alt"] = 0x12, ["win"] = 0x5B,
    };

    private static Dictionary<string, ushort> BuildKeys()
    {
        var k = new Dictionary<string, ushort>
        {
            ["enter"] = 0x0D, ["backspace"] = 0x08, ["tab"] = 0x09, ["esc"] = 0x1B, ["space"] = 0x20,
            ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28, ["delete"] = 0x2E,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pagedown"] = 0x22, ["win"] = 0x5B,
        };
        for (char c = 'a'; c <= 'z'; c++) k[c.ToString()] = (ushort)char.ToUpperInvariant(c);
        for (char c = '0'; c <= '9'; c++) k[c.ToString()] = c;
        for (int i = 1; i <= 12; i++) k["f" + i] = (ushort)(0x6F + i);
        return k;
    }

    // null = mensagem inválida (ignorada). Números fora de faixa são recusados, não "consertados".
    public static RemoteCommand? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            double Num(string name, double min, double max)
            {
                var v = r.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : double.NaN;
                return v >= min && v <= max ? v : throw new FormatException(name);
            }
            bool RightButton() => r.TryGetProperty("b", out var b) && b.GetString() == "right";
            return r.GetProperty("t").GetString() switch
            {
                "abs" => new RemoteCommand(RemoteKind.Abs, Num("x", 0, 1), Num("y", 0, 1)),
                "move" => new RemoteCommand(RemoteKind.Move, Num("dx", -MaxDelta, MaxDelta), Num("dy", -MaxDelta, MaxDelta)),
                "down" => new RemoteCommand(RemoteKind.Down, Right: RightButton()),
                "up" => new RemoteCommand(RemoteKind.Up, Right: RightButton()),
                "click" => new RemoteCommand(RemoteKind.Click, Right: RightButton(),
                    Count: r.TryGetProperty("n", out var n) && n.TryGetInt32(out var c) && c == 2 ? 2 : 1),
                "scroll" => new RemoteCommand(RemoteKind.Scroll, r.TryGetProperty("dx", out _) ? Num("dx", -MaxDelta, MaxDelta) : 0,
                    Num("dy", -MaxDelta, MaxDelta)),
                "text" => r.GetProperty("s").GetString() is { Length: > 0 and <= MaxText } s ? new RemoteCommand(RemoteKind.Text, Text: s) : null,
                "key" => ParseKey(r),
                _ => null,
            };
        }
        catch (Exception) { return null; }
    }

    private static RemoteCommand? ParseKey(JsonElement r)
    {
        if (!Keys.TryGetValue(r.GetProperty("k").GetString() ?? "", out var vk)) return null;
        var mods = new List<ushort>();
        if (r.TryGetProperty("mods", out var m))
            foreach (var x in m.EnumerateArray())
            {
                if (!Modifiers.TryGetValue(x.GetString() ?? "", out var mv)) return null;
                if (!mods.Contains(mv)) mods.Add(mv);
            }
        return new RemoteCommand(RemoteKind.Key, Vk: vk, Mods: mods.ToArray());
    }

    // Ponto físico de um Abs (0..1) dentro do monitor.
    public static Point ToScreen(double x, double y, Rectangle bounds) =>
        new(bounds.X + (int)Math.Round(x * (bounds.Width - 1)), bounds.Y + (int)Math.Round(y * (bounds.Height - 1)));

    // Sequência de teclas (vk, soltando?) de um atalho: modificadores descem, tecla, sobe na ordem inversa.
    public static (ushort Vk, bool Up)[] KeySequence(ushort vk, ushort[] mods) =>
        mods.Select(m => (m, false)).Append((vk, false)).Append((vk, true)).Concat(mods.Reverse().Select(m => (m, true))).ToArray();

    public static void Execute(RemoteCommand cmd, Rectangle bounds)
    {
        switch (cmd.Kind)
        {
            case RemoteKind.Abs:
                var p = ToScreen(cmd.X, cmd.Y, bounds);
                SetCursorPos(p.X, p.Y);
                break;
            case RemoteKind.Move:
                Send(Mouse(MOUSEEVENTF_MOVE, (int)cmd.X, (int)cmd.Y));
                break;
            case RemoteKind.Down:
                Send(Mouse(cmd.Right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN));
                break;
            case RemoteKind.Up:
                Send(Mouse(cmd.Right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP));
                break;
            case RemoteKind.Click:
                var (down, up) = cmd.Right ? (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP) : (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
                Send(Enumerable.Range(0, cmd.Count).SelectMany(_ => new[] { Mouse(down), Mouse(up) }).ToArray());
                break;
            case RemoteKind.Scroll:
                var wheel = new List<INPUT>();
                if (cmd.Y != 0) wheel.Add(Mouse(MOUSEEVENTF_WHEEL, data: (int)cmd.Y));
                if (cmd.X != 0) wheel.Add(Mouse(MOUSEEVENTF_HWHEEL, data: (int)cmd.X));
                Send(wheel.ToArray());
                break;
            case RemoteKind.Text:
                Send(cmd.Text.SelectMany(ch => new[] { Unicode(ch, false), Unicode(ch, true) }).ToArray());
                break;
            case RemoteKind.Key:
                Send(KeySequence(cmd.Vk, cmd.Mods ?? Array.Empty<ushort>())
                    .Select(k => Key(k.Vk, k.Up)).ToArray());
                break;
        }
    }

    // --- SendInput ---

    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_WHEEL = 0x800, MOUSEEVENTF_HWHEEL = 0x1000;
    private const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4, KEYEVENTF_EXTENDEDKEY = 0x1;

    private static INPUT Mouse(uint flags, int dx = 0, int dy = 0, int data = 0) =>
        new() { type = INPUT_MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = (uint)data, dwFlags = flags } } };

    private static INPUT Unicode(char ch, bool up) =>
        new() { type = INPUT_KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } } };

    // Setas, Home/End, PgUp/PgDn, Delete e Win são teclas "estendidas": sem a flag o Windows as lê como o teclado numérico.
    private static INPUT Key(ushort vk, bool up) =>
        new()
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (IsExtended(vk) ? KEYEVENTF_EXTENDEDKEY : 0) } },
        };

    private static bool IsExtended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2E or 0x5B;

    private static void Send(params INPUT[] inputs)
    {
        if (inputs.Length > 0) SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
}
