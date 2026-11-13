using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Serilog;

namespace Orbit.Services;

// "Essa tela cheia é um jogo?" Na dúvida, NÃO: filme, série, apresentação e navegador escondem a ilha
// como sempre. É jogo se o exe está na lista de jogos que o próprio Windows mantém (Game Bar,
// GameConfigStore) ou numa pasta de loja de jogos (Steam, Epic, Xbox…).
public static class GameDetector
{
    private const string Store = @"System\GameConfigStore\Children";

    // Players e navegadores: tela cheia deles é vídeo, mesmo que alguém os marque como jogo.
    private static readonly HashSet<string> VideoApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "arc", "zen",
        "vlc", "mpv", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "potplayer", "potplayermini", "potplayermini64",
        "wmplayer", "video.ui", "microsoft.media.player", "applicationframehost", "stremio", "plex", "kodi",
        "netflix", "disneyplus", "primevideo", "powerpnt", "obs64",
    };

    private static readonly string[] GameFolders =
    {
        @"\steamapps\common\", @"\epic games\", @"\xboxgames\", @"\riot games\", @"\gog galaxy\games\",
        @"\ea games\", @"\ubisoft game launcher\games\", @"\battle.net\",
        // ponytail: WindowsApps guarda jogos do Game Pass e também apps da loja; os de vídeo (Netflix, Filmes)
        // aparecem como ApplicationFrameHost, barrado acima. Outro app da loja em tela cheia vira faixa.
        @"\windowsapps\",
    };

    // Janela em primeiro plano → é jogo? Lê a lista do Windows a cada tela cheia (evento raro).
    public static bool IsGameWindow(IntPtr hwnd)
    {
        try
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            var exe = AudioMixer.ExePath(pid);
            bool game = IsGame(exe, KnownGames());
            Log.Information("Tela cheia: {Exe} → {Tipo}", exe is null ? "?" : Path.GetFileName(exe), game ? "jogo" : "esconde");
            return game;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Tela cheia: não identifiquei o app");
            return false;
        }
    }

    public static bool IsGame(string? exePath, ICollection<string> knownGames)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        if (VideoApps.Contains(Path.GetFileNameWithoutExtension(exePath))) return false;
        if (knownGames.Contains(exePath)) return true;
        var lower = exePath.ToLowerInvariant();
        return GameFolders.Any(lower.Contains);
    }

    // Caminhos dos jogos que o Windows reconheceu (Game Bar). Só os que têm o exe completo.
    public static HashSet<string> KnownGames()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var root = Registry.CurrentUser.OpenSubKey(Store);
        if (root is null) return set;
        foreach (var name in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(name);
            if (k?.GetValue("MatchedExeFullPath") is string p && p.Length > 0) set.Add(p);
        }
        return set;
    }

    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
}
