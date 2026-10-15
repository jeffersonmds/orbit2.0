using System;
using System.IO;
using System.Windows.Media;
using Orbit.Island;
using Orbit.Services;
using Orbit.Tabs;
using Orbit.Tabs.Energia;

namespace Orbit.Tests;

// Harness minúsculo: cada Check imprime PASS/FAIL; exit code != 0 se algo falhar.
public static class Program
{
    private static int _failures;

    [STAThread]
    public static int Main()
    {
        Console.WriteLine("== Orbit.Tests ==");
        SmokeTest();
        SettingsTests();
        IslandMachineTests();
        IslandGeometryTests();
        AccentTests();
        PowerTests();
        FullscreenTests();
        TabIsolationTests();
        SharedInfraTests();

        Console.WriteLine(_failures == 0 ? "TODOS OK" : $"{_failures} FALHA(S)");
        return _failures == 0 ? 0 : 1;
    }

    private static void SharedInfraTests()
    {
        // Sparkline (portado do 1.0)
        var big = new double[100];
        for (int i = 0; i < big.Length; i++) big[i] = i;
        var s = Sparkline.Subsample(big, 40);
        Check("sparkline: subsample tamanho", s.Length == 40);
        Check("sparkline: preserva primeiro", s[0] == big[0]);
        Check("sparkline: preserva último", s[^1] == big[^1]);
        Check("sparkline: série curta inalterada", Sparkline.Subsample(new double[] { 1, 2, 3 }, 40).Length == 3);

        // Ring (era PomodoroTileViewModel.BuildRingDashArray no 1.0)
        double circ = Math.PI * 92 / 4;
        var d0 = Ring.DashArray(0, 92, 4);
        Check("anel: 0 sem traço", Math.Abs(d0[0]) < 0.01 && Math.Abs(d0[1] - circ) < 0.01);
        var d50 = Ring.DashArray(0.5, 92, 4);
        Check("anel: 0,5 metade/metade", Math.Abs(d50[0] - circ / 2) < 0.01 && Math.Abs(d50[1] - circ / 2) < 0.01);
        var d100 = Ring.DashArray(1, 92, 4);
        Check("anel: 1 cheio", Math.Abs(d100[0] - circ) < 0.01 && Math.Abs(d100[1]) < 0.01);
        Check("anel: negativo clampa em 0", Math.Abs(Ring.DashArray(-0.5, 92, 4)[0]) < 0.01);
        Check("anel: >1 clampa em 1", Math.Abs(Ring.DashArray(1.5, 92, 4)[0] - circ) < 0.01);

        // Freshness
        var now = new DateTime(2026, 10, 15, 12, 0, 0);
        var ten = TimeSpan.FromMinutes(10);
        Check("frescor: nunca buscou = velho", Freshness.IsStale(null, ten, now));
        Check("frescor: 3 min = fresco", !Freshness.IsStale(now.AddMinutes(-3), ten, now));
        Check("frescor: exatamente 10 min = velho", Freshness.IsStale(now.AddMinutes(-10), ten, now));

        // ApiService: URLs e parsers (portados do 1.0)
        Check("api: url cripto", ApiService.BuildCryptoUrl(new[] { "bitcoin", "ethereum" }).Contains("ids=bitcoin,ethereum"));
        Check("api: url câmbio", ApiService.BuildExchangeUrl(new[] { "USD", "EUR" }).EndsWith("/USD-BRL,EUR-BRL"));
        var json = "{\"daily\":{\"time\":[\"2026-07-03\",\"2026-07-04\"]," +
                   "\"weather_code\":[3,0],\"temperature_2m_max\":[24,26],\"temperature_2m_min\":[18,17]," +
                   "\"sunrise\":[\"2026-07-03T06:12\",\"2026-07-04T06:12\"]," +
                   "\"sunset\":[\"2026-07-03T17:47\",\"2026-07-04T17:47\"]," +
                   "\"precipitation_probability_max\":[20,null]}}";
        var fc = ApiService.ParseForecast(json);
        Check("api: previsão conta dias", fc.Count == 2);
        Check("api: previsão nascer do sol", fc[0].Sunrise.Hour == 6 && fc[0].Sunrise.Minute == 12);
        Check("api: previsão chuva null vira 0", fc[1].PrecipProb == 0);
        var news = ApiService.ParseNewsItem("{\"id\":456,\"title\":\"Ask HN\"}");
        Check("api: notícia sem url cai no thread do HN", news.Url == "https://news.ycombinator.com/item?id=456");
    }

