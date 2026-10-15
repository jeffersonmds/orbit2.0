using CommunityToolkit.Mvvm.ComponentModel;

namespace Orbit.Tabs;

// Base das abas do painel. Só a aba visível do painel expandido fica ativa:
// SetActive(true) liga timers/fetches, SetActive(false) desliga (contrato de leveza).
public abstract class TabViewModel : ObservableObject
{
    public abstract string Id { get; }
    public abstract string Title { get; }

    public virtual void SetActive(bool active) { }
}
