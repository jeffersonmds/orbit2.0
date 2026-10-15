# Orbit 2.0 — Design

Data: 2026-10-15 · Status: aprovado no brainstorming

## 1. O que é

Uma "ilha" estilo Dynamic Island fixada no topo central da tela do Windows, inspirada no
[Hitodama](https://hitodama.in). Em repouso é uma pílula preta vazia e discreta; ao passar o
mouse ela expande para um painel com abas (controles, hoje, stats, clipboard, apps, energia,
mercado, notícias, Claude, iPhone). Herda features do Orbit 1.0 (`C:\repos\orbit1.0`) e
incorpora a ponte com o iPhone descrita em `ponte-notebook-iphone.md` (rede Tailscale + PWA +
Atalhos), rodando dentro do mesmo processo.

Uso estritamente pessoal. Requisito transversal: **leve** — CPU ~0% parado.

### Fora de escopo

- IA por voz do Hitodama (Circle to Ask, ditado, rotinas): fica para um projeto futuro.
- Aba Wallpaper e acento extraído do wallpaper (inclui Wallpaper Engine): backlog.
- Pipeline de reunião do Teams (gravação, Whisper, resumo) do 1.0: não será portado.
- Luz noturna e troca de dispositivo de saída de áudio: backlog (só via API não documentada).

## 2. Decisões

| Tema | Decisão | Motivo |
|---|---|---|
| UI | WPF, .NET 10 | Janela transparente em forma de pílula é nativa; alto reuso do 1.0; framework assinado pela Microsoft (o Smart App Control desta máquina bloqueia DLLs de terceiros sem reputação). WinUI 3 descartado: janela transparente é difícil. Web/Tauri descartados: memória e stack nova. |
| Presença | Pílula sempre visível, some em tela cheia | Estilo Hitodama, sem atrapalhar jogos e vídeos |
| Cor de destaque | Cor de destaque do Windows, com override por paleta manual | Efeito "segue o wallpaper" de graça quando o Windows está em automático |
| Ponte | Kestrel dentro do processo do Orbit | Um processo só. Um Windows Service rodaria na sessão 0 e não acessaria clipboard, navegador, mídia nem lock |
| Persistência | Arquivos JSON/JSONL, sem SQLite | `e_sqlite3.dll` é nativa de terceiro (risco com o Smart App Control) e não há consultas que justifiquem um banco |
| Tempo real | Polling do PWA (2 s, só com o PWA aberto), sem SignalR | Custo zero quando ninguém olha |
| Extensibilidade | Uma pasta por aba, sem `IModule`/plugins | ~10 abas fixas; ligar e desligar é um bool no settings |

## 3. Arquitetura

```
orbit2.0/
├─ Orbit/                      # WPF .NET 10 — um processo
│  ├─ Island/                  # janela, máquina de estados, animação, appbar/tela cheia
│  ├─ Tabs/
│  │  ├─ Controles/  Hoje/  Stats/  Clipboard/  Apps/  Energia/
│  │  ├─ Mercado/  Noticias/  ClaudeCode/  IPhone/
│  ├─ Bridge/                  # Kestrel: auth, rate limit, auditoria, endpoints
│  ├─ Services/                # Settings, SecretBox, Accent, Tray, Autostart, HotKey, Log
│  └─ App.xaml
├─ Orbit.Tests/                # harness Check(nome, bool), igual ao 1.0
├─ Orbit.Web/                  # Angular + @angular/pwa (build servido pelo Kestrel)
└─ docs/superpowers/{specs,plans}/
```

Convenções herdadas do 1.0: código, comentários e commits em português; MVVM sem DI
(`IslandViewModel` cria os VMs das abas na mão); `ImplicitUsings` desligado; `Nullable`
ligado; lógica testável extraída para métodos `static` puros; comentários `// ponytail:`
marcam simplificações deliberadas.

**Contrato de leveza.** Toda aba implementa `SetActive(bool)`. Só a aba visível do painel
expandido fica ativa. Com a ilha recolhida, rodam apenas fontes por evento: SMTC (mídia),
hook server do Claude, listener de clipboard, Pomodoro (se estiver correndo) e Kestrel. Não
há timer nenhum em repouso.

**Código compartilhado.** Endpoints da ponte e botões das abas chamam as mesmas classes de
serviço (ex.: `Power.Lock()` serve à aba Energia e a `POST /power/lock`).

## 4. A ilha

Uma única janela WPF (`WindowStyle=None`, `AllowsTransparency`, `Topmost`, sem item na barra
de tarefas) que anima o próprio tamanho. Fora da área desenhada é transparente, então os
cliques passam para o que está atrás. Fica no monitor principal.

| Estado | Visual | Tamanho aprox. |
|---|---|---|
| Repouso | Pílula preta vazia | ~110×10 px |
| Atividade | Pílula alargada com conteúdo contínuo. Prioridade: Claude aguardando > Pomodoro > mídia | ~320×30 px |
| Alerta (peek) | Evento pontual (link/arquivo do iPhone, Claude pediu permissão, troca de fase do Pomodoro). Dura ~4 s e volta; clique abre a aba relacionada | ~360×56 px |
| Expandida | Painel com barra de abas em pílula; abre na última aba usada | ~720×380 px |
| Oculta | Nada; há um app em tela cheia no monitor da ilha | — |

Gatilhos:
- Repouso/Atividade → Expandida: hover ~250 ms, scroll ↓ na pílula, clique ou hotkey global.
- Expandida → recolhe: mouse fora por ~600 ms, `Esc`, clique fora ou hotkey.
- Qualquer estado → Oculta: notificação `ABN_FULLSCREENAPP` (a janela se registra como appbar
  com `ABM_NEW`, sem `ABM_SETPOS`, e não reserva espaço). Plano B, se o appbar falhar:
  `SHQueryUserNotificationState` a cada 2 s.
- Alertas que chegam enquanto ela está oculta ficam numa fila e aparecem ao sair. Opção
  configurável: virar toast do Windows.

Bandeja: mostrar/ocultar, Ajustes, Sair. Ajustes abre como aba de configurações do painel.

**Visual:** painel quase preto (#0E0E10), cards #1B1B1E, texto claro, uma única cor de
destaque (lida em `UISettings.GetColorValue(Accent)` e atualizada no evento `ColorValuesChanged`,
com override por paleta manual), pílulas, anéis de progresso e números grandes.

## 5. Abas

| Aba | Conteúdo | Implementação | Custo enquanto visível |
|---|---|---|---|
| Controles | Wi‑Fi, Bluetooth, modo escuro, volume, Now Playing | `Windows.Devices.Radios`; registro `AppsUseLightTheme`/`SystemUsesLightTheme` + `WM_SETTINGCHANGE`; interop COM Core Audio (`IAudioEndpointVolume`); SMTC do 1.0 | Por evento |
| Hoje | Relógio grande, clima, mini-semana, Pomodoro, to-dos | Clima (Open-Meteo) e Pomodoro portados; to-dos em `todos.json` | Tick de 1 s |
| Stats | Anéis de CPU, RAM e GPU; disco; rede ↓↑; uptime; top 5 apps por RAM | SystemStats do 1.0; contadores "GPU Engine"; `NetworkInterface`; `Process.GetProcesses()` | Amostra a cada 2 s |
| Clipboard | Histórico de texto, link e imagem; busca; filtros; fixar; colar | `AddClipboardFormatListener`; últimos 200 itens; imagens em PNG; ignora itens com o formato `ExcludeClipboardContentFromMonitorProcessing` | Zero |
| Apps | Favoritos fixados + busca nos atalhos do Menu Iniciar | Varre `.lnk` do Menu Iniciar (usuário + todos); ícones extraídos uma vez com cache | Zero |
| Energia | Bloquear, suspender, reiniciar, desligar | `LockWorkStation`, `SetSuspendState`, `shutdown /r\|/s /t 0`; reiniciar e desligar pedem confirmação | Zero |
| Mercado | Cripto e câmbio com sparkline | Porta do 1.0 (CoinGecko, AwesomeAPI) | Fetch periódico |
| Notícias | Hacker News | Porta do 1.0 | Fetch periódico |
| Claude | Sessões e status; alimenta a pílula (atividade e alerta) | Porta do 1.0 (`ClaudeHookServer`, `ClaudeSessionReducer`, instalador do hook) | Por evento |
| iPhone | Conectado ou não, últimos itens recebidos, QR de pareamento, tokens | Lê o estado da ponte | Por evento |

## 6. Ponte com o iPhone

Baseada em `ponte-notebook-iphone.md`, com os ajustes abaixo.

**Rede.** Kestrel escuta só em `127.0.0.1:<porta>` e o `tailscale serve --bg <porta>` publica
em HTTPS. `tailscale funnel` é proibido. 2FA da conta, Tailnet Lock e ACLs (porta acessível só
pelo iPhone) são configuração documentada em `docs/tailscale-setup.md`, não código.

**Autenticação.** Tokens aleatórios de 256 bits, enviados como `Authorization: Bearer`. O Orbit
persiste só o SHA‑256 de cada token (com nome e data), em `tokens.json`. A comparação é em
tempo constante. Todas as rotas exigem token, exceto `/health`. Pareamento: a aba iPhone gera
um token e mostra um QR com `https://<host>.ts.net/#token=<token>`; o PWA guarda o token e
limpa o fragmento da URL. O mesmo token serve aos Atalhos. Há revogação por token na aba.
Passkey/WebAuthn fica no backlog.

**Proteções.** Rate limiting com o middleware nativo do ASP.NET Core; auditoria em
`audit.jsonl` (`quando`, `token`, `acao`, `resultado`); upload de arquivo só para a pasta de
destino configurada, com nome sanitizado e limite de tamanho configurável; validação de
caminhos rejeita `..`, caminho absoluto, symlink/junction e tudo fora da lista de pastas
permitidas.

**Endpoints do primeiro corte:**

| Rota | Ação |
|---|---|
| `GET /health` | Vivo (sem auth) |
| `POST /links/open` | Abre a URL no navegador padrão (só `http`/`https`); dispara alerta na ilha |
| `GET /clipboard` · `PUT /clipboard` | Lê/escreve o texto da área de transferência |
| `POST /power/{lock\|sleep\|restart\|shutdown}` | Energia (o PWA confirma as ações destrutivas) |
| `POST /media/{playpause\|next\|prev}` · `PUT /media/volume` | Mídia |
| `GET /stats` | CPU, RAM, disco, bateria |
| `POST /files` | Upload multipart para a pasta de destino; dispara alerta na ilha |

Depois: navegar e baixar arquivos, notas rápidas `.md`, biblioteca de scripts (allowlist) e
Web Push.

**PWA (`Orbit.Web`).** Angular + `@angular/pwa`, mobile-first, modo escuro, alvos de toque
≥ 44 px, manifest e ícones para a tela inicial do iOS. Uma tela por grupo de endpoints e
indicador online/offline. O build vai para o `wwwroot` do Kestrel.

**Atalhos iOS** (passo a passo em `docs/atalhos-ios.md`): "Abrir no PC", "Copiar para o PC" e
"Enviar para o PC", todos pelo menu Compartilhar via "Obter conteúdo do URL" com o header do
token.

## 7. Dados

Tudo em `%AppData%\Orbit2\`:

| Arquivo | Conteúdo |
|---|---|
| `settings.json` | Abas ligadas, ordem, hotkey, acento, configs das abas, porta e pastas da ponte |
| `clipboard.json` + `clipboard-img/` | Histórico (máx. 200, fixados não contam) |
| `todos.json` | To-dos |
| `tokens.json` | Hashes dos tokens da ponte |
| `audit.jsonl` | Auditoria da ponte (append) |
| `logs/` | Serilog, rolling por hora |

Segredos (ex.: chave de API, se alguma aba precisar) usam DPAPI via `SecretBox`, portado do 1.0.

## 8. Erros

- `SettingsStore.Load()` nunca lança (arquivo faltando ou corrompido → defaults); `Save()`
  engole erro de I/O. Mesmo padrão para os outros JSONs.
- APIs externas: falha → log `Warning`, retorna `null`; a aba mostra o último valor bom ou "—".
- Isolamento de aba: `SetActive` e handlers de cada aba ficam em try/catch com log;
  `DispatcherUnhandledException` loga e mantém o app vivo.
- Ponte: se a porta estiver ocupada ou o Kestrel falhar ao subir, a ponte fica desligada, a
  aba iPhone mostra o erro e o resto do app segue. Erros de endpoint → `ProblemDetails`
  4xx/5xx e linha no `audit.jsonl` com `resultado=erro`.
- Instância única via `Mutex` nomeado `Orbit2_SingleInstance`.

## 9. Testes

Harness `Orbit.Tests` (exe com `Check(nome, bool)`), testando só métodos estáticos puros:

- Máquina de estados da ilha: transições e prioridade da pílula.
- Validação de caminhos da ponte (`..`, absoluto, symlink, fora da lista permitida).
- Tokens: geração, hash, verificação e revogação.
- Clipboard: dedupe, limite de 200 com fixados, filtros, exclusão de itens sensíveis.
- Validação de URL em `/links/open` (só `http`/`https`).
- Testes portados junto com o código do 1.0 (clima, mercado, notícias, Pomodoro, Claude).

**Critérios de aceitação medidos:**
- Ilha recolhida e parada: CPU ~0% no Gerenciador de Tarefas.
- Working set < 120 MB com a ponte ligada.
- Some em tela cheia (jogo e vídeo) e volta ao sair.

## 10. Ordem de entrega

Cada fase tem o seu próprio plano em `docs/superpowers/plans/`.

1. **Shell:** solução, janela da ilha, os 5 estados e animações, appbar e tela cheia, acento,
   bandeja, hotkey, settings, logs, instância única e aba Energia.
2. **Portes do 1.0:** Hoje, Stats, Controles, Mercado, Notícias e Claude.
3. **Abas novas:** Clipboard e Apps.
4. **Ponte:** Kestrel, auth, QR, auditoria, endpoints do primeiro corte, aba iPhone, PWA e
   Atalhos.

## 11. Backlog

Acento extraído do wallpaper (incluindo Wallpaper Engine, via captura esporádica e reduzida
— requer spike); aba Wallpaper; luz noturna; troca de saída de áudio; passkey; Web Push;
navegar e baixar arquivos; notas rápidas; scripts; escolha de monitor; IA por voz.
