using System.Collections.Generic;
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

    public IReadOnlyList<AccentOption> Opcoes { get; }

    public AjustesViewModel(OrbitSettings settings)
    {
        _settings = settings;
        var sistema = new AccentOption("Windows", null, new SolidColorBrush(Accent.ReadSystem()));
        Opcoes = new[] { sistema }
            .Concat(Accent.Paleta.Select(p => new AccentOption(p.Nome, p.Hex, new SolidColorBrush(Accent.ParseHex(p.Hex)!.Value))))
            .ToList();
    }

    [RelayCommand]
    private void EscolherAcento(AccentOption opcao)
    {
        _settings.AccentOverride = opcao.Hex;
        SettingsStore.Save(_settings);
        Accent.Refresh();
    }
}
