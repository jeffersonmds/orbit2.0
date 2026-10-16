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
