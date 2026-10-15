using System;

namespace Orbit.Services;

// Portão de frescor das abas com rede: ao abrir a aba, só busca se o último dado bom
// tiver mais que maxAge. Abrir/fechar a ilha várias vezes não martela as APIs.
public static class Freshness
{
    public static bool IsStale(DateTime? lastOk, TimeSpan maxAge, DateTime now) =>
        lastOk is null || now - lastOk.Value >= maxAge;
}
