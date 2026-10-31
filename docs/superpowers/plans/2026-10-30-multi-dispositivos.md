# Multi-dispositivos Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Trocar o conceito de "iPhone" por dispositivos pareados, cada um com nome, permissões e caixa de saída próprios, escolhendo o destino ao mandar algo do PC.

**Architecture:** O token da ponte vira o registro do dispositivo, com `Hash` como Id estável. Toda rota sabe quem pediu (`ctx.Items["device"]` ou dono do tíquete) e checa a permissão no estado atual do `TokenStore`. Um `ChooseDevice` único atende às entradas de envio, usando as `PeekAction` que já existem.

**Tech Stack:** WPF .NET 10, Kestrel in-process, PWA em HTML/JS puro, harness próprio em `Orbit.Tests/Program.cs`.

**Spec:** `docs/superpowers/specs/2026-10-30-multi-dispositivos-design.md`

## Global Constraints

- Código, comentários e commits em português. `ImplicitUsings` desligado; `[ObservableProperty]` no estilo campo.
- Testes: só métodos puros/estáticos ou de ponta a ponta com `HttpClient`; nunca executar `SendInput`.
- Nenhum pacote NuGet novo. Glifos: `\uXXXX` em C#, `&#xXXXX;` em XAML.
- Nunca `SettingsStore.Load()` dentro de aba. Para encerrar o app em teste, mate pelo PID.
- Dispositivo novo começa com todas as permissões `false`. Permissão `null` (não semeada) nega acesso.
- Commits terminam com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Trabalhar em `feat/dispositivos`, criada a partir de `feat/cota-claude` (a base não está no `master`).
- Rodar testes: `dotnet run --project Orbit.Tests`. Sucesso = exit 0 e nenhuma linha de falha.
- Cada Task termina com `dotnet build` limpo, testes verdes e commit.

## Review Focus

- PC em tela cheia, 2 aparelhos, envio pelo Explorer: o seletor não pode ficar na fila e surgir horas depois. Toast avisa e nada é enviado (Task 6).
- Aparelho revogado entre abrir o seletor e clicar: ele sai da lista; lista vazia cancela e avisa (Task 6).
- Renomear um aparelho: push, tíquetes e caixa de saída continuam valendo, pois tudo liga pelo `Hash` (Tasks 4 e 5).
- Mesmo aparelho reconectando à própria sessão: não pode receber "O iPad assumiu a tela" (Task 7).
- Nome vazio, só espaços ou enorme ao renomear: vazio é recusado, o resto é aparado e limitado a 24 caracteres, para caber nos botões da pílula (Task 1).

---

### Task 1: Dispositivo no TokenStore

**Files:**
- Modify: `Orbit/Bridge/TokenStore.cs`, `Orbit/Bridge/BridgeServer.cs`
- Test: `Orbit.Tests/Program.cs`

**Interfaces:**
- Produces:
  - `enum Capability { Camera, PcFiles, Screen, Control, Mic, Keyboard }`
  - `record DevicePermissions(bool Camera, bool PcFiles, bool Screen, bool Control, bool Mic, bool Keyboard)` com `static None`, e `bool Has(Capability)`
  - `record TokenInfo(string Name, string Hash, DateTime CreatedAt, DevicePermissions? Permissions = null)`
  - `TokenStore`: `TokenInfo? Verify(string?)`, `TokenInfo? Find(string hash)`, `bool Allows(string hash, Capability c)`, `bool Rename(string hash, string name)`, `void SetPermissions(string hash, DevicePermissions p)`, `void SeedPermissions(DevicePermissions legacy)`, `DateTime? LastSeen(string hash)`
  - `const int TokenStore.MaxNameLength = 24`

- [ ] **Step 1: Crie `feat/dispositivos` e commite spec e plano**

```bash
git switch -c feat/dispositivos
git add docs/superpowers/specs/2026-10-30-multi-dispositivos-design.md docs/superpowers/plans/2026-10-30-multi-dispositivos.md
git commit -m "docs: spec e plano de multi-dispositivos"
```

- [ ] **Step 2: Escreva `DeviceStoreTests()` em `Program.cs`, chamada no `Main`, e adapte os `Check` atuais para `Verify(...)?.Name`**

