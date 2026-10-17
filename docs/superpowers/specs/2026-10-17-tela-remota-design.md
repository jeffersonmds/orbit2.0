# Tela remota: ver e controlar o PC pelo iPhone

Data: 2026-10-17. Extensão da ponte (Fase 4), mesmo modelo de segurança da câmera.

## Objetivo

No PWA, uma página "Tela do PC" mostra a tela ao vivo e permite controlar mouse e teclado.
Usos: apresentação, mídia do sofá, conferir algo rodando, clicar num diálogo sem levantar.

## Decisões (com o usuário)

- **Controle:** toque direto por padrão (tocar na imagem clica ali; arrastar arrasta; toque longo =
  clique direito; dois dedos rolam) + modo **trackpad** alternável (cursor relativo, preciso).
- **Permissões separadas**, desligadas por padrão, na aba iPhone: **Tela** (só ver) e **Controle**
  (mouse/teclado; só vale com Tela ligada). Desligado → 403.
- **Indicador:** enquanto houver sessão, a pílula mostra uma atividade fixa ("Tela no iPhone" /
  "iPhone controlando o PC") com prioridade máxima. A aba iPhone tem "Encerrar" que derruba tudo.

## Desenho

- **Captura** (`Services/ScreenCapture`): GDI `CopyFromScreen` do monitor escolhido, cursor desenhado
  por cima (o GDI não o captura), reduzido e codificado em JPEG. Processo PerMonitorV2 → pixels físicos.
  Quadro idêntico ao anterior não é reenviado. ponytail: GDI a ~10 fps; Windows.Graphics.Capture se
  precisar de mais fps/menos CPU.
- **Vídeo:** MJPEG (`multipart/x-mixed-replace`) como a câmera, autenticado por **tíquete de uso único**
  (a `<img>` não manda cabeçalho). Qualidade "leve" (960 px, ~10 fps) ou "nítida" (1600 px, ~6 fps).
- **Entrada:** WebSocket `/api/remote/input?ticket=` (baixa latência; o browser não manda cabeçalho no WS).
  Mensagens JSON pequenas, validadas por `RemoteInput.Parse` (puro): `abs` (0..1 no monitor), `move`
  (relativo), `down`/`up`/`click` (esquerdo/direito), `scroll`, `text` (Unicode), `key` (nome + modificadores).
  Execução por `SendInput`/`SetCursorPos`. Teto de mensagens por segundo.
- **Ponte não conhece WPF:** captura, execução da entrada e o aviso de sessão passam por `BridgeHandlers`
  (os testes injetam fakes — nenhum teste mexe no mouse de verdade).

## Limites conhecidos (explicados na tela)

- PC bloqueado: a área de trabalho muda e a captura não vê nada.
- Janelas de administrador (UAC/apps elevados) ignoram entrada de um app comum (UIPI).
- Com a ilha oculta (app em tela cheia) o indicador não aparece; o aviso de início ainda vai pro log/auditoria.

## Fora do escopo

Áudio do PC, zoom com pinça na imagem, transferência de arquivos pela tela, várias sessões simultâneas
(uma sessão de vídeo por vez; uma nova derruba a anterior).
