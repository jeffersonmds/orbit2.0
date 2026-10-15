using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace Orbit.Services;

public static class ClaudeHookInstaller
{
    private static readonly string[] Events =
        { "SessionStart", "UserPromptSubmit", "Stop", "Notification", "SessionEnd" };

    public static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // Garante um hook de comando em cada um dos 5 eventos, sem duplicar e sem
    // apagar hooks já existentes (ex.: rtk, statusline). Muta `root` in-place.
    // Pura o bastante pra testar sem tocar em disco.
    public static void MergeHooks(JsonObject root, string command)
    {
        if (root["hooks"] is not JsonObject hooksObj)
        {
            if (root["hooks"] is not null)
                Log.Warning("ClaudeHookInstaller: valor inesperado em \"hooks\" ({Kind}) foi substituído", root["hooks"]!.GetType().Name);
            hooksObj = new JsonObject();
            root["hooks"] = hooksObj;
        }

        foreach (var evt in Events)
        {
            if (hooksObj[evt] is not JsonArray matchers)
            {
                if (hooksObj[evt] is not null)
                    Log.Warning("ClaudeHookInstaller: valor inesperado em \"hooks.{Evento}\" ({Kind}) foi substituído", evt, hooksObj[evt]!.GetType().Name);
                matchers = new JsonArray();
                hooksObj[evt] = matchers;
            }

            RemoveStale(matchers, command);
            if (MatchersHaveCommand(matchers, command)) continue;

            matchers.Add(new JsonObject
            {
                ["hooks"] = new JsonArray
                {
                    new JsonObject { ["type"] = "command", ["command"] = command },
                },
            });
        }
    }

    // ponytail: dedup por string exata do comando; se o exe mudar de path, a
    // entrada antiga fica órfã — adicionar match por id/marker se isso virar problema.
    // Remove hooks "--claude-hook" de OUTRO executável (Orbit 1.0, ou build antigo em outro
    // caminho): sem isso o mesmo evento chegaria duas vezes ao pipe. Matcher que fica vazio sai.
    private static void RemoveStale(JsonArray matchers, string command)
    {
        for (int i = matchers.Count - 1; i >= 0; i--)
        {
            if (matchers[i] is not JsonObject mObj || mObj["hooks"] is not JsonArray inner) continue;
            for (int j = inner.Count - 1; j >= 0; j--)
            {
                if (inner[j] is JsonObject h && h["command"] is JsonValue v && v.TryGetValue<string>(out var c)
                    && c.EndsWith(" --claude-hook", StringComparison.Ordinal) && c != command)
                    inner.RemoveAt(j);
            }
            if (inner.Count == 0) matchers.RemoveAt(i);
        }
    }

    private static bool MatchersHaveCommand(JsonArray matchers, string command)
    {
        foreach (var m in matchers)
        {
            if (m is not JsonObject mObj || mObj["hooks"] is not JsonArray innerHooks) continue;
            foreach (var h in innerHooks)
                if (h is JsonObject hObj && hObj["command"] is JsonValue cmdVal && cmdVal.GetValue<string>() == command)
                    return true;
        }
        return false;
    }

    // Pura: true só se TODOS os 5 eventos já têm esse comando registrado.
    // Usada pra mostrar na tela se a integração está de fato ativa.
    public static bool HasAllHooks(JsonObject root, string command)
    {
        if (root["hooks"] is not JsonObject hooksObj) return false;
        foreach (var evt in Events)
            if (hooksObj[evt] is not JsonArray matchers || !MatchersHaveCommand(matchers, command))
                return false;
        return true;
    }

    // I/O: lê/cria ~/.claude/settings.json, aplica o merge, salva. Nunca lança —
    // mesma filosofia do SettingsStore. Falha silenciosa: o botão "Ativar" pode
    // ser clicado de novo sem problema.
    public static void Install(string exePath)
    {
        try
        {
            JsonObject root = File.Exists(SettingsPath)
                ? (JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject())
                : new JsonObject();

            MergeHooks(root, $"\"{exePath}\" --claude-hook");

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, root.ToJsonString(WriteOptions));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClaudeHookInstaller: falha ao instalar hooks em {Path}", SettingsPath);
        }
    }

    // I/O: mesma leitura do Install, sem escrever nada. Nunca lança.
    public static bool IsInstalled(string exePath)
    {
        try
        {
            if (!File.Exists(SettingsPath)) return false;
            var root = JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject;
            return root is not null && HasAllHooks(root, $"\"{exePath}\" --claude-hook");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClaudeHookInstaller: falha ao checar hooks em {Path}", SettingsPath);
            return false;
        }
    }
}
