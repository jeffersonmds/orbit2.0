# Câmera do notebook no iPhone — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ver as câmeras do notebook ao vivo pelo PWA do iPhone (via ponte Tailscale já existente), com botão para tirar foto e salvar nas Fotos do celular.

**Architecture:** Serviço novo `Camera` (WinRT `Windows.Media.Capture`, sem DLL de terceiro) captura e codifica JPEG. A ponte Kestrel existente ganha 4 endpoints atrás do mesmo token; o vídeo ao vivo vai por MJPEG (`multipart/x-mixed-replace`) numa `<img>`, autenticado por um tíquete de uso único (a `<img>` não manda cabeçalho). A câmera só abre quando um stream conecta e fecha quando ele cai. Capability desligada por padrão.

**Tech Stack:** .NET 10 WPF, WinRT Windows.Media.Capture, ASP.NET Core minimal API (Kestrel in-process), NAudio não entra aqui, PWA HTML/JS puro. Testes: harness `Orbit.Tests` (método `Check(nome, bool)`).

Spec: `docs/superpowers/specs/2026-10-16-camera-iphone-design.md`.

**Convenções do projeto (valem para todas as tasks):**
- Código e comentários em português; glifos Segoe como escape `\uXXXX`, nunca o caractere cru.
- Build Release com `-o` se o Orbit estiver rodando de `bin\Release`; matar o Orbit só por PID (nunca `taskkill /IM Orbit.exe` — mesmo nome do 1.0).
- Testes puros no `Orbit.Tests/Program.cs`: registrar o método na lista de chamadas e usar `Check("nome", condição)`. Rodar com `dotnet run --project Orbit.Tests` → espera `TODOS OK`.
- Simplificações deliberadas marcadas com `// ponytail:`.
- Trailer de commit: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

---

## File Structure

- Create `Orbit/Bridge/CameraPreset.cs` — record do preset (largura+fps) + parse/clamp. Puro.
- Create `Orbit/Bridge/StreamTickets.cs` — tíquete de uso único que guarda deviceId+preset. Puro, espelha `PairingCodes`.
- Create `Orbit/Bridge/Mjpeg.cs` — cabeçalho de cada parte MJPEG. Puro.
- Create `Orbit/Services/Camera.cs` — captura WinRT, enumeração, stream de JPEGs, foto. Stateful; vive só com stream aberto.
- Modify `Orbit/Models/OrbitSettings.cs` — flag `BridgeCameraEnabled` (default false).
- Modify `Orbit/Bridge/BridgeHandlers.cs` — `Func<bool> CameraEnabled`.
- Modify `Orbit/Bridge/BridgeServer.cs` — recebe `Camera` + `StreamTickets`; 4 endpoints; gate de capability; não auditar frames.
- Modify `Orbit/Bridge/BridgeRules.cs` — `ShouldAudit` ignora o stream.
- Modify `Orbit/Tabs/IPhone/IPhoneViewModel.cs` — cria/dispõe `Camera` e `StreamTickets`, passa pro Server, toggle "Câmera".
- Modify `Orbit/Tabs/IPhone/IPhoneView.xaml` — chip "Câmera".
- Modify `Orbit/Island/IslandViewModel.cs` — `CameraEnabled` nos handlers + Notify de câmera.
- Modify `Orbit/Bridge/wwwroot/{index.html,app.js,style.css}` — cartão Câmera.
- Modify `Orbit.Tests/Program.cs` — testes das partes puras.

---

## Task 1: Preset de qualidade (puro)

**Files:**
- Create: `Orbit/Bridge/CameraPreset.cs`
- Test: `Orbit.Tests/Program.cs`

- [ ] **Step 1: Escrever o teste que falha**

No `Orbit.Tests/Program.cs`, registrar na lista de chamadas (junto das outras, ex. depois de `MixerTests();`):

```csharp
        CameraPresetTests();
```

E adicionar o método (perto dos outros `*Tests`), usando `using Orbit.Bridge;` (já presente no arquivo):

```csharp
    private static void CameraPresetTests()
    {
        Check("preset: padrão 720p/24", CameraPreset.Default.Width == 1280 && CameraPreset.Default.Fps == 24);
        Check("preset: 480p", CameraPreset.Parse("480", "20") == new CameraPreset(640, 20));
        Check("preset: 720p", CameraPreset.Parse("720", "24") == new CameraPreset(1280, 24));
        Check("preset: fps preso em 5..30", CameraPreset.Parse("720", "99").Fps == 30 && CameraPreset.Parse("720", "1").Fps == 5);
        Check("preset: lixo vira padrão", CameraPreset.Parse(null, null) == CameraPreset.Default && CameraPreset.Parse("4k", "x") == CameraPreset.Default);
    }
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet run --project Orbit.Tests`
Expected: erro de compilação (`CameraPreset` não existe).

- [ ] **Step 3: Implementar**

`Orbit/Bridge/CameraPreset.cs`:

```csharp
using System;

namespace Orbit.Bridge;

// Qualidade do stream ao vivo. Largura fixa em 2 opções; altura o encoder deriva do aspecto.
public sealed record CameraPreset(int Width, int Fps)
{
    public static readonly CameraPreset Default = new(1280, 24);

    // quality: "720"|"480"; fps: 5..30. Qualquer coisa fora disso cai no padrão/limite.
    public static CameraPreset Parse(string? quality, string? fps)
    {
        int width = quality switch { "720" => 1280, "480" => 640, _ => 0 };
        if (width == 0) return Default;
        if (!int.TryParse(fps, out var f)) return new CameraPreset(width, Default.Fps);
        return new CameraPreset(width, Math.Clamp(f, 5, 30));
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet run --project Orbit.Tests`
Expected: `TODOS OK`.

