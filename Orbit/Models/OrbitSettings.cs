namespace Orbit.Models;

// POCO serializado em settings.json. Novos campos sempre com default.
public sealed class OrbitSettings
{
    // Id da última aba aberta no painel expandido.
    public string LastTab { get; set; } = "energia";

    // "#RRGGBB" escolhido em Ajustes; null = cor de destaque do Windows.
    public string? AccentOverride { get; set; }
}
