using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Orbit.Bridge;
using Serilog;

namespace Orbit.Services;

// Só existe durante a sessão. A página embutida não passa pelo Kestrel nem recebe tokens.
public sealed class WebRtcCapture
{
    private readonly string _page = "https://" + Guid.NewGuid().ToString("N") + ".orbit-media.invalid/index.html";
    private readonly Channel<string> _messages = Channel.CreateBounded<string>(128);
    private HwndSource? _window;
    private CoreWebView2Controller? _controller;
    private bool _closed;
    private bool _awake;
    public ChannelReader<string> Messages => _messages.Reader;

    public static bool LocalPage(string page, string? uri) => string.Equals(page, uri, StringComparison.Ordinal);

    public async Task OpenAsync(string kind, string deviceId, string label, CameraPreset preset, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException("Sem interface do PC.");
        await dispatcher.InvokeAsync(async () =>
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                _window = new HwndSource(new HwndSourceParameters("Orbit mídia")
                { Width = 1, Height = 1, PositionX = -32000, PositionY = -32000,
                    WindowStyle = unchecked((int)0x90000000), ExtendedWindowStyle = 0x08000080 });
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Orbit2", "WebView2"));
                ct.ThrowIfCancellationRequested();
                var options = env.CreateCoreWebView2ControllerOptions();
                options.ProfileName = "Media"; options.IsInPrivateModeEnabled = true;
                var controller = await env.CreateCoreWebView2ControllerAsync(_window.Handle, options);
                // A criação nativa não aceita cancelamento; um resultado tardio tem descarte explícito.
                if (ct.IsCancellationRequested || _closed) { controller.Close(); ct.ThrowIfCancellationRequested(); throw new OperationCanceledException(); }
                _controller = controller;
                // Chromium exige documento visível para enumerateDevices; HWND fora da tela, sem ativação/taskbar.
                controller.Bounds = new System.Drawing.Rectangle(0, 0, 1, 1);
                controller.IsVisible = true;
                var core = controller.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreHostObjectsAllowed = false;
                core.NavigationStarting += (_, e) => e.Cancel = !LocalPage(_page, e.Uri) || ct.IsCancellationRequested;
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                var permission = kind == "camera" ? CoreWebView2PermissionKind.Camera : CoreWebView2PermissionKind.Microphone;
                core.PermissionRequested += (_, e) =>
                {
                    e.SavesInProfile = false;
                    e.State = !ct.IsCancellationRequested && !_closed && LocalPage(_page, e.Uri) && e.PermissionKind == permission
                        ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                };
                // Libera labels sem abrir primeiro o sensor padrão (que pode ser o errado).
                await core.Profile.SetPermissionStateAsync(permission, new Uri(_page).GetLeftPart(UriPartial.Authority), CoreWebView2PermissionState.Allow);
                ct.ThrowIfCancellationRequested();
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (_, e) =>
                {
                    var script = new Uri(_page, UriKind.Absolute).GetLeftPart(UriPartial.Authority) + "/media.js";
                    string? resource = LocalPage(_page, e.Request.Uri) ? "page.html" : e.Request.Uri == script ? "media.js" : null;
                    var stream = resource is null ? null : typeof(WebRtcCapture).Assembly.GetManifestResourceStream("Orbit.Media." + resource);
                    e.Response = env.CreateWebResourceResponse(stream ?? new MemoryStream(), stream is null ? 403 : 200,
                        stream is null ? "Forbidden" : "OK", "Content-Type: " + (resource == "media.js" ? "text/javascript" : "text/html")
                        + "\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'; script-src 'self'; connect-src 'none'; frame-src 'none'; frame-ancestors 'none'");
                };
                var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                core.WebMessageReceived += (_, e) =>
                {
                    if (_closed || ct.IsCancellationRequested || !LocalPage(_page, e.Source)) return;
                    var json = e.WebMessageAsJson;
                    if (json == "{\"t\":\"ready\"}") { ready.TrySetResult(); return; }
                    if (RtcSignal.Valid(json, fromPc: true) && !_messages.Writer.TryWrite(json)) _messages.Writer.TryComplete(new IOException("Sinalização cheia."));
                };
                core.ProcessFailed += (_, _) => _messages.Writer.TryComplete(new IOException("Motor de mídia encerrado."));
                core.Navigate(_page);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
                ct.ThrowIfCancellationRequested();
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { t = "start", kind, deviceId, label, width = preset.Width, fps = preset.Fps }));
                if (kind == "camera") { KeepAwake.On(); _awake = true; }
            }
            catch { Close(); throw; }
        }).Task.Unwrap();
    }

    public async Task PostAsync(string json, CancellationToken ct)
    {
        var d = Application.Current?.Dispatcher ?? throw new OperationCanceledException();
        await d.InvokeAsync(() => { ct.ThrowIfCancellationRequested(); if (!_closed) _controller?.CoreWebView2.PostWebMessageAsJson(json); });
    }

    public async Task CloseAsync()
    {
        var d = Application.Current?.Dispatcher;
        if (d is not null) await d.InvokeAsync(Close);
    }

    private void Close()
    {
        _closed = true;
        // Close destrói o documento, inclusive getUserMedia pendente e seus tracks.
        try { _controller?.Close(); }
        catch (Exception ex) { Log.Debug(ex, "WebRTC: motor já encerrou"); }
        finally
        {
            _controller = null;
            try { _window?.Dispose(); }
            catch (Exception ex) { Log.Debug(ex, "WebRTC: janela já encerrou"); }
            _window = null; _messages.Writer.TryComplete();
            if (_awake) { _awake = false; KeepAwake.Off(); }
        }
    }
}