- [ ] **Step 5: Commit**

```bash
git add Orbit/Bridge/CameraPreset.cs Orbit.Tests/Program.cs
git commit -m "feat: preset de qualidade da câmera (puro)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 2: Tíquete de stream (puro)

A `<img>` não manda `Authorization`. O PWA troca o token (no cabeçalho, em `/open`) por um tíquete opaco de uso único, usado só na URL do stream. Espelha `Orbit/Bridge/PairingCodes.cs`.

**Files:**
- Create: `Orbit/Bridge/StreamTickets.cs`
- Test: `Orbit.Tests/Program.cs`

- [ ] **Step 1: Escrever o teste que falha**

Registrar `StreamTicketTests();` na lista e adicionar:

```csharp
    private static void StreamTicketTests()
    {
        var t = new StreamTickets();
        var now = new DateTime(2026, 1, 1, 12, 0, 0);
        var tk = t.Issue("dev-A", new CameraPreset(1280, 24), now);
        Check("tíquete: não vazio", !string.IsNullOrEmpty(tk));
        Check("tíquete: claim certo devolve device+preset", t.Claim(tk, now) is { DeviceId: "dev-A", Preset.Width: 1280 });
        Check("tíquete: uso único (2ª vez falha)", t.Claim(tk, now) is null);
        var tk2 = t.Issue("dev-B", CameraPreset.Default, now);
        Check("tíquete: expira", t.Claim(tk2, now.AddSeconds(31)) is null);
        Check("tíquete: errado falha", t.Claim("naoexiste", now) is null);
        var tk3 = t.Issue("dev-C", CameraPreset.Default, now);
        t.Issue("dev-D", CameraPreset.Default, now); // emitir de novo invalida o anterior
        Check("tíquete: emitir substitui", t.Claim(tk3, now) is null);
    }
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet run --project Orbit.Tests`
Expected: não compila (`StreamTickets` não existe).

- [ ] **Step 3: Implementar**

`Orbit/Bridge/StreamTickets.cs`:

```csharp
using System;
using System.Security.Cryptography;

namespace Orbit.Bridge;

// Um tíquete ativo por vez, uso único, 30 s para o stream conectar. Guarda a câmera/preset
// escolhidos em /open para o GET /stream (que só carrega o tíquete na URL) saber o que abrir.
// ponytail: um por vez basta — só existe um stream. Só em memória: reiniciar o Orbit zera.
public sealed class StreamTickets
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private string? _ticket;
    private string _deviceId = "";
    private CameraPreset _preset = CameraPreset.Default;
    private DateTime _expires;

    public string Issue(string deviceId, CameraPreset preset, DateTime now)
    {
        var tk = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        lock (_gate)
        {
            _ticket = tk;
            _deviceId = deviceId;
            _preset = preset;
            _expires = now + Lifetime;
        }
        return tk;
    }

    public (string DeviceId, CameraPreset Preset)? Claim(string? ticket, DateTime now)
    {
        lock (_gate)
        {
            if (_ticket is null || ticket != _ticket) return null;
            var expired = now > _expires;
            var device = _deviceId;
            var preset = _preset;
            _ticket = null; // uso único, mesmo expirado
            return expired ? null : (device, preset);
        }
    }
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet run --project Orbit.Tests`
Expected: `TODOS OK`.

- [ ] **Step 5: Commit**

```bash
git add Orbit/Bridge/StreamTickets.cs Orbit.Tests/Program.cs
git commit -m "feat: tíquete de stream de uso único (puro)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 3: Enquadramento MJPEG (puro)

**Files:**
- Create: `Orbit/Bridge/Mjpeg.cs`
- Test: `Orbit.Tests/Program.cs`

- [ ] **Step 1: Escrever o teste que falha**

Registrar `MjpegTests();` e adicionar:

```csharp
    private static void MjpegTests()
    {
        Check("mjpeg: boundary", Mjpeg.Boundary == "orbitcam");
        Check("mjpeg: content-type", Mjpeg.ContentType == "multipart/x-mixed-replace; boundary=orbitcam");
        var header = System.Text.Encoding.ASCII.GetString(Mjpeg.PartHeader(1234));
        Check("mjpeg: cabeçalho da parte", header == "\r\n--orbitcam\r\nContent-Type: image/jpeg\r\nContent-Length: 1234\r\n\r\n");
    }
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet run --project Orbit.Tests`
Expected: não compila (`Mjpeg` não existe).

- [ ] **Step 3: Implementar**

`Orbit/Bridge/Mjpeg.cs`:

```csharp
using System.Text;

namespace Orbit.Bridge;

// Fronteira entre quadros de um multipart/x-mixed-replace. Cada quadro: PartHeader(len) + bytes do JPEG.
public static class Mjpeg
{
    public const string Boundary = "orbitcam";
    public const string ContentType = "multipart/x-mixed-replace; boundary=" + Boundary;

    public static byte[] PartHeader(int jpegLength) =>
        Encoding.ASCII.GetBytes($"\r\n--{Boundary}\r\nContent-Type: image/jpeg\r\nContent-Length: {jpegLength}\r\n\r\n");
}
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet run --project Orbit.Tests`
Expected: `TODOS OK`.

