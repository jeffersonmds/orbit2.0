using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Orbit.Services;

namespace Orbit.Tabs.Hoje;

// To-dos em %AppData%\Orbit2\todos.json. Mesma filosofia do SettingsStore: nunca lança.
public static class TodoStore
{
    public static readonly string FilePath = Path.Combine(SettingsStore.Dir, "todos.json");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    // DTO separado: o TodoItem é ObservableObject e não precisa saber de JSON.
    private sealed record TodoDto(string? Text, bool Done);

    public static List<TodoItem> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var dtos = JsonSerializer.Deserialize<List<TodoDto>>(File.ReadAllText(path));
            return dtos?.Select(d => new TodoItem(d.Text ?? "", d.Done)).ToList() ?? new();
        }
        catch
        {
            return new(); // corrompido/I/O → lista vazia
        }
    }

    public static void Save(IEnumerable<TodoItem> items, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dtos = items.Select(i => new TodoDto(i.Text, i.Done)).ToList();
            File.WriteAllText(path, JsonSerializer.Serialize(dtos, WriteOptions));
        }
        catch
        {
            // disco cheio / sem permissão: não vale crashar a ilha por causa de to-do.
        }
    }
}
