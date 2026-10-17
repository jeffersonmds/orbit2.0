# Câmera do notebook no iPhone — design

Ver as câmeras do notebook ao vivo pelo PWA do iPhone, com botão para tirar
foto e salvar nas Fotos do celular. Extensão da ponte da Fase 4.

## Objetivo

Do iPhone, pelo PWA já pareado, abrir o vídeo ao vivo de uma câmera do
notebook (integrada ou USB) e, com um toque, capturar uma foto em resolução
cheia e salvá-la no celular. Usos: monitorar o ambiente de longe e dar uma
olhada rápida. Só imagem; áudio fica no backlog.

## Decisões

- **Pendura na ponte existente** (Kestrel em `127.0.0.1:5191`, Tailscale, token
  SHA-256, rate limit, `audit.jsonl`). Nenhuma porta ou serviço novo.
- **O PC não exibe a câmera.** Só captura e transmite — sem prévia na ilha, sem
  aviso (escolha do usuário). A luz física da webcam acende sempre que o sensor
  capta: é hardware, nenhum software apaga. Fica a Atividade da aba iPhone como
  registro.
- **Captura via `Windows.Media.Capture`** (WinRT, já no alvo do projeto). Sem
  DLL de terceiro — passa no Smart App Control. Ver [[orbit2-island]] e
  [[smart-app-control-blocks-thirdparty-dlls]].
- **Vídeo ao vivo por MJPEG** (`multipart/x-mixed-replace`) numa `<img>`. Padrão
  720p a ~24 q/s; seletor de qualidade no PWA (720p/480p + taxa) para baixar em
  rede ruim.
- **Só imagem.** O MJPEG é vídeo puro. Áudio do microfone fica no backlog.
- **Uma câmera por vez, um stream por vez.** Trocar de câmera solta a anterior
  antes de abrir a nova.

## Arquitetura

### PC — `Orbit/Services/Camera.cs`

Serviço novo, criado só quando há stream pedido; `Dispose` fecha o
`MediaCapture` e solta o dispositivo. Parado, não existe.

- `DevicesAsync()` → lista `(Id, Name)` das câmeras (`DeviceInformation.
  FindAllAsync(DeviceClass.VideoCapture)`), integrada e USB.
- `OpenAsync(deviceId, preset)` → inicializa `MediaCapture` no dispositivo e
  começa um `MediaFrameReader`. Cada frame vira JPEG (`SoftwareBitmap` →
  `BitmapEncoder`, qualidade ~0.7). Uma câmera por vez: abrir outra fecha a
  atual.
- `Frames` → os JPEGs mais recentes, consumidos pelo endpoint do stream; o
  encode acontece no ritmo do preset (clamp de taxa e resolução).
- `PhotoAsync()` → um JPEG em resolução cheia
  (`CapturePhotoToStreamAsync`), independente do preset do stream.
- Enquanto há stream ativo, mantém o PC acordado
  (`SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)`); ao fechar,
  limpa (`ES_CONTINUOUS`). Reusar o padrão se já houver em `Power`.
- Se o dispositivo estiver ocupado por outro app (Zoom/Teams) ou a inicialização
  falhar, lança/retorna erro claro que o endpoint converte em HTTP 409/503.

**Leveza:** a câmera abre no primeiro viewer e fecha quando o último stream cai
(sem viewer → sem captura → sem encode). Idle absoluto fora disso.

### Bridge — endpoints novos (`Orbit/Bridge/`)

Todos atrás do token, na mesma pipeline (auth antes do rate limit e do body) e
só quando o interruptor "Câmera" está ligado (senão 403):

- `GET /api/camera/devices` → `[{ id, name }]`.
- `POST /api/camera/open` → body `{ deviceId, preset }` (token no cabeçalho);
  devolve `{ ticket }` de uso único e curta duração. Começa a abrir a câmera.