- [ ] **Step 5: Commit**

```bash
git add Orbit/Bridge/Mjpeg.cs Orbit.Tests/Program.cs
git commit -m "feat: enquadramento MJPEG (puro)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 4: Serviço Camera (WinRT)

Captura via `Windows.Media.Capture`. Enumeração, stream de JPEGs com contagem de viewers (abre no 1º, fecha no último), e foto em resolução cheia. A parte de captura não é testável por unidade (hardware); só a ordenação da lista é pura e testada. Verificação é manual (Task 10).

**Pré-requisito de ambiente:** Windows → Configurações → Privacidade → Câmera → "Permitir que apps da área de trabalho acessem a câmera" ligado, senão `InitializeAsync` lança `UnauthorizedAccessException`.

**Files:**
- Create: `Orbit/Services/Camera.cs`
- Test: `Orbit.Tests/Program.cs` (só `OrderDevices`)

- [ ] **Step 1: Escrever o teste puro que falha**

Registrar `CameraTests();` e adicionar (`using Orbit.Services;` já existe):

```csharp
    private static void CameraTests()
    {
        var ordered = Camera.OrderDevices([
            new CameraDevice("b", "Logitech BRIO"),
            new CameraDevice("a", "Câmera Integrada"),
        ]);
        Check("câmera: ordena por nome", string.Join(",", ordered.Select(d => d.Id)) == "a,b");
    }
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet run --project Orbit.Tests`
Expected: não compila (`Camera`/`CameraDevice` não existem).

- [ ] **Step 3: Implementar**

`Orbit/Services/Camera.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;
using Windows.Devices.Enumeration;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace Orbit.Services;

public sealed record CameraDevice(string Id, string Name);

// Câmera do notebook como fonte de JPEGs. Uma câmera/stream por vez. Abre no 1º viewer, fecha no
// último; parado não existe. ponytail: InitializeAsync pode exigir apartamento específico — se
// quebrar em thread do pool num futuro Windows, criar um DispatcherQueueController dedicado aqui.
public sealed class Camera : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MediaCapture? _capture;
    private MediaFrameReader? _reader;
    private string? _openDevice;
    private int _viewers;

    private readonly object _frameLock = new();
    private byte[]? _latest;
    private TaskCompletionSource<byte[]> _next = NewSignal();
    private long _lastEncodeTicks;
    private int _minIntervalMs;

    public static async Task<IReadOnlyList<CameraDevice>> DevicesAsync()
    {
        try
        {
            var found = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            return OrderDevices(found.Select(d => new CameraDevice(d.Id, d.Name)).ToList());
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Câmera: não enumerei dispositivos");
            return [];
        }
    }

    public static IReadOnlyList<CameraDevice> OrderDevices(IEnumerable<CameraDevice> devices) =>
        devices.OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    // Garante a câmera aberta no dispositivo/preset dados e conta +1 viewer. O IDisposable solta.
    public async Task<IDisposable> OpenStreamAsync(string deviceId, Orbit.Bridge.CameraPreset preset)
    {
        await _gate.WaitAsync();
        try
        {
            if (_capture is not null && _openDevice != deviceId) await CloseLocked();
            if (_capture is null) await OpenLocked(deviceId, preset);
            _viewers++;
            KeepAwake.On();
            return new Release(this);
        }
        finally { _gate.Release(); }
    }

    private async Task OpenLocked(string deviceId, Orbit.Bridge.CameraPreset preset)
    {
        _minIntervalMs = 1000 / Math.Max(preset.Fps, 1);
        var capture = new MediaCapture();
        await capture.InitializeAsync(new MediaCaptureInitializationSettings
        {
            VideoDeviceId = deviceId,
            StreamingCaptureMode = StreamingCaptureMode.Video,
            MemoryPreference = MediaCaptureMemoryPreference.Cpu,
            SharingMode = MediaCaptureSharingMode.ExclusiveControl,
        });
        var source = capture.FrameSources.Values.FirstOrDefault(s => s.Info.MediaStreamType == MediaStreamType.VideoRecord)
                     ?? capture.FrameSources.Values.First(s => s.Info.SourceKind == MediaFrameSourceKind.Color);
        // Formato mais perto da largura do preset (desempata pela maior taxa).
        var format = source.SupportedFormats
            .Where(f => f.VideoFormat.Width > 0)
            .OrderBy(f => Math.Abs((int)f.VideoFormat.Width - preset.Width))
            .ThenByDescending(f => f.FrameRate.Numerator / Math.Max(1.0, f.FrameRate.Denominator))
            .FirstOrDefault();
        if (format is not null) await source.SetFormatAsync(format);
        var reader = await capture.CreateFrameReaderAsync(source);
        reader.FrameArrived += OnFrame;
        await reader.StartAsync();
        _capture = capture;
        _reader = reader;
        _openDevice = deviceId;
        Log.Information("Câmera: aberta {Dev}", deviceId);
    }

    private void OnFrame(MediaFrameReader sender, MediaFrameArrivedEventArgs _)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastEncodeTicks) < _minIntervalMs) { using (sender.TryAcquireLatestFrame()) { } return; }
        using var frame = sender.TryAcquireLatestFrame();
        var bmp = frame?.VideoMediaFrame?.SoftwareBitmap;
        if (bmp is null) return;
        Interlocked.Exchange(ref _lastEncodeTicks, now);
        _ = EncodeAsync(bmp); // SoftwareBitmap é copiado dentro; o frame é liberado ao sair
    }

    private async Task EncodeAsync(SoftwareBitmap bmp)
    {
        try
        {
            using var copy = SoftwareBitmap.Copy(bmp);
            using var ms = new InMemoryRandomAccessStream();
            var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, ms);
            using var ready = copy.BitmapPixelFormat == BitmapPixelFormat.Bgra8
                ? SoftwareBitmap.Convert(copy, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore)
                : SoftwareBitmap.Convert(copy, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore);
            enc.SetSoftwareBitmap(ready);
            await enc.FlushAsync();
            var bytes = new byte[ms.Size];
            await ms.ReadAsync(bytes.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
            Publish(bytes);
        }
        catch (Exception ex) { Log.Debug(ex, "Câmera: falha ao codificar quadro"); }
    }

    private void Publish(byte[] jpeg)
    {
        lock (_frameLock)
        {
            _latest = jpeg;
            var signal = _next;
            _next = NewSignal();
            signal.TrySetResult(jpeg);
        }
    }

    // Próximo JPEG (ou o atual, se ainda não houve novo). Bloqueia até chegar um ou ct cancelar.
    public async Task<byte[]> NextFrameAsync(CancellationToken ct)
    {
        Task<byte[]> wait;
        lock (_frameLock) wait = _next.Task;
        using (ct.Register(() => wait.TrySetCanceledSafe())) { }
        return await wait.WaitAsync(ct);
    }

    // Foto em resolução cheia: abre uma MediaCapture própria, captura e fecha (não mexe no stream).
    public async Task<byte[]> PhotoAsync(string deviceId)
    {
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                VideoDeviceId = deviceId,
                StreamingCaptureMode = StreamingCaptureMode.Video,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                SharingMode = MediaCaptureSharingMode.SharedReadOnly, // convive com o stream
            });
            using var ms = new InMemoryRandomAccessStream();
            await capture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), ms);
            var bytes = new byte[ms.Size];
            ms.Seek(0);
            await ms.ReadAsync(bytes.AsBuffer(), (uint)ms.Size, InputStreamOptions.None);
            return bytes;
        }
        finally { capture.Dispose(); }
    }

    private async Task CloseLocked()
    {
        if (_reader is not null)
        {
            _reader.FrameArrived -= OnFrame;
            try { await _reader.StopAsync(); } catch { }
            _reader.Dispose();
            _reader = null;
        }
        _capture?.Dispose();
        _capture = null;
        _openDevice = null;
        lock (_frameLock) _latest = null;
        Log.Information("Câmera: fechada");
    }

    private async Task ReleaseAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (--_viewers <= 0)
            {
                _viewers = 0;
                await CloseLocked();
                KeepAwake.Off();
            }
        }
        finally { _gate.Release(); }
    }

    private static TaskCompletionSource<byte[]> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Dispose()
    {
        try { ReleaseAsync().GetAwaiter().GetResult(); } catch { }
        _gate.Dispose();
    }

    private sealed class Release(Camera cam) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) _ = cam.ReleaseAsync();
        }
    }
}

