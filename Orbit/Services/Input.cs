using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Orbit.Services;

// Ctrl+V/Ctrl+C sintéticos (SendInput): colar no app que estava em foco e copiar a seleção dele.
public static class Input
{
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;

    // (tecla virtual, soltando?) — puro, testável.
    public static (ushort Vk, bool Up)[] CtrlVSequence() =>
        new (ushort, bool)[] { (0x11, false), (0x56, false), (0x56, true), (0x11, true) };

    public static (ushort Vk, bool Up)[] CtrlCSequence() =>
        new (ushort, bool)[] { (0x11, false), (0x43, false), (0x43, true), (0x11, true) };

    public static uint SendCtrlV() => Send(CtrlVSequence());

    // Copia a seleção do app em foco (Ctrl+Alt+I "manda o que selecionei"). Espera soltar Alt e a
    // tecla do atalho — senão o Ctrl+C sintético chega como Ctrl+Alt+C — e confirma pelo número de
    // sequência do clipboard. false = nada foi copiado (sem seleção): fica o que já estava lá.
    public static async Task<bool> CopySelectionAsync(ushort hotkeyVk)
    {
        for (int i = 0; i < 50 && (IsDown(0x12) || IsDown(hotkeyVk)); i++) await Task.Delay(20);
        uint before = GetClipboardSequenceNumber();
        Send(CtrlCSequence());
        for (int i = 0; i < 25; i++)
        {
            await Task.Delay(20);
            if (GetClipboardSequenceNumber() != before) return true;
        }
        return false;
    }

    private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    private static uint Send((ushort Vk, bool Up)[] keys)
    {
        var inputs = keys.Select(k => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = k.Vk, dwFlags = k.Up ? KEYEVENTF_KEYUP : 0 } },
        }).ToArray();
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
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
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
}