```csharp
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
```

- [ ] **Step 3: Rode os testes e confirme que não compila** (`Capability` não existe)

- [ ] **Step 4: Implemente em `TokenStore.cs`**

`Create` grava `Permissions = DevicePermissions.None`. `Rename`, `SetPermissions` e `SeedPermissions` gravam com `Save(rethrow: true)` e desfazem a memória na falha, como `Revoke`. `_lastSeen` é `Dictionary<string, DateTime>` só em memória, atualizado em `Verify` sob o `_gate`. `BridgeServer` usa `?.Name` por enquanto, sem mudar comportamento.

- [ ] **Step 5: Rode os testes e confirme que passam**

- [ ] **Step 6: Commit** `feat: token da ponte vira dispositivo com nome e permissões`

---

### Task 2: Permissão por dispositivo nas rotas e tíquetes

**Files:**
- Modify: `Orbit/Bridge/BridgeServer.cs`, `BridgeRules.cs`, `BridgeHandlers.cs`, `RemoteScreen.cs`, `MicStream.cs`, `StreamTickets.cs`, `PcFiles.cs`, `OutboxRoutes.cs`
- Modify: `Orbit/Island/IslandViewModel.cs`, `Orbit.Tests/Program.cs`

**Interfaces:**
- Consumes: `TokenStore.Allows`, `Verify` (Task 1)
- Produces:
  - `ctx.Items["device"]` com o `TokenInfo`
  - `BridgeRules.IsTicketRoute(string method, PathString path)` e `BridgeRules.RequiredCapabilities(PathString path) : Capability[]`
  - `OneShotTickets<T>.Issue(string owner, T value, DateTime now)` e `TryClaim(string? ticket, DateTime now, out T value, out string owner)`
  - `StreamTickets.Issue(string owner, string deviceId, CameraPreset preset, DateTime now)`, com `Claim` devolvendo `(string Owner, string DeviceId, CameraPreset Preset)?`
  - `FileTickets.Issue(string owner, string path, DateTime now)` e `Find(string? ticket, DateTime now) : (string Owner, string Path)?`, substituindo `Path(...)`
  - Remove de `BridgeHandlers`: `CameraEnabled`, `PcFilesEnabled`, `ScreenEnabled`, `ControlEnabled`, `KeyboardEnabled`, `MicEnabled`
  - `GET /api/me` → `{ name, permissions }`

- [ ] **Step 1: Escreva `DevicePermissionRouteTests()`, sobre um servidor real com dois tokens**

```csharp
Check("rota: a mesma URL muda com o aparelho", ipad.GetAsync("/api/screen/list").Result.StatusCode == HttpStatusCode.OK
    && iphone.GetAsync("/api/screen/list").Result.StatusCode == HttpStatusCode.Forbidden);
Check("rota: controle exige tela e controle", /* só Control = 403 em /api/remote/ticket */);
var tk = /* ticket de POST /api/screen/open pelo ipad */;
tokens.SetPermissions(ipadHash, DevicePermissions.None);
Check("tíquete: permissão desligada depois = 403", anon.GetAsync($"/api/screen/stream?ticket={tk}", HttpCompletionOption.ResponseHeadersRead).Result.StatusCode == HttpStatusCode.Forbidden);
/* idem com tíquete de pcfiles depois de tokens.Revoke(hash), que usa FileTickets */
Check("tíquete: revogado = 403", /* status */ );
Check("me: devolve o próprio nome", ipad.GetStringAsync("/api/me").Result.Contains("iPad"));
Check("regra: remote exige tela e controle", BridgeRules.RequiredCapabilities("/api/remote/ticket").SequenceEqual(new[] { Capability.Screen, Capability.Control }));
Check("regra: rota comum não exige nada", BridgeRules.RequiredCapabilities("/api/clipboard").Length == 0);
```

Adapte os testes existentes que usam `CameraEnabled = () => true` e afins para `tokens.SetPermissions(...)`, mantendo o que cada um prova.

- [ ] **Step 2: Rode e confirme a falha**

- [ ] **Step 3: Implemente**