internal static class TcsExtensions
{
    public static void TrySetCanceledSafe(this Task _) { }
}
```

> Nota de implementação: `TrySetCanceledSafe` acima é placeholder — remova o `using (ct.Register(...))` vazio e deixe só `await wait.WaitAsync(ct)`, que já cancela via `ct`. Mantido simples: o método `NextFrameAsync` fica:
> ```csharp
> public async Task<byte[]> NextFrameAsync(CancellationToken ct)
> {
>     Task<byte[]> wait;
>     lock (_frameLock) wait = _next.Task;
>     return await wait.WaitAsync(ct);
> }
> ```
> e apague a classe `TcsExtensions`.

- [ ] **Step 4: Criar o KeepAwake**

`Camera` usa `KeepAwake` para impedir suspensão enquanto transmite. Criar `Orbit/Services/KeepAwake.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace Orbit.Services;

// Impede suspensão automática enquanto algo está em uso (ex.: stream de câmera). Contado:
// várias chamadas On precisam do mesmo número de Off. Não impede fechar a tampa (política de energia).
public static class KeepAwake
{
    [Flags]
    private enum Es : uint { Continuous = 0x80000000, SystemRequired = 0x00000001 }

    private static int _count;

    public static void On()
    {
        if (Interlocked.Increment(ref _count) == 1)
            SetThreadExecutionState(Es.Continuous | Es.SystemRequired);
    }

