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

    public static double LeftFor(double screenWidth) => Math.Round((screenWidth - WindowWidth) / 2);
}
