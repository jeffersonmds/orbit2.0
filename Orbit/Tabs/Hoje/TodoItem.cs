using System;
using System.Globalization;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Orbit.Tabs.Hoje;

public sealed partial class TodoItem : ObservableObject
{
    public TodoItem(string text, bool done, DateTime? due = null, bool reminded = false)
    {
        Text = text;
        _done = done;
        _due = due;
        Reminded = reminded;
    }

    public string Text { get; }

    // Lembrete opcional; Reminded = já avisou (avisa uma vez só, mesmo se o PC estava desligado na hora).
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DueLabel))] private DateTime? _due;
    public bool Reminded { get; set; }

    // ponytail: calculado ao desenhar; atravessar a meia-noite com a aba aberta deixa "amanhã" velho até reabrir.
    public string? DueLabel => DueText(Due, DateTime.Today);

    [ObservableProperty] private bool _done;
    // Venceu e não foi feita: horário em laranja e sobe pro topo (HojeViewModel.RefreshOverdue).
    [ObservableProperty] private bool _overdue;

    public bool IsOverdue(DateTime now) => !Done && Due is { } d && d <= now;

    public static string? DueText(DateTime? due, DateTime today) => due switch
    {
        null => null,
        { } d when d.Date == today => d.ToString("HH:mm"),
        { } d when d.Date == today.AddDays(1) => "amanhã " + d.ToString("HH:mm"),
        { } d => d.ToString("dd/MM HH:mm"),
    };

    // Horário no FIM do texto vira lembrete: "ligar 15:30", "ligar às 15h", "15h30", "amanhã 9h", "em 30 min", "em 2h".
    // Exige ":" ou "h" (senão "comprar 2" viraria 2h). Hora que já passou hoje = amanhã.
    private static readonly Regex AtTime = new(@"\s*(?:\bamanh[ãa]\s+)?(?:\b[àa]s\s+)?\b(\d{1,2})(?::(\d{2})|h(\d{2})?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex InTime = new(@"\s*\bem\s+(\d{1,3})\s*(min|minutos?|h|horas?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static (string Text, DateTime? Due) ParseDue(string text, DateTime now)
    {
        text = text.Trim();
        var m = InTime.Match(text);
        if (m.Success && m.Index > 0)
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (n == 0) return (text, null);
            var delta = m.Groups[2].Value.StartsWith("h", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromHours(n) : TimeSpan.FromMinutes(n);
            return (text[..m.Index].Trim(), now + delta);
        }
        m = AtTime.Match(text);
        if (!m.Success || m.Index == 0) return (text, null); // só horário, sem tarefa: fica como texto
        var h = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var minText = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        var min = minText.Length > 0 ? int.Parse(minText, CultureInfo.InvariantCulture) : 0;
        if (h > 23 || min > 59) return (text, null);
        var due = now.Date.AddHours(h).AddMinutes(min);
        var tomorrow = m.Value.Contains("amanh", StringComparison.OrdinalIgnoreCase);
        if (tomorrow || due <= now) due = due.AddDays(1);
        return (text[..m.Index].Trim(), due);
    }
}