    private static void AccentTests()
    {
        var sys = Color.FromRgb(0x4C, 0x9B, 0xF0);
        Check("acento: parse #RRGGBB", Orbit.Services.Accent.ParseHex("#E2B04A") == Color.FromRgb(0xE2, 0xB0, 0x4A));
        Check("acento: parse aceita minúsculas", Orbit.Services.Accent.ParseHex("#e2b04a") == Color.FromRgb(0xE2, 0xB0, 0x4A));
        Check("acento: sem # é inválido", Orbit.Services.Accent.ParseHex("E2B04A") is null);
        Check("acento: hex inválido", Orbit.Services.Accent.ParseHex("#GGB04A") is null);
        Check("acento: null é inválido", Orbit.Services.Accent.ParseHex(null) is null);
        Check("acento: sem override usa o do Windows", Orbit.Services.Accent.Resolve(null, sys) == sys);
        Check("acento: override inválido usa o do Windows", Orbit.Services.Accent.Resolve("lixo", sys) == sys);
        Check("acento: override válido vence", Orbit.Services.Accent.Resolve("#E2B04A", sys) == Color.FromRgb(0xE2, 0xB0, 0x4A));
        var soft = Orbit.Services.Accent.Soft(sys);
        Check("acento: versão suave tem alpha 0x33 e mesma cor", soft.A == 0x33 && soft.R == sys.R && soft.G == sys.G && soft.B == sys.B);
        Check("acento: paleta toda é hex válido", Array.TrueForAll(Orbit.Services.Accent.Paleta, p => Orbit.Services.Accent.ParseHex(p.Hex) is not null));
    }

    private static void PowerTests()
    {
        Check("energia: bloquear não confirma", !Power.NeedsConfirmation(PowerAction.Bloquear));
        Check("energia: suspender não confirma", !Power.NeedsConfirmation(PowerAction.Suspender));
        Check("energia: reiniciar confirma", Power.NeedsConfirmation(PowerAction.Reiniciar));
        Check("energia: desligar confirma", Power.NeedsConfirmation(PowerAction.Desligar));
        Check("energia: args de reiniciar", Power.ShutdownArgs(PowerAction.Reiniciar) == "/r /t 0");
        Check("energia: args de desligar", Power.ShutdownArgs(PowerAction.Desligar) == "/s /t 0");
        Check("energia: bloquear não usa shutdown.exe", Power.ShutdownArgs(PowerAction.Bloquear) is null);

        Check("energia: clique em bloquear executa direto",
            EnergiaViewModel.Decide(PowerAction.Bloquear, null) == (true, (PowerAction?)null));
        Check("energia: 1º clique em desligar só arma",
            EnergiaViewModel.Decide(PowerAction.Desligar, null) == (false, (PowerAction?)PowerAction.Desligar));
        Check("energia: 2º clique em desligar executa",
            EnergiaViewModel.Decide(PowerAction.Desligar, PowerAction.Desligar) == (true, (PowerAction?)null));
        Check("energia: armado em desligar, clique em reiniciar re-arma",
            EnergiaViewModel.Decide(PowerAction.Reiniciar, PowerAction.Desligar) == (false, (PowerAction?)PowerAction.Reiniciar));
        Check("energia: armado em desligar, clique em bloquear executa e desarma",
            EnergiaViewModel.Decide(PowerAction.Bloquear, PowerAction.Desligar) == (true, (PowerAction?)null));
    }

    private static void FullscreenTests()
    {
        Check("tela cheia: app comum no mesmo monitor esconde",
            FullscreenWatcher.ShouldHide(true, "UnityWndClass", sameMonitor: true));
        Check("tela cheia: aviso de saída mostra",
            !FullscreenWatcher.ShouldHide(false, "UnityWndClass", sameMonitor: true));
        Check("tela cheia: outro monitor não esconde",
            !FullscreenWatcher.ShouldHide(true, "UnityWndClass", sameMonitor: false));
        Check("tela cheia: área de trabalho (Progman) é falso positivo",
            !FullscreenWatcher.ShouldHide(true, "Progman", sameMonitor: true));
        Check("tela cheia: área de trabalho (WorkerW) é falso positivo",
            !FullscreenWatcher.ShouldHide(true, "WorkerW", sameMonitor: true));
        Check("tela cheia: barra de tarefas é falso positivo",
            !FullscreenWatcher.ShouldHide(true, "Shell_TrayWnd", sameMonitor: true));
    }

    private static void SmokeTest() => Check("harness roda", 1 + 1 == 2);

    private static void SettingsTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");

        var d = Orbit.Services.SettingsStore.Load(path);
        Check("settings: arquivo inexistente → defaults", d.LastTab == "energia" && d.AccentOverride is null);

        Orbit.Services.SettingsStore.Save(new Orbit.Models.OrbitSettings { LastTab = "ajustes", AccentOverride = "#E2B04A" }, path);
        var r = Orbit.Services.SettingsStore.Load(path);
        Check("settings: round-trip preserva campos", r.LastTab == "ajustes" && r.AccentOverride == "#E2B04A");

        File.WriteAllText(path, "{ isso não é json");
        var c = Orbit.Services.SettingsStore.Load(path);
        Check("settings: arquivo corrompido → defaults", c.LastTab == "energia");

        var bad = Path.Combine(dir, "a|b", "settings.json");
        bool threw = false;
        try { Orbit.Services.SettingsStore.Save(new Orbit.Models.OrbitSettings(), bad); }
        catch { threw = true; }
        Check("settings: Save com caminho inválido não lança", !threw);