- `GET /api/camera/stream?ticket=…` → `multipart/x-mixed-replace;
  boundary=orbitcam`, escrevendo cada JPEG como uma parte. O tíquete é
  consumido ao conectar; sem ele, ou expirado, 401. Fecha a câmera quando a
  conexão cai.
- `POST /api/camera/photo` → `image/jpeg` em resolução cheia (token no
  cabeçalho; sem tíquete, requisição normal).

**Autenticação do `<img>`:** uma `<img>` não manda cabeçalho, então o token
nunca entra na URL. O PWA faz `POST /api/camera/open` com o token no cabeçalho,
recebe um **tíquete opaco, aleatório, de uso único**, e o usa só na URL do
stream. O tíquete expira em segundos para conectar e é consumido ao conectar;
já consumido, é inútil em `audit.jsonl` ou nos logs do Tailscale. Padrão
reaproveitado de `Orbit/Bridge/PairingCodes.cs`.

### PWA (`Orbit/Bridge/wwwroot`)

- Cartão "Câmera": seletor de câmera (de `/devices`), seletor de qualidade,
  botão abrir/fechar, área de vídeo (`<img>` do stream), botão de foto.
- Abrir: `POST /open` → recebe tíquete → aponta a `<img>` para
  `/stream?ticket=…`. Fechar: tira o `src` (a conexão cai, a câmera fecha no
  PC).
- Foto: `POST /photo` (token no cabeçalho) → blob JPEG →
  `navigator.share({ files: [file] })`. No Safari iOS a folha oferece "Salvar
  imagem" (vai pras Fotos). Fallback quando `navigator.share` com arquivo não
  existe: mostra a imagem em tela cheia para o usuário segurar e "Adicionar às
  Fotos".
- Erros (câmera ocupada, capability desligada) viram aviso no cartão.

### Aba iPhone (PC)

- Interruptor **"Câmera"**, **desligado por padrão**, em `OrbitSettings`. Mesmo
  com a ponte ligada, os endpoints de câmera só respondem com ele ligado.
- Abertura de stream e captura de foto entram na Atividade e no `audit.jsonl`
  (ex.: "Câmera aberta pelo iPhone", "Foto capturada").

## Fluxo

1. iPhone (ponte + "Câmera" ligados) abre o cartão Câmera → `GET /devices`.
2. Escolhe câmera/qualidade → `POST /open` → tíquete.
3. `<img src=/stream?ticket=…>` → MJPEG ao vivo; o PC abre a câmera (luz acende).
4. Toca na foto → `POST /photo` → folha do iOS → Salvar imagem.
5. Fecha o cartão → `src` sai → stream cai → o PC fecha a câmera (luz apaga).

## Tratamento de erro

- Câmera ocupada por outro app → 409, cartão mostra "Câmera em uso por outro
  programa".
- Capability "Câmera" desligada → 403, cartão orienta ligar na aba iPhone.
- Tíquete inválido/expirado → 401, PWA refaz o `/open`.
- Falha de inicialização do `MediaCapture` → 503 com log; nada trava.
- Troca de câmera no meio do stream: solta a anterior, reabre; se a nova falhar,
  volta ao estado fechado com aviso.

## Testes (partes puras)

- Enquadramento MJPEG: monta o cabeçalho de cada parte (boundary +
  `Content-Type` + `Content-Length`) para um JPEG dado.
- Tíquete: `Issue`/`Claim` com uso único e expiração (espelha
  `PairingCodes`).
- Lista de câmeras: mapeamento e ordenação (integrada primeiro).
- Presets de qualidade: parsing e clamp de resolução/taxa a partir da string do
  PWA.

## Fora de escopo (backlog)

- **Áudio do microfone** junto do vídeo (exige segundo canal + sincronização).
- Gravar vídeo em arquivo.
- Ver duas câmeras ao mesmo tempo.
- Detecção de movimento / alerta.
- ~~Câmera com o PC bloqueado~~ — testado pelo usuário em 2026-10-16: funciona com a
  sessão bloqueada.
