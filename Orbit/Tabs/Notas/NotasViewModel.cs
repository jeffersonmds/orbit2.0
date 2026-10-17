using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Orbit.Services;

namespace Orbit.Tabs.Notas;

// Bloco de notas rápido: um texto só, em %AppData%\Orbit2\notes.txt.
public sealed partial class NotasViewModel : TabViewModel
{
    public static readonly string FilePath = Path.Combine(SettingsStore.Dir, "notes.txt");

    private readonly string _path;

    public override string Id => "notas";
    public override string Title => "Notas";

    [ObservableProperty] private string _text;

    public NotasViewModel(string path)
    {
        _path = path;
        try { _text = File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { _text = ""; }
    }

    // ponytail: grava a cada tecla (arquivo pequeno, nada se perde num crash); debounce se o texto ficar enorme.
    partial void OnTextChanged(string value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, value);
        }
        catch
        {
            // disco cheio / sem permissão: não vale crashar a ilha por causa de nota.
        }
    }
}