O middleware grava `ctx.Items["device"]`. O filtro de capability ignora `IsTicketRoute` e, nas demais, exige `RequiredCapabilities(path)`. Filtro de auditoria usa `(Items["device"] as TokenInfo)?.Name ?? "?"`.

`StartAsync` passa `tokens` a `MapApi`, `MapPcFiles`, `RemoteScreen.Map` e `MicStream.Map`. Rotas que emitem tíquete recebem `HttpContext` e usam o `Hash` como dono. Depois de todo claim, responda 403 a menos que `RequiredCapabilities(ctx.Request.Path).All(c => tokens.Allows(owner, c))` e o dono ainda exista em `tokens.Find(owner)`.

Em `/camera/stream`, acrescente `tokens.Allows(owner, Capability.Camera)` à condição do `while`, para desligar ou revogar cortar a câmera, que não ocupa vaga em `RemoteSessions`.

Em `IslandViewModel`, remova os delegates e chame `_tokens.SeedPermissions(...)` com as seis chaves globais `Bridge*Enabled` no início do construtor. `ValidTokens` passa a devolver `Hash` na Task 4.

- [ ] **Step 4: Rode os testes e confirme que passam**

- [ ] **Step 5: Commit** `feat: permissões da ponte por dispositivo, também nas rotas por tíquete`

---

### Task 3: Lista de dispositivos na aba

**Files:**
- Create: `Orbit/Tabs/IPhone/DeviceRow.cs`
- Modify: `Orbit/Tabs/IPhone/IPhoneViewModel.cs`, `IPhoneView.xaml`, `Orbit.Tests/Program.cs`

**Interfaces:**
- Consumes: `Rename`, `SetPermissions`, `LastSeen`, `List` (Task 1)
- Produces:
  - `DeviceRow : ObservableObject`, com `Hash`, `Name`, `LastSeenText`, seis `bool` observáveis (`Camera`, `PcFiles`, `Screen`, `Control`, `Mic`, `Keyboard`) e `DevicePermissions ToPermissions()`
  - `ObservableCollection<DeviceRow> Devices`, substituindo `Tokens`
  - `[ObservableProperty] string _pairName`
  - `static string DeviceRow.SeenText(DateTime? seen, DateTime now)`

- [ ] **Step 1: Escreva os `Check`**

```csharp
var now = new DateTime(2026, 10, 30, 12, 0, 0);
Check("aba: nunca visto", DeviceRow.SeenText(null, now) == "sem acesso desde que abriu");
Check("aba: agora", DeviceRow.SeenText(now.AddSeconds(-30), now) == "online");
Check("aba: minutos", DeviceRow.SeenText(now.AddMinutes(-12), now) == "há 12 min");
Check("aba: horas", DeviceRow.SeenText(now.AddHours(-2), now) == "há 2 h");
```

- [ ] **Step 2: Rode e confirme a falha**

- [ ] **Step 3: Implemente**

`DeviceRow` chama um `Action<DeviceRow>` ao mudar uma chave. O ViewModel grava com `SetPermissions`; na falha mostra em `Status`, como em `Revoke`, e recarrega a lista. Ao desligar, chama `Server.StopRemote()` (a Task 7 troca por `StopDevice`). Remova as seis propriedades globais, seus `partial void` e as chaves do XAML.

`Pair()` usa `PairName` aparado, ou `Dispositivo {DateTime.Now:dd/MM HH:mm}`, e limpa o campo. No XAML, cada `Card` mostra nome editável (`TextBox`, confirma no `LostFocus` via `Rename`), `LastSeenText`, revogar e os seis `Chip`, com Controle habilitado só com Tela.

- [ ] **Step 4: Rode testes, abra o app e confira a aba com dois tokens** (`dotnet run --project Orbit`)

- [ ] **Step 5: Commit** `feat: aba lista dispositivos com nome e permissões próprios`

---

### Task 4: Push ligado ao Hash

**Files:**
- Modify: `Orbit/Bridge/WebPush.cs`, `BridgeServer.cs`, `Orbit/Island/IslandViewModel.cs`, `Orbit.Tests/Program.cs`

