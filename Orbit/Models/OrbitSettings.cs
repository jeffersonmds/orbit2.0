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

    // Mercado: ids selecionados, na ordem escolhida.
    public List<string> MarketCryptos { get; set; } = new() { "bitcoin", "ethereum" };
    public List<string> MarketCurrencies { get; set; } = new() { "USD", "EUR" };

    // Notícias: ids do HN ignorados (não reaparecem). Limitado a 200 pelo Prune.
    public List<int> DismissedNewsIds { get; set; } = new();

    // Em tela cheia, alerta vira toast do Windows em vez de esperar na fila.
    public bool AlertsAsToastInFullscreen { get; set; }

    // Pomodoro (defaults = clássico).
    public int PomodoroFocusMinutes { get; set; } = 25;
    public int PomodoroShortBreakMinutes { get; set; } = 5;
    public int PomodoroLongBreakMinutes { get; set; } = 15;
    public int PomodoroCyclesUntilLongBreak { get; set; } = 4;
    public bool PomodoroSoundEnabled { get; set; } = true;
}
