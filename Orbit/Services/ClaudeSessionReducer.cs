using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Orbit.Services;

public enum ClaudeSessionState { Running, WaitingInput, NeedsAttention }

// HostPid: processo com janela onde o Claude roda (Windows Terminal, VS Code…). Seen: você já focou
// essa janela depois do último aviso — sai da pílula (continua na aba), como abrir o app no iPhone.
public sealed record ClaudeSessionInfo(string SessionId, string ProjectName, ClaudeSessionState State, string? Message,
    int? HostPid = null, bool Seen = false, double? ContextPct = null)
{
    public string? ContextText => ContextPct is { } pct ? $"Contexto {pct:0}%" : null;

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

public sealed record ClaudeHookEvent(string HookEventName, string SessionId, string Cwd, string? Message, int? HostPid = null,
    ClaudeUsage? Usage = null)
{
    // O JSON da status line usa o mesmo pipe e é o único sem hook_event_name.
    public const string StatusLine = "StatusLine";

    public static ClaudeHookEvent Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string eventName = root.TryGetProperty("hook_event_name", out var e) ? e.GetString() ?? "" : StatusLine;
        string sessionId = root.TryGetProperty("session_id", out var s) ? s.GetString() ?? "" : "";
        string cwd = root.TryGetProperty("cwd", out var c) ? c.GetString() ?? "" : "";
        string? message = root.TryGetProperty("message", out var m) ? m.GetString() : null;
        int? host = root.TryGetProperty(ClaudeHookClient.HostPidField, out var h) && h.TryGetInt32(out var hp) ? hp : null;
        var usage = eventName == StatusLine ? ClaudeUsage.Parse(root) : null;
        return new ClaudeHookEvent(eventName, sessionId, cwd, message, host, usage);
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
            ClaudeHookEvent.StatusLine => current with { ContextPct = evt.Usage?.ContextPct ?? current.ContextPct },
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