**Interfaces:**
- Consumes: `TokenInfo`
- Produces:
  - `SendAsync(string title, string body, IReadOnlyCollection<string> validTokens, IReadOnlyCollection<string>? onlyTokens = null, string? url = null)`
  - `static List<PushSubscription> WebPush.MigrateTokenRefs(IEnumerable<PushSubscription> subs, IReadOnlyList<TokenInfo> tokens)` e `void MigrateTokenRefs(IReadOnlyList<TokenInfo> tokens)` (grava se mudou)
  - `PushSubscription.TokenName` passa a conter o `Hash` (nome do campo mantido no JSON)
  - `IslandViewModel.ValidTokenHashes()`, substituindo `ValidTokenNames()`

- [ ] **Step 1: Escreva os `Check`**

```csharp
var hA = new string('A', 64); var hB = new string('B', 64);
var toks = new[] { new TokenInfo("iPad", hA, t0), new TokenInfo("iPad", hB, t0.AddDays(1)) };
var subs = new[] { Sub("e1", "iPad"), Sub("e2", hA), Sub("e3", "sumiu") };
var m = WebPush.MigrateTokenRefs(subs, toks);
Check("push: nome vira hash do mais recente", m.Single(s => s.Endpoint == "e1").TokenName == hB);
Check("push: hash fica como está", m.Single(s => s.Endpoint == "e2").TokenName == hA);
Check("push: nome sem token sai", m.All(s => s.Endpoint != "e3"));
```

Teste de ponta a ponta: após `tokens.Rename(hash, "Outro")`, `push.CountFor(hash)` continua 1 e `/api/push/status` responde `subscriptions = 1`.

- [ ] **Step 2: Rode e confirme a falha**

- [ ] **Step 3: Implemente**

`MapPush` usa `((TokenInfo)ctx.Items["device"]!).Hash`, e `/push/test` passa `onlyTokens: new[] { hash }`. `IslandViewModel` chama `_push.MigrateTokenRefs(_tokens.List())` logo após semear permissões.

- [ ] **Step 4: Rode os testes e confirme que passam**

- [ ] **Step 5: Commit** `feat: inscrição de push liga ao dispositivo, não ao nome`

---

### Task 5: Caixa de saída por destinatário

**Files:**
- Modify: `Orbit/Bridge/Outbox.cs`, `OutboxRoutes.cs`, `Orbit/Island/IslandViewModel.cs`, `Orbit/Tabs/IPhone/IPhoneViewModel.cs`, `Orbit.Tests/Program.cs`

**Interfaces:**
- Consumes: `FileTickets.Issue/Find` (Task 2), `onlyTokens` (Task 4)
- Produces:
  - `record OutboxItem(string Id, string Name, long Size, string? File, string? Text, DateTime CreatedAt, IReadOnlyList<string>? Recipients = null)`
  - `Outbox.List(DateTime now, string device)`, `PathOf(string id, DateTime now, string device)`, `AddFile(string path, DateTime now, IReadOnlyList<string> recipients, string? displayName = null)`, `AddText(string text, DateTime now, IReadOnlyList<string> recipients)`, `Remove(string id, string device)`, `RemoveDevice(string device)`
  - `SendFilesToIPhone(files, name)` e `SendTextToIPhone(text)` mandam a todos os hashes por enquanto (Task 6 troca)
  - `IPhoneViewModel` recebe `Action<string> onRevoked` no construtor

- [ ] **Step 1: Escreva os `Check`**

```csharp
var box = new Outbox(dir);
var a = box.AddFile(f, t0, new[] { "A", "B" })!;
var c = box.AddText("só do B", t0, new[] { "B" })!;
Check("saída: cada um vê o seu", box.List(t0, "A").Count == 1 && box.List(t0, "B").Count == 2);
Check("saída: tíquete de outro aparelho não acha", box.PathOf(c.Id, t0, "A") is null);
Check("saída: apagar é só meu", box.Remove(a.Id, "A") && box.List(t0, "B").Any(i => i.Id == a.Id) && File.Exists(box.PathOf(a.Id, t0, "B")!));
Check("saída: apagar de novo = não é meu", !box.Remove(a.Id, "A"));
var path = box.PathOf(a.Id, t0, "B")!;
box.Remove(a.Id, "B");
Check("saída: último apaga o arquivo", !File.Exists(path));
box.RemoveDevice("B");
Check("saída: revogar limpa destinatário", box.List(t0, "B").Count == 0);
// outbox.json antigo, sem Recipients
Check("saída: item antigo é de todos", legacy.List(t0, "qualquer").Count == 1 && legacy.Remove(id, "qualquer") && legacy.List(t0, "outro").Count == 0);
```

