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

    // Apps fixados (ids do shell:AppsFolder), na ordem em que aparecem.
    public List<string> FavoriteApps { get; set; } = new();

    // Mercado: ids selecionados, na ordem escolhida.
    public List<string> MarketCryptos { get; set; } = new() { "bitcoin", "ethereum" };
    public List<string> MarketCurrencies { get; set; } = new() { "USD", "EUR" };

    // Notícias: ids do HN ignorados (não reaparecem). Limitado a 200 pelo Prune.
    public List<int> DismissedNewsIds { get; set; } = new();

    // Som nos alertas do Claude Code (Stop/Notification).
    public bool ClaudeSoundEnabled { get; set; } = true;

    // Em tela cheia, alerta vira toast do Windows em vez de esperar na fila.
    public bool AlertsAsToastInFullscreen { get; set; }

    // Volume, carregador, saída de som, Caps Lock e internet animando na pílula.
    public bool SystemSignalsEnabled { get; set; } = true;

    // Print novo (pasta Capturas de tela): a pílula oferece mandar pro aparelho.
    public bool ScreenshotOfferEnabled { get; set; } = true;

    // Aviso de chuva chegando (ilha + aparelhos), pela previsão de 15 em 15 min.
    public bool RainAlertEnabled { get; set; } = true;

    // Pomodoro (defaults = clássico).
    public int PomodoroFocusMinutes { get; set; } = 25;
    public int PomodoroShortBreakMinutes { get; set; } = 5;
    public int PomodoroLongBreakMinutes { get; set; } = 15;
    public int PomodoroCyclesUntilLongBreak { get; set; } = 4;
    public bool PomodoroSoundEnabled { get; set; } = true;

    // Ponte com o aparelho (desligada até o usuário ligar na aba Dispositivos).
    public bool BridgeEnabled { get; set; }
    public int BridgePort { get; set; } = 5191;
    public string BridgePublicUrl { get; set; } = "";       // ex.: https://notebook.tail1234.ts.net
    public string? BridgeUploadFolder { get; set; }         // null = Downloads\Orbit

    // Câmera pelo aparelho: desligada mesmo com a ponte ligada, até o usuário ligar na aba.
    public bool BridgeCameraEnabled { get; set; }

    // Arquivos do PC pelo aparelho (só leitura, pastas do usuário): desligado até ligar na aba.
    public bool BridgePcFilesEnabled { get; set; }

    // Tela remota pelo aparelho: ver (Tela) e mouse/teclado (Controle, só com Tela). Desligados por padrão.
    public bool BridgeScreenEnabled { get; set; }
    public bool BridgeControlEnabled { get; set; }

    // Microfone do PC no aparelho (ouvir com ou sem a câmera). Desligado por padrão.
    public bool BridgeMicEnabled { get; set; }

    // Teclado remoto (escrever/ditar no aparelho → app em foco no PC). Desligado por padrão.
    public bool BridgeKeyboardEnabled { get; set; }

    // Monitor da ilha (Screen.DeviceName, ex.: \\.\DISPLAY2). null = sempre o principal;
    // desconectado = principal até ele voltar.
    public string? IslandMonitor { get; set; }
}
