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

## Pendente / backlog

- Atalhos do iOS (`docs/atalhos-ios.md`) ainda não testados pelo usuário.
- "Trazer do PC" (clipboard → iPhone) depende do suporte a `ClipboardItem` do iOS.
- Agendar desligamento pelo iPhone (o usuário preferiu deixar só na ilha por enquanto).
- Backlog geral na spec principal (§11).
