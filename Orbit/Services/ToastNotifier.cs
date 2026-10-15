using System;
using Microsoft.Toolkit.Uwp.Notifications;
using Serilog;

namespace Orbit.Services;

// Toast nativo do Windows. ToastContentBuilder.Show() não exige atalho no Menu
// Iniciar nem AppUserModelID pra apps Win32 não empacotados (o pacote cuida
// disso). Se falhar mesmo assim (ambiente sem Action Center), só loga.
// Usado por IslandViewModel.Notify quando o alerta vira toast (tela cheia + preferência do usuário).
public static class ToastNotifier
{
    public static void Notify(string title, string message, bool soundEnabled)
    {
        try
        {
            new ToastContentBuilder()
                .AddText(title)
                .AddText(message)
                .AddAudio(new Uri("ms-winsoundevent:Notification.Default"), silent: !soundEnabled)
                .Show();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "ToastNotifier: falha ao mostrar toast");
        }
    }
}