    public static void Off()
    {
        if (Interlocked.Decrement(ref _count) <= 0)
        {
            Interlocked.Exchange(ref _count, 0);
            SetThreadExecutionState(Es.Continuous);
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(Es flags);
}
```

Aplicar a nota de implementação do Step 3 (simplificar `NextFrameAsync`, apagar `TcsExtensions`).

- [ ] **Step 5: Compilar e rodar os testes**

Run: `dotnet build Orbit -c Debug` e depois `dotnet run --project Orbit.Tests`
Expected: build sem erros; `TODOS OK`. (A captura em si é testada na Task 10.)

- [ ] **Step 6: Commit**

```bash
git add Orbit/Services/Camera.cs Orbit/Services/KeepAwake.cs Orbit.Tests/Program.cs
git commit -m "feat: serviço Camera (WinRT) + KeepAwake

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 5: Settings + capability nos handlers

**Files:**
- Modify: `Orbit/Models/OrbitSettings.cs`
- Modify: `Orbit/Bridge/BridgeHandlers.cs`

- [ ] **Step 1: Flag nas settings**

Em `Orbit/Models/OrbitSettings.cs`, logo após `BridgeUploadFolder`:

```csharp
    // Câmera pelo iPhone: desligada mesmo com a ponte ligada, até o usuário ligar na aba.
    public bool BridgeCameraEnabled { get; set; }
```

- [ ] **Step 2: Capability no bag de handlers**

Em `Orbit/Bridge/BridgeHandlers.cs`, dentro da classe, após `Notify`:

```csharp
    // true = a câmera pode responder. Lida a cada requisição /api/camera/*.
    public Func<bool> CameraEnabled { get; init; } = () => false;
```

- [ ] **Step 3: Compilar**

Run: `dotnet build Orbit -c Debug`
Expected: sem erros.

- [ ] **Step 4: Commit**

```bash
git add Orbit/Models/OrbitSettings.cs Orbit/Bridge/BridgeHandlers.cs
git commit -m "feat: flag BridgeCameraEnabled e capability nos handlers

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 6: Endpoints da câmera na ponte

**Files:**
- Modify: `Orbit/Bridge/BridgeServer.cs`
- Modify: `Orbit/Bridge/BridgeRules.cs`
- Test: `Orbit.Tests/Program.cs`

- [ ] **Step 1: Teste do ShouldAudit (não auditar frames)**

O stream é um GET longo; não deve ser auditado como ação. Em `Orbit.Tests/Program.cs`, dentro de um teste existente de BridgeRules ou num novo `CameraAuditTests();`:

```csharp
    private static void CameraAuditTests()
    {
        Check("audit: ignora o stream", !BridgeRules.ShouldAudit("GET", "/api/camera/stream"));
        Check("audit: lista de câmeras não audita", !BridgeRules.ShouldAudit("GET", "/api/camera/devices"));
        Check("audit: abrir câmera audita", BridgeRules.ShouldAudit("POST", "/api/camera/open"));
    }
```

- [ ] **Step 2: Rodar e ver falhar**

Run: `dotnet run --project Orbit.Tests`
Expected: FALHA em "audit: ignora o stream" (hoje `ShouldAudit` devolve true pra GET /camera/stream).

- [ ] **Step 3: Ajustar ShouldAudit**

Em `Orbit/Bridge/BridgeRules.cs`, no método `ShouldAudit`, acrescentar os GETs de câmera à lista de exceções:

```csharp
    public static bool ShouldAudit(string method, string path) =>
        !(string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
          && (path.EndsWith("/stats", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/media", StringComparison.OrdinalIgnoreCase)
              || path.EndsWith("/media/cover", StringComparison.OrdinalIgnoreCase)
              || path.EndsWith("/camera/stream", StringComparison.OrdinalIgnoreCase)
              || path.EndsWith("/camera/devices", StringComparison.OrdinalIgnoreCase)));
```

- [ ] **Step 4: Rodar e ver passar**

Run: `dotnet run --project Orbit.Tests`
Expected: `TODOS OK`.

- [ ] **Step 5: Assinatura do StartAsync + gate de capability + endpoints**

Em `Orbit/Bridge/BridgeServer.cs`:

(a) Estender `StartAsync` para receber a câmera e os tíquetes (opcionais, como `codes`). Trocar a assinatura:

```csharp
    public async Task StartAsync(int port, string webRoot, TokenStore tokens, AuditLog audit, BridgeHandlers h,
        PairingCodes? codes = null, Orbit.Services.Camera? camera = null, StreamTickets? tickets = null)
```

(b) Guardar `h` para o gate (o grupo `/api` precisa dele). Logo após `MapApi(api, h);` não serve — precisamos antes. Adicionar um filtro no grupo `api`, depois do `AddEndpointFilter` de auditoria existente:

```csharp
        // Câmera é capability à parte: 403 enquanto o usuário não liga "Câmera" na aba iPhone.
        api.AddEndpointFilter(async (ctx, next) =>
        {
            if (ctx.HttpContext.Request.Path.StartsWithSegments("/api/camera") && !h.CameraEnabled())
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            return await next(ctx);
        });
```

(c) No fim de `MapApi`, antes do fechamento do método, acrescentar as rotas. `MapApi` precisa receber `camera` e `tickets`; trocar a chamada `MapApi(api, h);` por `MapApi(api, h, camera, tickets);` e a assinatura:

```csharp
    private static void MapApi(RouteGroupBuilder api, BridgeHandlers h, Orbit.Services.Camera? camera, StreamTickets? tickets)
    {
```

E, ainda dentro de `MapApi`, após o bloco `/files`:

```csharp
        if (camera is not null && tickets is not null)
        {
            api.MapGet("/camera/devices", async () =>
                Results.Ok((await Orbit.Services.Camera.DevicesAsync()).Select(d => new { id = d.Id, name = d.Name })));

            api.MapPost("/camera/open", (CameraOpenRequest req) =>
            {
                if (string.IsNullOrWhiteSpace(req.DeviceId)) return Results.BadRequest(new { erro = "Sem câmera." });
                var preset = CameraPreset.Parse(req.Quality, req.Fps);
                var ticket = tickets.Issue(req.DeviceId, preset, DateTime.Now);
                h.Notify(new BridgeEvent("Câmera aberta pelo iPhone", req.DeviceId));
                return Results.Ok(new { ticket });
            });

            api.MapGet("/camera/stream", async (HttpContext ctx, string? ticket) =>
            {
                if (tickets.Claim(ticket, DateTime.Now) is not { } claim)
                {
                    ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                IDisposable handle;
                try { handle = await camera.OpenStreamAsync(claim.DeviceId, claim.Preset); }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Câmera: não abriu");
                    ctx.Response.StatusCode = StatusCodes.Status409Conflict; // ocupada por outro app, etc.
                    return;
                }
                using (handle)
                {
                    ctx.Response.ContentType = Mjpeg.ContentType;
                    ctx.Response.Headers.CacheControl = "no-cache, no-store";
                    var body = ctx.Response.BodyWriter;
                    try
                    {
                        while (!ctx.RequestAborted.IsCancellationRequested)
                        {
                            var jpeg = await camera.NextFrameAsync(ctx.RequestAborted);
                            await body.WriteAsync(Mjpeg.PartHeader(jpeg.Length), ctx.RequestAborted);
                            await body.WriteAsync(jpeg, ctx.RequestAborted);
                            await body.FlushAsync(ctx.RequestAborted);
                        }
                    }
                    catch (OperationCanceledException) { /* iPhone fechou */ }
                }
            });

            api.MapPost("/camera/photo", async (CameraPhotoRequest req) =>
            {
                if (string.IsNullOrWhiteSpace(req.DeviceId)) return Results.BadRequest(new { erro = "Sem câmera." });
                var jpeg = await camera.PhotoAsync(req.DeviceId);
                h.Notify(new BridgeEvent("Foto capturada pelo iPhone", req.DeviceId));
                return Results.File(jpeg, "image/jpeg");
            });
        }
```

(d) Adicionar os records de request no topo do arquivo, junto dos outros (`LinkRequest` etc.):

```csharp
public sealed record CameraOpenRequest(string? DeviceId, string? Quality, string? Fps);
public sealed record CameraPhotoRequest(string? DeviceId);
```

- [ ] **Step 6: Compilar**

Run: `dotnet build Orbit -c Debug`
Expected: sem erros. (Se faltar `using System.Linq;` no `BridgeServer.cs`, já existe.)

- [ ] **Step 7: Commit**

```bash
git add Orbit/Bridge/BridgeServer.cs Orbit/Bridge/BridgeRules.cs Orbit.Tests/Program.cs
git commit -m "feat: endpoints de câmera na ponte (devices/open/stream/photo) + gate de capability

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 7: Fiação na aba iPhone (Camera + tíquetes + toggle)

**Files:**
- Modify: `Orbit/Tabs/IPhone/IPhoneViewModel.cs`
- Modify: `Orbit/Tabs/IPhone/IPhoneView.xaml`
- Modify: `Orbit/Island/IslandViewModel.cs`

- [ ] **Step 1: Camera + tíquetes no IPhoneViewModel e passagem ao Server**

Em `Orbit/Tabs/IPhone/IPhoneViewModel.cs`:

(a) Campos, junto de `_codes`:

```csharp
    private readonly Orbit.Services.Camera _camera = new();
    private readonly StreamTickets _tickets = new();
```

(b) Na chamada do servidor em `StartAsync`, passar os dois:

```csharp
            await Task.Run(() => Server.StartAsync(_settings.BridgePort, _webRoot, _tokens, _audit, handlers, _codes, _camera, _tickets));
```

(c) Propriedade observável do toggle, junto de `Enabled` (procurar `[ObservableProperty] private string _publicUrl`):

```csharp
    [ObservableProperty] private bool _cameraEnabled;
```

(d) No construtor, onde faz `_syncing = true; Enabled = settings.BridgeEnabled; ...`, incluir:

```csharp
        CameraEnabled = settings.BridgeCameraEnabled;
```

dentro do mesmo bloco `_syncing`.

(e) Reagir à mudança (perto de `OnEnabledChanged`):

```csharp
    partial void OnCameraEnabledChanged(bool value)
    {
        if (_syncing) return;
        _settings.BridgeCameraEnabled = value;
        SettingsStore.Save(_settings);
    }
```

(f) Dispor a câmera ao desligar a ponte e ao fechar. No `StopAsync`, após parar o servidor:

```csharp
        _camera.Dispose();
```

> ponytail: `_camera` é recriado? Não — `Dispose` só solta o stream aberto e zera o keep-awake; a câmera reabre sozinha no próximo `OpenStreamAsync`. Trocar `private readonly ... _camera = new();` por recriação não é necessário: `OpenStreamAsync` reinicializa o `MediaCapture` quando `_capture` está null. Se `Dispose` tiver tornado o `SemaphoreSlim` inutilizável, remover o `_gate.Dispose()` do `Camera.Dispose()` para permitir reuso. **Decisão:** em `Camera.Dispose()`, NÃO chamar `_gate.Dispose()` (deixe o GC levar) para o serviço poder ser reusado após desligar/ligar a ponte. Ajustar a Task 4 Step 3 se necessário.

- [ ] **Step 2: CameraEnabled nos handlers (IslandViewModel)**

Em `Orbit/Island/IslandViewModel.cs`, dentro de `BuildBridgeHandlers`, no objeto `new BridgeHandlers { ... }`, adicionar (ao lado de `Notify`):

```csharp
            CameraEnabled = () => _settings.BridgeCameraEnabled,
```

- [ ] **Step 3: Chip "Câmera" na view**

Em `Orbit/Tabs/IPhone/IPhoneView.xaml`, ao lado do chip "Ponte ligada" (linha com `IsChecked="{Binding Enabled}"`), adicionar logo depois:

```xml
                    <CheckBox DockPanel.Dock="Left" Style="{StaticResource Chip}" IsChecked="{Binding CameraEnabled}"
                              Content="Câmera" Margin="0,0,10,0" ToolTip="Permite o iPhone ver as câmeras do PC"/>
```

- [ ] **Step 4: Compilar e testes**

Run: `dotnet build Orbit -c Debug` e `dotnet run --project Orbit.Tests`
Expected: sem erros; `TODOS OK`.

- [ ] **Step 5: Commit**

```bash
git add Orbit/Tabs/IPhone/IPhoneViewModel.cs Orbit/Tabs/IPhone/IPhoneView.xaml Orbit/Island/IslandViewModel.cs
git commit -m "feat: fiação da câmera na aba iPhone (serviço, tíquetes, toggle)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 8: Cartão Câmera no PWA

**Files:**
- Modify: `Orbit/Bridge/wwwroot/index.html`
- Modify: `Orbit/Bridge/wwwroot/app.js`
- Modify: `Orbit/Bridge/wwwroot/style.css`

Antes de editar, ler os três arquivos para seguir o padrão existente (como o token é guardado e enviado — procurar `Authorization`/`Bearer`/`fetch(` em `app.js`; como os cartões são marcados em `index.html`).

- [ ] **Step 1: HTML do cartão**

Em `index.html`, acrescentar um cartão junto dos outros (mídia, arquivos). Usar as mesmas classes de cartão já presentes:

```html
<section class="card" id="cam-card">
  <h2>Câmera</h2>
  <div class="row">
    <select id="cam-device"></select>
    <select id="cam-quality">
      <option value="720">720p</option>
      <option value="480">480p</option>
    </select>
    <button id="cam-toggle" class="ghost">Ver</button>
  </div>
  <div class="cam-view">
    <img id="cam-img" alt="" />
    <button id="cam-shot" class="ghost" hidden>Foto</button>
  </div>
  <p id="cam-msg" class="muted"></p>
</section>
```

- [ ] **Step 2: JS**

Em `app.js`, reutilizar o helper de fetch autenticado já existente (ex.: `api(path, opts)` que injeta o header `Authorization`; se o nome for outro, usar o existente). Acrescentar:

```javascript
const camImg = document.getElementById('cam-img');
const camToggle = document.getElementById('cam-toggle');
const camShot = document.getElementById('cam-shot');
const camDevice = document.getElementById('cam-device');
const camQuality = document.getElementById('cam-quality');
const camMsg = document.getElementById('cam-msg');
let camStreaming = false;

async function camLoadDevices() {
  try {
    const res = await api('/api/camera/devices');
    if (res.status === 403) { camMsg.textContent = 'Ligue "Câmera" na aba iPhone do PC.'; return; }
    const list = await res.json();
    camDevice.innerHTML = '';
    for (const d of list) {
      const o = document.createElement('option');
      o.value = d.id; o.textContent = d.name;
      camDevice.appendChild(o);
    }
    camMsg.textContent = list.length ? '' : 'Nenhuma câmera encontrada.';
  } catch { camMsg.textContent = 'Não consegui listar as câmeras.'; }
}

async function camStart() {
  const res = await api('/api/camera/open', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ deviceId: camDevice.value, quality: camQuality.value, fps: '24' }),
  });
  if (res.status === 403) { camMsg.textContent = 'Ligue "Câmera" na aba iPhone do PC.'; return; }
  if (!res.ok) { camMsg.textContent = 'Não consegui abrir a câmera.'; return; }
  const { ticket } = await res.json();
  // token só no cabeçalho do /open; aqui vai só o tíquete descartável
  camImg.src = `/api/camera/stream?ticket=${encodeURIComponent(ticket)}`;
  camImg.onerror = () => { camMsg.textContent = 'Câmera em uso por outro programa, ou stream caiu.'; camStop(); };
  camStreaming = true;
  camToggle.textContent = 'Parar';
  camShot.hidden = false;
  camMsg.textContent = '';
}

function camStop() {
  camImg.removeAttribute('src');
  camStreaming = false;
  camToggle.textContent = 'Ver';
  camShot.hidden = true;
}

camToggle.onclick = () => camStreaming ? camStop() : camStart();

camShot.onclick = async () => {
  try {
    const res = await api('/api/camera/photo', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ deviceId: camDevice.value }),
    });
    if (!res.ok) { camMsg.textContent = 'Não consegui tirar a foto.'; return; }
    const blob = await res.blob();
    const file = new File([blob], `orbit-${Date.now()}.jpg`, { type: 'image/jpeg' });
    if (navigator.canShare && navigator.canShare({ files: [file] })) {
      await navigator.share({ files: [file] }); // iOS: "Salvar imagem"
    } else {
      // fallback: abre a imagem p/ o usuário segurar e "Adicionar às Fotos"
      const url = URL.createObjectURL(blob);
      window.open(url, '_blank');
    }
  } catch (e) {
    if (e && e.name === 'AbortError') return; // usuário cancelou a folha
    camMsg.textContent = 'Não consegui salvar a foto.';
  }
};

