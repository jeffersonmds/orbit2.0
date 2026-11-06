# Câmera e microfone com WebRTC

Em **Câmera e microfone**, Transporte oferece:

- **Automático** (padrão): tenta WebRTC; sem mídia em 6 s ou após queda, abre o modo Compatível. O aviso informa o fallback. A retomada usa a intenção anterior: Encerrar, revogar, parar ou outro aparelho assumir impedem reabrir o sensor.
- **Leve (WebRTC)**: vídeo H.264/VP8 e áudio Opus. Falha pede escolher Automático/Compatível.
- **Compatível**: os transportes MJPEG e PCM anteriores.

A preferência fica no aparelho; restaurá-la não liga os sensores. Câmera e microfone mantêm permissões, donos, paradas e sessões independentes. No iPhone, seus tracks compõem o mesmo MediaStream, com vídeo mudo inline e áudio passando pelo ganho Web Audio. São duas conexões WebRTC independentes; não há garantia de sincronização A/V entre elas.

O PC usa Microsoft.Web.WebView2 (única dependência nova) e o WebView2 Runtime do Windows 11. Cada sessão cria um controller numa janela nativa de 1 px fora da tela, sem ativação nem barra de tarefas. O documento fica visível para o Chromium, necessário para enumerateDevices. Ao parar, fechar o socket, expirar o lease (30 s sem consulta de status), revogar ou Encerrar, o documento/controller é destruído, inclusive se getUserMedia ainda estiver pendente. A pílula e os avisos usam RemoteSessions/Notify como no modo Compatível. Os semáforos impedem sobrepor os motores antigo e novo para o mesmo sensor.

A página e o script estão embutidos em Orbit.dll (`Orbit/Media`), fora do wwwroot. Uma origem HTTPS aleatória por sessão recebe só esses dois recursos; navegação, downloads, janelas novas e demais requisições são bloqueados. Apenas a permissão do sensor escolhido é concedida nessa origem. O nome usado no mapeamento vem da enumeração no PC, não do aparelho. ID Chromium exato ou label único; ambiguidade/dispositivo ausente falha sem escolher outro.

Sinalização: POST autenticado `/api/{camera|mic}/rtc/open`, WebSocket `/rtc/signal?ticket=...` com tíquete de uso único/30 s, preso ao dono e à permissão. O PC envia oferta; o aparelho responde e ambos trocam ICE host. Não há STUN/TURN externo. Fora da mesma LAN ou de uma ligação Tailscale com conectividade UDP entre os aparelhos, WebRTC não conecta e Automático cai em Compatível. O proxy HTTPS do Tailscale serve a sinalização, não transporta a mídia WebRTC. Firewall, isolamento do Wi-Fi e mDNS do Safari também podem impedir a conexão direta. Em HTTP sem contexto seguro, Automático usa Compatível.

O publish continua por pasta, sem single-file: o SDK inclui Core.dll e WebView2Loader.dll/runtimes. `installer/build.ps1` verifica esses arquivos; Inno Setup e o zip do workflow copiam as subpastas. O runtime Evergreen não é empacotado; se ausente, Automático usa Compatível.

Validação física pendente: PC com Smart App Control e iPhone/Safari/PWA, primeira permissão do Windows, escolha de câmera/mic (inclusive nomes repetidos), som e Retomar áudio, rotação/zoom, fundo/volta, LED apagando em Parar/Encerrar/revogação durante a abertura, troca rápida, LAN/Tailscale e rede bloqueada. Comparar CPU, bateria, atraso e tráfego nos três modos; não há medições reais neste ambiente.
