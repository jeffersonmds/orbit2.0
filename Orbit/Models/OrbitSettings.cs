using System.Collections.Generic;

namespace Orbit.Models;

// POCO serializado em settings.json. Novos campos sempre com default.
public sealed class OrbitSettings
{
    // Id da última aba aberta no painel expandido.
    public string LastTab { get; set; } = "energia";

    // "#RRGGBB" escolhido em Ajustes; null = cor de destaque do Windows.
    public string? AccentOverride { get; set; }

    // Abas escondidas em Ajustes (ids). "ajustes" nunca é escondida.
    public List<string> HiddenTabs { get; set; } = new();

    // Em tela cheia, alerta vira toast do Windows em vez de esperar na fila.
    public bool AlertsAsToastInFullscreen { get; set; }
}
