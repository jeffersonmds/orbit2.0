# Orbit 2.0

**Uma Dynamic Island para o Windows.** Uma pílula discreta no topo da tela que, ao passar o mouse,
vira um painel com controles, stats, clipboard, apps, mercado, notícias e uma ponte segura com iPhone e iPad.

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-0078D4?logo=windows&logoColor=white)
![C#](https://img.shields.io/badge/C%23-239120?logo=csharp&logoColor=white)
![Testes](https://img.shields.io/badge/checks-500%2B-2ea44f)

<p align="center">
  <img src="docs/screenshots/controles.png" width="820" alt="Painel expandido na aba Controles: Wi-Fi, Bluetooth, modo escuro, volume e mídia tocando">
</p>

Inspirado no [Hitodama](https://hitodama.in). Em repouso é uma pílula preta vazia; quando algo
acontece (música tocando, Pomodoro correndo, câmera ligada, o Claude Code esperando resposta) ela
mostra a atividade. Passou o mouse, expande. Some sozinha em jogos e vídeos em tela cheia.

## Telas

| Stats | Mercado |
|---|---|
| ![Stats](docs/screenshots/stats.png) | ![Mercado](docs/screenshots/mercado.png) |
| **Energia** | **Ajustes** |
| ![Energia](docs/screenshots/energia.png) | ![Ajustes](docs/screenshots/ajustes.png) |

## O que faz

**Na ilha**
- **Controles**: Wi-Fi, Bluetooth, modo escuro, volume geral e por app, mídia tocando (SMTC) com capa.
- **Hoje**: relógio, clima, Pomodoro e tarefas.
- **Stats**: CPU, RAM, GPU, disco, rede e os apps que mais usam memória.
- **Clipboard**: histórico de texto e imagem; ignora o que gerenciadores de senha marcam como privado.
- **Notas**, **Apps** (busca no menu Iniciar), **Mercado** (cripto e câmbio), **Notícias**.
- **Claude**: sessões do Claude Code via hooks; a pílula avisa quando uma está esperando você.
- **Energia**: bloquear, suspender, desligar agendado, apagar só a tela.
- **Conta-gotas** (`Ctrl+Alt+C`) e **desenho sobre a tela** (`Ctrl+Alt+D`).
- Cor de destaque do Windows ou paleta própria; escolha de monitor; abas que dá pra esconder.

**Ponte com iPhone e iPad (vários aparelhos)** (desligada por padrão)
- Servidor Kestrel dentro do processo, só em `127.0.0.1`, exposto ao celular pelo
  [Tailscale](https://tailscale.com) (`tailscale serve`, nunca funnel).
- PWA instalável na tela inicial: clipboard nos dois sentidos, envio de arquivos e fotos, player,
  stats, energia, abrir links.
- Câmera do PC ao vivo, microfone, **tela remota** com mouse e teclado, arquivos do PC (só leitura).
- **PC → aparelho**: "Enviar para" no Explorer, arrastar pra pílula ou `Ctrl+Alt+I`.
- Notificações nos aparelhos por **Web Push** (RFC 8291/8292 com cripto nativa do .NET).

## Segurança da ponte

Expor o PC pro celular pede cuidado, então cada parte é opt-in e fechada por padrão:

- Pareamento por código de 6 dígitos (5 min, uso único, trava após 5 erros) ou QR.
- Tokens guardados só como SHA-256; revogáveis na aba Dispositivos.
- Autenticação antes do rate limit e antes de ler o corpo; auditoria em `audit.jsonl`.
- Câmera, microfone, tela, controle, teclado e arquivos são **permissões separadas por dispositivo**, todas desligadas (403), checadas também no uso do tíquete.
- Streams autenticados por tíquete de uso único; caminhos de arquivo validados contra a raiz (sem path traversal).
- Web Push só aceita endpoints de serviços de push conhecidos (anti-SSRF).

## Leve de propósito

Requisito de projeto: **CPU ~0% parado**.

- Só a aba visível do painel expandido roda timers ou faz requisições; recolhida, a ilha só escuta eventos.
- Render por software: no modo hardware o driver segurava ~165 MB nativos (medido: 260 → 95 MB).
- Persistência em JSON, sem banco; poucas dependências e nenhuma DLL nativa de terceiro.

## Arquitetura

```
Orbit/
├─ Island/     janela transparente, máquina de estados (Repouso/Atividade/Alerta/Expandida/Oculta)
├─ Tabs/       uma pasta por aba: XViewModel + XView.xaml (MVVM com CommunityToolkit.Mvvm)
├─ Services/   áudio, mídia, rede, energia, clipboard, hotkeys, hooks do Claude, autoatualização
└─ Bridge/     Kestrel + PWA (HTML/JS puro), auth, Web Push, tela remota, câmera, microfone
Orbit.Tests/   harness próprio com 500+ checks, incluindo a ponte de ponta a ponta via HttpClient
```

A lógica fica em métodos estáticos puros, testáveis sem UI. O design completo está em
[`docs/superpowers/specs`](docs/superpowers/specs).

## Rodando

Requer Windows 10/11 e o [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet run --project Orbit          # roda a ilha
dotnet run --project Orbit.Tests    # roda os testes (exit != 0 se algum falhar)
installer\build.ps1                 # instalador self-contained (precisa do Inno Setup)
```

Para a ponte com iPhone ou iPad, ligue-a na aba **Dispositivos** e siga o guia de [Atalhos do iOS](docs/atalhos-ios.md).

---

Projeto pessoal, feito em português do código aos commits.
