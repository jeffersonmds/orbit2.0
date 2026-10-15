using System;
using System.IO;
using System.Windows.Media;
using Orbit.Island;

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

        Console.WriteLine(_failures == 0 ? "TODOS OK" : $"{_failures} FALHA(S)");
        return _failures == 0 ? 0 : 1;
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

    public static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failures++;
    }
}
