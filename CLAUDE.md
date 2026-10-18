# CLAUDE.md

## O que é

Orbit 2.0: uma "ilha" estilo Dynamic Island (inspirada no Hitodama) no topo central da tela do
Windows. WPF .NET 10. Em repouso é uma pílula vazia; expande para um painel com abas. Spec:
`docs/superpowers/specs/2026-10-15-orbit2-design.md`. Sucessor do Orbit 1.0 (`C:\repos\orbit1.0`).

Código, comentários e commits são em **português**.

## Comandos

```bash
dotnet build                              # app + testes (Orbit.slnx)
dotnet run --project Orbit                # roda a ilha
dotnet run --project Orbit.Tests          # roda TODOS os testes; exit != 0 se falhar
installer\build.ps1                       # publish self-contained + Inno Setup → installer\Orbit2Setup.exe
```

`Orbit.Tests` é um exe com harness próprio (`Check(nome, bool)`), sem xUnit. Só testa métodos
puros/estáticos: extraia a lógica para `static` sem I/O nem UI e registre um `Check`.

Smart App Control está ligado nesta máquina: se um build local der `FileLoadException 0x800711C7`,
rebuild (novo hash) e tente de novo. Evite pacotes NuGet com DLL nativa de terceiro.

## Arquitetura

- **Uma janela fixa** (`Island/IslandWindow`, 760×400, transparente, topmost). Só a `Border`
  "Pill" anima; pixels alpha 0 deixam o clique passar.
  Render por **software** (`App.OnStartup`): no modo hardware o D3D + driver retinha ~165 MB
  nativos (medido 260 → 95 MB após usar as abas). Não volte pro hardware sem medir.
  Processo **PerMonitorV2** (`app.manifest`): `IslandWindow.Recenter` posiciona em pixels físicos no
  monitor de Ajustes (`IslandMonitor`; desconectado → principal; reposiciona no DisplaySettingsChanged).
- **`IslandMachine`** (puro) decide o estado: Repouso, Atividade, Alerta, Expandida, Oculta.
  `IslandViewModel` orquestra a máquina e as abas; a janela reage a `Mode` (layout/animação)
  e a `Peek` (reinicia o timer do alerta).
- **Abas** herdam `Tabs/TabViewModel` e ficam em `Tabs/<Nome>/` (`XViewModel` + `XView.xaml`, com
  `DataTemplate` por tipo em `IslandWindow.xaml`). São registradas no construtor de
  `IslandViewModel` (`Register(...)`, acima da linha-âncora) e ordenadas por
  `IslandViewModel.TabOrder`; o usuário esconde abas em Ajustes (`HiddenTabs`). **Contrato de
  leveza:** só a aba visível do painel expandido recebe `SetActive(true)`; timers e fetches só
  rodam ativos. Abas com rede usam `Freshness.IsStale` pra não buscar de novo a cada abertura.
  Leitura pesada (contadores, processos) vai em `Task.Run`, nunca na UI thread.
  `SafeSetActive` isola exceções. Ao recolher, `IslandWindow` faz um GC agressivo único.
- **Serviços de fundo** (rodam com a ilha recolhida, por evento) alimentam a pílula via
  `IslandViewModel.SetActivity`: `NowPlayingViewModel` (SMTC), `PomodoroViewModel` (timer só
  rodando) e `ClaudeHookServer` (pipe `Orbit2ClaudeHooks`, distinto do 1.0; o Claude Code chama
  `Orbit.exe --claude-hook`). Prioridade na pílula: Claude aguardando > Pomodoro > mídia.
