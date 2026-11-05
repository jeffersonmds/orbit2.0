using System;
using System.Threading.Tasks;

namespace Orbit.Services;

// Portão de frescor das abas com rede: ao abrir a aba, só busca se o último dado bom
// tiver mais que maxAge. Abrir/fechar a ilha várias vezes não martela as APIs.
public static class Freshness
{
    public static bool IsStale(DateTime? lastOk, TimeSpan maxAge, DateTime now) =>
        lastOk is null || now - lastOk.Value >= maxAge;

    // "Atualizado há 3 min" / "Sem conexão · atualizado há 2 h": dado velho tem que parecer velho.
    public static string Ago(DateTime? lastOk, DateTime now, bool failed)
    {
        if (lastOk is null) return failed ? "Sem conexão" : "Atualizando…";
        var age = now - lastOk.Value;
        var when = age < TimeSpan.FromMinutes(1) ? "agora"
            : age < TimeSpan.FromHours(1) ? $"há {(int)age.TotalMinutes} min"
            : age < TimeSpan.FromDays(1) ? $"há {(int)age.TotalHours} h"
            : $"em {lastOk:dd/MM HH:mm}";
        return (failed ? "Sem conexão · atualizado " : "Atualizado ") + when;
    }

    // Uma busca por vez: abrir/recolher a ilha antes da resposta reaproveita a que está em voo.
    public static Task Once(ref Task? inFlight, Func<Task> start) =>
        inFlight is { IsCompleted: false } ? inFlight : inFlight = start();
}
