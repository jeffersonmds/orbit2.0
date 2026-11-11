using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace Orbit.Services;

// Lado cliente: roda dentro de `Orbit.exe --claude-hook`, um processo curto
// disparado pelo próprio Claude Code a cada hook. Nunca pode lançar nem travar —
// se o Orbit não estiver aberto, ou o pipe não responder, só desiste em silêncio.
public static class ClaudeHookClient
{
    // Campo que o Orbit acrescenta ao JSON do hook: o processo com janela que hospeda o Claude.
    public const string HostPidField = "orbit_host_pid";

    public static void SendFromStdin()
    {
        try
        {
            // ponytail: assume stdin sempre redirecionado pelo hook runner do Claude Code;
            // uma chamada manual/interativa travaria aqui.
            string json = AddHost(Console.In.ReadToEnd(), out bool permission);
            using var client = new NamedPipeClientStream(".", ClaudeHookServer.PipeName, PipeDirection.InOut);
            // 2 s: com eventos colados (Stop + Notification) 300 ms perdia o 2º.
            client.Connect(2000);
            var writer = new StreamWriter(client); // sem using: fecharia o pipe antes da resposta
            writer.WriteLine(json);
            writer.Flush();
            if (!permission) return;
            // Pedido de permissão: espera a ilha (ou o "sem decisão" quando você foca o terminal). O timeout
            // do hook no Claude (600 s) mata este processo se ninguém responder.
            string? answer = new StreamReader(client).ReadLine();
            if (!string.IsNullOrEmpty(answer))
            {
                Console.Out.Write(answer);
                Console.Out.Flush();
            }
        }
        catch
        {
            // Sem Orbit rodando, timeout, pipe ocupado etc — o hook do Claude Code
            // não pode falhar/travar por causa disso. Sem saída = o Claude segue o fluxo normal.
        }
    }

    // Acrescenta o pid do terminal/editor ao JSON (o Claude não manda) e devolve numa linha só (o pipe
    // separa por linha). Sem achar, manda como veio.
    private static string AddHost(string json, out bool permission)
    {
        permission = false;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject obj) return json.ReplaceLineEndings(" ");
            permission = obj["hook_event_name"]?.GetValue<string>() == "PermissionRequest";
            if (HostPid() is { } host) obj[HostPidField] = host;
            return obj.ToJsonString();
        }
        catch { return json.ReplaceLineEndings(" "); }
    }

    // Sobe pelos processos pais (hook ← claude ← shell ← terminal) até o 1º com janela visível.
    private static int? HostPid()
    {
        var parents = ParentMap();
        return FirstWithWindow(Environment.ProcessId, parents, pid =>
        {
            try { using var p = Process.GetProcessById(pid); return p.MainWindowHandle != IntPtr.Zero; }
            catch { return false; }
        });
    }

    // Puro: primeiro ancestral (até 10 níveis) que tem janela.
    public static int? FirstWithWindow(int start, IReadOnlyDictionary<int, int> parents, Func<int, bool> hasWindow)
    {
        var cur = start;
        for (int i = 0; i < 10 && parents.TryGetValue(cur, out var parent) && parent != 0 && parent != cur; i++)
        {
            if (hasWindow(parent)) return parent;
            cur = parent;
        }
        return null;
    }

    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snap = CreateToolhelp32Snapshot(0x2, 0); // TH32CS_SNAPPROCESS
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            for (bool ok = Process32First(snap, ref e); ok; ok = Process32Next(snap, ref e))
                map[(int)e.th32ProcessID] = (int)e.th32ParentProcessID;
        }
        finally { CloseHandle(snap); }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32First(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32Next(IntPtr snap, ref PROCESSENTRY32 e);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