- **Clipboard** (`Tabs/Clips/`, namespace `Orbit.Tabs.Clips` — nunca `...Clipboard`, que esconderia
  `System.Windows.Clipboard`): `ClipboardWatcher` (WM_CLIPBOARDUPDATE) chama
  `ClipsViewModel.CaptureAsync`; histórico em `clipboard.json` + `clipboard-img\` (chave de texto =
  hash). Conteúdo marcado como privado (gerenciadores de senha) é ignorado; arquivo de imagem
  copiado no Explorer vira imagem. Imagem do clipboard é copiada pra `WriteableBitmap` antes do
  encode em `Task.Run` (BitmapFrame congelado ainda prende o decoder da UI thread).
  Colar = clipboard + recolher + `Input.SendCtrlV` no `_prevFg`.
- **`_prevFg`** (app em foco antes da ilha) vem de `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` —
  `WM_ACTIVATE` não serve (lParam nulo pra app de outro processo).
- **Apps** (`Tabs/Apps/`): `shell:AppsFolder` via COM numa thread STA (`Sta.Run`); ícones por
  `IShellItemImageFactory` (alfa direto → `Bgra32`) com cache em `app-icons\`; abre por shell
  execute de `shell:AppsFolder\<id>`.
- **Ponte** (`Bridge/`): Kestrel in-process (`BridgeServer`, `CreateSlimBuilder`) só em
  `127.0.0.1:BridgePort` (padrão 5191), **desligada por padrão** (aba iPhone liga). Não conhece
  WPF: chama `BridgeHandlers` (delegates que o `IslandViewModel` implementa com Dispatcher).
  `/api/*` exige `Authorization: Bearer` (tokens só como SHA-256 em `tokens.json`), rate limit e
  auditoria em `audit.jsonl`; `/health` e o PWA (`Bridge/wwwroot`, HTML/JS puro, copiado pra
  `<saída>\wwwroot`) são abertos. Testada de ponta a ponta no harness com `HttpClient` em porta
  efêmera. Exposição ao iPhone é pelo `tailscale serve` (docs/tailscale-setup.md), nunca funnel.
  Capabilities à parte, desligadas por padrão (403): Câmera e Arquivos do PC (`PcFiles`: só leitura,
  só pastas do usuário, caminho sempre raiz + relativo validado; download por tíquete de 10 min).
  Avisos no iPhone por **Web Push** (`WebPush`: RFC 8291/8292 com cripto nativa, testado contra o
  vetor do RFC; endpoint só de serviço de push conhecido — anti-SSRF; inscrição ligada ao token).
  `Notify` empurra os alertas da ilha com a ponte ligada, exceto os causados pelo próprio iPhone.
  **Tela remota** (`Bridge/RemoteScreen`, `Services/ScreenCapture` + `RemoteInput`; spec
  `2026-10-17-tela-remota-design.md`): permissões Tela e Controle; vídeo MJPEG e WebSocket de entrada
  por tíquete; `RemoteInput.Parse` recusa o que não conhece; sessão ativa = atividade `TelaRemota`
  (prioridade máxima) na pílula + "Encerrar" na aba iPhone. Testes nunca executam `SendInput`.
  **Microfone** (`Bridge/MicStream`, `Services/MicCapture`): permissão própria, independente da câmera;
  WebSocket por tíquete com PCM 16 bits mono ~24 kHz, Web Audio no iPhone. Na 1ª vez o Windows pede
  consentimento NA TELA DO PC e abrir trava até responder: `ConsentTimeout` avisa iPhone e ilha.
  **PC → iPhone** (`Bridge/Outbox`): caixa de saída de 24 h (cópias em `outbox\`), alimentada por Explorer
  → Enviar para (`Orbit.exe --send-iphone`, pipe `CurrentUserOnly`; atalho existe só com a ponte ligada),
  arrastar pra pílula e o botão iPhone da aba Clipboard; push abre "Recebidos do PC". Foto do iPhone →
  clipboard do PC (`/api/clipboard/image`, `PhotoDecoder` gira pelo EXIF).
- **Alertas** passam sempre por `IslandViewModel.Notify(peek, sound)`: peek na pílula, ou toast
  em tela cheia se `AlertsAsToastInFullscreen`.
- **Settings** é uma instância única (`IslandViewModel` repassa às abas): nunca faça
  `SettingsStore.Load()` dentro de aba — o próximo `Save` de outra aba sobrescreveria.
- **Tela cheia:** `FullscreenWatcher` (appbar + `ABN_FULLSCREENAPP`) esconde a ilha.
- **Acento:** `Services/Accent` usa a cor de destaque do Windows (`UISettings`) ou o override
  de Ajustes; recursos `AccentBrush`/`AccentSoftBrush` — sempre `DynamicResource`.
- **Settings:** `%AppData%\Orbit2\settings.json`; `Load()` nunca lança, `Save()` engole I/O.
  Logs Serilog em `%AppData%\Orbit2\logs\`.
- Hotkeys globais: Ctrl+Alt+I manda o clipboard pro iPhone (arquivos do Explorer vão como os originais;
  conteúdo privado nunca sai). Ctrl+Alt+O — a mesma do 1.0 (o 2.0 o substitui; com o 1.0 aberto o registro
  falha e vai pro log). Ignorada com a ilha Oculta (tela cheia) ou escondida pela bandeja.
- Ligar "Iniciar com o Windows" apaga a entrada `Run\Orbit` do 1.0.
- Itens "Debug: …" da bandeja (alerta/atividade de teste) só existem em build Debug.
- Instância única: mutex `Orbit2_SingleInstance`.

## Convenções

- Botões com conteúdo próprio usam `PlainButton`/`RowButton` (o `Button` padrão herda texto preto).
- Instalar os hooks do Claude remove hooks `Orbit.exe --claude-hook` de outro caminho (ex.: o do 1.0).
- Glifos da Segoe Fluent: `\uXXXX` em C#, `&#xXXXX;` em XAML — nunca o caractere literal.

- `ImplicitUsings` desligado; `Nullable` ligado; `[ObservableProperty]` no estilo campo.
- `// ponytail:` marca simplificação deliberada com teto conhecido — respeite.
- Para encerrar o app em testes, mate pelo PID — nunca `taskkill /IM Orbit.exe`: o Orbit 1.0
  tem o mesmo nome de processo.
