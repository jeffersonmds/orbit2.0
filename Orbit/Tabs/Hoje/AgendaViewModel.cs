using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Models;
using Orbit.Services;

namespace Orbit.Tabs.Hoje;

// Agenda dos links iCal de Ajustes. Roda com a ilha recolhida (o aviso de 5 min não pode depender da aba
// aberta): busca a cada 15 min e só com link configurado; o aviso é um timer único até o próximo evento.
public sealed partial class AgendaViewModel : ObservableObject
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(15), Before = TimeSpan.FromMinutes(5);
    private const int MaxShown = 4;

    private readonly ApiService _api;
    private readonly OrbitSettings _settings;
    private readonly DispatcherTimer _fetch = new() { Interval = Every };
    private readonly DispatcherTimer _remind = new();
    private readonly HashSet<string> _alerted = new();
    private List<AgendaEvent> _events = new();
    private string _urlsAt = "";
    private DateTime? _fetchedAt;
    private bool _fetching;

    public ObservableCollection<AgendaEvent> Today { get; } = new();
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private string? _empty;
    [ObservableProperty] private string _heading = "Agenda";

    // Dia mostrado (semana da aba Hoje); volta pra hoje a cada abertura.
    public DateTime Day { get; private set; } = DateTime.Today;
    public HashSet<DateTime> Days { get; private set; } = new();

    // Lista ou dias com evento mudaram (a semana redesenha os pontinhos).
    public event Action? Changed;

    // Evento começando em 5 min (só com o aviso ligado em Ajustes).
    public event Action<AgendaEvent>? Reminder;

    public AgendaViewModel(ApiService api, OrbitSettings settings)
    {
        _api = api;
        _settings = settings;
        _fetch.Tick += async (_, _) => await Refresh();
        _remind.Tick += (_, _) => { _remind.Stop(); Fire(); };
    }

    [RelayCommand]
    private void Join(AgendaEvent e)
    {
        if (e.JoinUrl is { } url) try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
    }

    public void Start()
    {
        _fetch.Start();
        _ = Refresh();
    }

    // Aba Hoje aberta: link novo/removido em Ajustes ou dado velho busca de novo; senão só tira o que já acabou.
    public void Touch()
    {
        Day = DateTime.Today;
        if (UrlsKey() != _urlsAt || Freshness.IsStale(_fetchedAt, Every, DateTime.Now)) _ = Refresh();
        else Publish();
    }

    public void Show(DateTime day)
    {
        Day = day.Date;
        Publish();
    }

    private List<string> Urls() => _settings.CalendarUrls.Select(Agenda.NormalizeUrl).OfType<string>().Distinct().ToList();
    private string UrlsKey() => string.Join("\n", Urls());

    private async Task Refresh()
    {
        var urls = Urls();
        Enabled = urls.Count > 0;
        if (_fetching) return;
        if (!Enabled)
        {
            _events = new();
            _urlsAt = "";
            Status = null;
            Publish();
            Plan();
            return;
        }
        _fetching = true;
        try
        {
            // Semana inteira (de segunda) pros pontinhos; no domingo vai até amanhã pro aviso de segunda cedo.
            var from = WeekStart(DateTime.Today);
            var to = new[] { from.AddDays(7), DateTime.Today.AddDays(2) }.Max();
            var texts = await Task.WhenAll(urls.Select(_api.GetTextAsync));
            var ok = texts.OfType<string>().ToList();
            // ponytail: com um link falhando, os eventos dele somem até a próxima busca (sem cache por link).
            if (ok.Count > 0)
                _events = await Task.Run(() => ok.SelectMany(t => Agenda.Parse(t, from, to)).OrderBy(e => e.Start).ToList());
            int failed = urls.Count - ok.Count;
            Status = failed == 0 ? null : failed == urls.Count ? "agenda indisponível" : $"{failed} agenda(s) sem resposta";
            _urlsAt = string.Join("\n", urls);
            if (ok.Count > 0) _fetchedAt = DateTime.Now;
        }
        finally { _fetching = false; }
        Publish();
        Plan();
    }

    private void Publish()
    {
        var now = DateTime.Now;
        bool today = Day == now.Date;
        Today.Clear();
        foreach (var e in Agenda.OfDay(_events, Day, now, MaxShown)) Today.Add(e);
        Empty = Today.Count > 0 || _fetchedAt is null ? null : today ? "Nada mais hoje" : "Nada neste dia";
        Heading = today ? "Agenda" : $"Agenda · {HojeViewModel.DayLabel(Day)} {Day.Day}";
        var from = WeekStart(now.Date);
        Days = Agenda.Days(_events, from, from.AddDays(7));
        Changed?.Invoke();
    }

    public static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

    private void Plan()
    {
        _remind.Stop();
        if (Agenda.NextReminder(_events, _alerted, DateTime.Now, Before) is not { } next) return;
        var wait = next.At - DateTime.Now;
        _remind.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
        _remind.Start();
    }

    private void Fire()
    {
        var now = DateTime.Now;
        if (Agenda.NextReminder(_events, _alerted, now, Before) is { } next && next.At <= now.AddSeconds(1))
        {
            _alerted.Add(next.Event.Key);
            if (_settings.CalendarAlertEnabled) Reminder?.Invoke(next.Event);
        }
        Plan();
    }
}
