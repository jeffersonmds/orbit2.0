# Orbit 2.0 — Fase 3 (Clipboard e Apps) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Duas abas novas na ilha: **Clipboard** (histórico de texto, links e imagens com busca, filtros, fixar e colar no app anterior) e **Apps** (favoritos fixados + busca em todos os apps do Windows, inclusive os da Microsoft Store).

**Architecture:** Clipboard é um serviço sempre ligado por evento (`AddClipboardFormatListener` → `WM_CLIPBOARDUPDATE`, zero polling) que grava em `%AppData%\Orbit2\clipboard.json` + `clipboard-img\*.png`; a aba exibe e age sobre esse histórico. Colar = pôr no clipboard, recolher a ilha (o foco volta ao app anterior) e mandar Ctrl+V via `SendInput`. Apps lista o `shell:AppsFolder` (mesma fonte do "Todos os apps" do Iniciar) numa thread STA separada, extrai ícones via `IShellItemImageFactory` com cache em PNG, e abre com `explorer.exe shell:AppsFolder\<id>`. Lógica testável em métodos `static` puros no harness.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm 8.4.1, Serilog, Win32 (`AddClipboardFormatListener`, `SendInput`, `SHCreateItemFromParsingName`/`IShellItemImageFactory`), COM `Shell.Application` via `dynamic`.

**Desvio consciente da spec (§5, Apps):** a spec dizia "busca nos atalhos do Menu Iniciar"; o `shell:AppsFolder` cobre os mesmos atalhos **e** os apps da Store, que a varredura de `.lnk` perderia.

**Spec:** `docs/superpowers/specs/2026-10-15-orbit2-design.md` (§5, §8, §9).

**Convenções:** português em código/comentários/commits; `ImplicitUsings` desligado; `Nullable` ligado; `[ObservableProperty]` estilo campo; `// ponytail:` para simplificações; testes estáticos puros em `Orbit.Tests/Program.cs` (cada `XTests()` novo é chamado em `Main()` depois do último registrado). Commits terminam com `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

**Regras do repositório (CLAUDE.md):**
- Glifos Segoe Fluent: `\uXXXX` em C#, `&#xXXXX;` em XAML — **nunca** o caractere literal. Confira com Python que não sobrou caractere entre U+E000 e U+F8FF nos `.cs`/`.xaml`.
- Botões com conteúdo próprio usam `PlainButton`/`RowButton` (o `Button` padrão herda texto preto).
- Leitura pesada (COM do shell, I/O de imagem) nunca na UI thread.
- **Nunca** `taskkill /IM Orbit.exe`; não suba o app nos passos de implementação.
- Smart App Control: `FileLoadException 0x800711C7` = rebuild e repetir.

**Namespace:** a pasta da aba de clipboard é `Orbit/Tabs/Clips/` (namespace `Orbit.Tabs.Clips`). **Não** use `Orbit.Tabs.Clipboard`: um namespace chamado `Clipboard` esconderia a classe `System.Windows.Clipboard` em todo o código sob `Orbit.Tabs`. Mesmo assim, no código, sempre escreva `System.Windows.Clipboard` por extenso.

**Rodar testes:** `cd /c/repos/orbit2.0 && dotnet run --project Orbit.Tests` → `TODOS OK`. Antes de cada commit: `dotnet build -c Debug`, `dotnet build -c Release` e `dotnet build --no-incremental Orbit/Orbit.csproj` com 0 erros/0 avisos.

---

## Mapa de arquivos

| Arquivo | Responsabilidade | Task |
|---|---|---|
| `Orbit/Island/IslandViewModel.cs` | `TabOrder` com clipboard/apps; registro das abas | 1, 3, 5 |
| `Orbit/Island/IslandStyles.xaml` | barra de abas compacta; conversores `FileToImage` | 1, 3 |
| `Orbit/Tabs/Clips/ClipItem.cs` | item do histórico | 2 |
| `Orbit/Tabs/Clips/ClipHistory.cs` | regras puras (classificar, privacidade, inserir/limite, filtrar, textos) | 2 |
| `Orbit/Tabs/Clips/ClipboardStore.cs` | `clipboard.json` (nunca lança) | 2 |
| `Orbit/Services/ClipboardWatcher.cs` | `WM_CLIPBOARDUPDATE` → evento | 3 |
| `Orbit/Services/ClipboardReader.cs` | ler/escrever o clipboard com retry | 3 |
| `Orbit/Services/Input.cs` | Ctrl+V via `SendInput` | 3 |
| `Orbit/Island/FileToImage.cs` | caminho de PNG → `BitmapImage` reduzido | 3 |
| `Orbit/Tabs/Clips/ClipsViewModel.cs` + `ClipsView.xaml(.cs)` | aba Clipboard | 3 |
| `Orbit/Island/IslandWindow.xaml(.cs)` | watcher, colar no app anterior, DataTemplates | 3, 5 |
| `Orbit/Tabs/Apps/AppSearch.cs` | regras puras (normalizar, ranquear, favoritos, ruído) | 4 |
| `Orbit/Models/OrbitSettings.cs` | `FavoriteApps` | 4 |
| `Orbit/Services/Sta.cs` | roda trabalho numa thread STA | 5 |
| `Orbit/Tabs/Apps/AppCatalog.cs` | lista `shell:AppsFolder` | 5 |
| `Orbit/Tabs/Apps/AppIcons.cs` | ícone → PNG com cache | 5 |
| `Orbit/Tabs/Apps/AppsViewModel.cs` + `AppsView.xaml(.cs)` | aba Apps | 5 |
| `CLAUDE.md` | guia atualizado | 6 |

---

### Task 1: Barra de abas compacta + ordem com Clipboard e Apps

Com 10 abas, a barra estoura os 720 px do painel. Fonte e espaçamento menores resolvem.

**Files:**
- Modify: `Orbit/Island/IslandViewModel.cs`, `Orbit/Island/IslandStyles.xaml`, `Orbit.Tests/Program.cs`

- [ ] **Step 1: Teste**

Registre `TabOrderTests();` em `Main()` e adicione:

```csharp
    private static void TabOrderTests()
    {
        var order = string.Join(",", IslandViewModel.TabOrder);
        Check("abas: clipboard e apps entre stats e mercado", order.Contains("stats,clipboard,apps,mercado"));
        Check("abas: 10 ids na ordem", IslandViewModel.TabOrder.Length == 10);
    }
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `cd /c/repos/orbit2.0 && dotnet run --project Orbit.Tests`
Expected: `FAIL abas: clipboard e apps entre stats e mercado` e `FAIL abas: 10 ids na ordem`.

- [ ] **Step 3: `TabOrder`**

Em `Orbit/Island/IslandViewModel.cs`, troque o array por:

```csharp
    public static readonly string[] TabOrder =
        { "controles", "hoje", "stats", "clipboard", "apps", "mercado", "noticias", "claude", "energia", "ajustes" };
```

- [ ] **Step 4: Barra compacta**

Em `Orbit/Island/IslandStyles.xaml`, no estilo `TabBar` (`ItemContainerStyle` → `ListBoxItem`):
- `<Setter Property="FontSize" Value="12"/>` → `<Setter Property="FontSize" Value="11.5"/>`
- no `Border x:Name="bg"` do template: `Padding="12,5" Margin="2,0"` → `Padding="9,5" Margin="1,0"`

- [ ] **Step 5: Build e testes; commit**

Run: `cd /c/repos/orbit2.0 && dotnet build -c Debug && dotnet build -c Release && dotnet run --project Orbit.Tests`
Expected: 0 erros/0 avisos; `TODOS OK`.

```bash
cd /c/repos/orbit2.0 && git add -A && git commit -m "feat: barra de abas compacta e ordem com Clipboard e Apps" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Clipboard — modelo, regras puras e armazenamento

**Files:**
- Create: `Orbit/Tabs/Clips/ClipItem.cs`, `Orbit/Tabs/Clips/ClipHistory.cs`, `Orbit/Tabs/Clips/ClipboardStore.cs`
- Modify: `Orbit.Tests/Program.cs`

- [ ] **Step 1: Testes**

Adicione `using Orbit.Tabs.Clips;` em `Program.cs`, registre `ClipHistoryTests();` e adicione:

```csharp
    private static ClipItem Txt(string t, DateTime at, bool pinned = false) =>
        new(ClipHistory.ClassifyText(t), t, null, "t:" + t, at, pinned, 0, 0);

    private static void ClipHistoryTests()
    {
        Check("clip: link https", ClipHistory.ClassifyText("https://example.com/a?b=1") == ClipKind.Link);
        Check("clip: link com espaços em volta", ClipHistory.ClassifyText("  http://x.io \n") == ClipKind.Link);
        Check("clip: frase com url é texto", ClipHistory.ClassifyText("veja https://x.io agora") == ClipKind.Text);
        Check("clip: ftp é texto", ClipHistory.ClassifyText("ftp://x.io") == ClipKind.Text);

        Check("clip: privado por formato", ClipHistory.IsPrivate(new[] { "Text", "ExcludeClipboardContentFromMonitorProcessing" }, null));
        Check("clip: privado por CanIncludeInClipboardHistory=0", ClipHistory.IsPrivate(new[] { "Text" }, 0));
        Check("clip: normal não é privado", !ClipHistory.IsPrivate(new[] { "Text" }, 1) && !ClipHistory.IsPrivate(new[] { "Text" }, null));

        var t0 = new DateTime(2026, 10, 15, 9, 0, 0);
        var list = new List<ClipItem>();
        ClipHistory.Add(list, Txt("a", t0));
        ClipHistory.Add(list, Txt("b", t0.AddMinutes(1)));
        var removed = ClipHistory.Add(list, Txt("a", t0.AddMinutes(2)));
        Check("clip: repetido sobe pro topo sem duplicar", list.Count == 2 && list[0].Text == "a" && removed.Count == 0);
        Check("clip: repetido atualiza a hora", list[0].CopiedAt == t0.AddMinutes(2));

        var big = new List<ClipItem> { Txt("fixo", t0, pinned: true) };
        for (int i = 0; i < ClipHistory.MaxUnpinned; i++) ClipHistory.Add(big, Txt("x" + i, t0.AddSeconds(i)));
        var gone = ClipHistory.Add(big, Txt("novo", t0.AddHours(1)));
        Check("clip: limite descarta o mais antigo não fixado", gone.Count == 1 && gone[0].Text == "x0");
        Check("clip: fixado nunca sai pelo limite", big.Exists(i => i.Text == "fixo") && big.Count == ClipHistory.MaxUnpinned + 1);

        var items = new List<ClipItem>
        {
            Txt("bom dia", t0),
            Txt("https://github.com", t0.AddMinutes(5)),
            new(ClipKind.Image, null, "abc.png", "i:abc", t0.AddMinutes(9), false, 1920, 1080),
            Txt("fixado antigo", t0.AddMinutes(-60), pinned: true),
        };
        var all = ClipHistory.View(items, "", ClipFilter.Tudo);
        Check("clip: fixados primeiro, depois mais recentes", all[0].Text == "fixado antigo" && all[1].Kind == ClipKind.Image);
        Check("clip: filtro links", ClipHistory.View(items, "", ClipFilter.Links).Count == 1);
        Check("clip: filtro imagens", ClipHistory.View(items, null, ClipFilter.Imagens).Count == 1);
        Check("clip: filtro texto", ClipHistory.View(items, "", ClipFilter.Texto).Count == 2);
        Check("clip: busca sem caixa", ClipHistory.View(items, "BOM", ClipFilter.Tudo).Count == 1);

        var now = new DateTime(2026, 10, 15, 12, 0, 0);
        Check("clip: agora", ClipHistory.RelativeTime(now, now.AddSeconds(-20)) == "agora");
        Check("clip: minutos", ClipHistory.RelativeTime(now, now.AddMinutes(-4)) == "4m");
        Check("clip: horas", ClipHistory.RelativeTime(now, now.AddHours(-2)) == "2h");
        Check("clip: ontem", ClipHistory.RelativeTime(now, new DateTime(2026, 10, 14, 23, 0, 0)) == "ontem");
        Check("clip: dias", ClipHistory.RelativeTime(now, now.AddDays(-3)) == "3d");

        Check("clip: descrição de texto", ClipHistory.Describe("oi tudo\nbem") == "11 caracteres · 3 palavras · 2 linhas");
        Check("clip: descrição singular", ClipHistory.Describe("oi") == "2 caracteres · 1 palavra · 1 linha");
        Check("clip: linha única p/ lista", ClipHistory.OneLine("  a\n  b  ") == "a b");

        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "clipboard.json");
        Check("clip store: inexistente = vazio", ClipboardStore.Load(path).Count == 0);
        ClipboardStore.Save(items, path);
        var back = ClipboardStore.Load(path);
        Check("clip store: round-trip", back.Count == 4 && back[2].Kind == ClipKind.Image && back[2].Width == 1920
            && back[3].Pinned && back[0].Text == "bom dia");
        File.WriteAllText(path, "{ quebrado");
        Check("clip store: corrompido = vazio", ClipboardStore.Load(path).Count == 0);
        Directory.Delete(dir, recursive: true);
    }
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `cd /c/repos/orbit2.0 && dotnet run --project Orbit.Tests`
Expected: erro de compilação (`Orbit.Tabs.Clips` não existe).

- [ ] **Step 3: Criar `Orbit/Tabs/Clips/ClipItem.cs`**

```csharp
using System;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Orbit.Tabs.Clips;

public enum ClipKind { Text, Link, Image }

// Um item do histórico. Imagem fica num PNG em clipboard-img\ (ImageFile = nome do arquivo).
public sealed partial class ClipItem : ObservableObject
{
    public ClipItem(ClipKind kind, string? text, string? imageFile, string key, DateTime copiedAt, bool pinned,
        int width, int height)
    {
        Kind = kind;
        Text = text;
        ImageFile = imageFile;
        Key = key;
        Width = width;
        Height = height;
        _copiedAt = copiedAt;
        _pinned = pinned;
    }

    public ClipKind Kind { get; }
    public string? Text { get; }
    public string? ImageFile { get; }
    public string Key { get; }   // dedupe: "t:<texto>" ou "i:<hash do PNG>"
    public int Width { get; }
    public int Height { get; }

    [ObservableProperty] private DateTime _copiedAt;
    [ObservableProperty] private bool _pinned;
    [ObservableProperty] private string _when = ""; // "4m", "ontem"… (o VM preenche)

    public string? ImagePath => ImageFile is null ? null : Path.Combine(ClipboardStore.ImagesDir, ImageFile);

    public string Preview => Kind == ClipKind.Image ? $"Imagem · {Width}×{Height}" : ClipHistory.OneLine(Text);
}
```

- [ ] **Step 4: Criar `Orbit/Tabs/Clips/ClipHistory.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Orbit.Tabs.Clips;

public enum ClipFilter { Tudo, Texto, Imagens, Links }

// Regras puras do histórico (sem I/O nem clipboard) — testadas no harness.
public static class ClipHistory
{
    public const int MaxUnpinned = 200;
    public const int MaxTextChars = 100_000; // texto maior (log/dump colado sem querer) não entra

    private static readonly Regex Spaces = new(@"\s+");

    public static ClipKind ClassifyText(string text)
    {
        var t = text.Trim();
        bool single = t.Length > 0 && !t.Any(char.IsWhiteSpace);
        return single && Uri.TryCreate(t, UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? ClipKind.Link
            : ClipKind.Text;
    }

    // Gerenciadores de senha (e o próprio Windows) marcam conteúdo que não deve ir pra histórico.
    public static bool IsPrivate(IEnumerable<string> formats, int? canIncludeInHistory) =>
        formats.Contains("ExcludeClipboardContentFromMonitorProcessing") || canIncludeInHistory == 0;

    // Insere no topo. Conteúdo repetido (mesma Key) sobe pro topo mantendo o "fixado".
    // Devolve os itens descartados pelo limite (pra apagar os PNGs).
    public static List<ClipItem> Add(List<ClipItem> items, ClipItem item)
    {
        var existing = items.FirstOrDefault(i => i.Key == item.Key);
        if (existing is not null)
        {
            items.Remove(existing);
            existing.CopiedAt = item.CopiedAt;
            items.Insert(0, existing);
            return new List<ClipItem>();
        }
        items.Insert(0, item);
        var removed = items.Where(i => !i.Pinned).Skip(MaxUnpinned).ToList();
        foreach (var old in removed) items.Remove(old);
        return removed;
    }

    // Fixados primeiro, depois do mais recente pro mais antigo.
    public static List<ClipItem> View(IEnumerable<ClipItem> items, string? query, ClipFilter filter)
    {
        var q = query?.Trim() ?? "";
        return items
            .Where(i => filter switch
            {
                ClipFilter.Texto => i.Kind == ClipKind.Text,
                ClipFilter.Imagens => i.Kind == ClipKind.Image,
                ClipFilter.Links => i.Kind == ClipKind.Link,
                _ => true,
            })
            .Where(i => q.Length == 0 || (i.Text?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false))
            .OrderByDescending(i => i.Pinned)
            .ThenByDescending(i => i.CopiedAt)
            .ToList();
    }

    public static string RelativeTime(DateTime now, DateTime at)
    {
        var d = now - at;
        if (d.TotalMinutes < 1) return "agora";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}m";
        if (at.Date == now.Date) return $"{(int)d.TotalHours}h";
        if (at.Date == now.Date.AddDays(-1)) return "ontem";
        return $"{(int)(now.Date - at.Date).TotalDays}d";
    }

    public static string Describe(string text)
    {
        int chars = text.Length;
        int words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        int lines = text.Split('\n').Length;
        return $"{chars} {Plural(chars, "caractere", "caracteres")} · {words} {Plural(words, "palavra", "palavras")} · " +
               $"{lines} {Plural(lines, "linha", "linhas")}";
    }

    // Texto numa linha só (lista): espaços colapsados, no máximo 140 caracteres.
    public static string OneLine(string? text)
    {
        var s = Spaces.Replace(text ?? "", " ").Trim();
        return s.Length > 140 ? s[..140] + "…" : s;
    }

    private static string Plural(int n, string one, string many) => n == 1 ? one : many;
}
```

- [ ] **Step 5: Criar `Orbit/Tabs/Clips/ClipboardStore.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Orbit.Services;

namespace Orbit.Tabs.Clips;

