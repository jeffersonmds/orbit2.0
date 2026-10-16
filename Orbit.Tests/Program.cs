using System;
using System.Threading.Tasks;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;
using Orbit.Bridge;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using Orbit.Island;
using Orbit.Services;
using Orbit.Tabs;
using Orbit.Tabs.Energia;
using Orbit.Tabs.Hoje;
using Orbit.Tabs.Controles;
using Orbit.Tabs.Stats;
using Orbit.Tabs.Mercado;
using Orbit.Tabs.Ajustes;
using Orbit.Tabs.Noticias;
using Orbit.Tabs.Claude;
using Orbit.Tabs.Clips;
using Orbit.Tabs.Apps;

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
        TabVisibilityTests();
        PomodoroTests();
        HojeTests();
        NowPlayingTests();
        ControlesTests();
        StatsTests();
        MercadoTests();
        NoticiasTests();
        ClaudeTests();
        TabOrderTests();
        ClipHistoryTests();
        ClipsTests();
        AppSearchTests();
        AppIconTests();
        BridgeRulesTests();
        TokenAuditTests();
        BridgeServerTests();
        IPhoneTests();
        PwaTests();

        Console.WriteLine(_failures == 0 ? "TODOS OK" : $"{_failures} FALHA(S)");
        return _failures == 0 ? 0 : 1;
    }

    private static void PwaTests()
    {
        var web = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        Check("pwa: copiado pra saída", File.Exists(Path.Combine(web, "index.html")) && File.Exists(Path.Combine(web, "sw.js")));
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var server = new BridgeServer();
        server.StartAsync(0, web, new TokenStore(Path.Combine(dir, "t.json")), new AuditLog(Path.Combine(dir, "a.jsonl")), new BridgeHandlers())
            .GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var html = http.GetStringAsync("/").Result;
        Check("pwa: / serve o index sem token", html.Contains("<title>Orbit</title>"));
        var manifest = http.GetAsync("/manifest.webmanifest").Result;
        Check("pwa: manifest com content-type", manifest.IsSuccessStatusCode
            && manifest.Content.Headers.ContentType?.MediaType == "application/manifest+json");
        Check("pwa: estático revalida (no-cache)", http.GetAsync("/app.js").Result.Headers.CacheControl?.NoCache == true);
        Check("pwa: ícone", http.GetAsync("/apple-touch-icon.png").Result.IsSuccessStatusCode);
        server.StopAsync().GetAwaiter().GetResult();
        Directory.Delete(dir, recursive: true);
    }

    private static void IPhoneTests()
    {
        var png = Qr.RenderPng("https://pc.tail1.ts.net/#token=abc");
        Check("qr: gera PNG", png is { Length: > 100 } && png[1] == (byte)'P' && png[2] == (byte)'N' && png[3] == (byte)'G');
        var d = new Orbit.Models.OrbitSettings();
        Check("settings: ponte desligada por padrão", !d.BridgeEnabled && d.BridgePort == 5191 && d.BridgePublicUrl == "");
        Check("bateria: percentual", Orbit.Services.Battery.Percent(0.8f, hasBattery: true) == 80
            && Orbit.Services.Battery.Percent(1.5f, hasBattery: true) is null && Orbit.Services.Battery.Percent(0.5f, hasBattery: false) is null);
    }

    private static void TabOrderTests()
    {
        var order = string.Join(",", IslandViewModel.TabOrder);
        Check("abas: clipboard e apps entre stats e mercado", order.Contains("stats,clipboard,apps,mercado"));
        Check("abas: iphone antes de energia", order.Contains("claude,iphone,energia"));
        Check("abas: 11 ids na ordem", IslandViewModel.TabOrder.Length == 11);
    }

    private static ClipItem Txt(string t, DateTime at, bool pinned = false) =>
        new(ClipHistory.ClassifyText(t), t, null, "t:" + t, at, pinned, 0, 0);

    private static void ClipHistoryTests()
    {
        Check("clip: link https", ClipHistory.ClassifyText("https://example.com/a?b=1") == ClipKind.Link);
        Check("clip: link com espaços em volta", ClipHistory.ClassifyText("  http://x.io \n") == ClipKind.Link);
        Check("clip: frase com url é texto", ClipHistory.ClassifyText("veja https://x.io agora") == ClipKind.Text);
        Check("clip: ftp é texto", ClipHistory.ClassifyText("ftp://x.io") == ClipKind.Text);

        Check("clip: privado por formato", ClipHistory.IsPrivate(new[] { "Text", "ExcludeClipboardContentFromMonitorProcessing" }, null));
        Check("clip: privado por CanIncludeInClipboardHistory=0", ClipHistory.IsPrivate(new[] { "Text" }, 0));
        Check("clip: normal não é privado", !ClipHistory.IsPrivate(new[] { "Text" }, 1) && !ClipHistory.IsPrivate(new[] { "Text" }, null));

        Check("clip: privado por 'Clipboard Viewer Ignore'", ClipHistory.IsPrivate(new[] { "Text", "Clipboard Viewer Ignore" }, null));
        Check("clip: chave de texto é hash estável e curta", ClipHistory.TextKey("abc") == ClipHistory.TextKey("abc")
            && ClipHistory.TextKey("abc") != ClipHistory.TextKey("abd") && ClipHistory.TextKey(new string('x', 5000)).Length == 18);

        var t0 = new DateTime(2026, 10, 15, 9, 0, 0);
        var list = new List<ClipItem>();
        ClipHistory.Add(list, Txt("a", t0));
        ClipHistory.Add(list, Txt("b", t0.AddMinutes(1)));
        var removed = ClipHistory.Add(list, Txt("a", t0.AddMinutes(2)));
        Check("clip: repetido sobe pro topo sem duplicar", list.Count == 2 && list[0].Text == "a" && removed.Count == 0);
        Check("clip: repetido atualiza a hora", list[0].CopiedAt == t0.AddMinutes(2));

        var big = new List<ClipItem> { Txt("fixo", t0, pinned: true) };
        for (int i = 0; i < ClipHistory.MaxUnpinned; i++) ClipHistory.Add(big, Txt("x" + i, t0.AddSeconds(i)));
        var gone = ClipHistory.Add(big, Txt("novo", t0.AddHours(1)));
        Check("clip: limite descarta o mais antigo não fixado", gone.Count == 1 && gone[0].Text == "x0");
        Check("clip: fixado nunca sai pelo limite", big.Exists(i => i.Text == "fixo") && big.Count == ClipHistory.MaxUnpinned + 1);

        var items = new List<ClipItem>
        {
            Txt("bom dia", t0),
            Txt("https://github.com", t0.AddMinutes(5)),
            new(ClipKind.Image, null, "abc.png", "i:abc", t0.AddMinutes(9), false, 1920, 1080),
            Txt("fixado antigo", t0.AddMinutes(-60), pinned: true),
        };
        var all = ClipHistory.View(items, "", ClipFilter.Tudo);
        Check("clip: fixados primeiro, depois mais recentes", all[0].Text == "fixado antigo" && all[1].Kind == ClipKind.Image);
        Check("clip: filtro links", ClipHistory.View(items, "", ClipFilter.Links).Count == 1);
        Check("clip: filtro imagens", ClipHistory.View(items, null, ClipFilter.Imagens).Count == 1);
        Check("clip: filtro texto", ClipHistory.View(items, "", ClipFilter.Texto).Count == 2);
        Check("clip: busca sem caixa", ClipHistory.View(items, "BOM", ClipFilter.Tudo).Count == 1);

        var now = new DateTime(2026, 10, 15, 12, 0, 0);
        Check("clip: agora", ClipHistory.RelativeTime(now, now.AddSeconds(-20)) == "agora");
        Check("clip: minutos", ClipHistory.RelativeTime(now, now.AddMinutes(-4)) == "4m");
        Check("clip: horas", ClipHistory.RelativeTime(now, now.AddHours(-2)) == "2h");
        Check("clip: ontem", ClipHistory.RelativeTime(now, new DateTime(2026, 10, 14, 23, 0, 0)) == "ontem");
        Check("clip: dias", ClipHistory.RelativeTime(now, now.AddDays(-3)) == "3d");

        Check("clip: descrição de texto", ClipHistory.Describe("oi tudo\nbem") == "11 caracteres · 3 palavras · 2 linhas");
        Check("clip: descrição singular", ClipHistory.Describe("oi") == "2 caracteres · 1 palavra · 1 linha");
        Check("clip: linha única p/ lista", ClipHistory.OneLine("  a\n  b  ") == "a b");

        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "clipboard.json");
        Check("clip store: inexistente = vazio", ClipboardStore.Load(path).Count == 0);
        ClipboardStore.Save(items, path);
        var back = ClipboardStore.Load(path);
        Check("clip store: round-trip", back.Count == 4 && back[2].Kind == ClipKind.Image && back[2].Width == 1920
            && back[3].Pinned && back[0].Text == "bom dia");
        File.WriteAllText(path, "{ quebrado");
        Check("clip store: corrompido = vazio", ClipboardStore.Load(path).Count == 0);
        Directory.Delete(dir, recursive: true);
    }

    private static void ClipsTests()
    {
        var seq = Input.CtrlVSequence();
        Check("colar: sequência Ctrl↓ V↓ V↑ Ctrl↑", seq.Length == 4
            && seq[0] == (0x11, false) && seq[1] == (0x56, false) && seq[2] == (0x56, true) && seq[3] == (0x11, true));

        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        bool enabled = true;
        var vm = new ClipsViewModel(Path.Combine(dir, "clipboard.json"), Path.Combine(dir, "img"), () => enabled);
        vm.IngestAsync(new ClipSnapshot("olá mundo", null, false)).GetAwaiter().GetResult();
        vm.IngestAsync(new ClipSnapshot("https://example.com", null, false)).GetAwaiter().GetResult();
        Check("clips: captura texto e link", vm.Visible.Count == 2 && vm.Visible[0].Kind == ClipKind.Link);
        vm.IngestAsync(new ClipSnapshot("olá mundo", null, false)).GetAwaiter().GetResult();
        Check("clips: repetido não duplica e sobe", vm.Visible.Count == 2 && vm.Visible[0].Text == "olá mundo");
        vm.IngestAsync(new ClipSnapshot("senha123", null, true)).GetAwaiter().GetResult();
        Check("clips: conteúdo privado é ignorado", vm.Visible.Count == 2);
        vm.IngestAsync(new ClipSnapshot("   ", null, false)).GetAwaiter().GetResult();
        Check("clips: só espaço é ignorado", vm.Visible.Count == 2);
        enabled = false;
        vm.IngestAsync(new ClipSnapshot("com aba escondida", null, false)).GetAwaiter().GetResult();
        Check("clips: aba escondida não captura", vm.Visible.Count == 2);

        vm.Filter = ClipFilter.Links;
        Check("clips: filtro aplica", vm.Visible.Count == 1);
        vm.Filter = ClipFilter.Tudo;
        vm.Selected = vm.Visible[1];
        vm.TogglePinCommand.Execute(null);
        Check("clips: fixado vai pro topo", vm.Visible[0].Pinned && vm.Visible[0].Text == "https://example.com");
        vm.Selected = vm.Visible[1];
        vm.RemoveCommand.Execute(null);
        Check("clips: remover", vm.Visible.Count == 1);
        vm.ClearCommand.Execute(null);
        Check("clips: limpar mantém fixados", vm.Visible.Count == 1 && vm.Visible[0].Pinned);

        var reloaded = new ClipsViewModel(Path.Combine(dir, "clipboard.json"), Path.Combine(dir, "img"), () => true);
        Check("clips: persiste entre execuções", reloaded.Visible.Count == 1 && reloaded.Visible[0].Pinned);
        Directory.Delete(dir, recursive: true);
    }

    private static void NoticiasTests()
    {
        var pool = new List<NewsItem> { new(1, "a", "u1"), new(2, "b", "u2"), new(3, "c", "u3"), new(4, "d", "u4") };
        var vis = NoticiasViewModel.SelectVisible(pool, new HashSet<int> { 2 }, 2);
        Check("notícias: filtra ignorado", vis.All(i => i.Id != 2));
        Check("notícias: limita e mantém ordem", vis.Count == 2 && vis[0].Id == 1 && vis[1].Id == 3);
        Check("notícias: prune mantém os maiores", string.Join(",", NoticiasViewModel.Prune(new List<int> { 10, 20, 30 }, 25, 2)) == "30,25");
        Check("notícias: prune sem duplicar", string.Join(",", NoticiasViewModel.Prune(new List<int> { 10, 20 }, 20, 5)) == "20,10");
        Check("notícias: domínio sem www", NoticiasViewModel.Domain("https://www.example.com/a/b") == "example.com");
        Check("notícias: domínio do HN", NoticiasViewModel.Domain("https://news.ycombinator.com/item?id=1") == "news.ycombinator.com");
        Check("notícias: url inválida = vazio", NoticiasViewModel.Domain("isso não é url") == "");
        Check("settings: ignorados default vazio", new Orbit.Models.OrbitSettings().DismissedNewsIds.Count == 0);
    }

    private static void ClaudeTests()
    {
        // Parse (portado)
        var start = ClaudeHookEvent.Parse("{\"hook_event_name\":\"SessionStart\",\"session_id\":\"s1\",\"cwd\":\"C:\\\\repos\\\\orbit2.0\"}");
        Check("claude: parse evento/sessão/cwd", start.HookEventName == "SessionStart" && start.SessionId == "s1" && start.Cwd == @"C:\repos\orbit2.0");
        var notif = ClaudeHookEvent.Parse("{\"hook_event_name\":\"Notification\",\"session_id\":\"s1\",\"cwd\":\"C:\\\\x\",\"message\":\"precisa de permiss\\u00e3o\"}");
        Check("claude: parse message", notif.Message == "precisa de permissão");

        // Reducer (portado)
        var sessions = new Dictionary<string, ClaudeSessionInfo>();
        ClaudeSessionReducer.Apply(sessions, new ClaudeHookEvent("SessionStart", "s1", @"C:\repos\orbit2.0", null));
        Check("claude: SessionStart cria Rodando com nome da pasta", sessions["s1"].State == ClaudeSessionState.Running && sessions["s1"].ProjectName == "orbit2.0");
        ClaudeSessionReducer.Apply(sessions, new ClaudeHookEvent("Stop", "s1", @"C:\repos\orbit2.0", null));
        Check("claude: Stop → aguardando", sessions["s1"].State == ClaudeSessionState.WaitingInput);
        ClaudeSessionReducer.Apply(sessions, new ClaudeHookEvent("Stop", "s2", @"C:\x", null));
        Check("claude: sessão desconhecida ignorada", !sessions.ContainsKey("s2"));
        ClaudeSessionReducer.Apply(sessions, new ClaudeHookEvent("SessionEnd", "s1", @"C:\repos\orbit2.0", null));
        Check("claude: SessionEnd remove", !sessions.ContainsKey("s1"));

        // Installer: merge preserva hooks alheios e é idempotente (portado)
        const string ours = "\"C:\\Orbit2\\Orbit.exe\" --claude-hook";
        var root = new System.Text.Json.Nodes.JsonObject
        {
            ["hooks"] = new System.Text.Json.Nodes.JsonObject
            {
                ["SessionStart"] = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["hooks"] = new System.Text.Json.Nodes.JsonArray
                        {
                            new System.Text.Json.Nodes.JsonObject { ["type"] = "command", ["command"] = "rtk-statusline" },
                        },
                    },
                },
                ["Stop"] = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["hooks"] = new System.Text.Json.Nodes.JsonArray
                        {
                            new System.Text.Json.Nodes.JsonObject { ["type"] = "command", ["command"] = "\"C:\\repos\\orbit1.0\\Orbit.exe\" --claude-hook" },
                        },
                    },
                },
            },
        };
        ClaudeHookInstaller.MergeHooks(root, ours);
        Check("claude: preserva hook alheio + adiciona o nosso", root["hooks"]!["SessionStart"]!.AsArray().Count == 2);
        Check("claude: remove hook do Orbit 1.0", !root.ToJsonString().Contains("orbit1.0"));
        Check("claude: Stop fica só com o nosso", root["hooks"]!["Stop"]!.AsArray().Count == 1);
        ClaudeHookInstaller.MergeHooks(root, ours);
        var alheio = new System.Text.Json.Nodes.JsonObject
        {
            ["hooks"] = new System.Text.Json.Nodes.JsonObject
            {
                ["Stop"] = new System.Text.Json.Nodes.JsonArray
                {
                    new System.Text.Json.Nodes.JsonObject
                    {
                        ["hooks"] = new System.Text.Json.Nodes.JsonArray
                        {
                            new System.Text.Json.Nodes.JsonObject { ["type"] = "command", ["command"] = "outra-ferramenta --claude-hook" },
                        },
                    },
                },
            },
        };
        ClaudeHookInstaller.MergeHooks(alheio, ours);
        Check("claude: não remove --claude-hook de outra ferramenta", alheio.ToJsonString().Contains("outra-ferramenta"));
        Check("claude: merge idempotente", root["hooks"]!["SessionStart"]!.AsArray().Count == 2 && ClaudeHookInstaller.HasAllHooks(root, ours));

        // Pílula
        var waiting = new ClaudeSessionInfo("s1", "orbit2.0", ClaudeSessionState.WaitingInput, null);
        var running = new ClaudeSessionInfo("s2", "api", ClaudeSessionState.Running, null);
        Check("claude: só rodando = sem atividade", ClaudeViewModel.BuildActivity(new[] { running }) is null);
        Check("claude: uma aguardando", ClaudeViewModel.BuildActivity(new[] { running, waiting }) == "Claude · orbit2.0 — Aguardando você");
        Check("claude: várias aguardando", ClaudeViewModel.BuildActivity(new[] { waiting, waiting with { SessionId = "s3" } }) == "Claude · 2 sessões aguardando você");

        // VM: alerta só em Stop/Notification
        int attention = 0;
        var vm = new ClaudeViewModel();
        vm.Attention += _ => attention++;
        vm.HandleHookEvent(new ClaudeHookEvent("SessionStart", "a", @"C:\p", null));
        Check("claude: SessionStart não alerta", attention == 0 && vm.Sessions.Count == 1);
        vm.HandleHookEvent(new ClaudeHookEvent("Stop", "a", @"C:\p", null));
        Check("claude: Stop alerta e vira atividade", attention == 1 && vm.ActivityText is not null);
        vm.HandleHookEvent(new ClaudeHookEvent("SessionEnd", "a", @"C:\p", null));
        Check("claude: SessionEnd limpa", vm.Sessions.Count == 0 && vm.ActivityText is null && attention == 1);
        vm.HandleHookEvent(new ClaudeHookEvent("SessionStart", "b", @"C:\p", null));
        vm.HandleHookEvent(new ClaudeHookEvent("Stop", "b", @"C:\p", null));
        vm.DismissCommand.Execute(vm.Sessions[0]);
        Check("claude: ✕ tira sessão órfã e libera a pílula", vm.Sessions.Count == 0 && vm.ActivityText is null);
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
        double circ = Math.PI * (92 - 4) / 4;
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

    private static void TabVisibilityTests()
    {
        var registered = new List<string> { "ajustes", "energia", "hoje", "controles" };
        var none = new List<string>();
        Check("abas: ordem segue TabOrder", string.Join(",", IslandViewModel.VisibleIds(registered, none)) == "controles,hoje,energia,ajustes");
        Check("abas: escondida some", !IslandViewModel.VisibleIds(registered, new List<string> { "hoje" }).Contains("hoje"));
        Check("abas: ajustes nunca some", IslandViewModel.VisibleIds(registered, new List<string> { "ajustes" }).Contains("ajustes"));
        Check("abas: id não registrado não aparece", !IslandViewModel.VisibleIds(registered, none).Contains("stats"));

        Check("alerta: tela cheia + preferência = toast", IslandViewModel.ShouldToast(fullscreen: true, preferToast: true));
        Check("alerta: tela cheia sem preferência = fila", !IslandViewModel.ShouldToast(fullscreen: true, preferToast: false));
        Check("alerta: fora da tela cheia nunca toast", !IslandViewModel.ShouldToast(fullscreen: false, preferToast: true));

        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "settings.json");
        var d = Orbit.Services.SettingsStore.Load(path);
        Check("settings: HiddenTabs default vazio", d.HiddenTabs.Count == 0 && !d.AlertsAsToastInFullscreen);
        Orbit.Services.SettingsStore.Save(new Orbit.Models.OrbitSettings { HiddenTabs = new List<string> { "stats" }, AlertsAsToastInFullscreen = true }, path);
        var r = Orbit.Services.SettingsStore.Load(path);
        Check("settings: HiddenTabs/alertas round-trip", r.HiddenTabs.Contains("stats") && r.AlertsAsToastInFullscreen);
        Directory.Delete(dir, recursive: true);
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
            FullscreenWatcher.Decide(true, "UnityWndClass", sameMonitor: true) == true);
        Check("tela cheia: aviso de saída mostra",
            FullscreenWatcher.Decide(false, "UnityWndClass", sameMonitor: true) == false);
        Check("tela cheia: outro monitor é ignorado (não mostra nem esconde)",
            FullscreenWatcher.Decide(true, "UnityWndClass", sameMonitor: false) is null);
        Check("tela cheia: área de trabalho (Progman) é falso positivo",
            FullscreenWatcher.Decide(true, "Progman", sameMonitor: true) == false);
        Check("tela cheia: área de trabalho (WorkerW) é falso positivo",
            FullscreenWatcher.Decide(true, "WorkerW", sameMonitor: true) == false);
        Check("tela cheia: barra de tarefas é falso positivo",
            FullscreenWatcher.Decide(true, "Shell_TrayWnd", sameMonitor: true) == false);
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

    private static void PomodoroTests()
    {
        Check("pomodoro: rótulos", PomodoroViewModel.PhaseLabel(PomodoroPhase.Focus) == "Foco"
            && PomodoroViewModel.PhaseLabel(PomodoroPhase.ShortBreak) == "Pausa"
            && PomodoroViewModel.PhaseLabel(PomodoroPhase.LongBreak) == "Pausa longa");

        var (p1, c1) = PomodoroViewModel.NextPhase(PomodoroPhase.Focus, completedCycles: 1, cyclesUntilLongBreak: 4);
        Check("pomodoro: foco → pausa curta soma ciclo", p1 == PomodoroPhase.ShortBreak && c1 == 2);
        var (p2, c2) = PomodoroViewModel.NextPhase(PomodoroPhase.Focus, completedCycles: 3, cyclesUntilLongBreak: 4);
        Check("pomodoro: foco fecha ciclo → pausa longa zera", p2 == PomodoroPhase.LongBreak && c2 == 0);
        var (p3, c3) = PomodoroViewModel.NextPhase(PomodoroPhase.ShortBreak, completedCycles: 2, cyclesUntilLongBreak: 4);
        Check("pomodoro: pausa → foco preserva contador", p3 == PomodoroPhase.Focus && c3 == 2);
        var (p4, c4) = PomodoroViewModel.NextPhase(PomodoroPhase.Focus, completedCycles: 0, cyclesUntilLongBreak: 0);
        Check("pomodoro: ciclos <= 0 vira 1", p4 == PomodoroPhase.LongBreak && c4 == 0);

        Check("pomodoro: texto da pílula parado = null", PomodoroViewModel.BuildActivity(false, "Foco", "25:00") is null);
        Check("pomodoro: texto da pílula rodando", PomodoroViewModel.BuildActivity(true, "Foco", "12:04") == "Foco · 12:04");

        Check("ajustes: número válido", AjustesViewModel.ParseRange("50", 25, 1, 180) == 50);
        Check("ajustes: lixo volta ao anterior", AjustesViewModel.ParseRange("abc", 25, 1, 180) == 25);
        Check("ajustes: fora da faixa volta ao anterior", AjustesViewModel.ParseRange("0", 25, 1, 180) == 25
            && AjustesViewModel.ParseRange("999", 25, 1, 180) == 25);

        var d = new Orbit.Models.OrbitSettings();
        Check("settings: pomodoro defaults", d.PomodoroFocusMinutes == 25 && d.PomodoroShortBreakMinutes == 5
            && d.PomodoroLongBreakMinutes == 15 && d.PomodoroCyclesUntilLongBreak == 4 && d.PomodoroSoundEnabled);
    }

    private static void HojeTests()
    {
        // Semana começa na segunda; quinta 15/10/2026.
        var week = HojeViewModel.BuildWeek(new DateTime(2026, 10, 15));
        Check("hoje: semana tem 7 dias", week.Count == 7);
        Check("hoje: começa na segunda 12", week[0].Label == "Seg" && week[0].Day == 12);
        Check("hoje: termina no domingo 18", week[6].Label == "Dom" && week[6].Day == 18);
        Check("hoje: só quinta é hoje", week.FindAll(d => d.IsToday).Count == 1 && week[3].IsToday);
        var sunday = HojeViewModel.BuildWeek(new DateTime(2026, 10, 18));
        Check("hoje: domingo pertence à semana que começou na segunda", sunday[0].Day == 12 && sunday[6].IsToday);

        var todos = new List<TodoItem> { new("a", true), new("b", false), new("c", false) };
        Check("hoje: contador de tarefas", HojeViewModel.Counter(todos) == "1/3");
        Check("hoje: contador vazio", HojeViewModel.Counter(new List<TodoItem>()) == "0/0");

        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "todos.json");
        Check("todos: arquivo inexistente = vazio", TodoStore.Load(path).Count == 0);
        TodoStore.Save(todos, path);
        var back = TodoStore.Load(path);
        Check("todos: round-trip", back.Count == 3 && back[0].Text == "a" && back[0].Done && !back[1].Done);
        File.WriteAllText(path, "{ quebrado");
        Check("todos: corrompido = vazio", TodoStore.Load(path).Count == 0);
        Directory.Delete(dir, recursive: true);

        // Clima (portado do 1.0)
        Check("clima: alerta de chuva acima de 60", !WeatherViewModel.IsRainAlert(60) && WeatherViewModel.IsRainAlert(61));
        var fc = new List<ForecastResult>
        {
            new(new DateTime(2026, 7, 3), 3, 24, 18, new DateTime(2026, 7, 3, 6, 12, 0), new DateTime(2026, 7, 3, 17, 47, 0), 20),
            new(new DateTime(2026, 7, 4), 0, 26, 17, new DateTime(2026, 7, 4, 6, 12, 0), new DateTime(2026, 7, 4, 17, 47, 0), 0),
        };
        var built = WeatherViewModel.BuildForecast(fc);
        Check("clima: previsão pula hoje", built.Count == 1);
        Check("clima: formato máx/mín e ícone", built[0].MaxMin == "26°/17°" && built[0].Icon == "☀");
    }

    private static void ControlesTests()
    {
        Check("volume: escalar → %", Volume.ToPercent(0.64f) == 64);
        Check("volume: % → escalar", Math.Abs(Volume.ToScalar(25) - 0.25f) < 0.0001);
        Check("volume: clampa", Volume.ToPercent(1.5f) == 100 && Volume.ToScalar(-10) == 0f);
        Check("modo escuro: valor do registro", DarkMode.RegistryValue(true) == 0 && DarkMode.RegistryValue(false) == 1);
        Check("controles: status ligado/desligado/indisponível", ControlesViewModel.Status(true) == "Ligado"
            && ControlesViewModel.Status(false) == "Desligado" && ControlesViewModel.Status(null) == "Indisponível");
    }

    private static void StatsTests()
    {
        Check("gpu: soma dos motores 3D", Math.Abs(GpuStats.Aggregate(new[] { 10f, 15.5f }) - 25.5) < 0.001);
        Check("gpu: clampa em 100", GpuStats.Aggregate(new[] { 80f, 70f }) == 100);
        Check("gpu: sem motores = 0", GpuStats.Aggregate(Array.Empty<float>()) == 0);

        Check("rede: taxa", NetStats.Rate(1000, 3000, 2) == 1000);
        Check("rede: contador zerou = 0", NetStats.Rate(5000, 100, 2) == 0);
        Check("rede: intervalo 0 = 0", NetStats.Rate(0, 100, 0) == 0);
        Check("rede: formato MB/s", NetStats.Format(1_258_291) == "1,2 MB/s");
        Check("rede: formato KB/s", NetStats.Format(2048) == "2 KB/s");
        Check("rede: formato B/s", NetStats.Format(512) == "512 B/s");

        var procs = new List<(string, long)> { ("chrome", 300), ("Code", 500), ("chrome", 400), ("x", 10) };
        var top = StatsViewModel.TopByMemory(procs, 2);
        Check("top apps: agrupa por nome e ordena", top.Count == 2 && top[0].Name == "chrome" && top[0].Bytes == 700 && top[1].Name == "Code");
        Check("top apps: formato GB", StatsViewModel.FormatBytes(1_610_612_736) == "1,5 GB");
        Check("top apps: formato MB", StatsViewModel.FormatBytes(820L * 1024 * 1024) == "820 MB");

        Check("uptime: horas", StatsViewModel.FormatUptime(new TimeSpan(5, 12, 0)) == "5h 12m");
        Check("uptime: dias", StatsViewModel.FormatUptime(new TimeSpan(2, 3, 40, 0)) == "2d 3h");
    }

    private static void MercadoTests()
    {
        Check("mercado: catálogo acha bitcoin/USD", MarketCatalog.FindCrypto("bitcoin")?.Name == "Bitcoin"
            && MarketCatalog.FindCurrency("USD")?.Name == "Dólar");
        Check("mercado: id desconhecido = null", MarketCatalog.FindCrypto("nao-existe") is null);
        Check("mercado: usd grande sem decimais", MercadoViewModel.FormatUsd(104235) == "$104,235");
        Check("mercado: usd médio 2 casas", MercadoViewModel.FormatUsd(3.5) == "$3.50");
        Check("mercado: usd sub-dólar 4 casas", MercadoViewModel.FormatUsd(0.6234) == "$0.6234");
        var ordered = MercadoViewModel.OrderCatalog(new List<string> { "a", "b", "c", "d" }, new List<string> { "c", "a" });
        Check("mercado: selecionados primeiro na ordem salva", string.Join(",", ordered) == "c,a,b,d");
        var ordered2 = MercadoViewModel.OrderCatalog(new List<string> { "a", "b", "c", "d" }, new List<string> { "z", "b" });
        Check("mercado: id fora do catálogo é ignorado", string.Join(",", ordered2) == "b,a,c,d");
        var d = new Orbit.Models.OrbitSettings();
        Check("settings: mercado defaults", string.Join(",", d.MarketCryptos) == "bitcoin,ethereum"
            && string.Join(",", d.MarketCurrencies) == "USD,EUR");
    }

    private static void NowPlayingTests()
    {
        Check("mídia: título normal", NowPlayingViewModel.DisplayTitle("Lucky Man") == "Lucky Man");
        Check("mídia: título nulo/vazio vira fallback", NowPlayingViewModel.DisplayTitle(null) == "Tocando…"
            && NowPlayingViewModel.DisplayTitle("  ") == "Tocando…");
        Check("mídia: glifo pausa tocando / play pausado", NowPlayingViewModel.PlayPauseGlyphFor(true) == "\uE769"
            && NowPlayingViewModel.PlayPauseGlyphFor(false) == "\uE768");

        Check("mídia: sem sessão = sem atividade", NowPlayingViewModel.BuildActivity(null) is null);
        Check("mídia: pausado = sem atividade",
            NowPlayingViewModel.BuildActivity(new NowPlayingInfo("x", "y", null, IsPlaying: false)) is null);
        Check("mídia: tocando com artista",
            NowPlayingViewModel.BuildActivity(new NowPlayingInfo("Faixa", "Banda", null, IsPlaying: true)) == "▶  Faixa · Banda");
        Check("mídia: tocando sem artista",
            NowPlayingViewModel.BuildActivity(new NowPlayingInfo("Faixa", "", null, IsPlaying: true)) == "▶  Faixa");
    }

    private static void AppSearchTests()
    {
        Check("apps: normaliza acento e caixa", AppSearch.Normalize("Configurações") == "configuracoes");

        var apps = new List<AppEntry>
        {
            new("Visual Studio Code", "code"),
            new("Spotify", "spotify"),
            new("Configurações", "settings"),
            new("Microsoft Edge", "edge"),
            new("Steam", "steam"),
        };
        var r = AppSearch.Search(apps, "s", 10);
        // "s": spotify/steam (prefixo) > visual studio code (palavra) > configurações/edge (contém)
        Check("apps: prefixo > palavra > contém", r[0].Id is "spotify" or "steam"
            && r.FindIndex(a => a.Id == "code") == 2 && r.FindIndex(a => a.Id == "settings") > 2);
        Check("apps: sem acento acha com acento", AppSearch.Search(apps, "config", 10)[0].Id == "settings");
        Check("apps: palavra interna", AppSearch.Search(apps, "code", 10)[0].Id == "code");
        Check("apps: contém no meio", AppSearch.Search(apps, "otif", 10).Count == 1);
        Check("apps: busca vazia = nada", AppSearch.Search(apps, "  ", 10).Count == 0);
        Check("apps: limite", AppSearch.Search(apps, "e", 2).Count == 2);

        var favs = AppSearch.ToggleFavorite(new List<string>(), "code");
        Check("apps: fixar", favs.Count == 1 && favs[0] == "code");
        Check("apps: desafixar", AppSearch.ToggleFavorite(favs, "code").Count == 0);
        var cheio = Enumerable.Range(0, AppSearch.MaxFavorites).Select(i => "a" + i).ToList();
        Check("apps: não passa do máximo", AppSearch.ToggleFavorite(cheio, "novo").Count == AppSearch.MaxFavorites);

        Check("apps: desinstalador é ruído", AppSearch.IsNoise("Uninstall Foo") && AppSearch.IsNoise("Desinstalar Bar"));
        Check("apps: app normal não é ruído", !AppSearch.IsNoise("Spotify"));
        Check("apps: nome do ícone estável", AppSearch.IconFileName("x") == AppSearch.IconFileName("x")
            && AppSearch.IconFileName("x") != AppSearch.IconFileName("y") && AppSearch.IconFileName("x").EndsWith(".png"));

        Check("settings: favoritos default vazio", new Orbit.Models.OrbitSettings().FavoriteApps.Count == 0);
    }

    private static void AppIconTests()
    {
        // 2x2 BGRA, linhas de baixo pra cima → inverter
        var px = new byte[] { 1, 1, 1, 255, 2, 2, 2, 255, /* linha 2 */ 3, 3, 3, 255, 4, 4, 4, 255 };
        AppIcons.FlipRows(px, stride: 8, height: 2);
        Check("ícone: inverte linhas", px[0] == 3 && px[8] == 1);

        var semAlfa = new byte[] { 9, 9, 9, 0, 9, 9, 9, 0 };
        AppIcons.FixAlpha(semAlfa);
        Check("ícone: sem canal alfa vira opaco", semAlfa[3] == 255 && semAlfa[7] == 255);
        var comAlfa = new byte[] { 9, 9, 9, 0, 9, 9, 9, 128 };
        AppIcons.FixAlpha(comAlfa);
        Check("ícone: com alfa fica como está", comAlfa[3] == 0 && comAlfa[7] == 128);

        var t = Sta.Run(() => System.Threading.Thread.CurrentThread.GetApartmentState()).GetAwaiter().GetResult();
        Check("sta: roda numa thread STA", t == System.Threading.ApartmentState.STA);
    }

    private static void BridgeRulesTests()
    {
        Check("ponte: url https ok", BridgeRules.IsOpenableUrl("https://example.com/x?y=1"));
        Check("ponte: url http ok", BridgeRules.IsOpenableUrl(" http://a.b "));
        Check("ponte: javascript bloqueado", !BridgeRules.IsOpenableUrl("javascript:alert(1)"));
        Check("ponte: file bloqueado", !BridgeRules.IsOpenableUrl("file:///C:/Windows/notepad.exe"));
        Check("ponte: vazio/nulo bloqueado", !BridgeRules.IsOpenableUrl("") && !BridgeRules.IsOpenableUrl(null));
        Check("ponte: host", BridgeRules.Host("https://www.github.com/a") == "www.github.com");

        Check("ponte: bearer", BridgeRules.BearerToken("Bearer abc123 ") == "abc123");
        Check("ponte: bearer sem caixa", BridgeRules.BearerToken("bearer x") == "x");
        Check("ponte: sem bearer = nulo", BridgeRules.BearerToken("Basic x") is null && BridgeRules.BearerToken(null) is null);

        Check("ponte: nome descarta pastas (traversal)", BridgeRules.SafeFileName("../../etc/passwd") == "passwd");
        Check("ponte: nome descarta caminho absoluto", BridgeRules.SafeFileName(@"C:\Windows\x.exe") == "x.exe");
        Check("ponte: nome troca inválidos", BridgeRules.SafeFileName("a:b?.txt") == "a_b_.txt");
        Check("ponte: nome reservado do Windows", BridgeRules.SafeFileName("CON.txt") == "_CON.txt" && BridgeRules.SafeFileName("nul") == "_nul");
        Check("ponte: nome vazio/pontos", BridgeRules.SafeFileName("") == "arquivo" && BridgeRules.SafeFileName("..") == "arquivo");
        Check("ponte: ponto final removido", BridgeRules.SafeFileName("foto.jpg. ") == "foto.jpg");
        Check("ponte: nome longo mantém extensão", BridgeRules.SafeFileName(new string('a', 300) + ".png") is { Length: 120 } n && n.EndsWith(".png"));

        Check("ponte: extensão longa não lança e sem ponto final", Safe("a." + new string('b', 200)) is { } e1 && e1.Length <= 120 && !e1.EndsWith('.'));
        Check("ponte: corte não deixa espaço final", !BridgeRules.SafeFileName(new string('a', 119) + " b").EndsWith(' '));
        Check("ponte: bidi/format trocado", !BridgeRules.SafeFileName("foto\u202Egpj.exe").Contains('\u202E'));

        var existing = new HashSet<string> { @"C:\up\foto.jpg", @"C:\up\foto (1).jpg" };
        Check("ponte: nome único", BridgeRules.UniqueName(@"C:\up", "foto.jpg", existing.Contains) == "foto (2).jpg");
        Check("ponte: nome livre fica igual", BridgeRules.UniqueName(@"C:\up", "novo.jpg", existing.Contains) == "novo.jpg");

        Check("ponte: ação de energia", BridgeRules.ParsePower("Shutdown") == Orbit.Services.PowerAction.Desligar
            && BridgeRules.ParsePower("lock") == Orbit.Services.PowerAction.Bloquear && BridgeRules.ParsePower("x") is null);
        Check("ponte: comando de mídia", BridgeRules.ParseMedia("playpause") == MediaCommand.PlayPause
            && BridgeRules.ParseMedia("PREV") == MediaCommand.Prev && BridgeRules.ParseMedia("x") is null);
        Check("ponte: GET de stats/mídia não audita", !BridgeRules.ShouldAudit("GET", "/api/stats") && !BridgeRules.ShouldAudit("GET", "/api/media"));
        Check("ponte: o resto audita", BridgeRules.ShouldAudit("POST", "/api/links/open") && BridgeRules.ShouldAudit("GET", "/api/clipboard"));
        Check("ponte: prévia curta numa linha", BridgeRules.Preview("a\nb") == "a b" && BridgeRules.Preview(new string('x', 100)).Length == 61);
        Check("ponte: link de pareamento", BridgeRules.PairingLink("https://pc.tail1.ts.net/", "tok-_1") == "https://pc.tail1.ts.net/#token=tok-_1");
        Check("ponte: endereço normalizado", BridgeRules.NormalizeBaseUrl(" https://pc.ts.net/ ") == "https://pc.ts.net"
            && BridgeRules.NormalizeBaseUrl("pc.ts.net") == "https://pc.ts.net" && BridgeRules.NormalizeBaseUrl("  ") == "");
    }

    private static string? Safe(string n)
    {
        try { return BridgeRules.SafeFileName(n); } catch { return null; }
    }

    private static void TokenAuditTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tokens.json");
        var store = new TokenStore(path);
        var t1 = store.Create("iPhone");
        var t2 = store.Create("Atalhos");
        Check("token: longo e diferente", t1.Length >= 40 && t1 != t2);
        Check("token: verifica e devolve o nome", store.Verify(t1) == "iPhone" && store.Verify(t2) == "Atalhos");
        Check("token: errado/vazio não passa", store.Verify("x") is null && store.Verify("") is null && store.Verify(null) is null);
        Check("token: arquivo não guarda o token", !File.ReadAllText(path).Contains(t1));
        var reloaded = new TokenStore(path);
        Check("token: persiste", reloaded.Verify(t1) == "iPhone" && reloaded.List().Count == 2);
        reloaded.Revoke(reloaded.List().First(t => t.Name == "iPhone").Hash);
        Check("token: revogado não passa", reloaded.Verify(t1) is null && reloaded.Verify(t2) == "Atalhos");
        File.WriteAllText(path, "{ quebrado");
        Check("token: arquivo corrompido = nenhum token", new TokenStore(path).List().Count == 0);
        File.WriteAllText(path, "[{\"Name\":\"x\",\"Hash\":\"" + new string('Z', 64) + "\",\"CreatedAt\":\"2026-01-01T00:00:00\"}]");
        Check("token: hash não-hex descartado", new TokenStore(path).List().Count == 0);

        var lockedPath = Path.Combine(dir, "travado.json");
        var locked = new TokenStore(lockedPath);
        var tl = locked.Create("a");
        File.Delete(lockedPath);
        Directory.CreateDirectory(lockedPath);
        File.WriteAllText(Path.Combine(lockedPath, "x"), "x"); // File.Move por cima de pasta não vazia falha
        bool threw = false;
        try { locked.Revoke(locked.List()[0].Hash); } catch { threw = true; }
        Check("token: revogar com disco travado lança e mantém consistência", threw && locked.Verify(tl) == "a" && locked.List().Count == 1);

        var auditPath = Path.Combine(dir, "audit.jsonl");
        var audit = new AuditLog(auditPath);
        AuditEntry? seen = null;
        audit.Written += e => seen = e;
        audit.Write("iPhone", "POST /api/links/open", "204");
        audit.Write("iPhone", "PUT /api/clipboard", "erro");
        var lines = File.ReadAllLines(auditPath);
        Check("auditoria: uma linha JSON por ação", lines.Length == 2 && lines[0].Contains("\"acao\":\"POST /api/links/open\"") && lines[1].Contains("\"resultado\":\"erro\""));
        Check("auditoria: recentes, mais nova primeiro", audit.RecentEntries()[0].Action == "PUT /api/clipboard");
        Check("auditoria: evento", seen?.Result == "erro");
        Directory.Delete(dir, recursive: true);
    }

    private static void BridgeServerTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tokens = new TokenStore(Path.Combine(dir, "tokens.json"));
        var token = tokens.Create("teste");
        var audit = new AuditLog(Path.Combine(dir, "audit.jsonl"));
        string? opened = null, clip = null;
        Orbit.Services.PowerAction? power = null;
        MediaCommand? media = null;
        double? volume = null;
        var events = new List<BridgeEvent>();
        var handlers = new BridgeHandlers
        {
            OpenUrl = u => { opened = u; return Task.CompletedTask; },
            GetClipboard = () => Task.FromResult("do pc"),
            SetClipboard = t => { clip = t; return Task.CompletedTask; },
            Power = a => { power = a; return Task.CompletedTask; },
            Media = m => { media = m; return Task.CompletedTask; },
            GetMedia = () => Task.FromResult(new MediaState("Faixa", "Banda", true, 40)),
            SetVolume = v => { volume = v; return Task.CompletedTask; },
            GetStats = () => new StatsSnapshot(12, 4.5, 16, 100, 500, 80, true),
            UploadFolder = () => Path.Combine(dir, "up"),
            Notify = e => { lock (events) events.Add(e); },
        };

        var server = new BridgeServer();
        server.StartAsync(0, Path.Combine(dir, "www-inexistente"), tokens, audit, handlers).GetAwaiter().GetResult();
        Check("ponte: servidor sobe numa porta", server.Running && server.Port > 0);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        Check("ponte: /health sem token", http.GetAsync("/health").Result.StatusCode == HttpStatusCode.OK);
        Check("ponte: /api sem token = 401", http.GetAsync("/api/stats").Result.StatusCode == HttpStatusCode.Unauthorized);
        using (var ruim = new StringContent("{ruim", System.Text.Encoding.UTF8, "application/json"))
            Check("ponte: corpo ruim sem token = 401 (auth antes do body)", http.PutAsync("/api/clipboard", ruim).Result.StatusCode == HttpStatusCode.Unauthorized);
        for (int i = 0; i < 130; i++) http.GetAsync("/api/stats").Result.Dispose();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Check("ponte: requisições sem token não gastam a cota", http.GetAsync("/api/stats").Result.StatusCode == HttpStatusCode.OK);
        using (var grande = new StringContent("{\"text\":\"" + new string('x', 2 * 1024 * 1024) + "\"}", System.Text.Encoding.UTF8, "application/json"))
        {
            var code = http.PutAsync("/api/clipboard", grande).Result.StatusCode;
            Check("ponte: JSON de 2 MB = 413", code == HttpStatusCode.RequestEntityTooLarge);
        }
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "errado");
        Check("ponte: token errado = 401", http.GetAsync("/api/stats").Result.StatusCode == HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var stats = http.GetStringAsync("/api/stats").Result;
        Check("ponte: stats em JSON camelCase", stats.Contains("\"cpu\":12") && stats.Contains("\"batteryPercent\":80"));

        var bad = http.PostAsJsonAsync("/api/links/open", new { url = "javascript:alert(1)" }).Result;
        Check("ponte: link inválido = 400 e não abre", bad.StatusCode == HttpStatusCode.BadRequest && opened is null);
        var ok = http.PostAsJsonAsync("/api/links/open", new { url = "https://example.com/a" }).Result;
        Check("ponte: link válido abre e avisa", ok.StatusCode == HttpStatusCode.NoContent && opened == "https://example.com/a"
            && events.Exists(e => e.Detail == "example.com"));

        var put = http.PutAsJsonAsync("/api/clipboard", new { text = "do iPhone" }).Result;
        Check("ponte: PUT clipboard", put.StatusCode == HttpStatusCode.NoContent && clip == "do iPhone");
        Check("ponte: GET clipboard", http.GetStringAsync("/api/clipboard").Result.Contains("do pc"));

        Check("ponte: energia", http.PostAsync("/api/power/shutdown", null).Result.StatusCode == HttpStatusCode.NoContent
            && power == Orbit.Services.PowerAction.Desligar);
        Check("ponte: energia desconhecida = 404", http.PostAsync("/api/power/explodir", null).Result.StatusCode == HttpStatusCode.NotFound);
        Check("ponte: mídia", http.PostAsync("/api/media/next", null).Result.StatusCode == HttpStatusCode.NoContent && media == MediaCommand.Next);
        Check("ponte: estado da mídia", http.GetStringAsync("/api/media").Result.Contains("\"title\":\"Faixa\""));
        Check("ponte: volume clampado", http.PutAsJsonAsync("/api/media/volume", new { percent = 150 }).Result.StatusCode == HttpStatusCode.NoContent
            && volume == 100);

        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "../evil.txt");
            var up = http.PostAsync("/api/files", form).Result;
            Check("ponte: upload salva com nome seguro", up.StatusCode == HttpStatusCode.OK && File.Exists(Path.Combine(dir, "up", "evil.txt")));
        }
        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(new byte[] { 4 }), "file", "evil.txt");
            http.PostAsync("/api/files", form).Result.Dispose();
            Check("ponte: upload não sobrescreve", File.Exists(Path.Combine(dir, "up", "evil (1).txt")));
        }
        using (var muitos = new MultipartFormDataContent())
        {
            for (int i = 0; i < 51; i++) muitos.Add(new ByteArrayContent(new byte[] { 1 }), "file", $"m{i}.txt");
            Check("ponte: mais de 50 arquivos = 400", http.PostAsync("/api/files", muitos).Result.StatusCode == HttpStatusCode.BadRequest);
        }
        using (var semArquivo = new MultipartFormDataContent { { new StringContent("x"), "nada" } })
            Check("ponte: upload sem arquivo = 400", http.PostAsync("/api/files", semArquivo).Result.StatusCode == HttpStatusCode.BadRequest);

        var log = File.ReadAllText(Path.Combine(dir, "audit.jsonl"));
        Check("ponte: auditoria registra ações", log.Contains("POST /api/links/open") && log.Contains("\"token\":\"teste\""));
        Check("ponte: 401 em flood audita no máximo 1 por segundo", log.Split("\"token\":\"?\"").Length - 1 < 20);
        Check("ponte: auditoria ignora polling de stats", !log.Contains("GET /api/stats\",\"resultado\":\"200"));

        tokens.Revoke(tokens.List()[0].Hash);
        Check("ponte: token revogado = 401", http.GetAsync("/api/stats").Result.StatusCode == HttpStatusCode.Unauthorized);

        server.StopAsync().GetAwaiter().GetResult();
        Check("ponte: para", !server.Running);
        Directory.Delete(dir, recursive: true);
    }

    public static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _failures++;
    }
}