// Carregar as câmeras quando já estiver pareado (seguir como o resto do app detecta o token).
camLoadDevices();
```

> Nota: se o helper autenticado no `app.js` não se chamar `api`, usar o nome real e o mesmo jeito de montar a URL base. Se o app já re-chama rotinas após parear, chamar `camLoadDevices()` no mesmo ponto.

- [ ] **Step 3: CSS**

Em `style.css`:

```css
.cam-view { position: relative; margin-top: 8px; }
#cam-img { width: 100%; border-radius: 12px; background: #000; display: block; min-height: 40px; }
#cam-img:not([src]) { display: none; }
#cam-shot { position: absolute; bottom: 10px; left: 50%; transform: translateX(-50%); }
#cam-card .row { display: flex; gap: 8px; align-items: center; }
#cam-card select { flex: 1; }
```

- [ ] **Step 4: Verificar que o wwwroot é copiado**

Run: `dotnet build Orbit -c Debug` e conferir que os arquivos novos/alterados aparecem em `Orbit/bin/Debug/net10.0-windows10.0.17763.0/wwwroot/`.
Expected: `index.html`, `app.js`, `style.css` atualizados no output (o `.csproj` já copia via `None Include` com Link).

- [ ] **Step 5: Commit**

```bash
git add Orbit/Bridge/wwwroot/index.html Orbit/Bridge/wwwroot/app.js Orbit/Bridge/wwwroot/style.css
git commit -m "feat: cartão de câmera no PWA (stream MJPEG + foto p/ Fotos do iOS)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 9: Teste do PWA no harness (opcional, se houver PwaTests)