// clipboard.json + pasta clipboard-img\. Mesma filosofia do SettingsStore: nunca lança.
public static class ClipboardStore
{
    public static readonly string FilePath = Path.Combine(SettingsStore.Dir, "clipboard.json");
    public static readonly string ImagesDir = Path.Combine(SettingsStore.Dir, "clipboard-img");

    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = false };

    private sealed record ClipDto(ClipKind Kind, string? Text, string? ImageFile, string? Key, DateTime CopiedAt,
        bool Pinned, int Width, int Height);

    public static List<ClipItem> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            var dtos = JsonSerializer.Deserialize<List<ClipDto>>(File.ReadAllText(path));
            return dtos?.Where(d => d.Key is not null)
                .Select(d => new ClipItem(d.Kind, d.Text, d.ImageFile, d.Key!, d.CopiedAt, d.Pinned, d.Width, d.Height))
                .ToList() ?? new();
        }
        catch
        {
            return new(); // corrompido/I/O → histórico vazio
        }
    }

    public static void Save(IEnumerable<ClipItem> items, string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var dtos = items.Select(i => new ClipDto(i.Kind, i.Text, i.ImageFile, i.Key, i.CopiedAt, i.Pinned, i.Width, i.Height));
            File.WriteAllText(path, JsonSerializer.Serialize(dtos, WriteOptions));
        }
        catch
        {
            // disco cheio / sem permissão: não vale derrubar a ilha.
        }
    }
}
```

- [ ] **Step 6: Build e testes; commit**

Run: `cd /c/repos/orbit2.0 && dotnet build -c Debug && dotnet build -c Release && dotnet run --project Orbit.Tests`
Expected: 0 erros/0 avisos; `PASS clip...` e `TODOS OK`.

```bash
cd /c/repos/orbit2.0 && git add -A && git commit -m "feat: histórico do clipboard (modelo, regras e armazenamento)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Clipboard — captura, colar e aba

**Files:**
- Create: `Orbit/Services/ClipboardWatcher.cs`, `Orbit/Services/ClipboardReader.cs`, `Orbit/Services/Input.cs`, `Orbit/Island/FileToImage.cs`, `Orbit/Tabs/Clips/ClipsViewModel.cs`, `Orbit/Tabs/Clips/ClipsView.xaml`, `Orbit/Tabs/Clips/ClipsView.xaml.cs`
- Modify: `Orbit/Island/IslandStyles.xaml`, `Orbit/Island/IslandViewModel.cs`, `Orbit/Island/IslandWindow.xaml`, `Orbit/Island/IslandWindow.xaml.cs`, `Orbit.Tests/Program.cs`

- [ ] **Step 1: Testes**

Registre `ClipsTests();` e adicione:

```csharp
    private static void ClipsTests()
    {
        var seq = Input.CtrlVSequence();
        Check("colar: sequência Ctrl↓ V↓ V↑ Ctrl↑", seq.Length == 4
            && seq[0] == (0x11, false) && seq[1] == (0x56, false) && seq[2] == (0x56, true) && seq[3] == (0x11, true));

        var dir = Path.Combine(Path.GetTempPath(), "orbit2-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        bool enabled = true;
        var vm = new ClipsViewModel(Path.Combine(dir, "clipboard.json"), Path.Combine(dir, "img"), () => enabled);
        vm.IngestAsync(new ClipSnapshot("olá mundo", null, false)).GetAwaiter().GetResult();
        vm.IngestAsync(new ClipSnapshot("https://example.com", null, false)).GetAwaiter().GetResult();
        Check("clips: captura texto e link", vm.Visible.Count == 2 && vm.Visible[0].Kind == ClipKind.Link);
        vm.IngestAsync(new ClipSnapshot("olá mundo", null, false)).GetAwaiter().GetResult();
        Check("clips: repetido não duplica e sobe", vm.Visible.Count == 2 && vm.Visible[0].Text == "olá mundo");
        vm.IngestAsync(new ClipSnapshot("senha123", null, true)).GetAwaiter().GetResult();
        Check("clips: conteúdo privado é ignorado", vm.Visible.Count == 2);
        vm.IngestAsync(new ClipSnapshot("   ", null, false)).GetAwaiter().GetResult();
        Check("clips: só espaço é ignorado", vm.Visible.Count == 2);
        enabled = false;
        vm.IngestAsync(new ClipSnapshot("com aba escondida", null, false)).GetAwaiter().GetResult();
        Check("clips: aba escondida não captura", vm.Visible.Count == 2);

        vm.Filter = ClipFilter.Links;
        Check("clips: filtro aplica", vm.Visible.Count == 1);
        vm.Filter = ClipFilter.Tudo;
        vm.Selected = vm.Visible[1];
        vm.TogglePinCommand.Execute(null);
        Check("clips: fixado vai pro topo", vm.Visible[0].Pinned && vm.Visible[0].Text == "https://example.com");
        vm.Selected = vm.Visible[1];
        vm.RemoveCommand.Execute(null);
        Check("clips: remover", vm.Visible.Count == 1);
        vm.ClearCommand.Execute(null);
        Check("clips: limpar mantém fixados", vm.Visible.Count == 1 && vm.Visible[0].Pinned);

        var reloaded = new ClipsViewModel(Path.Combine(dir, "clipboard.json"), Path.Combine(dir, "img"), () => true);
        Check("clips: persiste entre execuções", reloaded.Visible.Count == 1 && reloaded.Visible[0].Pinned);
        Directory.Delete(dir, recursive: true);
    }
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `cd /c/repos/orbit2.0 && dotnet run --project Orbit.Tests`
Expected: erro de compilação (`Input`, `ClipsViewModel`, `ClipSnapshot` não existem).

- [ ] **Step 3: Criar `Orbit/Services/ClipboardWatcher.cs`**

```csharp
using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Serilog;

namespace Orbit.Services;

// Avisa quando o conteúdo do clipboard muda (WM_CLIPBOARDUPDATE). Zero polling.
// Criar em OnSourceInitialized (precisa do HWND da ilha; funciona com a janela oculta).
public sealed class ClipboardWatcher : IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;

    private readonly IntPtr _hwnd;
    private readonly HwndSource _source;

    public event Action? Changed;

    public ClipboardWatcher(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("ClipboardWatcher precisa do HWND do WPF.");
        _source.AddHook(WndProc);
        if (!AddClipboardFormatListener(hwnd))
            Log.Warning("Clipboard: listener não registrado (Win32 {Erro})", Marshal.GetLastWin32Error());
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_CLIPBOARDUPDATE) Changed?.Invoke();
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        _source.RemoveHook(WndProc);
        RemoveClipboardFormatListener(_hwnd);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
```

- [ ] **Step 4: Criar `Orbit/Services/ClipboardReader.cs`**

```csharp
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;
using Orbit.Tabs.Clips;
using Serilog;

namespace Orbit.Services;

// O que interessa de um conteúdo do clipboard. Image vem congelada (pode ir pra outra thread).
public sealed record ClipSnapshot(string? Text, BitmapSource? Image, bool IsPrivate);

// Acesso ao clipboard com retry: outro app pode estar segurando (CLIPBRD_E_CANT_OPEN).
// Sempre na UI thread (o clipboard do WPF exige STA).
public static class ClipboardReader
{
    public static ClipSnapshot? Read()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var data = System.Windows.Clipboard.GetDataObject();
                if (data is null) return null;
                var formats = data.GetFormats();
                if (ClipHistory.IsPrivate(formats, ReadDword(data, "CanIncludeInClipboardHistory")))
                    return new ClipSnapshot(null, null, IsPrivate: true);
                if (data.GetDataPresent(DataFormats.UnicodeText))
                    return new ClipSnapshot(data.GetData(DataFormats.UnicodeText) as string, null, false);
                if (System.Windows.Clipboard.ContainsImage())
                {
                    var img = System.Windows.Clipboard.GetImage();
                    img?.Freeze();
                    return img is null ? null : new ClipSnapshot(null, img, false);
                }
                return null; // arquivos, formatos próprios de apps etc.: fora do histórico
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Clipboard: falha ao ler");
                return null;
            }
        }
        return null;
    }

    public static bool SetText(string text) => Retry(() => System.Windows.Clipboard.SetText(text));
    public static bool SetImage(BitmapSource image) => Retry(() => System.Windows.Clipboard.SetImage(image));

    private static int? ReadDword(IDataObject data, string format)
    {
        try
        {
            if (data.GetDataPresent(format) && data.GetData(format) is MemoryStream ms && ms.Length >= 4)
            {
                var b = new byte[4];
                ms.ReadExactly(b, 0, 4);
                return BitConverter.ToInt32(b, 0);
            }
        }
        catch
        {
            // formato estranho: trata como ausente
        }
        return null;
    }

    private static bool Retry(Action action)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                action();
                return true;
            }
            catch (COMException)
            {
                Thread.Sleep(40);
            }
        }
        Log.Warning("Clipboard: não consegui escrever (ocupado por outro app)");
        return false;
    }
}
```

- [ ] **Step 5: Criar `Orbit/Services/Input.cs`**

```csharp
using System;
using System.Linq;
using System.Runtime.InteropServices;

namespace Orbit.Services;

// Ctrl+V sintético (SendInput) pra colar no app que estava em foco.
public static class Input
{
    private const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x0002;

