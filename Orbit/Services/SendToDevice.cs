using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using Serilog;

namespace Orbit.Services;

// Botão direito → Enviar para → "Dispositivo (Orbit)" chama `Orbit.exe --send-device <arquivos…>`.
// Esse processo curto repassa os caminhos pro Orbit aberto por um pipe só do usuário atual e sai.
public static class SendToDevice
{
    public const string Arg = "--send-device";
    public const string PipeName = "Orbit2SendToDevice";
    public const string ShortcutName = "Dispositivo (Orbit).lnk";

    public static string ShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), ShortcutName);

    // Atalho da versão anterior (só iPhone): sai quando o novo é criado/removido.
    private static string OldShortcutPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.SendTo), "iPhone (Orbit).lnk");

    // Só arquivos que existem (pasta não vai: o dispositivo recebe arquivo).
    public static string[] FilesFrom(IEnumerable<string> args) =>
        args.Where(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a)).Distinct().ToArray();

    // Lado do processo curto. Nunca lança: sem Orbit aberto, só desiste.
    public static void SendToRunning(string[] files)
    {
        if (files.Length > 0) UserPipe.Send(PipeName, JsonSerializer.Serialize(files));
    }

    // Lado do Orbit aberto: chama onFiles (thread do pool) a cada pedido.
    public static void Listen(Action<string[]> onFiles, CancellationToken ct) =>
        UserPipe.Listen(PipeName, text => onFiles(FilesFrom(JsonSerializer.Deserialize<string[]>(text) ?? Array.Empty<string>())), ct);

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
            lnk.Description = "Mandar pro dispositivo pelo Orbit";
            lnk.Save();
            if (File.Exists(OldShortcutPath)) File.Delete(OldShortcutPath);
        }
        catch (Exception ex) { Log.Warning(ex, "Enviar pro dispositivo: não criou o atalho do Enviar para"); }
    }

    public static void RemoveShortcut()
    {
        try
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            if (File.Exists(OldShortcutPath)) File.Delete(OldShortcutPath);
        }
        catch (Exception ex) { Log.Warning(ex, "Enviar pro dispositivo: não removeu o atalho"); }
    }
}
