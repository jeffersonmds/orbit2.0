namespace Orbit.Tabs.Foco;

// Aba Foco: só exibe o Pomodoro e os timers, que são serviços de fundo (rodam com a ilha recolhida
// e são criados pelo IslandViewModel). Nada a ligar/desligar aqui além de atualizar o "hoje".
public sealed class FocoViewModel : TabViewModel
{
    public override string Id => "foco";
    public override string Title => "Foco";

    public PomodoroViewModel Pomodoro { get; }
    public TimersViewModel Timer { get; }

    public FocoViewModel(PomodoroViewModel pomodoro, TimersViewModel timer)
    {
        Pomodoro = pomodoro;
        Timer = timer;
    }

    public override void SetActive(bool active)
    {
        if (active) Pomodoro.RefreshToday();
    }
}
