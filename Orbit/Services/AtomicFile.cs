using System.IO;

namespace Orbit.Services;

// Grava num .tmp ao lado e troca de uma vez: cair a energia no meio não deixa o arquivo pela metade.
public static class AtomicFile
{
    public static void WriteAllText(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, overwrite: true);
    }
}
