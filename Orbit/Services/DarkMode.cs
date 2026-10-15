using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Orbit.Services;

// Modo escuro do Windows (apps + sistema) pelo registro do usuário + aviso aos apps abertos.
public static class DarkMode
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    private const uint WM_SETTINGCHANGE = 0x001A, SMTO_ABORTIFHUNG = 0x0002;

    public static bool IsDark()
    {
        using var k = Registry.CurrentUser.OpenSubKey(Key);
        return k?.GetValue("AppsUseLightTheme") is int v && v == 0;
    }

    public static int RegistryValue(bool dark) => dark ? 0 : 1;

    // Bloqueia até ~200 ms por janela travada (SMTO_ABORTIFHUNG pula as travadas):
    // chame fora da UI thread.
    public static void Set(bool dark)
    {
        using (var k = Registry.CurrentUser.CreateSubKey(Key))
        {
            k.SetValue("AppsUseLightTheme", RegistryValue(dark), RegistryValueKind.DWord);
            k.SetValue("SystemUsesLightTheme", RegistryValue(dark), RegistryValueKind.DWord);
        }
        SendMessageTimeout(HWND_BROADCAST, WM_SETTINGCHANGE, IntPtr.Zero, "ImmersiveColorSet",
            SMTO_ABORTIFHUNG, 200, out _);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