    // (tecla virtual, soltando?) — puro, testável.
    public static (ushort Vk, bool Up)[] CtrlVSequence() =>
        new (ushort, bool)[] { (0x11, false), (0x56, false), (0x56, true), (0x11, true) };

    public static void SendCtrlV()
    {
        var inputs = CtrlVSequence().Select(k => new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new InputUnion { ki = new KEYBDINPUT { wVk = k.Vk, dwFlags = k.Up ? KEYEVENTF_KEYUP : 0 } },
        }).ToArray();
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    // A união precisa do MOUSEINPUT (o maior membro) pro tamanho bater com o nativo em x64.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
}
```

- [ ] **Step 6: Criar `Orbit/Island/FileToImage.cs`**

```csharp
using System;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Orbit.Island;

// Caminho de PNG → BitmapImage já reduzido (DecodeWidth) e sem segurar o arquivo.
// Usado nas miniaturas do Clipboard e nos ícones de Apps: só existe enquanto a view existe.
public sealed class FileToImage : IValueConverter
{
    public int DecodeWidth { get; set; } = 96;

    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = DecodeWidth;
            bmp.UriSource = new Uri(path);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null; // PNG corrompido: a view mostra o glifo
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
```

Em `Orbit/Island/IslandStyles.xaml`, junto dos outros conversores:

```xml
    <island:FileToImage x:Key="Thumb" DecodeWidth="96"/>
    <island:FileToImage x:Key="PreviewImage" DecodeWidth="640"/>
    <island:FileToImage x:Key="AppIcon" DecodeWidth="48"/>
```

- [ ] **Step 7: Criar `Orbit/Tabs/Clips/ClipsViewModel.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Services;
using Serilog;

namespace Orbit.Tabs.Clips;

// Aba Clipboard. A captura roda sempre (evento WM_CLIPBOARDUPDATE → CaptureAsync), mesmo com a
// ilha recolhida — a menos que o usuário esconda a aba em Ajustes (aí não grava nada).
public sealed partial class ClipsViewModel : TabViewModel
{
    private readonly string _storePath;
    private readonly string _imagesDir;
    private readonly Func<bool> _enabled;
    private readonly List<ClipItem> _items;

    public override string Id => "clipboard";
    public override string Title => "Clipboard";

    public ObservableCollection<ClipItem> Visible { get; } = new();
    public IReadOnlyList<ClipFilter> Filters { get; } = Enum.GetValues<ClipFilter>();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private ClipFilter _filter = ClipFilter.Tudo;
    [ObservableProperty] private ClipItem? _selected;
    [ObservableProperty] private string _searchHint = "Buscar…";
    [ObservableProperty] private string _detailType = "";
    [ObservableProperty] private string _detailSize = "";
    [ObservableProperty] private string _detailCopied = "";

    public bool HasQuery => Query.Length > 0;
    public bool IsEmpty => Visible.Count == 0;
    public string PinLabel => Selected?.Pinned == true ? "Desafixar" : "Fixar";

    // Item já está no clipboard: a ilha recolhe e manda Ctrl+V pro app anterior.
    public event Action? PasteRequested;

    public ClipsViewModel(string storePath, string imagesDir, Func<bool> enabled)
    {
        _storePath = storePath;
        _imagesDir = imagesDir;
        _enabled = enabled;
        _items = ClipboardStore.Load(storePath);
        Refresh();
    }

    public override void SetActive(bool active)
    {
        if (active) Refresh(); // atualiza os "4m", "ontem"
    }

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        Refresh();
    }

    partial void OnFilterChanged(ClipFilter value) => Refresh();

    partial void OnSelectedChanged(ClipItem? value)
    {
        OnPropertyChanged(nameof(PinLabel));
        DetailType = value?.Kind switch { ClipKind.Link => "Link", ClipKind.Image => "Imagem", ClipKind.Text => "Texto", _ => "" };
        DetailSize = value is null ? ""
            : value.Kind == ClipKind.Image ? $"{value.Width}×{value.Height}"
            : ClipHistory.Describe(value.Text ?? "");
        DetailCopied = value is null ? "" : value.CopiedAt.ToString("dd/MM 'às' HH:mm");
    }

    // Chamado pela ilha a cada WM_CLIPBOARDUPDATE (UI thread).
    public async Task CaptureAsync()
    {
        if (!_enabled()) return;
        var snap = ClipboardReader.Read();
        if (snap is not null) await IngestAsync(snap);
    }

    // Separado do Read pra ser testável sem clipboard de verdade.
    public async Task IngestAsync(ClipSnapshot snap)
    {
        if (!_enabled() || snap.IsPrivate) return;
        ClipItem item;
        if (snap.Text is { } text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > ClipHistory.MaxTextChars) return;
            item = new ClipItem(ClipHistory.ClassifyText(text), text, null, "t:" + text, DateTime.Now, false, 0, 0);
        }
        else if (snap.Image is { } img)
        {
            try
            {
                // PNG + hash fora da UI thread: um print 4K leva ~100 ms pra codificar.
                var (png, hash) = await Task.Run(() => EncodePng(img));
                var file = hash + ".png";
                var path = Path.Combine(_imagesDir, file);
                if (!File.Exists(path))
                {
                    Directory.CreateDirectory(_imagesDir);
                    await File.WriteAllBytesAsync(path, png);
                }
                item = new ClipItem(ClipKind.Image, null, file, "i:" + hash, DateTime.Now, false, img.PixelWidth, img.PixelHeight);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Clipboard: falha ao salvar imagem");
                return;
            }
        }
        else
        {
            return;
        }