De ponta a ponta: tíquete de download emitido para o iPad e depois apagado pelo iPad responde 404; depois de `tokens.Rename`, o item segue listado.

- [ ] **Step 2: Rode e confirme a falha**

- [ ] **Step 3: Implemente**

As rotas passam o `Hash` de `ctx.Items["device"]`. `/outbox/download` compara o item contra `box.List(now, owner)`: se o dono do tíquete não é mais destinatário, 404. O `AnnounceToIPhone` passa `onlyTokens: recipients`. `Revoke` na aba chama `onRevoked(hash)`, que a ilha liga a `_outbox.RemoveDevice`.

- [ ] **Step 4: Rode os testes e confirme que passam**

- [ ] **Step 5: Commit** `feat: caixa de saída separada por dispositivo`

---

### Task 6: Escolha do destino

**Files:**
- Rename: `Orbit/Services/SendToIPhone.cs` → `SendToDevice.cs`
- Modify: `Orbit/App.xaml.cs`, `Orbit/Services/TrayIcon.cs`, `Orbit/Island/IslandViewModel.cs`, `IslandWindow.xaml`, `IslandWindow.xaml.cs`, `DrawWindow.cs`, `Orbit/Tabs/Clips/ClipsViewModel.cs`, `ClipsView.xaml`, `Orbit/Tabs/IPhone/IPhoneViewModel.cs`, `installer/Orbit.iss`, `Orbit.Tests/Program.cs`

**Interfaces:**
- Consumes: `Outbox.AddFile/AddText` (Task 5), `TokenStore.List`
- Produces:
  - `enum DeviceChoice { None, Direct, Ask, Blocked }`
  - `static DeviceChoice IslandViewModel.DecideChoice(int devices, bool fullscreen)`
  - `static List<PeekAction> DeviceActions(IReadOnlyList<TokenInfo> devices, Action<IReadOnlyList<TokenInfo>> send)`
  - `static List<string> LiveRecipients(IEnumerable<TokenInfo> chosen, IReadOnlyList<TokenInfo> current)`
  - `void ChooseDevice(string what, Action<IReadOnlyList<TokenInfo>> send)`
  - `SendFiles(IReadOnlyList<string> files, IReadOnlyList<TokenInfo> to, string? name = null)`, `SendText(string text, IReadOnlyList<TokenInfo> to)`, `SendClipboard()`
  - `Peek ScreenshotPeek(string path, IReadOnlyList<TokenInfo> devices)`
  - `record DropTarget(string Label, IReadOnlyList<TokenInfo> Devices)`, `ObservableCollection<DropTarget> DropTargets`
  - `SendToDevice.Arg = "--send-device"`, `PipeName = "Orbit2SendToDevice"`, `ShortcutName = "Dispositivo (Orbit).lnk"`

- [ ] **Step 1: Escreva os `Check`**

```csharp
Check("destino: nenhum", IslandViewModel.DecideChoice(0, false) == DeviceChoice.None);
Check("destino: um vai direto", IslandViewModel.DecideChoice(1, false) == DeviceChoice.Direct);
Check("destino: um vai direto até em tela cheia", IslandViewModel.DecideChoice(1, true) == DeviceChoice.Direct);
Check("destino: dois perguntam", IslandViewModel.DecideChoice(2, false) == DeviceChoice.Ask);
Check("destino: tela cheia não pergunta", IslandViewModel.DecideChoice(2, true) == DeviceChoice.Blocked);
IReadOnlyList<TokenInfo>? sent = null;
var acts = IslandViewModel.DeviceActions(new[] { ipad, iphone }, d => sent = d);
Check("destino: botões", acts.Select(a => a.Label).SequenceEqual(new[] { "iPad", "iPhone", "Todos" }));
acts[2].Run();
Check("destino: Todos manda aos dois", sent!.Count == 2);
Check("destino: um aparelho não tem Todos", IslandViewModel.DeviceActions(new[] { ipad }, _ => { }).Select(a => a.Label).SequenceEqual(new[] { "iPad" }));
Check("destino: revogado sai da lista", IslandViewModel.LiveRecipients(new[] { ipad, iphone }, new[] { ipad }).SequenceEqual(new[] { ipad.Hash }));
Check("destino: seletor aparece com o painel aberto", /* IslandMachine: Expand, Collapse, RaisePeek => Mode == Alerta */);
```

