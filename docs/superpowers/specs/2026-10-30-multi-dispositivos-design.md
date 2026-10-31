# Multi-dispositivos: do "iPhone" para "Dispositivos"

Data: 2026-10-30. Sucede o conceito de aparelho único da ponte (`2026-10-16-fase4-ponte-design.md`).

## Objetivo

Ter mais de um aparelho pareado (hoje: um iPhone e um iPad) com a mesma interação que o iPhone já
tem com o PC, e escolher o destino ao mandar algo do PC. O nome "iPhone" sai da interface e do
código e dá lugar a "Dispositivos" e ao nome real de cada aparelho.

## Decisões

| Tema | Decisão |
|---|---|
| Modelo | Token = dispositivo. Sem `DeviceStore` separado. |
| Destino do envio | Sempre escolher. Com 1 aparelho vai direto; com 2 ou mais, botões por aparelho + "Todos". |
| Permissões | Por dispositivo: Câmera, Arquivos, Tela, Controle, Microfone, Teclado. |
| Sessões remotas | Uma por vez. O aparelho novo assume e o anterior é avisado. |
| iPad | Ajuste leve em CSS (media query). |

## Fora do escopo

- Envio direto de aparelho para aparelho.
- Layout próprio de tablet.
- Ver a tela em dois aparelhos ao mesmo tempo.
- Dispositivo padrão.

## 1. Identidade e permissões

`TokenInfo` passa a ser o registro do dispositivo:

```csharp
public sealed record TokenInfo(string Name, string Hash, DateTime CreatedAt, DevicePermissions? Permissions = null);
public sealed record DevicePermissions(bool Camera, bool PcFiles, bool Screen, bool Control, bool Mic, bool Keyboard);
```

- **Id estável = `Hash`.** Já é único, imutável e usado por `Revoke`. `Name` vira só rótulo, editável.
- `TokenStore` ganha `Rename(hash, name)` e `SetPermissions(hash, permissions)`, com a mesma regra de
  `Revoke`: falha de gravação desfaz a memória e propaga, para a aba mostrar o erro.
- `Verify` devolve `TokenInfo?` em vez do nome. O middleware guarda o dispositivo em `ctx.Items`.
- Auditoria continua registrando o nome (é o que o usuário lê).
- O pareamento pede o nome do aparelho na aba. Nome vazio vira `Dispositivo <dd/MM HH:mm>`.
- Renomear apara espaços, recusa nome vazio e corta em 24 caracteres (o nome vira botão na pílula).
- Desligar a Câmera ou revogar interrompe o stream dela na hora: ela não ocupa vaga em `RemoteSessions`,
  então o laço do stream relê a permissão a cada quadro.

### Migração

- `tokens.json` sem `Permissions`: o token herda as chaves globais `Bridge*Enabled` e o resultado é
  gravado. Depois disso as chaves globais não valem mais nada além desse valor inicial; continuam em
  `OrbitSettings` só para isso.
- Dispositivo novo: `Permissions` com tudo `false`.
- `push.json`: o campo `TokenName` passa a guardar o `Hash`. Na carga, valor que não é hash de 64
  caracteres hexadecimais é trocado pelo `Hash` do token de mesmo nome; sem token correspondente, a
  inscrição sai. Se dois tokens tiverem o mesmo nome, a inscrição fica com o mais recente.
- O campo `Hash` e a coleção `ValidTokens` passam a ser a chave de todo vínculo.

### Checagem de permissão

O filtro de capability em `BridgeServer` passa a ler `Permissions` do dispositivo da requisição, sem
mudar as regras: `/api/remote` continua exigindo Tela e Controle.

As rotas por tíquete entram sem token, então o dispositivo não existe em `ctx.Items`. Por isso:

- `OneShotTickets`, `StreamTickets` e `FileTickets` guardam o `Hash` de quem pediu.
- A permissão é checada ao emitir (rota com token) e de novo ao usar (rota por tíquete), lendo o
  estado atual do `TokenStore`.