        foreach (var gone in ClipHistory.Add(_items, item)) DeleteImage(gone);
        Persist();
        Refresh();
    }

    [RelayCommand]
    private void Paste(ClipItem? item)
    {
        item ??= Selected;
        if (item is null) return;
        bool ok = item.Kind == ClipKind.Image
            ? item.ImagePath is { } p && File.Exists(p) && ClipboardReader.SetImage(LoadFull(p))
            : ClipboardReader.SetText(item.Text ?? "");
        if (ok) PasteRequested?.Invoke();
    }

    [RelayCommand]
    private void TogglePin()
    {
        if (Selected is not { } item) return;
        item.Pinned = !item.Pinned;
        Persist();
        Refresh();
        OnPropertyChanged(nameof(PinLabel));
    }

    [RelayCommand]
    private void Remove()
    {
        if (Selected is not { } item) return;
        _items.Remove(item);
        DeleteImage(item);
        Persist();
        Refresh();
    }

    // Limpa o histórico, preservando os fixados.
    [RelayCommand]
    private void Clear()
    {
        foreach (var item in _items.Where(i => !i.Pinned).ToList())
        {
            _items.Remove(item);
            DeleteImage(item);
        }
        Persist();
        Refresh();
    }

    private void Refresh()
    {
        var keep = Selected;
        var now = DateTime.Now;
        Visible.Clear();
        foreach (var item in ClipHistory.View(_items, Query, Filter))
        {
            item.When = ClipHistory.RelativeTime(now, item.CopiedAt);
            Visible.Add(item);
        }
        Selected = keep is not null && Visible.Contains(keep) ? keep : Visible.FirstOrDefault();
        SearchHint = $"Buscar em {_items.Count} {(_items.Count == 1 ? "item" : "itens")}…";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Persist() => ClipboardStore.Save(_items, _storePath);

    private void DeleteImage(ClipItem item)
    {
        if (item.ImageFile is null) return;
        // Outro item pode apontar pro mesmo PNG (mesmo hash): só apaga se ninguém mais usa.
        if (_items.Any(i => i.ImageFile == item.ImageFile)) return;
        try { File.Delete(Path.Combine(_imagesDir, item.ImageFile)); }
        catch (Exception ex) { Log.Warning(ex, "Clipboard: não apaguei {Arquivo}", item.ImageFile); }
    }

    private static (byte[] Png, string Hash) EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        var bytes = ms.ToArray();
        return (bytes, Convert.ToHexString(SHA256.HashData(bytes))[..16]);
    }

    private static BitmapImage LoadFull(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }
}
```

> Nota: `DeleteImage` é chamado **depois** de tirar o item de `_items` (Remove/Clear/limite), então a checagem "ninguém mais usa" vê só os que ficaram. Os `ClipItem` de `ClipHistory.Add` também já saíram da lista quando chegam aqui.

- [ ] **Step 8: Criar `Orbit/Tabs/Clips/ClipsView.xaml`**

```xml
<UserControl x:Class="Orbit.Tabs.Clips.ClipsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <UserControl.InputBindings>
        <KeyBinding Key="Enter" Command="{Binding PasteCommand}"/>
        <KeyBinding Key="P" Modifiers="Control" Command="{Binding TogglePinCommand}"/>
    </UserControl.InputBindings>
    <UserControl.Resources>
        <!-- Linha da lista: seleção em acento suave, hover em cartão -->
        <Style x:Key="ClipRow" TargetType="ListBoxItem">
            <Setter Property="Foreground" Value="{StaticResource InkHi}"/>
            <Setter Property="Cursor" Value="Hand"/>
            <Setter Property="FocusVisualStyle" Value="{x:Null}"/>
            <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="ListBoxItem">
                        <Border x:Name="bg" Background="Transparent" CornerRadius="10" Padding="8,6" Margin="0,0,0,2">
                            <ContentPresenter/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bg" Property="Background" Value="{StaticResource CardBg}"/>
                            </Trigger>
                            <Trigger Property="IsSelected" Value="True">
                                <Setter TargetName="bg" Property="Background" Value="{DynamicResource AccentSoftBrush}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </UserControl.Resources>

    <Grid>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="330"/>
            <ColumnDefinition Width="14"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <!-- Esquerda: busca, filtros, lista -->
        <DockPanel>
            <Grid DockPanel.Dock="Top" Margin="0,0,0,8">
                <TextBox x:Name="search" Style="{StaticResource Field}" Text="{Binding Query, UpdateSourceTrigger=PropertyChanged}"/>
                <TextBlock Text="{Binding SearchHint}" Foreground="{StaticResource InkLo}" FontSize="13" Margin="12,7,0,0"
                           IsHitTestVisible="False" Visibility="{Binding HasQuery, Converter={StaticResource NotBoolToVis}}"/>
            </Grid>
            <ListBox DockPanel.Dock="Top" Style="{StaticResource TabBar}" HorizontalAlignment="Left" Margin="-2,0,0,8"
                     ItemsSource="{Binding Filters}" SelectedItem="{Binding Filter}">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <TextBlock Text="{Binding}"/>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
            <Button DockPanel.Dock="Bottom" Style="{StaticResource SmallButton}" HorizontalAlignment="Left" Margin="0,8,0,0"
                    Content="Limpar (mantém fixados)" Command="{Binding ClearCommand}"/>
            <Grid>
                <TextBlock Text="Nada copiado ainda" Foreground="{StaticResource InkLo}" FontSize="13"
                           HorizontalAlignment="Center" VerticalAlignment="Center"
                           Visibility="{Binding IsEmpty, Converter={StaticResource BoolToVis}}"/>
                <ListBox ItemsSource="{Binding Visible}" SelectedItem="{Binding Selected}" ItemContainerStyle="{StaticResource ClipRow}"
                         Background="Transparent" BorderThickness="0" ScrollViewer.HorizontalScrollBarVisibility="Disabled">
                    <ListBox.Template>
                        <ControlTemplate TargetType="ListBox">
                            <ScrollViewer VerticalScrollBarVisibility="Hidden" Focusable="False">
                                <ItemsPresenter/>
                            </ScrollViewer>
                        </ControlTemplate>
                    </ListBox.Template>
                    <ListBox.InputBindings>
                        <MouseBinding Gesture="LeftDoubleClick" Command="{Binding PasteCommand}"/>
                        <KeyBinding Key="Delete" Command="{Binding RemoveCommand}"/>
                    </ListBox.InputBindings>
                    <ListBox.ItemTemplate>
                        <DataTemplate>
                            <DockPanel>
                                <Grid DockPanel.Dock="Left" Width="28" Height="28" Margin="0,0,10,0">
                                    <Border CornerRadius="7" Background="{StaticResource CardHover}"/>
                                    <TextBlock x:Name="glyph" Text="&#xE8A5;" FontFamily="{StaticResource IconFont}" FontSize="13"
                                               Foreground="{StaticResource InkLo}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                    <Border x:Name="thumb" CornerRadius="7" Visibility="Collapsed">
                                        <Border.Background>
                                            <ImageBrush ImageSource="{Binding ImagePath, Converter={StaticResource Thumb}}" Stretch="UniformToFill"/>
                                        </Border.Background>
                                    </Border>
                                </Grid>
                                <StackPanel DockPanel.Dock="Right" Orientation="Horizontal" VerticalAlignment="Center" Margin="8,0,2,0">
                                    <TextBlock x:Name="pin" Text="&#xE718;" FontFamily="{StaticResource IconFont}" FontSize="11"
                                               Foreground="{DynamicResource AccentBrush}" Margin="0,0,6,0" Visibility="Collapsed"
                                               VerticalAlignment="Center"/>
                                    <TextBlock Text="{Binding When}" FontSize="11" Foreground="{StaticResource InkLo}" VerticalAlignment="Center"/>
                                </StackPanel>
                                <TextBlock Text="{Binding Preview}" FontSize="13" TextTrimming="CharacterEllipsis" VerticalAlignment="Center"/>
                            </DockPanel>
                            <DataTemplate.Triggers>
                                <DataTrigger Binding="{Binding Kind}" Value="Link">
                                    <Setter TargetName="glyph" Property="Text" Value="&#xE71B;"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding Kind}" Value="Image">
                                    <Setter TargetName="thumb" Property="Visibility" Value="Visible"/>
                                    <Setter TargetName="glyph" Property="Text" Value="&#xEB9F;"/>
                                </DataTrigger>
                                <DataTrigger Binding="{Binding Pinned}" Value="True">
                                    <Setter TargetName="pin" Property="Visibility" Value="Visible"/>
                                </DataTrigger>
                            </DataTemplate.Triggers>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                </ListBox>
            </Grid>
        </DockPanel>

        <!-- Direita: prévia + detalhes + ações -->
        <Border Grid.Column="2" Style="{StaticResource Card}">
            <DockPanel>
                <StackPanel DockPanel.Dock="Bottom">
                    <Grid Margin="0,10,0,10">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="70"/>
                            <ColumnDefinition Width="*"/>
                        </Grid.ColumnDefinitions>
                        <Grid.RowDefinitions>
                            <RowDefinition/>
                            <RowDefinition/>
                            <RowDefinition/>
                        </Grid.RowDefinitions>
                        <TextBlock Text="Tipo" FontSize="11" Foreground="{StaticResource InkLo}"/>
                        <TextBlock Grid.Column="1" Text="{Binding DetailType}" FontSize="11"/>
                        <TextBlock Grid.Row="1" Text="Tamanho" FontSize="11" Foreground="{StaticResource InkLo}"/>
                        <TextBlock Grid.Row="1" Grid.Column="1" Text="{Binding DetailSize}" FontSize="11"/>
                        <TextBlock Grid.Row="2" Text="Copiado" FontSize="11" Foreground="{StaticResource InkLo}"/>
                        <TextBlock Grid.Row="2" Grid.Column="1" Text="{Binding DetailCopied}" FontSize="11"/>
                    </Grid>
                    <StackPanel Orientation="Horizontal">
                        <Button Style="{StaticResource AccentButton}" Content="Colar" Command="{Binding PasteCommand}" Margin="0,0,6,0"/>
                        <Button Style="{StaticResource SmallButton}" Content="{Binding PinLabel}" Command="{Binding TogglePinCommand}" Margin="0,0,6,0"/>
                        <Button Style="{StaticResource SmallButton}" Content="Remover" Command="{Binding RemoveCommand}"/>
                    </StackPanel>
                    <TextBlock Text="Enter cola · Ctrl+P fixa · Del remove · duplo clique cola" FontSize="10.5"
                               Foreground="{StaticResource InkLo}" Margin="0,8,0,0"/>
                </StackPanel>
                <Grid DataContext="{Binding Selected}">
                    <ScrollViewer VerticalScrollBarVisibility="Hidden">
                        <TextBlock Text="{Binding Text}" FontSize="13" TextWrapping="Wrap"/>
                    </ScrollViewer>
                    <Image Source="{Binding ImagePath, Converter={StaticResource PreviewImage}}" Stretch="Uniform"/>
                </Grid>
            </DockPanel>
        </Border>
    </Grid>
</UserControl>
```

> A prévia mostra o texto **ou** a imagem: num item de texto `ImagePath` é nulo (Image vazia); num de imagem `Text` é nulo. Sem item selecionado, `DataContext` nulo deixa os dois vazios.

`Orbit/Tabs/Clips/ClipsView.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace Orbit.Tabs.Clips;

public partial class ClipsView : UserControl
{
    public ClipsView() => InitializeComponent();
}
```

- [ ] **Step 9: Registrar na ilha**

Em `Orbit/Island/IslandViewModel.cs`:
- `using Orbit.Tabs.Clips;`
- propriedade `public ClipsViewModel Clips { get; }`
- acima de `// novas abas/serviços entram acima desta linha`:

```csharp
        // Clipboard: captura sempre por evento; aba escondida em Ajustes = não grava nada.
        Clips = new ClipsViewModel(ClipboardStore.FilePath, ClipboardStore.ImagesDir,
            () => !settings.HiddenTabs.Contains("clipboard"));
        Register(Clips);
```

Em `Orbit/Island/IslandWindow.xaml`: `xmlns:clips="clr-namespace:Orbit.Tabs.Clips"` na tag `<Window>` e em `<Window.Resources>`:
```xml
        <DataTemplate DataType="{x:Type clips:ClipsViewModel}">
            <clips:ClipsView/>
        </DataTemplate>
```

- [ ] **Step 10: Watcher e "colar no app anterior" na janela**

Em `Orbit/Island/IslandWindow.xaml.cs`:
- campo `private ClipboardWatcher? _clipboard;`
- no construtor, depois das assinaturas do `_tray`: `_vm.Clips.PasteRequested += PasteIntoPrevious;`
- no fim de `OnSourceInitialized`:

```csharp
        _clipboard = new ClipboardWatcher(hwnd);
        _clipboard.Changed += () => _ = _vm.Clips.CaptureAsync();
```
- em `ReleaseResources`, dentro do `try`, depois de `_fullscreen?.Dispose();`: `_clipboard?.Dispose();`
- novo método:

```csharp
    // Cola no app que estava em foco antes da ilha: recolhe (OnModeChanged devolve o foco pro
    // _prevFg) e, se ele de fato voltou ao primeiro plano, manda Ctrl+V.
    private void PasteIntoPrevious()
    {
        var target = _prevFg;
        _vm.Collapse();
        if (target == IntPtr.Zero) return;
        var wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        wait.Tick += (_, _) =>
        {
            wait.Stop();
            if (GetForegroundWindow() == target) Input.SendCtrlV();
        };
        wait.Start();
    }
```
- junto dos outros `DllImport`: `[DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();`

- [ ] **Step 11: Build e testes; commit**

Run: `cd /c/repos/orbit2.0 && dotnet build -c Debug && dotnet build -c Release && dotnet build --no-incremental Orbit/Orbit.csproj && dotnet run --project Orbit.Tests`
Expected: 0 erros/0 avisos; `PASS colar/clips: ...` e `TODOS OK`.

```bash
cd /c/repos/orbit2.0 && git add -A && git commit -m "feat: aba Clipboard (captura por evento, busca, filtros, fixar e colar no app anterior)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Apps — regras puras e favoritos

**Files:**
- Create: `Orbit/Tabs/Apps/AppSearch.cs`
- Modify: `Orbit/Models/OrbitSettings.cs`, `Orbit.Tests/Program.cs`

- [ ] **Step 1: Testes**

Adicione `using Orbit.Tabs.Apps;` em `Program.cs`, registre `AppSearchTests();` e adicione:

```csharp
    private static void AppSearchTests()
    {
        Check("apps: normaliza acento e caixa", AppSearch.Normalize("Configurações") == "configuracoes");

        var apps = new List<AppEntry>
        {
            new("Visual Studio Code", "code"),
            new("Spotify", "spotify"),
            new("Configurações", "settings"),
            new("Microsoft Edge", "edge"),
            new("Steam", "steam"),
        };
        var r = AppSearch.Search(apps, "s", 10);
        // "s": spotify/steam (prefixo) > visual studio code (palavra) > configurações/edge (contém)
        Check("apps: prefixo > palavra > contém", r[0].Id is "spotify" or "steam"
            && r.FindIndex(a => a.Id == "code") == 2 && r.FindIndex(a => a.Id == "settings") > 2);
        Check("apps: sem acento acha com acento", AppSearch.Search(apps, "config", 10)[0].Id == "settings");
        Check("apps: palavra interna", AppSearch.Search(apps, "code", 10)[0].Id == "code");
        Check("apps: contém no meio", AppSearch.Search(apps, "otif", 10).Count == 1);
        Check("apps: busca vazia = nada", AppSearch.Search(apps, "  ", 10).Count == 0);
        Check("apps: limite", AppSearch.Search(apps, "e", 2).Count == 2);

        var favs = AppSearch.ToggleFavorite(new List<string>(), "code");
        Check("apps: fixar", favs.Count == 1 && favs[0] == "code");
        Check("apps: desafixar", AppSearch.ToggleFavorite(favs, "code").Count == 0);
        var cheio = Enumerable.Range(0, AppSearch.MaxFavorites).Select(i => "a" + i).ToList();
        Check("apps: não passa do máximo", AppSearch.ToggleFavorite(cheio, "novo").Count == AppSearch.MaxFavorites);

        Check("apps: desinstalador é ruído", AppSearch.IsNoise("Uninstall Foo") && AppSearch.IsNoise("Desinstalar Bar"));
        Check("apps: app normal não é ruído", !AppSearch.IsNoise("Spotify"));
        Check("apps: nome do ícone estável", AppSearch.IconFileName("x") == AppSearch.IconFileName("x")
            && AppSearch.IconFileName("x") != AppSearch.IconFileName("y") && AppSearch.IconFileName("x").EndsWith(".png"));

        Check("settings: favoritos default vazio", new Orbit.Models.OrbitSettings().FavoriteApps.Count == 0);
    }
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `cd /c/repos/orbit2.0 && dotnet run --project Orbit.Tests`
Expected: erro de compilação (`Orbit.Tabs.Apps`, `FavoriteApps` não existem).

- [ ] **Step 3: Settings**

Em `Orbit/Models/OrbitSettings.cs`:

```csharp
    // Apps fixados (ids do shell:AppsFolder), na ordem em que aparecem.
    public List<string> FavoriteApps { get; set; } = new();
```

- [ ] **Step 4: Criar `Orbit/Tabs/Apps/AppSearch.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Orbit.Tabs.Apps;

// Id = nome de parsing do shell:AppsFolder (AUMID de app da Store, ou caminho/GUID de desktop).
public sealed record AppEntry(string Name, string Id);

// Regras puras da aba Apps — testadas no harness.
public static class AppSearch
{
    public const int MaxFavorites = 12;

    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // 3 = nome começa com a busca; 2 = alguma palavra começa; 1 = contém; 0 = não casa.
    public static List<AppEntry> Search(IEnumerable<AppEntry> apps, string query, int max)
    {
        var q = Normalize(query.Trim());
        if (q.Length == 0) return new();
        return apps
            .Select(a => (App: a, Score: Score(Normalize(a.Name), q)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.App.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(max)
            .Select(x => x.App)
            .ToList();
    }

    private static int Score(string name, string q) =>
        name.StartsWith(q, StringComparison.Ordinal) ? 3
        : name.Split(' ', '-', '.', '(').Any(w => w.StartsWith(q, StringComparison.Ordinal)) ? 2
        : name.Contains(q, StringComparison.Ordinal) ? 1
        : 0;

    public static List<string> ToggleFavorite(IReadOnlyList<string> favorites, string id) =>
        favorites.Contains(id) ? favorites.Where(f => f != id).ToList()
        : favorites.Count >= MaxFavorites ? favorites.ToList()
        : favorites.Append(id).ToList();

    // Desinstaladores e afins poluem a busca.
    public static bool IsNoise(string name) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || name.Contains("desinstalar", StringComparison.OrdinalIgnoreCase);

    public static string IconFileName(string id) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)))[..16] + ".png";
}
```

- [ ] **Step 5: Build e testes; commit**

Run: `cd /c/repos/orbit2.0 && dotnet build -c Debug && dotnet build -c Release && dotnet run --project Orbit.Tests`
Expected: 0 erros/0 avisos; `PASS apps/settings: ...` e `TODOS OK`.

```bash
cd /c/repos/orbit2.0 && git add -A && git commit -m "feat: busca e favoritos da aba Apps (regras puras)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Apps — catálogo, ícones e aba

**Files:**
- Create: `Orbit/Services/Sta.cs`, `Orbit/Tabs/Apps/AppCatalog.cs`, `Orbit/Tabs/Apps/AppIcons.cs`, `Orbit/Tabs/Apps/AppsViewModel.cs`, `Orbit/Tabs/Apps/AppsView.xaml`, `Orbit/Tabs/Apps/AppsView.xaml.cs`
- Modify: `Orbit/Island/IslandViewModel.cs`, `Orbit/Island/IslandWindow.xaml`, `Orbit.Tests/Program.cs`

- [ ] **Step 1: Testes**

Registre `AppIconTests();` e adicione:

```csharp
    private static void AppIconTests()
    {
        // 2x2 BGRA, linhas de baixo pra cima → inverter
        var px = new byte[] { 1, 1, 1, 255, 2, 2, 2, 255, /* linha 2 */ 3, 3, 3, 255, 4, 4, 4, 255 };
        AppIcons.FlipRows(px, stride: 8, height: 2);
        Check("ícone: inverte linhas", px[0] == 3 && px[8] == 1);

        var semAlfa = new byte[] { 9, 9, 9, 0, 9, 9, 9, 0 };
        AppIcons.FixAlpha(semAlfa);
        Check("ícone: sem canal alfa vira opaco", semAlfa[3] == 255 && semAlfa[7] == 255);
        var comAlfa = new byte[] { 9, 9, 9, 0, 9, 9, 9, 128 };
        AppIcons.FixAlpha(comAlfa);
        Check("ícone: com alfa fica como está", comAlfa[3] == 0 && comAlfa[7] == 128);

        var t = Sta.Run(() => System.Threading.Thread.CurrentThread.GetApartmentState()).GetAwaiter().GetResult();
        Check("sta: roda numa thread STA", t == System.Threading.ApartmentState.STA);
    }
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `cd /c/repos/orbit2.0 && dotnet run --project Orbit.Tests`
Expected: erro de compilação (`AppIcons`, `Sta` não existem).

- [ ] **Step 3: Criar `Orbit/Services/Sta.cs`**

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Orbit.Services;

// COM do shell (Shell.Application, IShellItemImageFactory) quer thread STA. Roda numa thread
// STA dedicada, fora da UI thread — senão listar ~300 apps/ícones travaria a ilha.
public static class Sta
{
    public static Task<T> Run<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true,
            Name = "Orbit STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }
}
```

- [ ] **Step 4: Criar `Orbit/Tabs/Apps/AppCatalog.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace Orbit.Tabs.Apps;

