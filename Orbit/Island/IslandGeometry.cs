using System;

namespace Orbit.Island;

public readonly record struct IslandSize(double Width, double Height);

// Tamanhos em DIPs. A janela é fixa; só a pílula anima até estes tamanhos.
public static class IslandGeometry
{
    public const double WindowWidth = 760;
    public const double WindowHeight = 400;

    public static IslandSize SizeFor(IslandMode mode) => mode switch
    {
        IslandMode.Atividade => new IslandSize(320, 30),
        IslandMode.Alerta => new IslandSize(360, 56),
        IslandMode.Expandida => new IslandSize(720, 380),
        _ => new IslandSize(110, 10), // Repouso e Oculta
    };

    // Raio dos cantos de baixo (os de cima encostam na borda da tela).
    public static double RadiusFor(IslandMode mode) => mode switch
    {
        IslandMode.Expandida => 22,
        IslandMode.Alerta => 18,
        IslandMode.Atividade => 15,
        _ => 5,
    };

    // Índice do monitor da ilha: o salvo se estiver conectado, senão o principal.
    public static int PickScreen(string[] deviceNames, int primaryIndex, string? wanted)
    {
        int i = wanted is null ? -1 : Array.FindIndex(deviceNames, n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));
        return i >= 0 ? i : Math.Max(primaryIndex, 0);
    }

    // Retângulo físico da janela, centralizada no topo do monitor (bounds em pixels físicos;
    // scale = DPI do monitor / 96).
    public static (int X, int Y, int W, int H) PlaceOn(int boundsX, int boundsY, int boundsWidth, double scale)
    {
        int w = (int)Math.Round(WindowWidth * scale), h = (int)Math.Round(WindowHeight * scale);
        return (boundsX + (boundsWidth - w) / 2, boundsY, w, h);
    }
}
