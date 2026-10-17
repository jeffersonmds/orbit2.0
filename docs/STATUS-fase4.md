# Fase 4 — Ponte com o iPhone: concluída

Mergeada no `master` em 2026-10-16, após teste real no iPhone (via Tailscale).

## O que entrou

- **Ponte** (`Orbit/Bridge/`): Kestrel em `127.0.0.1:5191`, desligada por padrão; token (só SHA-256
  salvo), auth antes do rate limit e do body, auditoria em `audit.jsonl`, revisão de segurança
  aplicada (sem path traversal, sem bypass de auth, bind só loopback).
- **Aba iPhone**: liga/desliga, integração com o Tailscale instalado (status, endereço automático,
  "Publicar no Tailscale" = `tailscale serve`, nunca funnel), pareamento por **código de 6 dígitos**
  (5 min, uso único, trava após 5 erros) ou QR, tokens com revogar, atividade, pasta de uploads.
- **PWA** (HTML/JS puro): link (completa `https://`), clipboard nos dois sentidos, upload (diz onde
  salvou), player com capa/ícones/volume, stats, energia com confirmação em folha própria.
- **Aba Energia** ganhou bateria, desligamento agendado e apagar a tela.

## Testado no iPhone (usuário)

Pareamento por código, mídia, envio de arquivos, energia com confirmação, abrir link.

## Câmera do notebook no iPhone (2026-10-16)

Extensão da ponte: ver as câmeras do PC ao vivo no PWA e tirar foto pras Fotos do iOS.
Spec `docs/superpowers/specs/2026-10-16-camera-iphone-design.md`, plano
`docs/superpowers/plans/2026-10-16-camera-iphone.md`. **Testado no iPhone** (integrada + Logitech
C920): stream, troca de câmera/qualidade ao vivo, foto salva nas Fotos, parar fecha a câmera e
apaga o LED na hora. Capability "Câmera" desligada por padrão; stream autenticado por tíquete de
uso único (a `<img>` não manda cabeçalho). O LED físico acende sempre que a câmera capta (hardware);
pedido de "não acender" recusado por ser indicador de privacidade. Áudio do microfone fica no backlog. Funciona com o PC bloqueado (testado).

## Pendente / backlog

- Atalhos do iOS (`docs/atalhos-ios.md`) ainda não testados pelo usuário.
- Agendar desligamento pelo iPhone (o usuário preferiu deixar só na ilha por enquanto).
- Backlog geral na spec principal (§11).
