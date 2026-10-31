using System;
using System.Threading.Tasks;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Net;
using Orbit.Bridge;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
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
        EnergiaExtrasTests();
        MixerTests();
        CameraPresetTests();
        StreamTicketTests();
        MjpegTests();
        CameraTests();
        CameraAuditTests();
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
        DeviceStoreTests();
        BridgeServerTests();
        IPhoneTests();
        TailscaleTests();
        PairingCodeTests();
        CameraStreamAuthTests();
        PcFilesTests();
        WebPushTests();
        RemoteScreenTests();
        MicTests();
        OutboxTests();
        KeyboardTests();
        SelfUpdateTests();
        IslandExtrasTests();
        SystemSignalsTests();
        PwaTests();

        Console.WriteLine(_failures == 0 ? "TODOS OK" : $"{_failures} FALHA(S)");
        return _failures == 0 ? 0 : 1;
    }

    // O stream é autenticado por tíquete (a <img> não manda cabeçalho): tem de passar pelo
    // middleware de token. Com tíquete válido e device inexistente, o handler falha em 409 — nunca 401.
    private static void CameraStreamAuthTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-cam-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var web = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        var camera = new Camera();
        var tickets = new StreamTickets();
        var handlers = new BridgeHandlers { CameraEnabled = () => true };
        var server = new BridgeServer();
        server.StartAsync(0, web, new TokenStore(Path.Combine(dir, "t.json")), new AuditLog(Path.Combine(dir, "a.jsonl")),
            handlers, null, camera, tickets).GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };

        var semTicket = http.GetAsync("/api/camera/stream").Result;
        Check("câmera: stream sem tíquete = 401", semTicket.StatusCode == HttpStatusCode.Unauthorized);

        var tk = tickets.Issue("device-inexistente", CameraPreset.Default, DateTime.Now);
        var comTicket = http.GetAsync($"/api/camera/stream?ticket={tk}").Result; // sem Authorization
        Check("câmera: stream com tíquete passa o token e falha no device (409)", comTicket.StatusCode == HttpStatusCode.Conflict);

        server.StopAsync().GetAwaiter().GetResult();
        camera.Dispose();
        Directory.Delete(dir, recursive: true);
    }

    // Tela remota: parse da entrada, tíquetes, sessões e rotas com captura/execução falsas
    // (nenhum teste mexe no mouse de verdade). Uma captura real confere que sai JPEG.
    private static void RemoteScreenTests()
    {
        var abs = RemoteInput.Parse("{\"t\":\"abs\",\"x\":0.5,\"y\":1}");
        Check("remoto: abs", abs is { Kind: RemoteKind.Abs, X: 0.5, Y: 1 });
        Check("remoto: abs fora de 0..1 recusado", RemoteInput.Parse("{\"t\":\"abs\",\"x\":1.5,\"y\":0}") is null);
        Check("remoto: move", RemoteInput.Parse("{\"t\":\"move\",\"dx\":-12,\"dy\":3.5}") is { Kind: RemoteKind.Move, X: -12, Y: 3.5 });
        Check("remoto: move gigante recusado", RemoteInput.Parse("{\"t\":\"move\",\"dx\":99999,\"dy\":0}") is null);
        Check("remoto: clique direito duplo", RemoteInput.Parse("{\"t\":\"click\",\"b\":\"right\",\"n\":2}") is { Kind: RemoteKind.Click, Right: true, Count: 2 });
        Check("remoto: clique padrão = esquerdo simples", RemoteInput.Parse("{\"t\":\"click\"}") is { Right: false, Count: 1 });
        Check("remoto: rolar", RemoteInput.Parse("{\"t\":\"scroll\",\"dy\":-120}") is { Kind: RemoteKind.Scroll, X: 0, Y: -120 });
        Check("remoto: texto", RemoteInput.Parse("{\"t\":\"text\",\"s\":\"olá ✓\"}") is { Kind: RemoteKind.Text, Text: "olá ✓" });
        Check("remoto: texto grande recusado", RemoteInput.Parse("{\"t\":\"text\",\"s\":\"" + new string('a', RemoteInput.MaxText + 1) + "\"}") is null);
        var ctrlC = RemoteInput.Parse("{\"t\":\"key\",\"k\":\"c\",\"mods\":[\"ctrl\"]}");
        Check("remoto: Ctrl+C", ctrlC is { Kind: RemoteKind.Key, Vk: 0x43 } && ctrlC.Mods!.SequenceEqual(new ushort[] { 0x11 }));
        Check("remoto: tecla desconhecida recusada", RemoteInput.Parse("{\"t\":\"key\",\"k\":\"launchmissiles\"}") is null);
        Check("remoto: modificador desconhecido recusado", RemoteInput.Parse("{\"t\":\"key\",\"k\":\"a\",\"mods\":[\"hyper\"]}") is null);
        Check("remoto: lixo recusado", RemoteInput.Parse("{ruim") is null && RemoteInput.Parse("{\"t\":\"format\"}") is null);
        Check("remoto: sequência do atalho", RemoteInput.KeySequence(0x43, new ushort[] { 0x11, 0x10 }).SequenceEqual(
            new (ushort, bool)[] { (0x11, false), (0x10, false), (0x43, false), (0x43, true), (0x10, true), (0x11, true) }));
        var b = new System.Drawing.Rectangle(-2560, 76, 2560, 1440);
        Check("remoto: abs no monitor da esquerda", RemoteInput.ToScreen(0, 0, b) == new System.Drawing.Point(-2560, 76)
            && RemoteInput.ToScreen(1, 1, b) == new System.Drawing.Point(-1, 1515));

        var screens = ScreenCapture.Order(new[] { ("B", new System.Drawing.Rectangle(-2560, 0, 2560, 1440), false), ("A", new System.Drawing.Rectangle(0, 0, 1920, 1080), true) });
        Check("tela: principal é o 1", screens[0].Id == "A" && screens[0].Name == "1 · 1920×1080" && screens[1].Name == "2 · 2560×1440");
        Check("tela: reduz mantendo proporção", ScreenCapture.Fit(new System.Drawing.Size(2560, 1600), 960) == new System.Drawing.Size(960, 600));
        Check("tela: não amplia", ScreenCapture.Fit(new System.Drawing.Size(800, 600), 960) == new System.Drawing.Size(800, 600));
        Check("tela: qualidade", ScreenQuality.Parse("nitida") == ScreenQuality.Nitida && ScreenQuality.Parse("original") == ScreenQuality.Original
            && ScreenQuality.Parse("x") == ScreenQuality.Leve);
        Check("tela: original não reduz", ScreenCapture.Fit(new System.Drawing.Size(2560, 1600), ScreenQuality.Original.MaxWidth) == new System.Drawing.Size(2560, 1600));
        var real = ScreenCapture.Screens();
        var shot = real.Count > 0 ? ScreenCapture.Capture(real[0].Bounds, ScreenQuality.Leve) : null;
        Check("tela: captura real sai JPEG", shot is { Length: > 1000 } && shot[0] == 0xFF && shot[1] == 0xD8);

        var tk = new OneShotTickets<string>();
        var t1 = tk.Issue("x", DateTime.Now);
        Check("tela: tíquete uso único", tk.TryClaim(t1, DateTime.Now, out var v1) && v1 == "x" && !tk.TryClaim(t1, DateTime.Now, out _));
        var t2 = tk.Issue("y", DateTime.Now);
        Check("tela: tíquete vencido", !tk.TryClaim(t2, DateTime.Now.AddSeconds(31), out _));

        Check("tela: texto da pílula", IslandViewModel.RemoteActivity(new RemoteState(true, false, false)) == "Tela no iPhone"
            && IslandViewModel.RemoteActivity(new RemoteState(true, true, false)) == "iPhone controlando o PC"
            && IslandViewModel.RemoteActivity(new RemoteState(false, false, false)) is null);
        Check("mic: texto da pílula", IslandViewModel.RemoteActivity(new RemoteState(false, false, true)) == "Microfone no iPhone"
            && IslandViewModel.RemoteActivity(new RemoteState(true, false, true)) == "Tela no iPhone · microfone");
        Check("mic: texto da aba iPhone", Orbit.Tabs.IPhone.IPhoneViewModel.IPhoneRemoteText(new RemoteState(true, true, true))
            == "vendo e controlando a tela e ouvindo o microfone");

        // Rotas
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tela-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tokens = new TokenStore(Path.Combine(dir, "t.json"));
        var token = tokens.Create("iPhone teste");
        bool screenOn = false, controlOn = false;
        var fakeScreen = new ScreenInfo("DISPLAY1", "1 · 640×480", new System.Drawing.Rectangle(0, 0, 640, 480));
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9 };
        var received = new System.Collections.Concurrent.ConcurrentQueue<RemoteCommand>();
        var states = new System.Collections.Concurrent.ConcurrentQueue<RemoteState>();
        var handlers = new BridgeHandlers
        {
            ScreenEnabled = () => screenOn,
            ControlEnabled = () => controlOn,
            Screens = () => new[] { fakeScreen },
            CaptureScreen = (_, _) => jpeg,
            RemoteInput = (c, _) => received.Enqueue(c),
            RemoteChanged = st => states.Enqueue(st),
        };
        var server = new BridgeServer();
        server.StartAsync(0, Path.Combine(dir, "sem-web"), tokens, new AuditLog(Path.Combine(dir, "a.jsonl")), handlers).GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Check("tela: desligada = 403", http.GetAsync("/api/screen/list").Result.StatusCode == HttpStatusCode.Forbidden);
        screenOn = true;
        Check("tela: lista", http.GetStringAsync("/api/screen/list").Result.Contains("DISPLAY1"));
        Check("tela: controle desligado = 403", http.PostAsJsonAsync("/api/remote/ticket", new { screen = "DISPLAY1" }).Result.StatusCode == HttpStatusCode.Forbidden);
        using var anon = new HttpClient { BaseAddress = http.BaseAddress };
        Check("tela: stream sem tíquete = 401", anon.GetAsync("/api/screen/stream").Result.StatusCode == HttpStatusCode.Unauthorized);
        var vt = http.PostAsJsonAsync("/api/screen/open", new { screen = "DISPLAY1", quality = "leve" }).Result
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().Result.GetProperty("ticket").GetString();
        using (var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5)))
        using (var resp = anon.GetAsync($"/api/screen/stream?ticket={vt}", HttpCompletionOption.ResponseHeadersRead, cts.Token).Result)
        {
            var stream = resp.Content.ReadAsStream(cts.Token);
            var buf = new byte[256];
            int n = 0;
            while (n < 80) { int r = stream.Read(buf, n, buf.Length - n); if (r == 0) break; n += r; }
            var head = System.Text.Encoding.ASCII.GetString(buf, 0, n);
            Check("tela: stream MJPEG com tíquete (sem token)", resp.StatusCode == HttpStatusCode.OK
                && resp.Content.Headers.ContentType?.MediaType == "multipart/x-mixed-replace" && head.Contains("Content-Type: image/jpeg"));
            System.Threading.Thread.Sleep(200);
            Check("tela: sessão de vídeo avisa a ilha", states.Contains(new RemoteState(true, false, false)));
        }
        Check("tela: tíquete do stream é de uso único", anon.GetAsync($"/api/screen/stream?ticket={vt}").Result.StatusCode == HttpStatusCode.Unauthorized);

        controlOn = true;
        var it = http.PostAsJsonAsync("/api/remote/ticket", new { screen = "DISPLAY1" }).Result
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().Result.GetProperty("ticket").GetString();
        using (var ws = new System.Net.WebSockets.ClientWebSocket())
        {
            ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/api/remote/input?ticket={it}"), System.Threading.CancellationToken.None).Wait();
            foreach (var m in new[] { "{\"t\":\"abs\",\"x\":0.25,\"y\":0.5}", "{\"t\":\"click\"}", "{lixo", "{\"t\":\"text\",\"s\":\"oi\"}" })
                ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(m), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None).Wait();
            for (int i = 0; i < 50 && received.Count < 3; i++) System.Threading.Thread.Sleep(20);
            var got = received.ToArray();
            Check("tela: WebSocket entrega os comandos válidos e ignora lixo", got.Length == 3 && got[0].Kind == RemoteKind.Abs
                && got[1].Kind == RemoteKind.Click && got[2].Text == "oi");
            Check("tela: controle avisa a ilha", states.Any(st => st.Controlling));
            server.StopRemote(); // o "Encerrar" da aba iPhone
            for (int i = 0; i < 50 && ws.State == System.Net.WebSockets.WebSocketState.Open; i++)
            {
                try { ws.ReceiveAsync(new byte[16], new System.Threading.CancellationTokenSource(100).Token).Wait(); } catch { }
            }
            Check("tela: Encerrar derruba o controle", ws.State != System.Net.WebSockets.WebSocketState.Open && states.Last() == new RemoteState(false, false, false));
        }
        using (var ws2 = new System.Net.WebSockets.ClientWebSocket())
        {
            var ok = true;
            try { ws2.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/api/remote/input?ticket={it}"), System.Threading.CancellationToken.None).Wait(); }
            catch { ok = false; }
            Check("tela: tíquete do controle é de uso único", !ok);
        }
        server.StopAsync().GetAwaiter().GetResult();
        Directory.Delete(dir, recursive: true);
    }

    // Eventos do sistema na pílula: alerta com chave substitui o anterior (girar o volume não enfileira).
    private static void SystemSignalsTests()
    {
        var m = new IslandMachine();
        m.RaisePeek(new Peek("Volume · 40%", null, null) { Key = "sinal:Volume" });
        m.RaisePeek(new Peek("Volume · 45%", null, null) { Key = "sinal:Volume" });
        m.RaisePeek(new Peek("Volume · 50%", null, null) { Key = "sinal:Volume" });
        Check("sinais: volume substitui em vez de enfileirar", m.CurrentPeek?.Title == "Volume · 50%");
        m.PeekDone();
        Check("sinais: nada sobra na fila", m.CurrentPeek is null);
        m.RaisePeek(new Peek("Claude · orbit2.0", null, "claude"));
        m.RaisePeek(new Peek("Volume · 60%", null, null) { Key = "sinal:Volume" });
        m.RaisePeek(new Peek("Volume · 70%", null, null) { Key = "sinal:Volume" });
        Check("sinais: alerta comum não é atropelado", m.CurrentPeek?.Title == "Claude · orbit2.0");
        m.PeekDone();
        Check("sinais: na fila fica só o volume mais novo", m.CurrentPeek?.Title == "Volume · 70%");

        Check("sinais: texto do volume", SystemSignals.VolumeSignal(0.643f, false) is { Title: "Volume · 64%", Level: > 0.64 and < 0.65 });
        Check("sinais: mudo", SystemSignals.VolumeSignal(0.5f, true) is { Title: "Sem som", Level: 0 });
        Check("sinais: carregando", SystemSignals.PowerSignal(64, true, null) is { Title: "Carregando · 64%", Detail: null });
        Check("sinais: na bateria com tempo", SystemSignals.PowerSignal(80, false, 3 * 3600) is { Title: "Na bateria · 80%" } p && p.Detail!.EndsWith("restantes"));
        var peek = IslandViewModel.SignalPeek(new SystemSignal(SignalKind.Volume, "Volume · 10%", null, 0.1));
        Check("sinais: volume some em 1,5 s com barra", peek.Duration == TimeSpan.FromSeconds(1.5) && peek.HasLevel && peek.Key == "sinal:Volume");
        Check("sinais: carregador fica 2,5 s", IslandViewModel.SignalPeek(new SystemSignal(SignalKind.Power, "x", null)).Duration == TimeSpan.FromSeconds(2.5));

        var netsh = "\r\nHá 1 interface no sistema:\r\n\r\n    Nome                   : Wi-Fi\r\n    Estado                 : conectado\r\n"
            + "    SSID                   : Casa 5G\r\n    BSSID                  : aa:bb:cc:dd:ee:ff\r\n    Tipo de rede           : Infraestrutura\r\n";
        Check("sinais: nome do Wi-Fi (netsh em português)", SystemSignals.ParseSsid(netsh) == "Casa 5G");
        Check("sinais: sem Wi-Fi", SystemSignals.ParseSsid("Não há nenhuma interface sem fio no sistema.") is null);

        // Wi-Fi por QR
        var perfil = "    Autenticação           : WPA3-Personal\r\n    Cifra                  : CCMP\r\n    Conteúdo da Chave      : s3nh@;forte\r\n";
        var wifi = WifiShare.Parse("Casa 5G", perfil);
        Check("wifi: lê a senha do netsh", wifi is { Ssid: "Casa 5G", Password: "s3nh@;forte", Open: false });
        Check("wifi: QR escapa ; : , \\ \"", WifiShare.QrPayload(new WifiInfo("A;B:C", "x,y\\z\"", false)) == "WIFI:T:WPA;S:A\\;B\\:C;P:x\\,y\\\\z\\\";;");
        Check("wifi: rede aberta sem senha", WifiShare.QrPayload(WifiShare.Parse("Cafe", "    Autenticação : Aberta\r\n")) == "WIFI:T:nopass;S:Cafe;;");
        Check("wifi: netsh em inglês", WifiShare.Parse("Home", "    Authentication : WPA2-Personal\r\n    Key Content : abc12345\r\n").Password == "abc12345");

        // Chuva: um aviso por chuva
        var chuva = new RainAlert();
        Check("chuva: tempo firme não avisa", chuva.Next(new RainForecast(0, new[] { 0.0, 0, 0, 0, 0, 0, 0, 0 })) is null);
        Check("chuva: chuva em 30 min avisa", chuva.Next(new RainForecast(0, new[] { 0.0, 0, 0.6, 1.2, 0, 0, 0, 0 })) == 30);
        Check("chuva: não repete na mesma chuva", chuva.Next(new RainForecast(0, new[] { 0.0, 0.5, 1, 0, 0, 0, 0, 0 })) is null
            && chuva.Next(new RainForecast(2.0, new[] { 1.0, 0, 0, 0, 0, 0, 0, 0 })) is null);
        Check("chuva: depois de firmar, avisa de novo", chuva.Next(new RainForecast(0, new[] { 0.0, 0, 0, 0, 0, 0, 0, 0 })) is null
            && chuva.Next(new RainForecast(0, new[] { 0.3, 0, 0, 0, 0, 0, 0, 0 })) == 15);
        Check("chuva: garoa abaixo de 0,1 mm não conta", new RainAlert().Next(new RainForecast(0, new[] { 0.05, 0.05, 0, 0, 0, 0, 0, 0 })) is null);
        Check("chuva: chuva depois de 1 h ainda não avisa", new RainAlert().Next(new RainForecast(0, new[] { 0.0, 0, 0, 0, 1, 1, 0, 0 })) is null);
        Check("chuva: textos", RainAlert.Text(15) == "Chuva chegando em instantes" && RainAlert.Text(45) == "Chuva em ~45 min");
    }

    // Barrinhas da música, câmera/microfone em uso e conta-gotas.
    private static void IslandExtrasTests()
    {
        var silencio = AudioMeter.Bars(0, 1.0);
        Check("barras: silêncio deixa só o tracinho", silencio.Length == 4 && silencio.All(h => Math.Abs(h - 0.15) < 1e-9));
        var alto = AudioMeter.Bars(1, 1.0);
        Check("barras: som alto sobe e fica em 0..1", alto.All(h => h >= 0.15 && h <= 1) && alto.Max() > 0.6);
        Check("barras: cada barra numa altura", alto.Distinct().Count() > 1);

        var m = new IslandMachine();
        Check("pílula: sem atividade", m.CurrentActivity is null);
        m.SetActivity(ActivityKind.Midia, "música");
        m.SetActivity(ActivityKind.CameraMic, "Discord · microfone");
        Check("pílula: câmera/microfone passa na frente da música", m.CurrentActivity == ActivityKind.CameraMic && m.ActivityText == "Discord · microfone");
        m.SetActivity(ActivityKind.TelaRemota, "Tela no iPhone");
        Check("pílula: sessão do iPhone continua no topo", m.CurrentActivity == ActivityKind.TelaRemota);

        var t = new DateTime(2026, 10, 18, 20, 0, 0).ToFileTime();
        var raw = new[]
        {
            ("webcam", @"C:\Apps\Teams.exe", t, 0L),
            ("microphone", @"C:\Apps\Teams.exe", t, 0L),
            ("webcam", @"C:\Apps\Velho.exe", t, t + 1),          // já terminou
            ("webcam", @"C:\Orbit\Orbit.exe", t, 0L),             // o próprio Orbit
            ("webcam", "Microsoft.WindowsCamera_8wekyb3d8bbwe", t, 0L),
        };
        var ativos = CameraMicWatcher.Active(raw, @"C:\Orbit\Orbit.exe");
        Check("câmera/mic: só o que está em uso, sem o Orbit", ativos.Count == 2 && ativos.Any(u => u.App == "Teams" && u.Camera && u.Mic)
            && ativos.Any(u => u.App == "WindowsCamera" && u.Camera && !u.Mic));
        var juntos = CameraMicWatcher.Merge(new[] { new CameraMicUse("Discord", true, false, new DateTime(2026, 1, 1, 10, 0, 0)) },
            new[] { new CameraMicUse("Discord", false, true, new DateTime(2026, 1, 1, 9, 0, 0)) });
        Check("câmera/mic: mesmo app vira uma linha com os dois", juntos.Count == 1 && juntos[0].Camera && juntos[0].Mic
            && juntos[0].Since == new DateTime(2026, 1, 1, 9, 0, 0));
        Check("câmera/mic: nome de pacote da loja", CameraMicWatcher.FriendlyName("SpotifyAB.SpotifyMusic_zpdnekdrzrea0") == "SpotifyMusic");
        Check("câmera/mic: exe sumido usa o nome do arquivo", CameraMicWatcher.FriendlyName(@"C:\nao\existe\Discord.exe") == "Discord");
        var now = new DateTime(2026, 10, 18, 20, 12, 30);
        Check("câmera/mic: texto de um app com tempo", CameraMicWatcher.Describe(new[] { new CameraMicUse("Teams", true, true, now.AddMinutes(-12)) }, now)
            == "Teams · câmera e microfone · 12 min");
        Check("câmera/mic: texto de vários", CameraMicWatcher.Describe(new[]
            {
                new CameraMicUse("Discord", false, true, now), new CameraMicUse("Brave", false, true, now),
            }, now) == "Microfone: Discord, Brave");
        Check("câmera/mic: nada em uso some da pílula", CameraMicWatcher.Describe(Array.Empty<CameraMicUse>(), now) is null);
        Check("câmera/mic: desenho da pílula com tempo", CameraMicWatcher.Display(new[] { new CameraMicUse("Zoom Meetings", false, true, now.AddMinutes(-12)) }, now)
            == new CameraMicDisplay("Zoom Meetings", false, true, "12 min"));
        Check("câmera/mic: desenho junta apps, ícones e passa de 1 h", CameraMicWatcher.Display(new[]
            {
                new CameraMicUse("Teams", true, false, now.AddMinutes(-75)), new CameraMicUse("Discord", false, true, now),
            }, now) == new CameraMicDisplay("Teams, Discord", true, true, "1 h 15"));
        Check("câmera/mic: recém-começado diz agora", CameraMicWatcher.Display(new[] { new CameraMicUse("X", false, true, now) }, now)!.Time == "agora");

        Check("conta-gotas: hex", Orbit.Island.FrozenScreen.Hex(System.Windows.Media.Color.FromRgb(30, 144, 255)) == "#1E90FF");
        var area = new System.Drawing.Rectangle(-3840, 0, 6400, 2274);
        Check("desenho: copia só o monitor (à esquerda)", Orbit.Island.DrawWindow.CropTo(new System.Drawing.Rectangle(-3840, 114, 3840, 2160), area)
            == new System.Drawing.Rectangle(0, 114, 3840, 2160));
        Check("desenho: copia só o monitor (principal)", Orbit.Island.DrawWindow.CropTo(new System.Drawing.Rectangle(0, 0, 2560, 1600), area)
            == new System.Drawing.Rectangle(3840, 0, 2560, 1600));
    }

    // Autoatualização: versão gravada no build, situação em relação ao código e o script de troca.
    private static void SelfUpdateTests()
    {
        Check("atualizar: versão com commit", SelfUpdate.ParseVersion("1.0.0+6ac9043") == ("6ac9043", false));
        Check("atualizar: versão suja", SelfUpdate.ParseVersion("1.0.0+6ac9043-dirty") == ("6ac9043", true));
        Check("atualizar: sem commit", SelfUpdate.ParseVersion("1.0.0") == (null, false) && SelfUpdate.ParseVersion(null) == (null, false));
        Check("atualizar: build atual tem commit gravado", SelfUpdate.Current.Commit is { Length: >= 7 });
        Check("atualizar: sabe onde está o código", SelfUpdate.RepoRoot is { } root && Directory.Exists(Path.Combine(root, ".git")));
        Check("atualizar: rodando de dentro do repo", SelfUpdate.IsInside(@"C:\repos\orbit2.0\Orbit\bin\Debug", @"C:\repos\orbit2.0")
            && !SelfUpdate.IsInside(@"C:\repos\orbit2.0-copia\bin", @"C:\repos\orbit2.0")
            && !SelfUpdate.IsInside(@"C:\Users\x\AppData\Local\Programs\Orbit2", @"C:\repos\orbit2.0"));

        Check("atualizar: texto em dia", SelfUpdate.Describe(new UpdateStatus("abc1234", 0, false, true, "")) == "Versão abc1234 · em dia com o código");
        Check("atualizar: texto com commits e sujeira", SelfUpdate.Describe(new UpdateStatus("abc1234", 3, true, true, ""))
            == "Versão abc1234 · 3 commits novos no código, código com alterações não commitadas");
        Check("atualizar: texto quando não dá", SelfUpdate.Describe(new UpdateStatus("abc1234", 0, false, false, "Rodando do código (Debug): use dotnet run"))
            == "Versão abc1234 · Rodando do código (Debug): use dotnet run");

        var now = new DateTime(2026, 10, 18, 15, 0, 0);
        Check("atualizar: troca de agora há pouco deu certo", SelfUpdate.RecentOutcome("2026-10-18 14:58:30 atualizado", now) is { Ok: true });
        Check("atualizar: troca desfeita", SelfUpdate.RecentOutcome("2026-10-18 14:59:00 falhou: x — voltando", now) is { Ok: false } r && r.Message.StartsWith("falhou"));
        Check("atualizar: troca antiga não avisa", SelfUpdate.RecentOutcome("2026-10-18 10:00:00 atualizado", now) is null
            && SelfUpdate.RecentOutcome("lixo", now) is null && SelfUpdate.RecentOutcome(null, now) is null);

        var script = SelfUpdate.SwapScript(1234, @"C:\Users\O'Neil\Programs\Orbit2", @"C:\tmp\publish", @"C:\tmp\update.log");
        Check("atualizar: script escapa aspas", script.Contains(@"'C:\Users\O''Neil\Programs\Orbit2'") && script.Contains("$leaf = 'Orbit2'"));
        Check("atualizar: script espera o Orbit fechar, preserva desinstalador e volta atrás",
            script.Contains("Wait-Process -Id 1234") && script.Contains("'unins*'") && script.Contains("Rename-Item $old $leaf")
            && script.Contains("$p.HasExited"));
    }

    // Teclado remoto: permissão própria e o canal só deixa passar texto e teclas (mouse é descartado).
    private static void KeyboardTests()
    {
        Check("teclado: filtro só texto e tecla", RemoteScreen.KeyboardOnly(new RemoteCommand(RemoteKind.Text, Text: "a"))
            && RemoteScreen.KeyboardOnly(new RemoteCommand(RemoteKind.Key, Vk: 0x0D))
            && !RemoteScreen.KeyboardOnly(new RemoteCommand(RemoteKind.Abs, 0.5, 0.5))
            && !RemoteScreen.KeyboardOnly(new RemoteCommand(RemoteKind.Click)));

        var dir = Path.Combine(Path.GetTempPath(), "orbit2-kbd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tokens = new TokenStore(Path.Combine(dir, "t.json"));
        var token = tokens.Create("iPhone teste");
        bool on = false;
        var got = new System.Collections.Concurrent.ConcurrentQueue<RemoteCommand>();
        var states = new System.Collections.Concurrent.ConcurrentQueue<RemoteState>();
        var handlers = new BridgeHandlers
        {
            KeyboardEnabled = () => on,
            RemoteInput = (c, _) => got.Enqueue(c),
            RemoteChanged = st => states.Enqueue(st),
        };
        var server = new BridgeServer();
        server.StartAsync(0, Path.Combine(dir, "sem-web"), tokens, new AuditLog(Path.Combine(dir, "a.jsonl")), handlers).GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Check("teclado: desligado = 403", http.PostAsync("/api/keyboard/ticket", null).Result.StatusCode == HttpStatusCode.Forbidden);
        on = true;
        var tk = http.PostAsync("/api/keyboard/ticket", null).Result
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().Result.GetProperty("ticket").GetString();
        using (var ws = new System.Net.WebSockets.ClientWebSocket())
        {
            ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/api/keyboard/input?ticket={tk}"), System.Threading.CancellationToken.None).Wait();
            foreach (var m in new[] { "{\"t\":\"text\",\"s\":\"olá do iPhone\"}", "{\"t\":\"abs\",\"x\":0.1,\"y\":0.1}",
                         "{\"t\":\"click\"}", "{\"t\":\"key\",\"k\":\"enter\"}" })
                ws.SendAsync(System.Text.Encoding.UTF8.GetBytes(m), System.Net.WebSockets.WebSocketMessageType.Text, true, System.Threading.CancellationToken.None).Wait();
            for (int i = 0; i < 50 && got.Count < 2; i++) System.Threading.Thread.Sleep(20);
            System.Threading.Thread.Sleep(100);
            var arr = got.ToArray();
            Check("teclado: texto e tecla passam, mouse é descartado", arr.Length == 2 && arr[0].Text == "olá do iPhone" && arr[1].Kind == RemoteKind.Key);
            Check("teclado: conta como controle na pílula", states.Any(st => st.Controlling));
        }
        Check("teclado: tíquete de uso único", !TryConnect($"ws://127.0.0.1:{server.Port}/api/keyboard/input?ticket={tk}"));
        server.StopAsync().GetAwaiter().GetResult();
        Directory.Delete(dir, recursive: true);
    }

    // PC → iPhone (caixa de saída) e foto do iPhone → clipboard do PC.
    private static void OutboxTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-out-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var src = Path.Combine(dir, "relatório final.pdf");
        File.WriteAllText(src, "conteúdo do pdf");
        var boxDir = Path.Combine(dir, "box");
        var box = new Outbox(boxDir);
        var t0 = new DateTime(2026, 10, 17, 10, 0, 0);
        var a = box.AddFile(src, t0);
        var srcLen = new FileInfo(src).Length;
        Check("saída: arquivo copiado", a is { Name: "relatório final.pdf" } && a.Size == srcLen && File.Exists(box.PathOf(a.Id, t0)!));
        File.Delete(src);
        Check("saída: original apagado não quebra", File.ReadAllText(box.PathOf(a!.Id, t0)!) == "conteúdo do pdf");
        var b = box.AddText("https://example.com/x", t0.AddMinutes(1));
        Check("saída: texto", b is { Text: "https://example.com/x", File: null });
        Check("saída: mais novo primeiro", box.List(t0.AddMinutes(2)).Select(i => i.Id).SequenceEqual(new[] { b!.Id, a.Id }));
        Check("saída: nome seguro", box.AddFile(Path.Combine(dir, "nao-existe.bin"), t0) is null && box.AddText("   ", t0) is null);
        Check("saída: sobrevive a reiniciar", new Outbox(boxDir).List(t0.AddMinutes(2)).Count == 2);
        var stored = box.PathOf(a.Id, t0)!;
        Check("saída: vence em 24 h e apaga o arquivo", box.List(t0.AddHours(24).AddMinutes(1)).Count == 1 && !File.Exists(stored));
        Check("saída: remover", box.Remove(b.Id) && box.List(t0.AddMinutes(3)).Count == 0 && !box.Remove(b.Id));

        Check("saída: aviso de 1 arquivo", IslandViewModel.OutboxAnnouncement(new[] { new OutboxItem("1", "foto.jpg", 2 * 1024 * 1024, "1.jpg", null, t0) })
            == ("Arquivo do PC", "foto.jpg · 2 MB"));
        Check("saída: aviso de vários", IslandViewModel.OutboxAnnouncement(new[] { new OutboxItem("1", "a.txt", 1, "1.txt", null, t0), new OutboxItem("2", "b.txt", 1, "2.txt", null, t0) })
            == ("2 arquivos do PC", "a.txt, b.txt"));

        Check("clip→iPhone: privado nunca sai", IslandViewModel.DecideClip(new[] { "a" }, true, true, "senha") == IslandViewModel.ClipToIPhone.Private);
        Check("clip→iPhone: arquivos do Explorer primeiro", IslandViewModel.DecideClip(new[] { "a" }, false, true, "x") == IslandViewModel.ClipToIPhone.Files);
        Check("clip→iPhone: imagem, depois texto, depois vazio", IslandViewModel.DecideClip(null, false, true, "x") == IslandViewModel.ClipToIPhone.Image
            && IslandViewModel.DecideClip(Array.Empty<string>(), false, false, "oi") == IslandViewModel.ClipToIPhone.Text
            && IslandViewModel.DecideClip(null, false, false, "  ") == IslandViewModel.ClipToIPhone.Empty);
        var link = new OutboxItem("1", "x", 1, null, "https://example.com/a", t0);
        var txt = new OutboxItem("2", "x", 1, null, new string('a', 200), t0);
        Check("clip→iPhone: link abre direto no iPhone", IslandViewModel.OutboxPushUrl(new[] { link }) == "https://example.com/a"
            && IslandViewModel.OutboxAnnouncement(new[] { link }) == ("Link do PC", "https://example.com/a"));
        Check("clip→iPhone: texto abre Recebidos, aviso resumido", IslandViewModel.OutboxPushUrl(new[] { txt }) == "./#recebidos"
            && IslandViewModel.OutboxAnnouncement(new[] { txt }).Body.Length == 161);

        var f1 = Path.Combine(dir, "um.txt");
        File.WriteAllText(f1, "x");
        Check("enviar para: só arquivos que existem", SendToIPhone.FilesFrom(new[] { "--send-iphone", f1, dir, Path.Combine(dir, "sumiu.txt"), f1 })
            .SequenceEqual(new[] { f1 }));

        Check("foto: EXIF 6 = 90°, 3 = 180°, 8 = 270°", PhotoDecoder.Rotation(6) == 90 && PhotoDecoder.Rotation(3) == 180 && PhotoDecoder.Rotation(8) == 270 && PhotoDecoder.Rotation(1) == 0);
        byte[] png;
        {
            var bmp = System.Windows.Media.Imaging.BitmapSource.Create(4, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[4 * 2 * 4], 16);
            var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
            enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
            using var ms = new MemoryStream();
            enc.Save(ms);
            png = ms.ToArray();
        }
        var decoded = PhotoDecoder.Decode(png);
        Check("foto: decodifica PNG sem EXIF", decoded.PixelWidth == 4 && decoded.PixelHeight == 2 && decoded.IsFrozen);
        bool threw = false;
        try { PhotoDecoder.Decode(new byte[] { 1, 2, 3 }); } catch { threw = true; }
        Check("foto: lixo não vira imagem", threw);

        // Rotas
        var tokens = new TokenStore(Path.Combine(dir, "t.json"));
        var token = tokens.Create("iPhone teste");
        var box2 = new Outbox(Path.Combine(dir, "box2"));
        var item = box2.AddFile(f1, DateTime.Now, "nome bonito.txt")!;
        byte[]? clip = null;
        var handlers = new BridgeHandlers
        {
            Outbox = box2,
            SetClipboardImage = bytes => { clip = bytes; return Task.FromResult(bytes.Length > 3); },
        };
        var server = new BridgeServer();
        server.StartAsync(0, Path.Combine(dir, "sem-web"), tokens, new AuditLog(Path.Combine(dir, "a.jsonl")), handlers).GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        using var anon = new HttpClient { BaseAddress = http.BaseAddress };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Check("saída: lista exige token", anon.GetAsync("/api/outbox").Result.StatusCode == HttpStatusCode.Unauthorized);
        Check("saída: lista", http.GetStringAsync("/api/outbox").Result.Contains("nome bonito.txt"));
        Check("saída: tíquete de item que não existe = 404", http.PostAsJsonAsync("/api/outbox/ticket", new { id = "zzz" }).Result.StatusCode == HttpStatusCode.NotFound);
        var tk = http.PostAsJsonAsync("/api/outbox/ticket", new { id = item.Id }).Result
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().Result.GetProperty("ticket").GetString();
        var down = anon.GetAsync($"/api/outbox/download?ticket={tk}").Result;
        Check("saída: download por tíquete com o nome bonito", down.StatusCode == HttpStatusCode.OK && down.Content.ReadAsStringAsync().Result == "x"
            && down.Content.Headers.ContentDisposition?.FileNameStar == "nome bonito.txt");
        Check("saída: tíquete errado = 401", anon.GetAsync("/api/outbox/download?ticket=errado").Result.StatusCode == HttpStatusCode.Unauthorized);
        Check("saída: apagar pelo iPhone", http.DeleteAsync($"/api/outbox/{item.Id}").Result.StatusCode == HttpStatusCode.NoContent
            && box2.List(DateTime.Now).Count == 0);

        using (var form = new MultipartFormDataContent { { new ByteArrayContent(png), "file", "foto.png" } })
            Check("foto: vai pro clipboard", http.PostAsync("/api/clipboard/image", form).Result.StatusCode == HttpStatusCode.NoContent && clip!.Length == png.Length);
        using (var form = new MultipartFormDataContent { { new ByteArrayContent(new byte[] { 1, 2 }), "file", "x.jpg" } })
            Check("foto: não-imagem = 400", http.PostAsync("/api/clipboard/image", form).Result.StatusCode == HttpStatusCode.BadRequest);
        server.StopAsync().GetAwaiter().GetResult();
        Directory.Delete(dir, recursive: true);
    }

    // Microfone: conversão pra PCM mono 16 bits e o WebSocket com um microfone falso (nenhum teste abre o de verdade).
    private static void MicTests()
    {
        Check("mic: fator 48k → 2, 44,1k → 2, 16k → 1", MicCapture.Factor(48000) == 2 && MicCapture.Factor(44100) == 2 && MicCapture.Factor(16000) == 1);
        // float estéreo: (0,5 ; 0,5) e (−0,5 ; −0,5)
        var f = new byte[16];
        BitConverter.GetBytes(0.5f).CopyTo(f, 0); BitConverter.GetBytes(0.5f).CopyTo(f, 4);
        BitConverter.GetBytes(-0.5f).CopyTo(f, 8); BitConverter.GetBytes(-0.5f).CopyTo(f, 12);
        var m = MicCapture.ToMonoPcm16(f, 2, true, 1);
        Check("mic: float estéreo → mono 16 bits", m.Length == 4 && BitConverter.ToInt16(m, 0) == 16383 && BitConverter.ToInt16(m, 2) == -16383);
        var avg = MicCapture.ToMonoPcm16(f, 2, true, 2);
        Check("mic: reduzir faz média (0,5 e −0,5 → 0)", avg.Length == 2 && BitConverter.ToInt16(avg, 0) == 0);
        var pcm = new byte[4];
        BitConverter.GetBytes((short)-16384).CopyTo(pcm, 0); BitConverter.GetBytes((short)8192).CopyTo(pcm, 2);
        var back = MicCapture.ToMonoPcm16(pcm, 1, false, 1);
        Check("mic: PCM 16 mono preservado", Math.Abs(BitConverter.ToInt16(back, 0) - -16384) <= 1 && Math.Abs(BitConverter.ToInt16(back, 2) - 8192) <= 1);
        Check("mic: limita no máximo", BitConverter.ToInt16(MicCapture.ToMonoPcm16(BitConverter.GetBytes(4f), 1, true, 1), 0) == short.MaxValue);

        var dir = Path.Combine(Path.GetTempPath(), "orbit2-mic-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tokens = new TokenStore(Path.Combine(dir, "t.json"));
        var token = tokens.Create("iPhone teste");
        bool micOn = false, stopped = false;
        Action<byte[]>? feed = null;
        var states = new System.Collections.Concurrent.ConcurrentQueue<RemoteState>();
        var handlers = new BridgeHandlers
        {
            MicEnabled = () => micOn,
            MicDevices = () => new[] { new MicDevice("mic-1", "Microfone teste") },
            OpenMic = (_, onPcm) => { feed = onPcm; return (24000, new Disposer(() => stopped = true)); },
            RemoteChanged = st => states.Enqueue(st),
        };
        var server = new BridgeServer();
        server.StartAsync(0, Path.Combine(dir, "sem-web"), tokens, new AuditLog(Path.Combine(dir, "a.jsonl")), handlers).GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Check("mic: desligado = 403", http.GetAsync("/api/mic/devices").Result.StatusCode == HttpStatusCode.Forbidden);
        micOn = true;
        Check("mic: lista", http.GetStringAsync("/api/mic/devices").Result.Contains("Microfone teste"));
        Check("mic: dispositivo desconhecido = 404", http.PostAsJsonAsync("/api/mic/open", new { deviceId = "nao-existe" }).Result.StatusCode == HttpStatusCode.NotFound);
        var tk = http.PostAsJsonAsync("/api/mic/open", new { deviceId = "mic-1", name = "Microfone teste" }).Result
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().Result.GetProperty("ticket").GetString();
        using (var ws = new System.Net.WebSockets.ClientWebSocket())
        {
            ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server.Port}/api/mic/stream?ticket={tk}"), System.Threading.CancellationToken.None).Wait();
            var buf = new byte[1024];
            var first = ws.ReceiveAsync(buf, System.Threading.CancellationToken.None).Result;
            Check("mic: 1ª mensagem diz a taxa", first.MessageType == System.Net.WebSockets.WebSocketMessageType.Text
                && System.Text.Encoding.UTF8.GetString(buf, 0, first.Count) == "{\"rate\":24000}");
            feed!(new byte[] { 1, 2, 3, 4 });
            var audio = ws.ReceiveAsync(buf, new System.Threading.CancellationTokenSource(3000).Token).Result;
            Check("mic: PCM chega em mensagem binária", audio.MessageType == System.Net.WebSockets.WebSocketMessageType.Binary
                && audio.Count == 4 && buf[0] == 1 && buf[3] == 4);
            Check("mic: ouvir avisa a ilha", states.Any(st => st.Listening));
            http.PostAsync("/api/mic/close", null).Result.Dispose();
            for (int i = 0; i < 50 && !stopped; i++) System.Threading.Thread.Sleep(20);
            System.Threading.Thread.Sleep(50);
            Check("mic: parar solta o microfone", stopped && states.Last() == new RemoteState(false, false, false));
        }
        Check("mic: tíquete de uso único", !TryConnect($"ws://127.0.0.1:{server.Port}/api/mic/stream?ticket={tk}"));
        server.StopAsync().GetAwaiter().GetResult();

        // Consentimento do Windows pendente: abrir fica parado; o iPhone recebe o motivo e, quando
        // autorizarem depois, o microfone é solto na hora (ninguém está ouvindo).
        var gate = new System.Threading.ManualResetEventSlim(false);
        bool lateStopped = false;
        var events = new System.Collections.Concurrent.ConcurrentQueue<BridgeEvent>();
        var waiting = new BridgeHandlers
        {
            MicEnabled = () => true,
            MicDevices = () => new[] { new MicDevice("mic-1", "Microfone teste") },
            OpenMic = (_, _) => { gate.Wait(); return (24000, new Disposer(() => lateStopped = true)); },
            Notify = e => events.Enqueue(e),
        };
        var oldTimeout = MicStream.ConsentTimeout;
        MicStream.ConsentTimeout = TimeSpan.FromMilliseconds(300);
        var server2 = new BridgeServer();
        server2.StartAsync(0, Path.Combine(dir, "sem-web"), tokens, new AuditLog(Path.Combine(dir, "a2.jsonl")), waiting).GetAwaiter().GetResult();
        using (var http2 = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server2.Port}") })
        {
            http2.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var tk2 = http2.PostAsJsonAsync("/api/mic/open", new { deviceId = "mic-1" }).Result
                .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().Result.GetProperty("ticket").GetString();
            using var ws = new System.Net.WebSockets.ClientWebSocket();
            ws.ConnectAsync(new Uri($"ws://127.0.0.1:{server2.Port}/api/mic/stream?ticket={tk2}"), System.Threading.CancellationToken.None).Wait();
            var r = ws.ReceiveAsync(new byte[64], new System.Threading.CancellationTokenSource(5000).Token).Result;
            Check("mic: consentimento pendente avisa o iPhone", r.MessageType == System.Net.WebSockets.WebSocketMessageType.Close
                && ws.CloseStatusDescription == MicStream.ConsentReason);
            Check("mic: e avisa quem está no PC", events.Any(e => e.Title.Contains("Autorize")));
            gate.Set(); // "autorizaram" depois
            for (int i = 0; i < 50 && !lateStopped; i++) System.Threading.Thread.Sleep(20);
            Check("mic: autorizado tarde = solto na hora", lateStopped);
        }
        MicStream.ConsentTimeout = oldTimeout;
        server2.StopAsync().GetAwaiter().GetResult();
        Directory.Delete(dir, recursive: true);
    }

    private sealed class Disposer(Action onDispose) : IDisposable { public void Dispose() => onDispose(); }

    private static bool TryConnect(string url)
    {
        using var ws = new System.Net.WebSockets.ClientWebSocket();
        try { ws.ConnectAsync(new Uri(url), System.Threading.CancellationToken.None).Wait(); return true; }
        catch { return false; }
    }

    // Web Push: o teste faz o papel do iPhone — decifra o payload (RFC 8291) com a chave privada do
    // "aparelho" e confere a assinatura VAPID com a chave pública que o PWA recebe.
    private static void WebPushTests()
    {
        using System.Security.Cryptography.ECDiffieHellman ua = System.Security.Cryptography.ECDiffieHellman.Create(
            System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var uaPublic = WebPush.Uncompressed(ua.ExportParameters(false).Q);
        var auth = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var msg = System.Text.Encoding.UTF8.GetBytes("{\"title\":\"Claude · orbit\",\"body\":\"aguardando você\"}");
        var body = WebPush.Encrypt(msg, uaPublic, auth);

        // lado do aparelho: cabeçalho aes128gcm = salt(16) | rs(4) | idlen(1) | chave do servidor | cifra
        var salt = body[..16];
        var rs = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4));
        int idlen = body[20];
        var asPublic = body[21..(21 + idlen)];
        var cipher = body[(21 + idlen)..];
        using var asKey = System.Security.Cryptography.ECDiffieHellman.Create(WebPush.PublicParams(asPublic));
        var ecdh = ua.DeriveRawSecretAgreement(asKey.PublicKey);
        byte[] keyInfo = [.. "WebPush: info\0"u8, .. uaPublic, .. asPublic];
        var sha = System.Security.Cryptography.HashAlgorithmName.SHA256;
        var ikm = System.Security.Cryptography.HKDF.DeriveKey(sha, ecdh, 32, auth, keyInfo);
        var prk = System.Security.Cryptography.HKDF.Extract(sha, ikm, salt);
        var cek = System.Security.Cryptography.HKDF.Expand(sha, prk, 16, "Content-Encoding: aes128gcm\0"u8.ToArray());
        var nonce = System.Security.Cryptography.HKDF.Expand(sha, prk, 12, "Content-Encoding: nonce\0"u8.ToArray());
        var plain = new byte[cipher.Length - 16];
        bool decrypted;
        try
        {
            using var gcm = new System.Security.Cryptography.AesGcm(cek, 16);
            gcm.Decrypt(nonce, cipher[..^16], cipher[^16..], plain);
            decrypted = true;
        }
        catch { decrypted = false; }
        Check("push: cabeçalho aes128gcm (rs 4096, chave de 65 bytes)", rs == 4096 && idlen == 65 && asPublic[0] == 0x04);
        Check("push: aparelho decifra o payload", decrypted && plain[^1] == 0x02 && plain[..^1].AsSpan().SequenceEqual(msg));
        // Vetor oficial do RFC 8291, Apêndice A: com as mesmas chaves e salt, o corpo tem de sair idêntico.
        {
            byte[] B(string s) => System.Buffers.Text.Base64Url.DecodeFromChars(s);
            var rfc = WebPush.PublicParams(B("BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8"));
            rfc.D = B("yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw");
            using var rfcKey = System.Security.Cryptography.ECDiffieHellman.Create(rfc);
            var rfcBody = WebPush.Encrypt("When I grow up, I want to be a watermelon"u8.ToArray(),
                B("BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4"),
                B("BTBZMqHH6r4Tts7J_aSIgg"), rfcKey, B("DGv6ra1nlYgDCS1FRnbzlw"));
            Check("push: igual ao vetor do RFC 8291", System.Buffers.Text.Base64Url.EncodeToString(rfcBody) ==
                "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN");
        }
        Check("push: cada envio usa salt e chave novos", !WebPush.Encrypt(msg, uaPublic, auth).AsSpan(0, 86).SequenceEqual(body.AsSpan(0, 86)));

        // VAPID: JWT ES256 verificável com a chave pública anunciada
        using var vapid = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var jwt = WebPush.VapidJwt(vapid, "https://web.push.apple.com", new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc));
        var parts = jwt.Split('.');
        using var verifier = System.Security.Cryptography.ECDsa.Create(WebPush.PublicParams(WebPush.Uncompressed(vapid.ExportParameters(false).Q)));
        bool sigOk = parts.Length == 3 && verifier.VerifyData(System.Text.Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]),
            System.Buffers.Text.Base64Url.DecodeFromChars(parts[2]), sha, System.Security.Cryptography.DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var claims = System.Text.Encoding.UTF8.GetString(System.Buffers.Text.Base64Url.DecodeFromChars(parts[1]));
        Check("push: JWT VAPID assinado (ES256)", sigOk);
        Check("push: JWT com aud, exp em 12 h e sub", claims.Contains("\"aud\":\"https://web.push.apple.com\"")
            && claims.Contains($"\"exp\":{new DateTimeOffset(2026, 10, 18, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds()}") && claims.Contains("\"sub\":\"mailto:"));

        Check("push: endpoint da Apple aceito", WebPush.IsAllowedEndpoint("https://web.push.apple.com/QGx3abc"));
        Check("push: endpoint do Google aceito", WebPush.IsAllowedEndpoint("https://fcm.googleapis.com/fcm/send/x"));
        Check("push: host parecido recusado", !WebPush.IsAllowedEndpoint("https://push.apple.com.evil.net/x")
            && !WebPush.IsAllowedEndpoint("https://evilpush.apple.com.br/x"));
        Check("push: rede local recusada (SSRF)", !WebPush.IsAllowedEndpoint("https://192.168.0.1/x") && !WebPush.IsAllowedEndpoint("http://web.push.apple.com/x")
            && !WebPush.IsAllowedEndpoint("https://web.push.apple.com:8443/x"));
        var p256 = System.Buffers.Text.Base64Url.EncodeToString(uaPublic);
        var auth64 = System.Buffers.Text.Base64Url.EncodeToString(auth);
        Check("push: chaves válidas", WebPush.TryKeys(p256, auth64, out _, out _));
        Check("push: chaves inválidas", !WebPush.TryKeys(p256[..20], auth64, out _, out _) && !WebPush.TryKeys(p256, "!!", out _, out _));

        // Rotas: inscrição fica ligada ao token que a fez.
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-push-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tokens = new TokenStore(Path.Combine(dir, "t.json"));
        var token = tokens.Create("iPhone teste");
        var push = new WebPush(Path.Combine(dir, "vapid.json"), Path.Combine(dir, "push.json"));
        var handlers = new BridgeHandlers { Push = push, ValidTokens = () => tokens.List().Select(t => t.Name).ToHashSet() };
        var server = new BridgeServer();
        server.StartAsync(0, Path.Combine(dir, "sem-web"), tokens, new AuditLog(Path.Combine(dir, "a.jsonl")), handlers).GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Check("push: chave pública = 65 bytes", System.Buffers.Text.Base64Url.DecodeFromChars(
            http.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/push/key").Result.GetProperty("publicKey").GetString()!).Length == 65);
        var ok = http.PostAsJsonAsync("/api/push/subscribe", new { endpoint = "https://web.push.apple.com/abc", keys = new { p256dh = p256, auth = auth64 } }).Result;
        Check("push: inscrição aceita", ok.StatusCode == HttpStatusCode.NoContent && push.CountFor("iPhone teste") == 1);
        var bad = http.PostAsJsonAsync("/api/push/subscribe", new { endpoint = "https://10.0.0.1/abc", keys = new { p256dh = p256, auth = auth64 } }).Result;
        Check("push: endpoint fora da lista = 400", bad.StatusCode == HttpStatusCode.BadRequest && push.CountFor("iPhone teste") == 1);
        Check("push: status conta as do token", http.GetStringAsync("/api/push/status").Result.Contains("\"subscriptions\":1"));
        Check("push: inscrição sobrevive a reiniciar", new WebPush(Path.Combine(dir, "vapid.json"), Path.Combine(dir, "push.json")).CountFor("iPhone teste") == 1
            && new WebPush(Path.Combine(dir, "vapid.json"), Path.Combine(dir, "push.json")).PublicKey == push.PublicKey);
        http.PostAsJsonAsync("/api/push/unsubscribe", new { endpoint = "https://web.push.apple.com/abc" }).Result.Dispose();
        Check("push: desinscrever", push.CountFor("iPhone teste") == 0);
        server.StopAsync().GetAwaiter().GetResult();
        Directory.Delete(dir, recursive: true);
    }

    // Arquivos do PC: só dentro da raiz (sem traversal, unidade, oculto ou link) e download por tíquete.
    private static void PcFilesTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-pcf-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(dir, "raiz");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "olá");
        File.WriteAllText(Path.Combine(root, "sub", "b.txt"), "b");
        File.WriteAllText(Path.Combine(dir, "fora.txt"), "segredo");
        var oculto = Path.Combine(root, "oculto.txt");
        File.WriteAllText(oculto, "x");
        File.SetAttributes(oculto, FileAttributes.Hidden);
        var roots = new[] { new PcRoot("r", "Raiz", root) };

        Check("pcfiles: raiz", PcFiles.Resolve(roots, "r", "") == root);
        Check("pcfiles: subpasta com /", PcFiles.Resolve(roots, "r", "sub/b.txt") == Path.Combine(root, "sub", "b.txt"));
        Check("pcfiles: raiz desconhecida", PcFiles.Resolve(roots, "x", "a.txt") is null);
        Check("pcfiles: .. sai da raiz", PcFiles.Resolve(roots, "r", "../fora.txt") is null);
        Check("pcfiles: .. disfarçado", PcFiles.Resolve(roots, "r", "sub/../../fora.txt") is null);
        Check("pcfiles: caminho absoluto", PcFiles.Resolve(roots, "r", Path.Combine(dir, "fora.txt")) is null);
        Check("pcfiles: prefixo parecido (raiz2)", PcFiles.Resolve(roots, "r", "../raiz2/x") is null);
        Check("pcfiles: fluxo alternativo", PcFiles.Resolve(roots, "r", "a.txt:zone") is null);
        Check("pcfiles: oculto recusado", PcFiles.Resolve(roots, "r", "oculto.txt") is null);
        var lista = PcFiles.List(root);
        Check("pcfiles: lista pasta antes, sem oculto", lista.Count == 2 && lista[0].Dir && lista[0].Name == "sub" && lista[1].Name == "a.txt");
        // Link pra fora da raiz (junção não precisa de admin; se o ambiente não deixar, pula).
        var link = Path.Combine(root, "atalho");
        using (var mk = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{dir}\"")
                   { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true }))
            mk?.WaitForExit(5000);
        if (!Directory.Exists(link)) link = null;
        Check("pcfiles: junção criada pro teste", link is not null);
        if (link is not null)
        {
            Check("pcfiles: link pra fora recusado", PcFiles.Resolve(roots, "r", "atalho/fora.txt") is null);
            Check("pcfiles: link fora da lista", PcFiles.List(root).All(e => e.Name != "atalho"));
        }

        var enabled = false;
        var handlers = new BridgeHandlers { PcFilesEnabled = () => enabled, PcRoots = () => roots };
        var tokens = new TokenStore(Path.Combine(dir, "t.json"));
        var token = tokens.Create("teste");
        var server = new BridgeServer();
        server.StartAsync(0, Path.Combine(dir, "sem-web"), tokens, new AuditLog(Path.Combine(dir, "a.jsonl")), handlers)
            .GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        Check("pcfiles: desligado = 403", http.GetAsync("/api/pcfiles/roots").Result.StatusCode == HttpStatusCode.Forbidden);
        enabled = true;
        Check("pcfiles: raízes", http.GetStringAsync("/api/pcfiles/roots").Result.Contains("\"id\":\"r\""));
        Check("pcfiles: lista", http.GetStringAsync("/api/pcfiles/list?root=r&path=sub").Result.Contains("b.txt"));
        Check("pcfiles: lista fora da raiz = 404", http.GetAsync("/api/pcfiles/list?root=r&path=..").Result.StatusCode == HttpStatusCode.NotFound);
        Check("pcfiles: tíquete fora da raiz = 404",
            http.PostAsJsonAsync("/api/pcfiles/ticket", new { root = "r", path = "../fora.txt" }).Result.StatusCode == HttpStatusCode.NotFound);
        var tk = http.PostAsJsonAsync("/api/pcfiles/ticket", new { root = "r", path = "a.txt" }).Result
            .Content.ReadFromJsonAsync<System.Text.Json.JsonElement>().Result.GetProperty("ticket").GetString();
        using var anon = new HttpClient { BaseAddress = http.BaseAddress }; // download sem token, só tíquete
        var down = anon.GetAsync($"/api/pcfiles/download?ticket={tk}").Result;
        Check("pcfiles: download com tíquete", down.StatusCode == HttpStatusCode.OK && down.Content.ReadAsStringAsync().Result == "olá"
            && down.Content.Headers.ContentDisposition?.FileNameStar == "a.txt");
        Check("pcfiles: tíquete aguenta 2ª requisição (faixas)", anon.GetAsync($"/api/pcfiles/download?ticket={tk}").Result.StatusCode == HttpStatusCode.OK);
        Check("pcfiles: tíquete errado = 401", anon.GetAsync("/api/pcfiles/download?ticket=errado").Result.StatusCode == HttpStatusCode.Unauthorized);
        Check("pcfiles: lista sem token = 401", anon.GetAsync("/api/pcfiles/list?root=r").Result.StatusCode == HttpStatusCode.Unauthorized);

        server.StopAsync().GetAwaiter().GetResult();
        if (link is not null) Directory.Delete(link);
        File.SetAttributes(oculto, FileAttributes.Normal);
        Directory.Delete(dir, recursive: true);
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
        Check("pwa: cartão de câmera", html.Contains("id=\"cam-card\""));
        var appjs = File.ReadAllText(Path.Combine(web, "app.js"));
        Check("pwa: foto via share", appjs.Contains("navigator.share"));
        Check("pwa: tíquete na URL do stream", appjs.Contains("/api/camera/stream?ticket="));
        var manifest = http.GetAsync("/manifest.webmanifest").Result;
        Check("pwa: manifest com content-type", manifest.IsSuccessStatusCode
            && manifest.Content.Headers.ContentType?.MediaType == "application/manifest+json");
        Check("pwa: estático revalida (no-cache)", http.GetAsync("/app.js").Result.Headers.CacheControl?.NoCache == true);
        Check("pwa: ícone", http.GetAsync("/apple-touch-icon.png").Result.IsSuccessStatusCode);
        server.StopAsync().GetAwaiter().GetResult();
        Directory.Delete(dir, recursive: true);
    }

    private static void PairingCodeTests()
    {
        var now = new DateTime(2026, 10, 16, 10, 0, 0);
        var codes = new PairingCodes();
        var code = codes.Issue("TOKEN-A", now);
        Check("pareamento: código de 6 dígitos", code.Length == 6 && code.All(char.IsAsciiDigit));
        Check("pareamento: formato 3+3", PairingCodes.Format("482913") == "482 913");
        Check("pareamento: aceita com espaço/hífen", PairingCodes.Normalize(" 482-913 ") == "482913");
        Check("pareamento: código certo entrega o token", codes.Claim(PairingCodes.Format(code), now.AddMinutes(1)) == "TOKEN-A");
        Check("pareamento: uso único", codes.Claim(code, now.AddMinutes(1)) is null);

        var c2 = codes.Issue("TOKEN-B", now);
        Check("pareamento: expira em 5 min", codes.Claim(c2, now.AddMinutes(5).AddSeconds(1)) is null);

        var c3 = codes.Issue("TOKEN-C", now);
        var wrong = c3 == "000000" ? "111111" : "000000";
        for (int i = 0; i < PairingCodes.MaxAttempts; i++) codes.Claim(wrong, now);
        Check("pareamento: trava após 5 erros", codes.Claim(c3, now) is null);

        var c4 = codes.Issue("TOKEN-D", now);
        var c5 = codes.Issue("TOKEN-E", now);
        // (1 chance em 1 milhão de os dois códigos sorteados serem iguais: aí o check não se aplica)
        Check("pareamento: código novo substitui o anterior", c4 == c5 || (codes.Claim(c4, now) is null && codes.Claim(c5, now) == "TOKEN-E"));

        // ponta a ponta: POST /pair (sem token) → token que abre a /api
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var tokens = new TokenStore(Path.Combine(dir, "t.json"));
        var live = new PairingCodes();
        var server = new BridgeServer();
        server.StartAsync(0, Path.Combine(dir, "www"), tokens, new AuditLog(Path.Combine(dir, "a.jsonl")), new BridgeHandlers(), live)
            .GetAwaiter().GetResult();
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{server.Port}") };
        var token = tokens.Create("iPhone");
        var pin = live.Issue(token, DateTime.Now);
        Check("pareamento: código errado = 400", http.PostAsJsonAsync("/pair", new { code = pin == "123456" ? "654321" : "123456" }).Result.StatusCode == HttpStatusCode.BadRequest);
        var ok = http.PostAsJsonAsync("/pair", new { code = pin }).Result;
        var body = ok.Content.ReadAsStringAsync().Result;
        Check("pareamento: código certo = 200 com token", ok.StatusCode == HttpStatusCode.OK && body.Contains(token));
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Check("pareamento: token recebido abre a API", http.GetAsync("/api/stats").Result.StatusCode == HttpStatusCode.OK);
        server.StopAsync().GetAwaiter().GetResult();
        Directory.Delete(dir, recursive: true);
    }

    private static void TailscaleTests()
    {
        var ok = TailscaleCli.ParseStatus("{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"pc.tail1.ts.net.\"},\"CertDomains\":[\"pc.tail1.ts.net\"]}");
        Check("tailscale: status ok", ok.Running && ok.DnsName == "pc.tail1.ts.net" && ok.HttpsReady);
        var semHttps = TailscaleCli.ParseStatus("{\"BackendState\":\"Running\",\"Self\":{\"DNSName\":\"pc.tail1.ts.net.\"},\"CertDomains\":null}");
        Check("tailscale: sem HTTPS", semHttps.Running && !semHttps.HttpsReady);
        var parado = TailscaleCli.ParseStatus("{\"BackendState\":\"Stopped\"}");
        Check("tailscale: desconectado", !parado.Running && parado.DnsName is null);
        Check("tailscale: JSON quebrado = desconectado", !TailscaleCli.ParseStatus("{ruim").Running);

        var serving = "{\"TCP\":{\"443\":{\"HTTPS\":true}},\"Web\":{\"pc.tail1.ts.net:443\":{\"Handlers\":{\"/\":{\"Proxy\":\"http://127.0.0.1:5191\"}}}}}";
        Check("tailscale: serve aponta pra ponte", TailscaleCli.IsServing(serving, 5191));
        Check("tailscale: serve em outra porta não conta", !TailscaleCli.IsServing(serving, 8080));
        Check("tailscale: serve vazio", !TailscaleCli.IsServing("{}", 5191) && !TailscaleCli.IsServing("", 5191));

        Check("tailscale: args do serve", string.Join(" ", TailscaleCli.ServeArgs(5191)) == "serve --bg --yes --https=443 http://127.0.0.1:5191");
        Check("tailscale: nunca funnel", !TailscaleCli.ServeArgs(5191).Contains("funnel") && !TailscaleCli.ServeOffArgs().Contains("funnel"));
        Check("tailscale: link de ativação", TailscaleCli.ExtractEnableUrl("Serve is not enabled.\nTo enable, visit:\n\n  https://login.tailscale.com/f/serve?node=abc123\n")
            == "https://login.tailscale.com/f/serve?node=abc123");
        Check("tailscale: sem link", TailscaleCli.ExtractEnableUrl("tudo certo") is null);

        Check("tailscale: textos", TailscaleCli.Describe(new TailscaleInfo(false, false, null, false), false).Contains("não instalado")
            && TailscaleCli.Describe(semHttps, false).Contains("HTTPS")
            && TailscaleCli.Describe(ok, true).Contains("Publicado"));
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
        Check("abas: clipboard, notas e apps entre stats e mercado", order.Contains("stats,clipboard,notas,apps,mercado"));
        Check("abas: iphone antes de energia", order.Contains("claude,iphone,energia"));
        Check("abas: 12 ids na ordem", IslandViewModel.TabOrder.Length == 12);
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
        Check("copiar seleção: sequência Ctrl↓ C↓ C↑ Ctrl↑ (sem Alt)", Input.CtrlCSequence().SequenceEqual(
            new (ushort, bool)[] { (0x11, false), (0x43, false), (0x43, true), (0x11, true) }));

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
        Check("hover: cursor parado não abre", !Orbit.Island.IslandGeometry.CursorMoved(0, 0));
        Check("hover: tremida de 1 px não abre", !Orbit.Island.IslandGeometry.CursorMoved(1, 1));
        Check("hover: mexer o mouse abre", Orbit.Island.IslandGeometry.CursorMoved(2, 0));

        // Cota: JSON da status line, ritmo e avisos.
        {
            const string resp = "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"r1\",\"response\":{\"rate_limits\":{"
                + "\"five_hour\":{\"utilization\":3,\"resets_at\":\"2026-10-31T03:39:59.608777+00:00\"},\"seven_day\":{\"utilization\":18.5,\"resets_at\":\"2026-11-03T23:59:59+00:00\"},\"seven_day_opus\":null}}}}";
            var cota = ClaudeUsage.ParseResponse(resp, "r1");
            Check("cota: lê a resposta do CLI", cota?.FiveHour?.UsedPct == 3 && cota.SevenDay?.UsedPct == 18.5
                && cota.FiveHour.ResetsAt == new DateTimeOffset(2026, 10, 31, 3, 39, 59, 608, 777, TimeSpan.Zero) && cota.Opus is null);
            Check("cota: resposta de outro pedido não vale", ClaudeUsage.ParseResponse(resp, "r2") is null);
            Check("cota: outra linha do CLI não vale", ClaudeUsage.ParseResponse("{\"type\":\"system\"}", "r1") is null);
            Check("cota: lixo não quebra", ClaudeUsage.ParseResponse("nada", "r1") is null);
            Check("cota: sem rate_limits", ClaudeUsage.ParseResponse("{\"type\":\"control_response\",\"response\":{\"request_id\":\"r1\",\"response\":{\"rate_limits\":null}}}", "r1") is null);

            const string usage = "\"usage\":{\"input_tokens\":4,\"cache_creation_input_tokens\":1633,\"cache_read_input_tokens\":241102,\"output_tokens\":50}";
            string tail = "{\"usage\":{\"cortada\n"
                + "{\"type\":\"assistant\",\"message\":{\"usage\":{\"input_tokens\":1}}}\n"
                + "{\"type\":\"assistant\",\"message\":{" + usage + "}}\n"
                + "{\"type\":\"assistant\",\"isSidechain\":true,\"message\":{\"usage\":{\"input_tokens\":9}}}\n"
                + "{\"type\":\"user\",\"message\":{\"content\":\"\\\"usage\\\"\"}}\n";
            Check("contexto: última resposta da sessão, sem subagente", ClaudeContext.LastTokens(tail) == 242739);
            Check("contexto: transcript sem resposta", ClaudeContext.LastTokens("{\"type\":\"user\"}\n") is null);
            Check("contexto: texto em milhares", new ClaudeSessionInfo("s", "p", ClaudeSessionState.Running, null, ContextTokens: 242739).ContextText == "243k de contexto");
            Check("contexto: caminho vem do hook", ClaudeHookEvent.Parse("{\"hook_event_name\":\"Stop\",\"session_id\":\"x\",\"transcript_path\":\"C:\\\\t.jsonl\"}").TranscriptPath == @"C:\t.jsonl");

            var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
            var five = ClaudeUsageMath.FiveHours;
            Check("ritmo: no começo não opina", ClaudeUsageMath.Describe(new ClaudeWindow(40, now.AddMinutes(295)), five, now) == "reseta em 4h 55m");
            Check("ritmo: dentro da tolerância", ClaudeUsageMath.Describe(new ClaudeWindow(52, now.AddMinutes(150)), five, now) == "reseta em 2h 30m · no ritmo");
            Check("ritmo: abaixo", ClaudeUsageMath.Describe(new ClaudeWindow(20, now.AddMinutes(150)), five, now) == "reseta em 2h 30m · 30 pts abaixo do ritmo");
            Check("ritmo: projeta o limite", ClaudeUsageMath.Describe(new ClaudeWindow(75, now.AddMinutes(150)), five, now) == "reseta em 2h 30m · limite em 50m");
            Check("ritmo: no limite", ClaudeUsageMath.Describe(new ClaudeWindow(100, now.AddMinutes(10)), five, now) == "no limite · reseta em 10m");
            var vencida = new ClaudeWindow(98, now.AddMinutes(-1));
            Check("ritmo: janela vencida zera", ClaudeUsageMath.Row("5 horas", vencida, five, now).Percent == 0
                && ClaudeUsageMath.Describe(vencida, five, now) == "janela reiniciada");
            Check("ritmo: span em dias", ClaudeUsageMath.Span(TimeSpan.FromHours(33)) == "1d 9h");

            Check("limite: abaixo de 97 não avisa", ClaudeUsageMath.Crossing(0, 96.9) == (0, false));
            Check("limite: cruzou 97 avisa", ClaudeUsageMath.Crossing(0, 97) == (1, true));
            Check("limite: continuar perto não repete", ClaudeUsageMath.Crossing(1, 98) == (1, false));
            Check("limite: chegar a 100 avisa de novo", ClaudeUsageMath.Crossing(1, 100) == (2, true));
            Check("limite: janela nova rearma", ClaudeUsageMath.Crossing(2, 1) == (0, false));
        }

        // "Visto" ao focar a janela da sessão: sai da pílula; aviso novo volta.
        {
            var ss = new Dictionary<string, ClaudeSessionInfo>();
            ClaudeSessionReducer.Apply(ss, new ClaudeHookEvent("SessionStart", "s1", @"C:\repos\orbit2.0", null, 4242));
            ClaudeSessionReducer.Apply(ss, new ClaudeHookEvent("Stop", "s1", @"C:\repos\orbit2.0", null)); // sem host: mantém o da sessão
            Check("claude: guarda a janela da sessão", ss["s1"].HostPid == 4242 && ss["s1"].State == ClaudeSessionState.WaitingInput);
            Check("claude: aguardando aparece na pílula", ClaudeViewModel.BuildActivity(ss.Values) == "Claude · orbit2.0 — Aguardando você");
            Check("claude: focar outra janela não muda nada", !ClaudeSessionReducer.MarkSeen(ss, 999));
            Check("claude: focar a janela da sessão tira da pílula", ClaudeSessionReducer.MarkSeen(ss, 4242) && ss["s1"].Seen
                && ClaudeViewModel.BuildActivity(ss.Values) is null);
            ClaudeSessionReducer.Apply(ss, new ClaudeHookEvent("Notification", "s1", @"C:\repos\orbit2.0", "Permissão pra rodar"));
            Check("claude: aviso novo volta pra pílula", !ss["s1"].Seen && ClaudeViewModel.BuildActivity(ss.Values) == "Claude · orbit2.0 — Permissão pra rodar");
            Check("claude: host vem do JSON do hook", ClaudeHookEvent.Parse("{\"hook_event_name\":\"Stop\",\"session_id\":\"x\",\"orbit_host_pid\":77}").HostPid == 77
                && ClaudeHookEvent.Parse("{\"hook_event_name\":\"Stop\",\"session_id\":\"x\"}").HostPid is null);
            // hook(10) ← claude(20) ← shell(30) ← terminal(40, tem janela)
            var parents = new Dictionary<int, int> { [10] = 20, [20] = 30, [30] = 40, [40] = 1 };
            Check("claude: sobe até o terminal com janela", ClaudeHookClient.FirstWithWindow(10, parents, pid => pid == 40) == 40);
            Check("claude: sem janela na cadeia = sem host", ClaudeHookClient.FirstWithWindow(10, parents, _ => false) is null);
        }
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

    private static void CameraPresetTests()
    {
        Check("preset: padrão 720p/24", CameraPreset.Default.Width == 1280 && CameraPreset.Default.Fps == 24);
        Check("preset: 480p", CameraPreset.Parse("480", "20") == new CameraPreset(640, 20));
        Check("preset: 720p", CameraPreset.Parse("720", "24") == new CameraPreset(1280, 24));
        Check("preset: fps preso em 5..30", CameraPreset.Parse("720", "99").Fps == 30 && CameraPreset.Parse("720", "1").Fps == 5);
        Check("preset: lixo vira padrão", CameraPreset.Parse(null, null) == CameraPreset.Default && CameraPreset.Parse("4k", "x") == CameraPreset.Default);
    }

    private static void StreamTicketTests()
    {
        var t = new StreamTickets();
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var tk = t.Issue("dev-A", new CameraPreset(1280, 24), now);
        Check("tíquete: não vazio", !string.IsNullOrEmpty(tk));
        Check("tíquete: claim certo devolve device+preset", t.Claim(tk, now) is { DeviceId: "dev-A", Preset.Width: 1280 });
        Check("tíquete: uso único (2ª vez falha)", t.Claim(tk, now) is null);
        var tk2 = t.Issue("dev-B", CameraPreset.Default, now);
        Check("tíquete: expira", t.Claim(tk2, now.AddSeconds(31)) is null);
        Check("tíquete: errado falha", t.Claim("naoexiste", now) is null);
        var tk3 = t.Issue("dev-C", CameraPreset.Default, now);
        t.Issue("dev-D", CameraPreset.Default, now); // emitir de novo invalida o anterior
        Check("tíquete: emitir substitui", t.Claim(tk3, now) is null);
    }

    private static void CameraTests()
    {
        var ordered = Camera.OrderDevices([
            new CameraDevice("b", "Logitech BRIO"),
            new CameraDevice("a", "Câmera Integrada"),
        ]);
        Check("câmera: ordena por nome", string.Join(",", ordered.Select(d => d.Id)) == "a,b");
    }

    private static void CameraAuditTests()
    {
        Check("audit: ignora o stream", !BridgeRules.ShouldAudit("GET", "/api/camera/stream"));
        Check("audit: lista de câmeras não audita", !BridgeRules.ShouldAudit("GET", "/api/camera/devices"));
        Check("audit: abrir câmera audita", BridgeRules.ShouldAudit("POST", "/api/camera/open"));
    }

    private static void MjpegTests()
    {
        Check("mjpeg: boundary", Mjpeg.Boundary == "orbitcam");
        Check("mjpeg: content-type", Mjpeg.ContentType == "multipart/x-mixed-replace; boundary=orbitcam");
        var header = System.Text.Encoding.ASCII.GetString(Mjpeg.PartHeader(1234));
        Check("mjpeg: cabeçalho da parte", header == "\r\n--orbitcam\r\nContent-Type: image/jpeg\r\nContent-Length: 1234\r\n\r\n");
    }

    private static void MixerTests()
    {
        Check("mixer: nome pela descrição do exe", AudioMixer.DisplayName(" Spotify ", "C:/x/Spotify.exe", null) == "Spotify");
        Check("mixer: nome da sessão se não há descrição", AudioMixer.DisplayName(null, "C:/x/a.exe", "Meu App") == "Meu App");
        Check("mixer: ignora nome de recurso (@dll)", AudioMixer.DisplayName("", "C:/x/discord.exe", "@%SystemRoot%/x.dll,-1") == "discord");
        Check("mixer: sem nada vira App", AudioMixer.DisplayName(null, null, null) == "App");
        var ordered = AudioMixer.Order([
            new AudioMixer.App(AudioMixer.SystemKey, "Sons do sistema", null, 50, false),
            new AudioMixer.App("z", "zoom", null, 50, false),
            new AudioMixer.App("c", "Chrome", null, 50, false),
        ]);
        Check("mixer: por nome, sistema por último", string.Join(",", ordered.Select(a => a.Key)) == "c,z,system");
    }

    private static void EnergiaExtrasTests()
    {
        Check("energia: agendar 30 min", Power.ScheduleArgs(TimeSpan.FromMinutes(30)) == "/s /t 1800");
        Check("energia: agendar nunca 0 (seria imediato)", Power.ScheduleArgs(TimeSpan.Zero) == "/s /t 1");
        Check("bateria: duração", Battery.FormatDuration(TimeSpan.FromMinutes(80)) == "1 h 20 min"
            && Battery.FormatDuration(TimeSpan.FromMinutes(35)) == "35 min" && Battery.FormatDuration(TimeSpan.FromSeconds(30)) == "1 min");
        Check("bateria: sem bateria", Battery.Describe(null, true, null).Detail.Contains("Sem bateria"));
        Check("bateria: carregando/carregada", Battery.Describe(80, true, null).Detail == "Carregando" && Battery.Describe(100, true, null).Detail.StartsWith("Carregada"));
        Check("bateria: na bateria com tempo", Battery.Describe(55, false, 5400) == ("55%", "Na bateria · 1 h 30 min restantes"));
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
        Check("monitor: salvo conectado", IslandGeometry.PickScreen(new[] { @"\.\DISPLAY1", @"\.\DISPLAY5" }, 0, @"\.\display5") == 1);
        Check("monitor: salvo desconectado = principal", IslandGeometry.PickScreen(new[] { @"\.\DISPLAY1", @"\.\DISPLAY2" }, 1, @"\.\DISPLAY5") == 1);
        Check("monitor: null = principal", IslandGeometry.PickScreen(new[] { "a", "b" }, 1, null) == 1);
        Check("monitor: centraliza em 1920 a 100%", IslandGeometry.PlaceOn(0, 0, 1920, 1.0) == (580, 0, 760, 400));
        Check("monitor: à esquerda a 150%", IslandGeometry.PlaceOn(-2560, 76, 2560, 1.5) == (-2560 + (2560 - 1140) / 2, 76, 1140, 600));
        Check("monitor: nome", Orbit.Tabs.Ajustes.AjustesViewModel.MonitorName(2, 2560, 1440) == "2 · 2560×1440");
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
        Check("ponte: link sem https é completado", BridgeRules.NormalizeOpenUrl("youtube.com") == "https://youtube.com/"
            && BridgeRules.NormalizeOpenUrl(" www.x.com/a?b=1 ") == "https://www.x.com/a?b=1");
        Check("ponte: texto qualquer não vira link", BridgeRules.NormalizeOpenUrl("bom dia") is null
            && BridgeRules.NormalizeOpenUrl("javascript:alert(1)") is null && BridgeRules.NormalizeOpenUrl("") is null);
        Check("ponte: pasta padrão de upload", BridgeRules.UploadFolder(null).EndsWith(@"Downloads\Orbit")
            && BridgeRules.UploadFolder(@"D:\x") == @"D:\x");
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
        Check("ponte: capa JPEG/PNG pelo cabeçalho", BridgeRules.ImageType(new byte[] { 0xFF, 0xD8, 0xFF }) == "image/jpeg"
            && BridgeRules.ImageType(new byte[] { 0x89, 0x50, 0x4E, 0x47 }) == "image/png");
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

    private static void DeviceStoreTests()
    {
        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "tokens.json");
        var s = new TokenStore(path);
        var a = s.Create("iPhone"); var b = s.Create("iPad");
        var ha = s.Verify(a)!.Hash; var hb = s.Verify(b)!.Hash;
        Check("device: novo nasce sem permissão", !s.Allows(ha, Capability.Screen) && s.Verify(a)!.Permissions == DevicePermissions.None);
        s.SetPermissions(ha, DevicePermissions.None with { Screen = true });
        Check("device: permissão é por aparelho", s.Allows(ha, Capability.Screen) && !s.Allows(hb, Capability.Screen));
        Check("device: renomear mantém o hash", s.Rename(ha, "  Meu iPhone  ") && s.Find(ha)!.Name == "Meu iPhone");
        Check("device: nome vazio é recusado", !s.Rename(ha, "   ") && s.Find(ha)!.Name == "Meu iPhone");
        Check("device: nome longo é cortado", s.Rename(ha, new string('x', 50)) && s.Find(ha)!.Name.Length == TokenStore.MaxNameLength);
        Check("device: visto agora", s.LastSeen(ha) is not null && new TokenStore(path).LastSeen(ha) is null);
        s.Revoke(ha);
        Check("device: revogado não tem permissão", !s.Allows(ha, Capability.Screen));
        // tokens.json antigo, sem Permissions
        File.WriteAllText(path, JsonSerializer.Serialize(new[] { new { Name = "velho", Hash = new string('A', 64), CreatedAt = DateTime.Now } }));
        var old = new TokenStore(path);
        var h = new string('A', 64);
        Check("device: não semeado nega", !old.Allows(h, Capability.Camera));
        old.SeedPermissions(DevicePermissions.None with { Camera = true });
        Check("device: legado herda e persiste", new TokenStore(path).Allows(h, Capability.Camera));
        old.SetPermissions(h, DevicePermissions.None);
        old.SeedPermissions(DevicePermissions.None with { Camera = true });
        Check("device: semear não sobrescreve", !old.Allows(h, Capability.Camera));
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
        Check("token: verifica e devolve o nome", store.Verify(t1)?.Name == "iPhone" && store.Verify(t2)?.Name == "Atalhos");
        Check("token: errado/vazio não passa", store.Verify("x") is null && store.Verify("") is null && store.Verify(null) is null);
        Check("token: arquivo não guarda o token", !File.ReadAllText(path).Contains(t1));
        var reloaded = new TokenStore(path);
        Check("token: persiste", reloaded.Verify(t1)?.Name == "iPhone" && reloaded.List().Count == 2);
        reloaded.Revoke(reloaded.List().First(t => t.Name == "iPhone").Hash);
        Check("token: revogado não passa", reloaded.Verify(t1) is null && reloaded.Verify(t2)?.Name == "Atalhos");
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
        Check("token: revogar com disco travado lança e mantém consistência", threw && locked.Verify(tl)?.Name == "a" && locked.List().Count == 1);

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
        string? opened = null, clip = null, notes = null;
        Orbit.Services.PowerAction? power = null;
        MediaCommand? media = null;
        double? volume = null;
        var events = new List<BridgeEvent>();
        var handlers = new BridgeHandlers
        {
            OpenUrl = u => { opened = u; return Task.CompletedTask; },
            GetClipboard = () => Task.FromResult("do pc"),
            SetClipboard = t => { clip = t; return Task.CompletedTask; },
            GetNotes = () => Task.FromResult(notes ?? "nota do pc"),
            SetNotes = t => { notes = t; return Task.CompletedTask; },
            Power = a => { power = a; return Task.CompletedTask; },
            Media = m => { media = m; return Task.CompletedTask; },
            GetMedia = () => Task.FromResult(new MediaState("Faixa", "Banda", true, 40)),
            GetCover = () => Task.FromResult<byte[]?>(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
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
        Check("ponte: GET notas", http.GetStringAsync("/api/notes").Result.Contains("nota do pc"));
        Check("ponte: PUT notas", http.PutAsJsonAsync("/api/notes", new { text = "do iPhone\nlinha 2" }).Result.StatusCode == HttpStatusCode.NoContent
            && notes == "do iPhone\nlinha 2");
        Check("ponte: PUT notas sem texto = 400", http.PutAsJsonAsync("/api/notes", new { }).Result.StatusCode == HttpStatusCode.BadRequest);

        Check("ponte: energia", http.PostAsync("/api/power/shutdown", null).Result.StatusCode == HttpStatusCode.NoContent
            && power == Orbit.Services.PowerAction.Desligar);
        Check("ponte: energia desconhecida = 404", http.PostAsync("/api/power/explodir", null).Result.StatusCode == HttpStatusCode.NotFound);
        Check("ponte: mídia", http.PostAsync("/api/media/next", null).Result.StatusCode == HttpStatusCode.NoContent && media == MediaCommand.Next);
        Check("ponte: estado da mídia", http.GetStringAsync("/api/media").Result.Contains("\"title\":\"Faixa\""));
        var cover = http.GetAsync("/api/media/cover").Result;
        Check("ponte: capa em PNG", cover.StatusCode == HttpStatusCode.OK && cover.Content.Headers.ContentType?.MediaType == "image/png");
        Check("ponte: volume clampado", http.PutAsJsonAsync("/api/media/volume", new { percent = 150 }).Result.StatusCode == HttpStatusCode.NoContent
            && volume == 100);

        using (var form = new MultipartFormDataContent())
        {
            form.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "../evil.txt");
            var up = http.PostAsync("/api/files", form).Result;
            Check("ponte: upload devolve a pasta", up.Content.ReadAsStringAsync().Result.Contains("\"folder\""));
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
