using System;
using System.IO;
using System.Text.Json;
using Orbit.Models;

namespace Orbit.Services;

public static class SettingsStore
{
    // Pasta própria do 2.0 — convive com o %AppData%\Orbit do 1.0.
    public static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Orbit2");

    public static readonly string FilePath = Path.Combine(Dir, "settings.json");
    public static readonly string LogsDir = Path.Combine(Dir, "logs");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // path só é passado pelos testes; o app usa sempre FilePath.
    public static OrbitSettings Load(string? path = null)
    {
        try
        {
            var p = path ?? FilePath;
            if (!File.Exists(p)) return new OrbitSettings();
            var s = JsonSerializer.Deserialize<OrbitSettings>(File.ReadAllText(p)) ?? new OrbitSettings();
            MigrateTabIds(s); // em memória; o próximo Save grava
            return s;
        }
        catch
        {
            return new OrbitSettings(); // faltando/corrompido/I/O → defaults
        }
    }

    // A aba "iphone" virou "dispositivos" (multi-dispositivos).
    public static void MigrateTabIds(OrbitSettings s)
    {
        if (s.LastTab == "iphone") s.LastTab = "dispositivos";
        if (s.HiddenTabs.Remove("iphone") && !s.HiddenTabs.Contains("dispositivos")) s.HiddenTabs.Add("dispositivos");
    }

    public static void Save(OrbitSettings s, string? path = null)
    {
        try
        {
            var p = path ?? FilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(s, WriteOptions));
        }
        catch
        {
            // disco cheio / sem permissão: não vale crashar a ilha por causa de settings.
        }
    }
}
