using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Orbit.Services;

// Botão direito → Enviar para → "iPhone (Orbit)" chama `Orbit.exe --send-iphone <arquivos…>`.
// Esse processo curto repassa os caminhos pro Orbit aberto por um pipe só do usuário atual e sai.
public static class SendToIPhone
{
    public const string Arg = "--send-iphone";
    public const string PipeName = "Orbit2SendToIPhone";
    public const string ShortcutName = "iPhone (Orbit).lnk";

    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), ShortcutName);

    // Só arquivos que existem (pasta não vai: o iPhone recebe arquivo).
    public static string[] FilesFrom(IEnumerable<string> args) =>
        args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)).Distinct().ToArray();

    // Lado do processo curto. Nunca lança: sem Orbit aberto, só desiste.
    public static void SendToRunning(string[] files)
    {
        if (files.Length == 0) return;
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(1000);
            using var writer = new StreamWriter(client);
            writer.Write(JsonSerializer.Serialize(files));
        }
        catch { }
    }

    // Lado do Orbit aberto: chama onFiles (thread do pool) a cada pedido.
    public static void Listen(Action<string[]> onFiles, CancellationToken ct) => _ = Task.Run(async () =>
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);
                using var reader = new StreamReader(server);
                var files = JsonSerializer.Deserialize<string[]>(await reader.ReadToEndAsync(ct)) ?? Array.Empty<string>();
                onFiles(FilesFrom(files));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log.Warning(ex, "Enviar pro iPhone: falha no pipe");
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { }
            }
        }
    }, ct);

    // Cria/atualiza o atalho do "Enviar para" (o caminho do exe muda entre Debug e instalado).
    public static void EnsureShortcut(string exePath)
    {
        try
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic lnk = shell.GetType().InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell,
                new object[] { ShortcutPath })!;
            lnk.TargetPath = exePath;
            lnk.Arguments = Arg;
            lnk.IconLocation = exePath + ",0";
            lnk.Description = "Mandar pro iPhone pelo Orbit";
            lnk.Save();
        }
        catch (Exception ex) { Log.Warning(ex, "Enviar pro iPhone: não criou o atalho do Enviar para"); }
    }

    public static void RemoveShortcut()
    {
        try { if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath); }
        catch (Exception ex) { Log.Warning(ex, "Enviar pro iPhone: não removeu o atalho"); }
    }
}