// Todos os apps do "Todos os apps" do Iniciar (desktop + Microsoft Store), via shell:AppsFolder.
// Rodar dentro de Sta.Run (COM STA).
public static class AppCatalog
{
    public static List<AppEntry> Load()
    {
        var type = Type.GetTypeFromProgID("Shell.Application")
                   ?? throw new InvalidOperationException("Shell.Application indisponível");
        dynamic shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic folder = shell.NameSpace("shell:AppsFolder");
            var list = new List<AppEntry>();
            foreach (dynamic item in folder.Items())
            {
                string? name = item.Name;
                string? id = item.Path;
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id) || AppSearch.IsNoise(name)) continue;
                list.Add(new AppEntry(name, id));
            }
            return list.GroupBy(a => a.Id).Select(g => g.First())
                .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }
    }
}
```

- [ ] **Step 5: Criar `Orbit/Tabs/Apps/AppIcons.cs`**

```csharp
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Orbit.Services;
using Serilog;

namespace Orbit.Tabs.Apps;

// Ícone de um app do shell:AppsFolder → PNG em cache (%AppData%\Orbit2\app-icons). Rodar em Sta.Run.
// Extração via IShellItemImageFactory (serve pra app desktop e da Store). O HBITMAP vem 32bpp
// pré-multiplicado; Imaging.CreateBitmapSourceFromHBitmap perderia o alfa (fundo preto),
// então os bytes são copiados à mão.
public static class AppIcons
{
    public static readonly string CacheDir = Path.Combine(SettingsStore.Dir, "app-icons");
    private const int Size = 48;
    private const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    // Caminho do PNG (do cache ou recém-extraído), ou null se não deu.
    public static string? GetOrCreate(string id, string cacheDir)
    {
        var path = Path.Combine(cacheDir, AppSearch.IconFileName(id));
        if (File.Exists(path)) return path;
        IShellItemImageFactory? factory = null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName("shell:AppsFolder\\" + id, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            if (factory.GetImage(new SIZE { cx = Size, cy = Size }, SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out hbmp) != 0 || hbmp == IntPtr.Zero)
                return null;
            var png = ToPng(hbmp);
            if (png is null) return null;
            Directory.CreateDirectory(cacheDir);
            File.WriteAllBytes(path, png);
            return path;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Apps: sem ícone para {Id}", id);
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (factory is not null) Marshal.ReleaseComObject(factory);
        }
    }

    private static byte[]? ToPng(IntPtr hbmp)
    {
        var ds = new DIBSECTION();
        if (GetObject(hbmp, Marshal.SizeOf<DIBSECTION>(), ref ds) == 0 || ds.dsBm.bmBits == IntPtr.Zero || ds.dsBm.bmBitsPixel != 32)
            return null;
        int w = ds.dsBm.bmWidth, h = ds.dsBm.bmHeight, stride = ds.dsBm.bmWidthBytes;
        var px = new byte[stride * h];
        Marshal.Copy(ds.dsBm.bmBits, px, 0, px.Length);
        if (ds.dsBmih.biHeight > 0) FlipRows(px, stride, h); // DIB de baixo pra cima
        FixAlpha(px);
        var src = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, stride);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    // --- Lógica pura testável ---

    public static void FlipRows(byte[] px, int stride, int height)
    {
        var tmp = new byte[stride];
        for (int top = 0, bottom = height - 1; top < bottom; top++, bottom--)
        {
            Buffer.BlockCopy(px, top * stride, tmp, 0, stride);
            Buffer.BlockCopy(px, bottom * stride, px, top * stride, stride);
            Buffer.BlockCopy(tmp, 0, px, bottom * stride, stride);
        }
    }

    // Ícone sem canal alfa (todos os A = 0) sairia invisível: torna opaco.
    public static void FixAlpha(byte[] bgra)
    {
        for (int i = 3; i < bgra.Length; i += 4)
            if (bgra[i] != 0) return;
        for (int i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(string pszPath, IntPtr pbc,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")] private static extern int GetObject(IntPtr h, int c, ref DIBSECTION pv);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr ho);
}
```

- [ ] **Step 6: Criar `Orbit/Tabs/Apps/AppsViewModel.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Models;
using Orbit.Services;
using Serilog;

namespace Orbit.Tabs.Apps;

public sealed partial class AppTile : ObservableObject
{
    public AppTile(AppEntry entry, bool favorite)
    {
        Entry = entry;
        _isFavorite = favorite;
    }

    public AppEntry Entry { get; }
    public string Name => Entry.Name;

    [ObservableProperty] private string? _iconPath;
    [ObservableProperty] private bool _isFavorite;
}

// Aba Apps: favoritos fixados + busca em todos os apps. A lista e os ícones vêm do shell numa
// thread STA (Sta.Run); ícones ficam em cache em disco. Só trabalha com a aba aberta.
public sealed partial class AppsViewModel : TabViewModel
{
    private static readonly TimeSpan CatalogMaxAge = TimeSpan.FromMinutes(10);
    private const int MaxResults = 8;

    private readonly OrbitSettings _settings;
    private readonly string _iconDir;
    private readonly Action _collapse;
    private List<AppEntry> _all = new();
    private DateTime? _loadedAt;

    public override string Id => "apps";
    public override string Title => "Apps";

    public ObservableCollection<AppTile> Favorites { get; } = new();
    public ObservableCollection<AppTile> Results { get; } = new();

    [ObservableProperty] private string _query = "";
    [ObservableProperty] private bool _loading;

    public bool HasQuery => Query.Trim().Length > 0;
    public bool NoFavorites => Favorites.Count == 0 && !Loading;

    public AppsViewModel(OrbitSettings settings, string iconDir, Action collapse)
    {
        _settings = settings;
        _iconDir = iconDir;
        _collapse = collapse;
    }

    public override void SetActive(bool active)
    {
        if (!active)
        {
            Query = "";
            return;
        }
        if (Freshness.IsStale(_loadedAt, CatalogMaxAge, DateTime.Now)) _ = LoadCatalogAsync();
        else BuildFavorites();
    }

    private async Task LoadCatalogAsync()
    {
        Loading = true;
        OnPropertyChanged(nameof(NoFavorites));
        try
        {
            _all = await Sta.Run(AppCatalog.Load);
            _loadedAt = DateTime.Now;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Apps: não consegui listar os apps");
        }
        finally
        {
            Loading = false;
        }
        BuildFavorites();
        BuildResults();
    }

    partial void OnQueryChanged(string value)
    {
        OnPropertyChanged(nameof(HasQuery));
        BuildResults();
    }

    private void BuildFavorites()
    {
        Favorites.Clear();
        foreach (var id in _settings.FavoriteApps)
            if (_all.FirstOrDefault(a => a.Id == id) is { } app)
                Favorites.Add(new AppTile(app, favorite: true));
        OnPropertyChanged(nameof(NoFavorites));
        _ = LoadIconsAsync(Favorites.ToList());
    }

    private void BuildResults()
    {
        Results.Clear();
        foreach (var app in AppSearch.Search(_all, Query, MaxResults))
            Results.Add(new AppTile(app, _settings.FavoriteApps.Contains(app.Id)));
        _ = LoadIconsAsync(Results.ToList());
    }

    // ponytail: extrai só os visíveis (≤ 12 favoritos ou 8 resultados); o cache em disco deixa
    // a 2ª vez instantânea. Uma busca nova antes de terminar só desperdiça a extração antiga.
    private async Task LoadIconsAsync(List<AppTile> tiles)
    {
        if (tiles.Count == 0) return;
        try
        {
            var dir = _iconDir;
            var paths = await Sta.Run(() => tiles.Select(t => AppIcons.GetOrCreate(t.Entry.Id, dir)).ToList());
            for (int i = 0; i < tiles.Count; i++) tiles[i].IconPath = paths[i];
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Apps: falha ao carregar ícones");
        }
    }

    [RelayCommand]
    private void Launch(AppTile? tile)
    {
        tile ??= Results.FirstOrDefault();
        if (tile is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + tile.Entry.Id) { UseShellExecute = false });
            _collapse();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Apps: não abri {App}", tile.Name);
        }
    }

    [RelayCommand]
    private void ToggleFavorite(AppTile tile)
    {
        _settings.FavoriteApps = AppSearch.ToggleFavorite(_settings.FavoriteApps, tile.Entry.Id);
        SettingsStore.Save(_settings);
        tile.IsFavorite = _settings.FavoriteApps.Contains(tile.Entry.Id);
        foreach (var r in Results.Where(r => r.Entry.Id == tile.Entry.Id)) r.IsFavorite = tile.IsFavorite;
        BuildFavorites();
    }
}
```

- [ ] **Step 7: Criar `Orbit/Tabs/Apps/AppsView.xaml`**

```xml
<UserControl x:Class="Orbit.Tabs.Apps.AppsView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <DockPanel>
        <Grid DockPanel.Dock="Top" Margin="0,0,0,12">
            <TextBox Style="{StaticResource Field}" Text="{Binding Query, UpdateSourceTrigger=PropertyChanged}">
                <TextBox.InputBindings>
                    <KeyBinding Key="Enter" Command="{Binding LaunchCommand}"/>
                </TextBox.InputBindings>
            </TextBox>
            <TextBlock Text="Buscar apps…  (Enter abre o primeiro)" Foreground="{StaticResource InkLo}" FontSize="13" Margin="12,7,0,0"
                       IsHitTestVisible="False" Visibility="{Binding HasQuery, Converter={StaticResource NotBoolToVis}}"/>
        </Grid>