- [ ] **Step 2: Rode e confirme a falha**

- [ ] **Step 3: Implemente `ChooseDevice`**

Com a ponte desligada, peek "Ligue a ponte" como hoje, antes de tudo. `None`: peek "Pareie um dispositivo". `Direct`: chama `send`. `Blocked`: `ToastNotifier.Notify("Não mandei", "Saia da tela cheia para escolher o dispositivo", false)`. `Ask`: `Collapse()` e `RaisePeek(new Peek("Mandar para…", what, "iphone") { Key = "escolher-dispositivo", Duration = TimeSpan.FromSeconds(10), Actions = DeviceActions(...) })`. O peek fica atrás do painel se não recolher, por isso o `Collapse`.

`SendFiles`/`SendText` calculam `LiveRecipients` antes de copiar. Lista vazia: peek "Dispositivo revogado" e nada é copiado.

- [ ] **Step 4: Ligue as entradas, conforme a tabela da seção 3 da spec**

Ctrl+Alt+I e bandeja chamam `SendClipboard`, que decide `DecideClip` primeiro e só escolhe o destino com conteúdo válido. O botão da aba Clipboard e o chip do desenho passam a ter o rótulo `Dispositivo`.

`EnsureShortcut` apaga `iPhone (Orbit).lnk` se existir. `RemoveShortcut` apaga os dois. Em `Orbit.iss`, liste os dois nomes em `UninstallDelete`.

- [ ] **Step 5: Implemente as zonas de soltar**

`OnPillDragEnter` monta `DropTargets` (um por aparelho + `Todos`; com um aparelho, zona única com o nome dele). `DropHint` usa `ItemsControl` com `UniformGrid` de 1 linha. Em `OnPillDrop`, o alvo vem de `(e.OriginalSource as FrameworkElement)?.DataContext as DropTarget`; sem alvo, cai em `ChooseDevice`, para o arquivo nunca se perder.

- [ ] **Step 6: Rode os testes e confira no app as entradas de envio**

Roteiro manual com 2 tokens: Ctrl+Alt+I, bandeja, Enviar para, botão da aba Clipboard, chip do desenho, print e arrastar. Confira se os botões cabem na pílula.

- [ ] **Step 7: Commit** `feat: escolher o dispositivo em cada envio do PC`

---

### Task 7: Sessões com dono e aviso

**Files:**
- Modify: `Orbit/Bridge/RemoteScreen.cs`, `MicStream.cs`, `BridgeServer.cs`, `Orbit/Island/IslandViewModel.cs`, `Orbit/Tabs/IPhone/IPhoneViewModel.cs`, `Orbit/Bridge/wwwroot/app.js`, `index.html`, `Orbit.Tests/Program.cs`

**Interfaces:**
- Consumes: dono do tíquete (Task 2), `TokenStore.Find`
- Produces:
  - `record SessionOwner(string Hash, string Name)`
  - `record RemoteState(bool Viewing, bool Controlling, bool Listening, string? Viewer = null, string? Controller = null, string? Listener = null)`
  - `BeginVideo/BeginInput/BeginAudio(CancellationToken aborted, SessionOwner owner)`
  - `string? RemoteSessions.DisplacedBy(CancellationTokenSource s)`, `void StopDevice(string hash)`, `SessionOwner? Viewer`
  - `BridgeServer.StopDevice(string hash)`
  - `const int RemoteScreen.TakenCloseCode = 4001`
  - `GET /api/screen/owner` → `{ name, mine }`

- [ ] **Step 1: Escreva os `Check`**

