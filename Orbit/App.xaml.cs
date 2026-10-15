using System;
using System.IO;
using System.Threading;
using System.Windows;
using Orbit.Island;
using Orbit.Services;
using Serilog;

namespace Orbit;

public partial class App : Application
{
    private Mutex? _mutex;
    private IslandWindow? _island;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Instância única ANTES do log: a 2ª instância não pode escrever no log da 1ª.
        var mutex = new Mutex(initiallyOwned: true, "Orbit2_SingleInstance", out bool isNew);
        if (!isNew)
        {
            mutex.Dispose();
            Shutdown();
            return;
        }
        _mutex = mutex;

        Directory.CreateDirectory(SettingsStore.LogsDir);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(SettingsStore.LogsDir, "orbit-.txt"),
                rollingInterval: RollingInterval.Hour,
                retainedFileCountLimit: 168,
                outputTemplate: "{Timestamp:HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        // Loga e mantém a ilha viva (spec §8).
        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error(args.Exception, "Exceção não tratada no dispatcher");
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Log.Fatal(args.ExceptionObject as Exception, "Exceção fatal não tratada");

        Log.Information("Orbit 2 iniciado");
        try
        {
            var settings = SettingsStore.Load();
            Accent.Start(() => settings.AccentOverride);
            _island = new IslandWindow(new IslandViewModel(settings));
            _island.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Falha no startup");
            _island?.ReleaseResources();
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _island?.ReleaseResources();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        Log.Information("Orbit 2 encerrando");
        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
