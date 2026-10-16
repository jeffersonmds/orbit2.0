using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace Orbit.Services;

// Ctrl+V sintético (SendInput) pra colar no app que estava em foco.
public static class Input
{
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;

    // (tecla virtual, soltando?) — puro, testável.
    public static (ushort Vk, bool Up)[] CtrlVSequence() =>
        new (ushort, bool)[] { (0x11, false), (0x56, false), (0x56, true), (0x11, true) };

    public static void SendCtrlV()
    {
        var inputs = CtrlVSequence().Select(k => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = k.Vk, dwFlags = k.Up ? KEYEVENTF_KEYUP : 0 } },
        }).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // A união precisa do MOUSEINPUT (o maior membro) pro tamanho bater com o nativo em x64.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
