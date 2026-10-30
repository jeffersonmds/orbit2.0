using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Serilog;

namespace Orbit.Services;

public static class ClaudeHookInstaller
{
    public const string StatusArg = "--claude-status";

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
                    && c.EndsWith("Orbit.exe\" --claude-hook", StringComparison.OrdinalIgnoreCase) && c != command)
                    inner.RemoveAt(j);
            }
            if (inner.Count == 0) matchers.RemoveAt(i);
        }
    }

    // Dedup por string exata do comando (exe em outro caminho é limpo pelo RemoveStale).
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

    // Só existe um slot de status line: o Orbit entra na frente e leva a anterior em base64 no próprio
    // comando, o que permite executá-la (ClaudeHookClient) e restaurá-la sem arquivo à parte.
    public static void WrapStatusLine(JsonObject root, string exePath)
    {
        string current = StatusCommand(root);
        string previous = PreviousOf(current) ?? current; // já é nosso (outro caminho): não embrulha duas vezes
        string command = $"\"{exePath}\" {StatusArg}";
        if (previous.Length > 0) command += " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(previous));

        if (root["statusLine"] is not JsonObject line) root["statusLine"] = line = new JsonObject();
        line["type"] = "command";
        line["command"] = command;
    }

    public static void UnwrapStatusLine(JsonObject root)
    {
        if (PreviousOf(StatusCommand(root)) is not { } previous) return;
        if (previous.Length == 0) root.Remove("statusLine");
        else root["statusLine"]!["command"] = previous;
    }

    public static bool HasStatusLine(JsonObject root, string exePath) =>
        StatusCommand(root).StartsWith($"\"{exePath}\" {StatusArg}", StringComparison.Ordinal);

    public static string DecodePrevious(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        try { return Encoding.UTF8.GetString(Convert.FromBase64String(base64)); }
        catch (FormatException) { return ""; }
    }

    // Comando anterior embrulhado em `command`; null se esse comando não é do Orbit.
    private static string? PreviousOf(string command)
    {
        string marker = $"Orbit.exe\" {StatusArg}";
        int at = command.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : DecodePrevious(command[(at + marker.Length)..].Trim());
    }

    private static string StatusCommand(JsonObject root) =>
        root["statusLine"] is JsonObject line && line["command"] is JsonValue v && v.TryGetValue<string>(out var c) ? c : "";

    // I/O: lê/cria ~/.claude/settings.json, aplica o merge, salva. Nunca lança —
    // mesma filosofia do SettingsStore. Falha silenciosa: o botão "Ativar" pode
    // ser clicado de novo sem problema.
    public static void Install(string exePath) => Edit(root => MergeHooks(root, $"\"{exePath}\" --claude-hook"));

    public static void InstallStatusLine(string exePath) => Edit(root => WrapStatusLine(root, exePath));

    public static void RemoveStatusLine() => Edit(UnwrapStatusLine);

    private static void Edit(Action<JsonObject> change)
    {
        try
        {
            JsonObject root = File.Exists(SettingsPath)
                ? (JsonNode.Parse(File.ReadAllText(SettingsPath)) as JsonObject ?? new JsonObject())
                : new JsonObject();

            change(root);

            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, root.ToJsonString(WriteOptions));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClaudeHookInstaller: falha ao gravar {Path}", SettingsPath);
        }
    }

    public static bool IsStatusLineInstalled(string exePath)
    {
        try
        {
            return File.Exists(SettingsPath) && JsonNode.Parse(File.ReadAllText(SettingsPath)) is JsonObject root
                && HasStatusLine(root, exePath);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ClaudeHookInstaller: falha ao checar status line em {Path}", SettingsPath);
            return false;
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
