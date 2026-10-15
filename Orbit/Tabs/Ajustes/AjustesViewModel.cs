using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Media;
using CommunityToolkit.Mvvm.Input;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Tabs.Ajustes;

// Hex null = "usar a cor do Windows".
public sealed record AccentOption(string Nome, string? Hex, Brush Amostra);

public sealed partial class AjustesViewModel : TabViewModel
{
    private readonly OrbitSettings _settings;

    public override string Id => "ajustes";
    public override string Title => "Ajustes";

    public ObservableCollection<AccentOption> Opcoes { get; }

    public AjustesViewModel(OrbitSettings settings)
    {
        _settings = settings;
        Opcoes = new ObservableCollection<AccentOption>(new[] { SystemOption() }
            .Concat(Accent.Paleta.Select(p => new AccentOption(p.Nome, p.Hex, new SolidColorBrush(Accent.ParseHex(p.Hex)!.Value)))));
        // A amostra "Windows" acompanha a cor do sistema (antes ficava congelada na abertura).
        Accent.Changed += () => Opcoes[0] = SystemOption();
    }

    private static AccentOption SystemOption() => new("Windows", null, new SolidColorBrush(Accent.ReadSystem()));

    [RelayCommand]
    private void EscolherAcento(AccentOption opcao)
    {
        _settings.AccentOverride = opcao.Hex;
        SettingsStore.Save(_settings);
        Accent.Refresh();
    }
}