```csharp
var ss = new RemoteSessions(_ => { });
var a = ss.BeginVideo(default, new SessionOwner("A", "iPhone"));
var b = ss.BeginVideo(default, new SessionOwner("B", "iPad"));
Check("sessão: novo dono derruba o anterior", a.IsCancellationRequested && ss.DisplacedBy(a) == "iPad");
var c = ss.BeginVideo(default, new SessionOwner("B", "iPad"));
Check("sessão: reconectar não vira assumiu", b.IsCancellationRequested && ss.DisplacedBy(b) is null);
var d = ss.BeginAudio(default, new SessionOwner("A", "iPhone"));
ss.StopDevice("B");
Check("sessão: StopDevice poupa o outro", c.IsCancellationRequested && !d.IsCancellationRequested);
Check("pílula: controlando", IslandViewModel.RemoteActivity(new RemoteState(true, true, false, "iPad", "iPad")) == "iPad controlando o PC");
Check("pílula: vendo", IslandViewModel.RemoteActivity(new RemoteState(true, false, false, "iPad")) == "iPad vendo a tela");
Check("pílula: mesmo aparelho", IslandViewModel.RemoteActivity(new RemoteState(true, false, true, "iPad", null, "iPad")) == "iPad vendo a tela · microfone");
Check("pílula: aparelhos diferentes", IslandViewModel.RemoteActivity(new RemoteState(true, false, true, "iPad", null, "iPhone")) == "iPad vendo a tela · Microfone no iPhone");
Check("pílula: só microfone", IslandViewModel.RemoteActivity(new RemoteState(false, false, true, null, null, "iPad")) == "Microfone no iPad");
```

De ponta a ponta: dois tokens abrem `/api/remote/input`; o primeiro WebSocket fecha com `CloseStatus == 4001` e `CloseStatusDescription == "iPad"`.

- [ ] **Step 2: Rode e confirme a falha**

- [ ] **Step 3: Implemente no servidor**

Em `Begin`, se a vaga é de outro dono, grave `DisplacedBy` (dicionário por CTS, limpo em `End`). Nos `finally` dos WebSockets, consulte `DisplacedBy` antes de `End` e feche com `4001` e o nome. `IslandViewModel.RemoteActivity` monta os textos dos `Check`, e a aba usa o mesmo texto: apague `IPhoneRemoteText`.

Na aba, troque `Server.StopRemote()` por `Server.StopDevice(hash)` ao revogar ou desligar chave. "Encerrar" continua com `StopRemote`.

- [ ] **Step 4: Implemente no PWA**

WebSocket fechado com `4001`: mostra `O ${ev.reason} assumiu a tela` e o botão "Retomar", que refaz o fluxo de abrir. Para o MJPEG, enquanto a tela estiver aberta consulte `/api/screen/owner` a cada 5 s, porque o Safari não sinaliza o fim do stream de forma confiável; `mine: false` mostra a mesma mensagem. Nomes entram só por `textContent`.

- [ ] **Step 5: Rode os testes e confirme que passam**

- [ ] **Step 6: Commit** `feat: sessão remota mostra quem está conectado e avisa quem perdeu a vez`

---

### Task 8: Origem nos alertas

**Files:**
- Modify: `Orbit/Bridge/BridgeHandlers.cs`, `BridgeServer.cs`, `RemoteScreen.cs`, `MicStream.cs`, `OutboxRoutes.cs`, `Orbit/Island/IslandViewModel.cs`, `Orbit.Tests/Program.cs`

**Interfaces:**
- Produces:
  - `record BridgeEvent(string Title, string Detail, string? OpenPath = null, string? Device = null)`
  - `static string IslandViewModel.PeekTitle(BridgeEvent e)`

- [ ] **Step 1: Escreva os `Check`**

```csharp
Check("alerta: com origem", IslandViewModel.PeekTitle(new BridgeEvent("Link aberto no PC", "x", null, "iPad")) == "iPad · Link aberto no PC");
Check("alerta: sem origem", IslandViewModel.PeekTitle(new BridgeEvent("Autorize o microfone", "x")) == "Autorize o microfone");
```

De ponta a ponta: `POST /api/links/open` com o token do iPad registra `BridgeEvent` com `Device == "iPad"`.

- [ ] **Step 2: Rode e confirme a falha**

- [ ] **Step 3: Implemente**

