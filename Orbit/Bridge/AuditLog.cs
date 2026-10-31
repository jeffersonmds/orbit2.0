using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Orbit.Services;

namespace Orbit.Bridge;

public sealed record AuditEntry(DateTime When, string Token, string Action, string Result);

// Uma linha JSON por ação em audit.jsonl + as últimas N em memória (aba Dispositivos).
// Write roda em threads do Kestrel: tudo sob lock; Written dispara fora da UI thread.
public sealed class AuditLog
{
    public static readonly string FilePath = Path.Combine(SettingsStore.Dir, "audit.jsonl");
    private const int RecentCount = 30;
    private const long MaxBytes = 5 * 1024 * 1024; // ponytail: rotação simples (1 arquivo .old)

    private readonly string _path;
    private readonly object _gate = new();
    private readonly LinkedList<AuditEntry> _recent = new();

    public event Action<AuditEntry>? Written;

    public AuditLog(string path) => _path = path;

    public void Write(string token, string action, string result)
    {
        var entry = new AuditEntry(DateTime.Now, token, action, result);
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                    File.Move(_path, _path + ".old", overwrite: true);
                File.AppendAllText(_path, FormatLine(entry) + "\n");
            }
            catch
            {
                // sem disco: fica só em memória
            }
            _recent.AddFirst(entry);
            if (_recent.Count > RecentCount) _recent.RemoveLast();
        }
        Written?.Invoke(entry);
    }

    public IReadOnlyList<AuditEntry> RecentEntries()
    {
        lock (_gate) return _recent.ToList();
    }

    public static string FormatLine(AuditEntry e) => JsonSerializer.Serialize(new
    {
        quando = e.When.ToString("o"),
        token = e.Token,
        acao = e.Action,
        resultado = e.Result,
    });
}
