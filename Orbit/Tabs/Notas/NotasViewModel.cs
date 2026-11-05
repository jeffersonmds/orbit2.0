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
    // Última gravação falhou (disco cheio, arquivo bloqueado): a aba avisa e a ponte responde erro.
    [ObservableProperty] private string? _saveError;

    public NotasViewModel(string path)
    {
        _path = path;
        try { _text = File.Exists(path) ? File.ReadAllText(path) : ""; }
        catch { _text = ""; }
    }

    // ponytail: grava a cada tecla (arquivo pequeno, nada se perde num crash); debounce se o texto ficar enorme.
    partial void OnTextChanged(string value) => Save(value);

    // A ponte grava sempre, mesmo com o texto igual: depois de uma falha (disco cheio), reenviar
    // o mesmo texto tem que tentar de novo (o setter ignora valor igual).
    public void Write(string value)
    {
        if (Text == value) Save(value);
        else Text = value;
    }

    private void Save(string value)
    {
        try
        {
            AtomicFile.WriteAllText(_path, value);
            SaveError = null;
        }
        catch (System.Exception ex)
        {
            Serilog.Log.Warning(ex, "Notas: não salvou");
            // disco cheio / sem permissão: não vale crashar a ilha por causa de nota, mas avisa.
            SaveError = "Não consegui salvar as notas no disco — o texto está só na memória.";
        }
    }
}