Rotas com `h.Notify` recebem `HttpContext` e passam `Device`. Títulos: `Arquivo recebido`, `{n} arquivos recebidos`, `Foto no clipboard`, `Tela compartilhada`, `Câmera aberta`, `Foto capturada`, `Microfone aberto`. O `/pair` usa `Pareado` / `O {nome} recebeu o acesso`. O alerta de consentimento do microfone leva o nome do dono do tíquete.

- [ ] **Step 4: Rode os testes e confirme que passam**

- [ ] **Step 5: Commit** `feat: alertas mostram qual dispositivo agiu`

---

### Task 9: Renomeação, iPad e documentação

**Files:**
- Rename: `Orbit/Tabs/IPhone/` → `Orbit/Tabs/Dispositivos/` (`DispositivosViewModel`, `DispositivosView`, namespace `Orbit.Tabs.Dispositivos`)
- Modify: `Orbit/Island/IslandViewModel.cs`, `IslandWindow.xaml`, `IslandWindow.xaml.cs`, `IslandMachine.cs`, `Orbit/Services/SettingsStore.cs`, `Orbit/Models/OrbitSettings.cs`, `Orbit/Bridge/wwwroot/{index.html,app.js,style.css}`, comentários em `Orbit/Bridge/*.cs` e `Orbit/Services/*.cs`
- Modify: `CLAUDE.md`, `README.md`, `docs/tailscale-setup.md`, `docs/atalhos-ios.md`, `Orbit.Tests/Program.cs`

**Interfaces:**
- Produces:
  - `static void SettingsStore.MigrateTabIds(OrbitSettings s)`
  - id de aba `dispositivos`, título `Dispositivos`

- [ ] **Step 1: Escreva os `Check`**

```csharp
var s = new OrbitSettings { LastTab = "iphone", HiddenTabs = new() { "iphone", "notas" } };
SettingsStore.MigrateTabIds(s);
Check("migra: LastTab", s.LastTab == "dispositivos");
Check("migra: HiddenTabs", s.HiddenTabs.Contains("dispositivos") && !s.HiddenTabs.Contains("iphone") && s.HiddenTabs.Contains("notas"));
```

Teste de ponta a ponta: `settings.json` com `"LastTab":"iphone"` carregado por `SettingsStore.Load(path)` já vem migrado.

- [ ] **Step 2: Rode e confirme a falha**

- [ ] **Step 3: Renomeie com `git mv` e atualize `TabOrder`, `IslandWindow.xaml`, `Notify` (`peek.TabId != "dispositivos"`) e os demais usos de `"iphone"`**

- [ ] **Step 4: Atualize textos e comentários**

Mensagens de 403 do PWA: `Ligue "X" na aba Dispositivos do PC.`. Textos do PWA usam "aparelho". Descrições da plataforma continuam, como "No iPhone não há Fullscreen API". Teste que valida `"Tela no iPhone"` já mudou na Task 7.

Depois, confirme o que resta:

```bash
rtk proxy rg -i iphone Orbit --glob '!**/bin/**' --glob '!**/obj/**'
```

Cada ocorrência restante deve descrever a plataforma, ou ser o nome antigo do atalho, usado para apagá-lo.

- [ ] **Step 5: Adicione a grade em `style.css`**

```css
@media (min-width: 900px) {
  header, main { max-width: 1040px; }
  main { grid-template-columns: repeat(2, minmax(0, 1fr)); }
}
@media (min-width: 1200px) {
  header, main { max-width: 1320px; }
  main { grid-template-columns: repeat(3, minmax(0, 1fr)); }
}
```

Telas abertas de página cheia precisam ocupar a grade inteira: use `grid-column: 1 / -1` onde o card precisar de largura.

- [ ] **Step 6: Atualize `CLAUDE.md`, `README.md` e os docs do iPhone para dispositivos, incluindo a regra de permissão em tíquete**

- [ ] **Step 7: Rode testes e build, e confira o PWA em largura de iPad**

`dotnet build` e `dotnet run --project Orbit.Tests`. Em seguida, abra o PWA no navegador em 820 px e 1180 px, e depois em iPhone e iPad reais.

- [ ] **Step 8: Commit** `feat: aba Dispositivos substitui iPhone, com layout de iPad e docs`