        Directory.Delete(dir, recursive: true);
    }

    private static void IslandMachineTests()
    {
        var m = new IslandMachine();
        Check("ilha: começa em Repouso", m.Mode == IslandMode.Repouso && m.ActivityText is null);

        m.SetActivity(ActivityKind.Midia, "▶ lofi");
        Check("ilha: atividade → Atividade", m.Mode == IslandMode.Atividade && m.ActivityText == "▶ lofi");

        m.SetActivity(ActivityKind.Claude, "Claude aguardando você");
        m.SetActivity(ActivityKind.Pomodoro, "Foco 12:04");
        Check("ilha: prioridade Claude > Pomodoro > Mídia", m.ActivityText == "Claude aguardando você");

        m.SetActivity(ActivityKind.Claude, null);
        Check("ilha: limpar Claude cai pro Pomodoro", m.ActivityText == "Foco 12:04");

        m.SetActivity(ActivityKind.Pomodoro, "");
        m.SetActivity(ActivityKind.Midia, null);
        Check("ilha: sem atividades volta a Repouso", m.Mode == IslandMode.Repouso);

        m.Expand();
        Check("ilha: Expand → Expandida", m.Mode == IslandMode.Expandida);
        m.Collapse();
        Check("ilha: Collapse → Repouso", m.Mode == IslandMode.Repouso);

        var a = new Peek("A", null, null);
        var b = new Peek("B", "det", "energia");
        m.RaisePeek(a);
        m.RaisePeek(b);
        Check("ilha: peek → Alerta com o primeiro", m.Mode == IslandMode.Alerta && m.CurrentPeek == a);
        m.PeekDone();
        Check("ilha: PeekDone mostra o próximo da fila", m.CurrentPeek == b);
        m.PeekDone();
        Check("ilha: fila vazia → Repouso", m.Mode == IslandMode.Repouso && m.CurrentPeek is null);

        m.SetFullscreen(true);
        Check("ilha: tela cheia → Oculta", m.Mode == IslandMode.Oculta);
        m.Expand();
        Check("ilha: Expand ignorado em tela cheia", m.Mode == IslandMode.Oculta && !m.Expanded);
        m.RaisePeek(a);
        Check("ilha: peek em tela cheia fica na fila", m.CurrentPeek is null && m.Mode == IslandMode.Oculta);
        m.SetFullscreen(false);
        Check("ilha: sair da tela cheia mostra o peek pendente", m.Mode == IslandMode.Alerta && m.CurrentPeek == a);

        m.SetFullscreen(true);
        Check("ilha: peek interrompido sai de cena", m.CurrentPeek is null);
        m.SetFullscreen(false);
        Check("ilha: peek interrompido reaparece", m.CurrentPeek == a);
        m.PeekDone();

        m.Expand();
        m.SetFullscreen(true);
        m.SetFullscreen(false);
        Check("ilha: tela cheia recolhe o painel", m.Mode == IslandMode.Repouso);

        var x1 = new Peek("X", null, null);
        var x2 = new Peek("X", null, null);
        m.RaisePeek(x1);
        m.RaisePeek(x2);
        m.PeekDone();
        Check("ilha: alertas iguais em sequência são eventos distintos", m.CurrentPeek == x2 && !ReferenceEquals(x1, x2));
        m.PeekDone();
    }

    private static void IslandGeometryTests()
    {
        bool allFit = true;
        foreach (IslandMode mode in Enum.GetValues<IslandMode>())
        {
            var s = IslandGeometry.SizeFor(mode);
            if (s.Width > IslandGeometry.WindowWidth || s.Height > IslandGeometry.WindowHeight) allFit = false;
        }
        Check("geometria: todo estado cabe na janela", allFit);
        Check("geometria: repouso é pílula fininha", IslandGeometry.SizeFor(IslandMode.Repouso) == new IslandSize(110, 10));
        Check("geometria: expandida 720x380", IslandGeometry.SizeFor(IslandMode.Expandida) == new IslandSize(720, 380));
        Check("geometria: janela centralizada em 1920", IslandGeometry.LeftFor(1920) == 580);
        Check("geometria: raio cresce com o estado",
            IslandGeometry.RadiusFor(IslandMode.Repouso) < IslandGeometry.RadiusFor(IslandMode.Expandida));
    }

    private sealed class BoomTab : TabViewModel
    {
        public override string Id => "boom";
        public override string Title => "Boom";
        public override void SetActive(bool active) => throw new InvalidOperationException("boom");
    }

    private static void TabIsolationTests()
    {
        bool threw = false;
        try { IslandViewModel.SafeSetActive(new BoomTab(), true); }
        catch { threw = true; }
        Check("abas: exceção em SetActive não vaza pra ilha", !threw);

        threw = false;
        try { IslandViewModel.SafeSetActive(null, true); }
        catch { threw = true; }
        Check("abas: SafeSetActive aceita null", !threw);
    }

    public static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failures++;
    }
}
