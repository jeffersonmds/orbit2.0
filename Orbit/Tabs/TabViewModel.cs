using CommunityToolkit.Mvvm.ComponentModel;

namespace Orbit.Tabs;

// Base das abas do painel. Só a aba visível do painel expandido fica ativa:
// SetActive(true) liga timers/fetches, SetActive(false) desliga (contrato de leveza).
public abstract class TabViewModel : ObservableObject
{
    public abstract string Id { get; }
    public abstract string Title { get; }
    public string Icon => IconFor(Id) ?? "";

    // Glifo da Segoe Fluent por aba (trilho do painel e ícone dos alertas). null = sem ícone.
    public static string? IconFor(string? id) => id switch
    {
        "controles" => "",
        "hoje" => "",
        "stats" => "",
        "clipboard" => "",
        "notas" => "",
        "apps" => "",
        "mercado" => "",
        "noticias" => "",
        "claude" => "",
        "dispositivos" => "",
        "energia" => "",
        "ajustes" => "",
        _ => null,
    };

    public virtual void SetActive(bool active) { }
}