- Tíquete de dispositivo revogado ou sem a permissão responde 403 e não abre stream.
- `/api/camera/stream`, `/api/pcfiles/download`, `/api/screen/stream`, `/api/remote/input`,
  `/api/mic/stream` e `/api/keyboard/input` são cobertas. `/api/outbox/download` usa a mesma ligação
  do tíquete ao dispositivo, verificando se ele ainda é destinatário do item.

Links, clipboard, notas, mídia, envio de arquivos para o PC e energia seguem sem permissão própria:
qualquer dispositivo pareado usa, como hoje.

## 2. Caixa de saída e push

`OutboxItem` ganha `IReadOnlyList<string>? Recipients` (hashes).

- `List(now, deviceHash)`, `PathOf(id, now, deviceHash)` filtram pelo destinatário. Sem
  `Recipients` (item gravado antes da mudança) o item é visível a todos até vencer.
- "Todos" é resolvido no envio para a lista dos aparelhos daquele momento. Aparelho pareado depois não
  recebe o que veio antes.
- `Remove(id, deviceHash)` tira o aparelho de `Recipients`. O item e o arquivo só somem quando a lista
  esvazia ou em 24 h. Item sem `Recipients` é removido por inteiro no primeiro apagar.
- Revogar um dispositivo o tira de `Recipients` de todos os itens, com a mesma regra de esvaziar.
- `WebPush.SendAsync` troca `onlyToken` por `IReadOnlyCollection<string>? onlyTokens`. Anúncio de
  envio vai só para os destinatários.
- Alertas gerais (`Notify`) vão para todos com inscrição ativa. Cada aparelho decide em "Avisos" no
  próprio PWA, sem preferência nova no PC.
- `GET /api/me` devolve `{ name, permissions }` para o PWA. As mensagens de 403 passam a orientar pela
  aba Dispositivos.

## 3. Escolha do destino

Um único `IslandViewModel.ChooseDevice(string what, Action<IReadOnlyList<TokenInfo>> send)`:

- 0 dispositivos: `RaisePeek` com "Pareie um dispositivo", levando à aba.
- 1 dispositivo: chama `send` direto.
- 2 ou mais: recolhe o painel (o alerta não aparece com ele aberto) e mostra `Peek` com uma
  `PeekAction` por aparelho + "Todos", duração de 10 s. Expirou, nada é enviado e nada é apagado.
- 2 ou mais em tela cheia: toast "Saia da tela cheia para escolher o dispositivo" e nada é enviado. O
  seletor não vai para a fila, senão apareceria muito depois, sem contexto.

Ponte desligada mantém o comportamento de hoje (peek "Ligue a ponte") antes de qualquer escolha.

| Entrada | Comportamento |
|---|---|
| Ctrl+Alt+I | `ChooseDevice` com o conteúdo já lido do clipboard. Conteúdo privado continua recusado antes. |
| Bandeja | Igual ao Ctrl+Alt+I. |
| Enviar para (Explorer) | `ChooseDevice`. Um atalho só, `Dispositivo (Orbit).lnk`. |
| Aba Clipboard | Botão "Dispositivo", depois `ChooseDevice`. |
| Desenho | Chip "Dispositivo". O PNG é salvo depois de a janela fechar, como hoje, e aí `ChooseDevice`. |
| Print | Botões do alerta: uma ação por aparelho + "Todos" + "Abrir". Com 1 aparelho, botão com o nome dele. |
| Arrastar para a ilha | Uma zona de soltar por aparelho + "Todos". Com 1 aparelho, a zona única atual com o nome dele. |

