using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Orbit.Services;

public enum ClaudeSessionState { Running, WaitingInput, NeedsAttention }

// HostPid: processo com janela onde o Claude roda (Windows Terminal, VS Code…). Seen: você já focou
// essa janela depois do último aviso — sai da pílula (continua na aba), como abrir o app no aparelho.
public sealed record ClaudeSessionInfo(string SessionId, string ProjectName, ClaudeSessionState State, string? Message,
    int? HostPid = null, bool Seen = false, long? ContextTokens = null, bool Asking = false, bool CanAlways = false)
{
    public string? ContextText => ContextTokens is { } tokens ? $"{tokens / 1000.0:0}k de contexto" : null;

    // Mostrado sempre no tile pra diferenciar sessões do mesmo projeto sem precisar do session_id inteiro.
    public string ShortSessionId => SessionId.Length <= 6 ? SessionId : SessionId[..6];

    public string StatusText => State switch
    {
        ClaudeSessionState.Running => "Rodando",
        ClaudeSessionState.WaitingInput => "Aguardando você",
        ClaudeSessionState.NeedsAttention => Message is { Length: > 0 } ? Message : "Precisa de atenção",
        _ => "",
    };
}

// PermissionRequest traz ToolName/ToolDetail (o que o Claude quer fazer) e Suggestions (JSON cru do
// permission_suggestions: o "sempre permitir" que o próprio Claude propõe).
public sealed record ClaudeHookEvent(string HookEventName, string SessionId, string Cwd, string? Message, int? HostPid = null,
    string? TranscriptPath = null, string? ToolName = null, string? ToolDetail = null, string? Suggestions = null)
{
    public static ClaudeHookEvent Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string eventName = root.TryGetProperty("hook_event_name", out var e) ? e.GetString() ?? "" : "";
        string sessionId = root.TryGetProperty("session_id", out var s) ? s.GetString() ?? "" : "";
        string cwd = root.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "";
        string? message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
        int? host = root.TryGetProperty(ClaudeHookClient.HostPidField, out var h) && h.TryGetInt32(out var hp) ? hp : null;
        string? transcript = root.TryGetProperty("transcript_path", out var t) ? t.GetString() : null;
        string? tool = root.TryGetProperty("tool_name", out var tn) ? tn.GetString() : null;
        string? detail = root.TryGetProperty("tool_input", out var ti) ? Detail(ti) : null;
        string? sugg = root.TryGetProperty("permission_suggestions", out var ps) && ps.ValueKind == JsonValueKind.Array
            && ps.GetArrayLength() > 0 ? ps.GetRawText() : null;
        return new ClaudeHookEvent(eventName, sessionId, cwd, message, host, transcript, tool, detail, sugg);
    }

    // O campo que diz o que a ferramenta vai fazer (comando, arquivo, URL…), numa linha curta.
    private static string? Detail(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "command", "file_path", "url", "pattern", "query", "description", "prompt" })
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } text)
            {
                text = text.ReplaceLineEndings(" ").Trim();
                return text.Length > 120 ? text[..120] + "…" : text;
            }
        return null;
    }

    public string PermissionText => ToolDetail is { } d ? $"Permitir {ToolName}: {d}" : $"Permitir {ToolName}?";
}

public enum PermissionAnswer { Allow, AlwaysAllow, Deny }

public static class ClaudePermission
{
    // Saída do hook PermissionRequest (stdout). null = sem decisão ("" no stdout): o terminal mostra o
    // diálogo de sempre. "Sempre" aplica as sugestões do próprio Claude (updatedPermissions).
    public static string Response(PermissionAnswer? answer, string? suggestions)
    {
        if (answer is null) return "";
        var decision = new JsonObject { ["behavior"] = answer == PermissionAnswer.Deny ? "deny" : "allow" };
        if (answer == PermissionAnswer.Deny) decision["message"] = "Negado pela ilha do Orbit.";
        if (answer == PermissionAnswer.AlwaysAllow && suggestions is not null) decision["updatedPermissions"] = JsonNode.Parse(suggestions);
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = "PermissionRequest", ["decision"] = decision },
        }.ToJsonString();
    }
}

public static class ClaudeSessionReducer
{
    // Só SessionStart cria uma sessão nova; os demais eventos só atualizam uma
    // sessão já conhecida. Se o Orbit abriu depois do SessionStart de uma sessão,
    // os eventos dela são ignorados até ela terminar e uma nova começar — sem
    // estado persistido entre reinícios do Orbit (YAGNI, ver spec).
    public static void Apply(Dictionary<string, ClaudeSessionInfo> sessions, ClaudeHookEvent evt)
    {
        if (string.IsNullOrEmpty(evt.SessionId)) return;

        if (evt.HookEventName == "SessionEnd")
        {
            sessions.Remove(evt.SessionId);
            return;
        }

        if (evt.HookEventName == "SessionStart")
        {
            sessions[evt.SessionId] = new ClaudeSessionInfo(
                evt.SessionId, ProjectName(evt.Cwd, evt.SessionId, sessions), ClaudeSessionState.Running, null, evt.HostPid);
            return;
        }

        if (!sessions.TryGetValue(evt.SessionId, out var current)) return;

        var host = evt.HostPid ?? current.HostPid;
        sessions[evt.SessionId] = evt.HookEventName switch
        {
            "UserPromptSubmit" => current with { State = ClaudeSessionState.Running, Message = null, HostPid = host, Seen = false },
            // aviso novo: volta pra pílula até você focar a janela de novo
            "Stop" => current with { State = ClaudeSessionState.WaitingInput, Message = null, HostPid = host, Seen = false },
            "Notification" => current with { State = ClaudeSessionState.NeedsAttention, Message = evt.Message, HostPid = host, Seen = false },
            "PermissionRequest" => current with { State = ClaudeSessionState.NeedsAttention, Message = evt.PermissionText, HostPid = host, Seen = false },
            _ => current with { HostPid = host },
        };
    }

    // Você focou a janela do processo pid: as sessões aguardando dali contam como vistas.
    // true = alguma mudou (pra republicar a pílula).
    public static bool MarkSeen(Dictionary<string, ClaudeSessionInfo> sessions, int pid)
    {
        bool changed = false;
        foreach (var (id, s) in sessions.ToList())
            if (s.HostPid == pid && s.State != ClaudeSessionState.Running && !s.Seen)
            {
                sessions[id] = s with { Seen = true };
                changed = true;
            }
        return changed;
    }

    // Stop e Notification são os únicos eventos que valem um toast (ver spec); PermissionRequest tem
    // aviso próprio, com botões.
    public static bool ShouldNotify(string hookEventName) => hookEventName is "Stop" or "Notification";

    // Nome do projeto = pasta do cwd; se outra sessão já usa o mesmo nome, sufixa
    // com os 6 primeiros caracteres do session_id pra distinguir.
    private static string ProjectName(string cwd, string sessionId, Dictionary<string, ClaudeSessionInfo> existing)
    {
        string trimmed = cwd.TrimEnd('\\', '/');
        string name = string.IsNullOrEmpty(trimmed) ? "?" : (Path.GetFileName(trimmed) is { Length: > 0 } n ? n : trimmed);

        bool duplicate = false;
        foreach (var s in existing.Values)
            if (s.SessionId != sessionId && s.ProjectName == name) { duplicate = true; break; }

        return duplicate ? $"{name} ({sessionId[..Math.Min(6, sessionId.Length)]})" : name;
    }
}