        <Grid>
            <!-- Favoritos (sem busca) -->
            <Grid Visibility="{Binding HasQuery, Converter={StaticResource NotBoolToVis}}">
                <TextBlock Text="Carregando apps…" Foreground="{StaticResource InkLo}" FontSize="13"
                           HorizontalAlignment="Center" VerticalAlignment="Center"
                           Visibility="{Binding Loading, Converter={StaticResource BoolToVis}}"/>
                <TextBlock Text="Busque um app e toque na estrela para fixá-lo aqui." Foreground="{StaticResource InkLo}" FontSize="13"
                           HorizontalAlignment="Center" VerticalAlignment="Center"
                           Visibility="{Binding NoFavorites, Converter={StaticResource BoolToVis}}"/>
                <ItemsControl ItemsSource="{Binding Favorites}">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <UniformGrid Columns="6" VerticalAlignment="Top"/>
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                    <ItemsControl.ItemTemplate>
                        <DataTemplate>
                            <Grid Margin="4">
                                <Button Style="{StaticResource RowButton}" Padding="6,12" Height="96"
                                        Command="{Binding DataContext.LaunchCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                        CommandParameter="{Binding}">
                                    <StackPanel HorizontalAlignment="Center">
                                        <Grid Width="40" Height="40" Margin="0,0,0,8">
                                            <TextBlock Text="&#xE71D;" FontFamily="{StaticResource IconFont}" FontSize="22"
                                                       Foreground="{StaticResource InkLo}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                            <Image Source="{Binding IconPath, Converter={StaticResource AppIcon}}" Width="40" Height="40"/>
                                        </Grid>
                                        <TextBlock Text="{Binding Name}" FontSize="11" TextTrimming="CharacterEllipsis"
                                                   TextAlignment="Center" MaxWidth="90"/>
                                    </StackPanel>
                                </Button>
                                <Button Style="{StaticResource GhostRound}" Content="&#xE711;" ToolTip="Desafixar"
                                        HorizontalAlignment="Right" VerticalAlignment="Top" Margin="0,2,2,0"
                                        Command="{Binding DataContext.ToggleFavoriteCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                        CommandParameter="{Binding}"/>
                            </Grid>
                        </DataTemplate>
                    </ItemsControl.ItemTemplate>
                </ItemsControl>
            </Grid>

            <!-- Resultados da busca -->
            <ItemsControl ItemsSource="{Binding Results}" Visibility="{Binding HasQuery, Converter={StaticResource BoolToVis}}">
                <ItemsControl.ItemTemplate>
                    <DataTemplate>
                        <Button Style="{StaticResource RowButton}" Margin="0,0,0,4" Padding="10,6,6,6"
                                Command="{Binding DataContext.LaunchCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                CommandParameter="{Binding}">
                            <DockPanel>
                                <Grid DockPanel.Dock="Left" Width="24" Height="24" Margin="0,0,12,0">
                                    <TextBlock Text="&#xE71D;" FontFamily="{StaticResource IconFont}" FontSize="14"
                                               Foreground="{StaticResource InkLo}" HorizontalAlignment="Center" VerticalAlignment="Center"/>
                                    <Image Source="{Binding IconPath, Converter={StaticResource AppIcon}}" Width="24" Height="24"/>
                                </Grid>
                                <Button x:Name="star" DockPanel.Dock="Right" Style="{StaticResource GhostRound}" Content="&#xE734;"
                                        ToolTip="Fixar nos favoritos"
                                        Command="{Binding DataContext.ToggleFavoriteCommand, RelativeSource={RelativeSource AncestorType=ItemsControl}}"
                                        CommandParameter="{Binding}"/>
                                <TextBlock Text="{Binding Name}" FontSize="13" VerticalAlignment="Center" TextTrimming="CharacterEllipsis"/>
                            </DockPanel>
                        </Button>
                        <DataTemplate.Triggers>
                            <DataTrigger Binding="{Binding IsFavorite}" Value="True">
                                <Setter TargetName="star" Property="Content" Value="&#xE735;"/>
                                <Setter TargetName="star" Property="Foreground" Value="{DynamicResource AccentBrush}"/>
                                <Setter TargetName="star" Property="ToolTip" Value="Desafixar"/>
                            </DataTrigger>
                        </DataTemplate.Triggers>
                    </DataTemplate>
                </ItemsControl.ItemTemplate>
            </ItemsControl>
        </Grid>
    </DockPanel>
</UserControl>
```

> `&#xE71D;` = "AllApps" (glifo de reserva enquanto o ícone não carrega); `&#xE734;`/`&#xE735;` = estrela vazia/cheia.

`Orbit/Tabs/Apps/AppsView.xaml.cs`:
```csharp
using System.Windows.Controls;

namespace Orbit.Tabs.Apps;

public partial class AppsView : UserControl
{
    public AppsView() => InitializeComponent();
}
```

- [ ] **Step 8: Registrar na ilha**

Em `Orbit/Island/IslandViewModel.cs` (com `using Orbit.Tabs.Apps;`), acima da linha-âncora:
`Register(new AppsViewModel(settings, AppIcons.CacheDir, Collapse));`

Em `Orbit/Island/IslandWindow.xaml`: `xmlns:apps="clr-namespace:Orbit.Tabs.Apps"` e
```xml
        <DataTemplate DataType="{x:Type apps:AppsViewModel}">
            <apps:AppsView/>
        </DataTemplate>
```

- [ ] **Step 9: Build e testes; commit**

Run: `cd /c/repos/orbit2.0 && dotnet build -c Debug && dotnet build -c Release && dotnet build --no-incremental Orbit/Orbit.csproj && dotnet run --project Orbit.Tests`
Expected: 0 erros/0 avisos; `PASS ícone/sta: ...` e `TODOS OK`.

```bash
cd /c/repos/orbit2.0 && git add -A && git commit -m "feat: aba Apps (todos os apps do Windows, ícones em cache, favoritos e busca)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Aceitação e `CLAUDE.md`

- [ ] **Step 1: Checklist manual com o usuário** (o controlador conduz; app aberto pelo caminho do exe, encerrado pelo PID)

1. A barra mostra as 10 abas sem cortar: Controles, Hoje, Stats, Clipboard, Apps, Mercado, Notícias, Claude, Energia, Ajustes.
2. **Clipboard:** copiar um texto, um link e um print (Win+Shift+S) em outros apps → os três aparecem no topo, com ícone/miniatura e "agora".
3. Selecionar um item → a prévia e os detalhes (tipo, tamanho, hora) aparecem à direita.
4. Abrir o Bloco de Notas, abrir a ilha (Ctrl+Alt+O), escolher um item e apertar Enter (ou duplo clique, ou "Colar") → a ilha recolhe e o conteúdo é colado no Bloco de Notas.
5. Busca filtra enquanto digita; filtros Texto/Imagens/Links; Ctrl+P fixa (vai pro topo com o alfinete); Del remove; "Limpar" mantém os fixados.
6. Copiar uma senha de um gerenciador (Bitwarden/1Password/KeePass) → **não** aparece no histórico.
7. Esconder a aba Clipboard em Ajustes → copiar algo → reexibir: o item não foi gravado.
8. **Apps:** a 1ª abertura mostra "Carregando apps…" e a ilha não trava. Buscar "spot", "code", "calc" → aparecem com ícone (inclusive apps da Microsoft Store). Enter abre o primeiro; clique abre e a ilha recolhe.
9. Estrela fixa nos favoritos; sem busca, a grade de favoritos mostra ícone e nome; ✕ desafixa; os favoritos continuam depois de reiniciar.
10. **Leveza** (Release, ilha recolhida, 1 min parado): CPU ~0%. Copiar 10 coisas seguidas não faz a CPU ficar alta depois.

Corrigir o que falhar (cada correção = commit `fix: ...`).

- [ ] **Step 2: Atualizar `CLAUDE.md`**

Na seção "Arquitetura", depois do item que começa com `- **Serviços de fundo**`, acrescente:

```markdown
- **Clipboard** (`Tabs/Clips/`, namespace `Orbit.Tabs.Clips` — nunca `...Clipboard`, que esconderia
  `System.Windows.Clipboard`): `ClipboardWatcher` (WM_CLIPBOARDUPDATE) chama
  `ClipsViewModel.CaptureAsync`; histórico em `clipboard.json` + `clipboard-img\`. Conteúdo
  marcado como privado (gerenciadores de senha) é ignorado. Colar = clipboard + recolher (foco
  volta ao `_prevFg`) + `Input.SendCtrlV`.
- **Apps** (`Tabs/Apps/`): `shell:AppsFolder` via COM numa thread STA (`Sta.Run`); ícones por
  `IShellItemImageFactory` com cache em `app-icons\`; abre com `explorer.exe shell:AppsFolder\<id>`.
```

- [ ] **Step 3: Commit**

```bash
cd /c/repos/orbit2.0 && git add -A && git commit --allow-empty -m "test: aceitação da fase 3 (checklist ok; CPU parado <valor>%)" -m "Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

## Fora desta fase

Arquivos copiados no Explorer (CFSTR_FILEDESCRIPTOR/FileDrop) no histórico; reordenar favoritos;
seções "Hoje/Ontem" na lista do clipboard; atalho global só para o clipboard. Fase 4: ponte com o iPhone.
