# Onde parei — Fase 4 (ponte com o iPhone)

Atualizado em 2026-10-16, ao encerrar a sessão autônoma a pedido do usuário.

## Estado

- Branch **`fase4-ponte`** (ainda **não** mergeada no `master`). Último commit de código: ver `git log`.
- Build Debug/Release: 0 erros / 0 avisos. `dotnet run --project Orbit.Tests`: **303 checks, TODOS OK**.
- Spec: `docs/superpowers/specs/2026-10-16-fase4-ponte-design.md`. Plano: `docs/superpowers/plans/2026-10-16-fase4-ponte.md`.

| Task do plano | Situação |
|---|---|
| 1. Regras, tokens (hash), auditoria | ✅ feita + revisão de segurança aplicada |
| 2. Servidor Kestrel (127.0.0.1, auth, rate limit, rotas) | ✅ feita + revisão de segurança aplicada (auth antes do rate limit e do body, 1 MB por padrão, revogação que não falha calada, nomes de arquivo robustos) |
| 3. Aba iPhone, handlers reais, QR | ✅ feita + revisão aplicada (start fora da UI thread, suspender responde antes de dormir) |
| 4. PWA (HTML/JS puro) | ✅ feita — correções da revisão aplicadas |
| 5. Docs Tailscale/Atalhos + CLAUDE.md | ✅ feita — correções da revisão aplicadas |

## Pendências (revisão do lote 4C — RESOLVIDAS)

1. **Crítico — pareamento no app da Tela de Início.** No iOS o app instalado não herda o
   `localStorage` do Safari: o token salvo ao abrir o QR não vai junto e o app pede token.
   - `app.js`: o campo de token deve aceitar o **link inteiro ou só o token**
     (`raw.replace(/^.*[#&]token=/, '')` + `decodeURIComponent` dentro de `try`); também pôr `try`
     no `decodeURIComponent` do hash do topo (hash malformado mata o script).
   - `index.html`: texto do cartão explicando "abriu pelo ícone? copie o link do QR (toque e segure
     o aviso da câmera → Copiar) e cole aqui"; placeholder "link ou token".
   - `docs/tailscale-setup.md` passo 6: o app instalado pede o token uma vez; caminho para colar.
2. **Importante — "Trazer do PC" no iOS:** `writeText` depois de `await` perde o gesto. Usar
   `navigator.clipboard.write([new ClipboardItem({'text/plain': promiseDoBlob})])` criado
   sincronamente no clique (código pronto no histórico da conversa/revisão).
3. **Importante — cache:** `UseStaticFiles` sem `Cache-Control` → Safari pode servir `app.js` velho
   por dias. `OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache"` + check
   em `PwaTests`; no `sw.js`, só cachear `r.ok`.
4. **Docs:** ACL de exemplo precisa de `"hosts": { "notebook": "100.x.y.z" }` (e dizer "seus
   aparelhos", não "só o iPhone"); citar `tailscale serve --https=443 off`/`reset`; atalhos: código
   413, limite de 100.000 caracteres no clipboard, 120/min compartilhado.
5. **Menores do PWA:** erro de rede em pt-BR + `setOnline(false)`; upload sem token/413/429 com
   mensagens próprias; flag `dragging` no slider de volume; `.row:first-of-type` cola botões no
   textarea (trocar por classe explícita).

Pendências 1–5 acima aplicadas no commit "fix: PWA aceita link colado…".
Verificado depois sem iPhone: PWA renderizado no Edge headless (layout ok; corrigido botões de mídia
esticados) e rotas exercitadas por HTTP num servidor isolado (link, clipboard UTF-8, upload, energia, 401, no-cache). Falta: revisão rápida, teste real no iPhone (precisa do Tailscale — **não
instalado nesta máquina**; instalar é decisão do usuário), aceitação e merge no `master`.

## Verificado no Orbit real (2026-10-16, sem iPhone)

- Barra com as 11 abas cabe; aba iPhone renderiza.
- Ligar a ponte → "Ligada · 127.0.0.1:5191"; `netstat` mostra só `127.0.0.1:5191`.
- `/health` e `/` (PWA) 200 sem token; `/api/stats` 401 sem token.
- Parear (sem endereço do Tailscale) → mostra só o token + "Copiar"; com o token, `/api/stats` e
  `/api/media` devolvem dados reais (CPU/RAM/disco/bateria; música do Spotify + volume).
- Revogar → 401; desligar → porta fechada; `BridgeEnabled` volta a `false`, `tokens.json` vazio.

Falta só: Tailscale instalado + `tailscale serve`, abrir no iPhone, adicionar à Tela de Início e
os Atalhos (docs/tailscale-setup.md, docs/atalhos-ios.md).

## Como testar sem iPhone

Um servidor isolado (handlers falsos, dados em `%TEMP%`) foi usado em
`<scratchpad>\pwaqa` — ver `Orbit.Tests` (`BridgeServerTests`, `PwaTests`) para o mesmo fluxo
automatizado. A ponte real fica **desligada por padrão**; ligar na aba iPhone do Orbit.
