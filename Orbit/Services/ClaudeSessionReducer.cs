using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Orbit.Services;

public enum ClaudeSessionState { Running, WaitingInput, NeedsAttention }

public sealed record ClaudeSessionInfo(string SessionId, string ProjectName, ClaudeSessionState State, string? Message)
{
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

public sealed record ClaudeHookEvent(string HookEventName, string SessionId, string Cwd, string? Message)
{
    public static ClaudeHookEvent Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string eventName = root.TryGetProperty("hook_event_name", out var e) ? e.GetString() ?? "" : "";
        string sessionId = root.TryGetProperty("session_id", out var s) ? s.GetString() ?? "" : "";
        string cwd = root.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "";
        string? message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
        return new ClaudeHookEvent(eventName, sessionId, cwd, message);
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
                evt.SessionId, ProjectName(evt.Cwd, evt.SessionId, sessions), ClaudeSessionState.Running, null);
            return;
        }

        if (!sessions.TryGetValue(evt.SessionId, out var current)) return;

        sessions[evt.SessionId] = evt.HookEventName switch
        {
            "UserPromptSubmit" => current with { State = ClaudeSessionState.Running, Message = null },
            "Stop" => current with { State = ClaudeSessionState.WaitingInput, Message = null },
            "Notification" => current with { State = ClaudeSessionState.NeedsAttention, Message = evt.Message },
            _ => current,
        };
    }

    // Stop e Notification são os únicos eventos que valem um toast (ver spec).
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
