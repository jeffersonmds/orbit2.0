# Orbit 2.0 — Fase 4: Ponte com o iPhone — Design

Data: 2026-10-16 · Complementa a §6 de `2026-10-15-orbit2-design.md` · Status: decidido pelo
agente durante execução autônoma (usuário autorizou seguir sem revisão; revisar ao acordar).

## Decisões novas (em relação à spec principal)

| Tema | Decisão | Motivo |
|---|---|---|
| PWA | HTML/CSS/JS puro em `Orbit/Bridge/wwwroot`, sem Angular e sem build | Angular CLI depende de binários nativos baixados pelo npm (esbuild), que o Smart App Control desta máquina tende a bloquear; sem build não há o que quebrar. Também é mais leve. |
| Liga/desliga | Ponte **desligada por padrão**; liga na aba iPhone (`BridgeEnabled`) | Sem Tailscale instalado o servidor não serve pra nada; desligado, o ASP.NET nem sobe (memória). |
| Bind | `127.0.0.1:<porta>` (padrão 5191), nunca `0.0.0.0` | RNF01: só o `tailscale serve` (local) alcança. |
| Endereço público | Campo "Endereço do Tailscale" na aba iPhone (`BridgePublicUrl`, ex.: `https://notebook.tail1234.ts.net`) | O Orbit não depende do CLI do Tailscale; o QR usa esse endereço. |
| QR de pareamento | `QRCoder` (NuGet, gerenciado). Se o Smart App Control bloquear a DLL, a aba mostra só link + token copiáveis | Única dependência nova; tem plano B. |
| PWA sem autenticação nos arquivos estáticos | O HTML/JS não tem segredo; **toda** rota `/api/*` exige token | O token chega pelo `#token=` do QR e fica no `localStorage` do PWA. |
| Upload | Pasta `%USERPROFILE%\Downloads\Orbit` (configurável), nome sanitizado, sem sobrescrever (`foto (1).jpg`), limite 500 MB por requisição | RF08/RF10. |

## Arquitetura

```
Orbit/Bridge/
├─ BridgeRules.cs     # puro: validar URL, sanitizar nome, nome único, parse de ação
├─ TokenStore.cs      # tokens.json: só SHA-256; criar/verificar(tempo constante)/revogar
├─ AuditLog.cs        # audit.jsonl (append) + últimas N em memória pra aba
├─ BridgeHandlers.cs  # delegates que a ilha implementa (abrir URL, clipboard, energia, mídia…)
├─ BridgeServer.cs    # Kestrel in-process (CreateSlimBuilder), rate limit, auth, rotas
└─ wwwroot/           # PWA: index.html, app.js, style.css, manifest, sw.js, ícones
Orbit/Tabs/IPhone/    # aba: liga/desliga, status, endereço, parear (QR), tokens, atividade
```

- `BridgeServer` não conhece WPF: tudo que mexe em UI/clipboard/mídia passa por
  `BridgeHandlers` (delegates), implementados no `IslandViewModel` com `Dispatcher.InvokeAsync`.
  Isso também deixa o servidor testável com handlers falsos (testes de integração com `HttpClient`
  numa porta efêmera).
- Rotas (todas sob `/api`, com token e rate limit de 60/min por processo, exceto `/health`):
  `GET /api/stats`, `POST /api/links/open`, `GET|PUT /api/clipboard`,
  `POST /api/power/{lock|sleep|restart|shutdown}`, `GET /api/media`,
  `POST /api/media/{playpause|next|prev}`, `PUT /api/media/volume`, `POST /api/files`.
- Cada ação gera linha em `audit.jsonl` (`quando`, `token`, `acao`, `resultado`) e, quando faz
  sentido, alerta na pílula ("iPhone · link aberto", "iPhone · arquivo recebido").
- Erros: `ProblemDetails`/status HTTP; exceção em handler → 500 + auditoria `erro`; porta ocupada
  → ponte fica desligada e a aba mostra o erro.

## Fora desta fase
Web Push, navegar/baixar arquivos do PC, notas, scripts, passkey (backlog da spec principal).
Instalar/configurar o Tailscale é do usuário (passo a passo em `docs/tailscale-setup.md`).