`SendFilesToIPhone`/`SendTextToIPhone` viram `SendFiles`/`SendText` e recebem os destinatários.
`--send-iphone` vira `--send-device`, e `SendToIPhone` vira `SendToDevice`. `EnsureShortcut` apaga
`iPhone (Orbit).lnk` se existir. A cópia para `outbox\` acontece depois da escolha.

## 4. Sessões

- `RemoteSessions` guarda dono (`Hash` + nome) em cada vaga: vídeo, entrada, áudio.
- `RemoteState` ganha o nome do dispositivo de cada vaga. Pílula e aba mostram "iPad vendo a tela".
- Quem perde a vaga recebe aviso: o WebSocket de entrada ou de áudio fecha com um código de encerramento
  próprio; para o vídeo MJPEG, que não tem canal de volta e cujo fim o Safari não sinaliza de forma
  confiável, o PWA consulta `GET /api/screen/owner` a cada 5 s enquanto a tela está aberta (exige só
  Tela: quem apenas vê não tem Controle). A mensagem é "O iPad assumiu a tela", com botão "Retomar".
- `StopDevice(hash)` encerra só as vagas do dispositivo. É chamado ao revogar e ao desligar qualquer
  permissão dele. "Encerrar" na aba continua derrubando tudo.
- Filtro `RemoteScreen.KeyboardOnly` e as regras de `RemoteInput.Parse` não mudam.

## 5. Renomeação e iPad

- `Tabs/IPhone/` vira `Tabs/Dispositivos/` (`DispositivosViewModel`, `DispositivosView`), id
  `dispositivos`, título "Dispositivos".
- `SettingsStore.Load` troca `"iphone"` por `"dispositivos"` em `HiddenTabs` e `LastTab`.
- A lista de dispositivos da aba mostra nome, "visto há X" (último `Verify`, só em memória),
  renomear, revogar e as seis chaves.
- `BridgeEvent` ganha `Device` (nome). `Notify` monta `"<nome> · <título>"`. A regra de hoje não muda:
  evento causado por um aparelho não vira push para nenhum aparelho. A condição passa a ser
  `peek.TabId != "dispositivos"`.
- Textos do PWA e da ilha, `manifest`, docs de setup e `CLAUDE.md` são atualizados. Texto que descreve
  a plataforma ("no iPhone não há Fullscreen API") continua.
- `installer\Orbit.iss` revisado para as duas ocorrências.
- `style.css`: acima de 900 px, `main` usa grade de 2 a 3 colunas, e `max-width` de `header` e `main`
  acompanha. A tela remota já ocupa a área disponível.

## Erros

- Gravar tokens falhou: a aba mostra o erro e a memória não muda (mesma regra de `Revoke`).
- Destinatário revogado entre a escolha e a cópia: sai da lista. Lista vazia cancela e avisa na pílula.
- Falha de push não afeta o envio: o item fica na caixa de saída.

## Testes

`Check` no harness, sem tocar em `SendInput`:

- Migração: `tokens.json` antigo herda as chaves; `push.json` por nome vira hash; nome inexistente sai;
  `"iphone"` vira `"dispositivos"` em `HiddenTabs` e `LastTab`.
- Permissões: dois tokens com permissões diferentes, de ponta a ponta com `HttpClient`.
- Tíquetes: emitido e depois permissão desligada = 403; dispositivo revogado = 403; tíquete de um não
  vale como de outro.
- Caixa de saída: filtro por destinatário, remoção parcial, arquivo some ao esvaziar, item antigo
  visível a todos, revogação limpa destinatário.
- Push: `onlyTokens` só seleciona destinatários.
- Sessões: novo dono substitui o anterior e o estado traz o nome; `StopDevice` não derruba o outro.
- Escolha: `ChooseDevice` puro para 0, 1 e 2 dispositivos, extraído como função estática de decisão.

Verificação manual (fora do harness): seletor na pílula, zonas de arrastar, Enviar para no Explorer e
o PWA em iPhone e iPad reais.

## Ordem de entrega

Cada etapa deixa o app funcionando e os testes passando.

1. Identidade, permissões e tíquetes.
2. Caixa de saída e push por destinatário.
3. Escolha do destino.
4. Sessões com dono e aviso.
5. Renomeação, textos, CSS do iPad, docs.
