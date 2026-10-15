# CLAUDE.md

## O que é

Orbit 2.0: uma "ilha" estilo Dynamic Island (inspirada no Hitodama) no topo central da tela do
Windows. WPF .NET 10. Em repouso é uma pílula vazia; expande para um painel com abas. Spec:
`docs/superpowers/specs/2026-10-15-orbit2-design.md`. Sucessor do Orbit 1.0 (`C:\repos\orbit1.0`).

Código, comentários e commits são em **português**.

## Comandos

```bash
dotnet build                              # app + testes (Orbit.slnx)
dotnet run --project Orbit                # roda a ilha
dotnet run --project Orbit.Tests          # roda TODOS os testes; exit != 0 se falhar
```

`Orbit.Tests` é um exe com harness próprio (`Check(nome, bool)`), sem xUnit. Só testa métodos
puros/estáticos: extraia a lógica para `static` sem I/O nem UI e registre um `Check`.

Smart App Control está ligado nesta máquina: se um build local der `FileLoadException 0x800711C7`,
rebuild (novo hash) e tente de novo. Evite pacotes NuGet com DLL nativa de terceiro.

## Arquitetura

- **Uma janela fixa** (`Island/IslandWindow`, 760×400, transparente, topmost). Só a `Border`
  "Pill" anima; pixels alpha 0 deixam o clique passar.
- **`IslandMachine`** (puro) decide o estado: Repouso, Atividade, Alerta, Expandida, Oculta.
  `IslandViewModel` orquestra a máquina e as abas; a janela só reage a `Mode`.
- **Abas** herdam `Tabs/TabViewModel` e ficam em `Tabs/<Nome>/`. São criadas na mão no
  construtor de `IslandViewModel` (sem DI). **Contrato de leveza:** só a aba visível do painel
  expandido recebe `SetActive(true)`; timers e fetches só rodam ativos. `SafeSetActive` isola exceções.
- **Tela cheia:** `FullscreenWatcher` (appbar + `ABN_FULLSCREENAPP`) esconde a ilha.
- **Acento:** `Services/Accent` usa a cor de destaque do Windows (`UISettings`) ou o override
  de Ajustes; recursos `AccentBrush`/`AccentSoftBrush` — sempre `DynamicResource`.
- **Settings:** `%AppData%\Orbit2\settings.json`; `Load()` nunca lança, `Save()` engole I/O.
  Logs Serilog em `%AppData%\Orbit2\logs\`.
- Hotkey global: Ctrl+Alt+O. Instância única: mutex `Orbit2_SingleInstance`.

## Convenções

- `ImplicitUsings` desligado; `Nullable` ligado; `[ObservableProperty]` no estilo campo.
- `// ponytail:` marca simplificação deliberada com teto conhecido — respeite.