O projeto tem `PwaTests` que confere strings no HTML/JS copiado. Se existir, acrescentar uma verificação mínima.

**Files:**
- Modify: `Orbit.Tests/Program.cs`

- [ ] **Step 1: Procurar PwaTests**

Run: `grep -n "PwaTests" Orbit.Tests/Program.cs`
Se não existir, pular esta task.

- [ ] **Step 2: Acrescentar checagem**

Dentro de `PwaTests`, somar (ajustando ao jeito como o teste lê os arquivos — normalmente lê de `AppContext.BaseDirectory/wwwroot`):

```csharp
        Check("pwa: cartão de câmera", html.Contains("id=\"cam-card\""));
        Check("pwa: foto via share", js.Contains("navigator.share"));
        Check("pwa: tíquete na URL do stream", js.Contains("/api/camera/stream?ticket="));
```

- [ ] **Step 3: Rodar**

Run: `dotnet run --project Orbit.Tests`
Expected: `TODOS OK`.

- [ ] **Step 4: Commit**

```bash
git add Orbit.Tests/Program.cs
git commit -m "test: PWA tem cartão de câmera

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Task 10: Teste ao vivo (manual, com o usuário)

Não é automatizável — é a verificação de hardware/rede. Preparar e pedir ao usuário.

- [ ] **Step 1: Build Release e reiniciar o Orbit (por PID)**

```bash
# achar o PID atual do 2.0 (caminho em bin\Release), parar SÓ ele, nunca por nome
dotnet build Orbit -c Release
```
Reiniciar o Orbit a partir de `Orbit/bin/Release/net10.0-windows10.0.17763.0/Orbit.exe`.

- [ ] **Step 2: Ligar a capability**

Na ilha → aba iPhone → ligar "Ponte ligada" e "Câmera". Confirmar que o Tailscale está publicado (como na Fase 4).

- [ ] **Step 3: Checklist no iPhone (pedir ao usuário)**

1. Abrir o PWA → cartão Câmera lista Integrada e Logitech.
2. Escolher uma, "Ver" → vídeo ao vivo aparece; a luz da webcam acende.
3. Trocar pra outra câmera → a primeira solta, a segunda abre.
4. "Foto" → folha do iOS → "Salvar imagem" → conferir nas Fotos.
5. "Parar" (ou fechar) → a luz apaga em alguns segundos.
6. Com "Câmera" desligada na aba iPhone, o cartão avisa pra ligar (403).
7. Conferir a Atividade na aba iPhone: "Câmera aberta", "Foto capturada".

- [ ] **Step 4: Anotar o que não funcionar**

Registrar no `docs/STATUS-*.md` ou na spec os pontos pendentes (ex.: câmera com PC bloqueado, fluidez na rede 4G).

---

## Self-review (feito)

- **Cobertura da spec:** captura WinRT (Task 4), 4 endpoints (Task 6), tíquete p/ `<img>` (Task 2+6), MJPEG (Task 3+6), uma câmera/stream por vez (Task 4), capability off por padrão + Atividade/audit (Task 5,6,7), PWA com foto via share (Task 8), keep-awake (Task 4), presets (Task 1), erro câmera ocupada → 409 (Task 6), câmera bloqueada/áudio no backlog (spec). ✔
- **Nomes consistentes:** `CameraPreset`, `StreamTickets.Issue/Claim`, `Mjpeg.PartHeader/ContentType/Boundary`, `Camera.DevicesAsync/OpenStreamAsync/NextFrameAsync/PhotoAsync/OrderDevices`, `CameraDevice(Id,Name)`, `BridgeHandlers.CameraEnabled`, `OrbitSettings.BridgeCameraEnabled`, `CameraOpenRequest/CameraPhotoRequest`. ✔
- **Risco conhecido e documentado:** apartamento do `MediaCapture` em thread do pool (ponytail na Task 4); `Camera.Dispose` não dispõe o `SemaphoreSlim` p/ permitir reuso (Task 7 Step 1 nota). ✔
