using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Orbit.Services;

namespace Orbit.Tabs.Hoje;

public sealed record WeekDay(string Label, int Day, bool IsToday, DateTime Date = default, bool HasEvents = false, bool IsSelected = false);

public sealed partial class HojeViewModel : TabViewModel
{
    private static readonly CultureInfo Pt = new("pt-BR");
    private static readonly string[] Labels = { "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb", "Dom" };

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _todosPath;
    // Lembretes das tarefas: confere a cada 30 s, e só com algum lembrete pendente (roda com a ilha recolhida).
    private readonly DispatcherTimer _reminders = new() { Interval = TimeSpan.FromSeconds(30) };

    public event Action<TodoItem>? Reminder;

    public override string Id => "hoje";
    public override string Title => "Hoje";

    public WeatherViewModel Weather { get; }
    public AgendaViewModel Agenda { get; }
    public ObservableCollection<WeekDay> Week { get; } = new();
    public ObservableCollection<TodoItem> Todos { get; } = new();

    [ObservableProperty] private string _time = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _newTodo = "";
    [ObservableProperty] private string _todoCounter = "0/0";

    public HojeViewModel(ApiService api, string todosPath, AgendaViewModel agenda)
    {
        Agenda = agenda;
        Weather = new WeatherViewModel(api);
        _todosPath = todosPath;
        foreach (var t in TodoStore.Load(todosPath)) Todos.Add(t);
        RefreshOverdue();
        agenda.Tasks = TasksOf;
        TodoCounter = Counter(Todos);
        _reminders.Tick += (_, _) => FireReminders();
        _reminders.IsEnabled = Todos.Any(Pending); // venceu com o PC desligado: avisa no 1º tick (já com quem ouve inscrito)
        _clock.Tick += (_, _) => UpdateClock();
        agenda.Changed += RebuildWeek;
    }

    public override void SetActive(bool active)
    {
        Weather.SetActive(active);
        if (!active)
        {
            _clock.Stop();
            return;
        }
        UpdateClock();
        Agenda.Touch();
        RebuildWeek();
        _clock.Start();
    }

    private void RebuildWeek()
    {
        Week.Clear();
        foreach (var d in BuildWeek(DateTime.Today, Agenda.Day, Agenda.Days)) Week.Add(d);
    }

    // Clique num dia da semana mostra a agenda dele (sem agenda configurada não faz nada).
    [RelayCommand]
    private void SelectDay(WeekDay d)
    {
        if (Agenda.Enabled) Agenda.Show(d.Date);
    }

    public static string DayLabel(DateTime d) => Labels[((int)d.DayOfWeek + 6) % 7];

    private void UpdateClock()
    {
        var now = DateTime.Now;
        Time = now.ToString("HH:mm", Pt);
        var date = now.ToString("dddd, d 'de' MMMM", Pt); // "domingo, 15 de novembro" → só a 1ª maiúscula
        Date = char.ToUpper(date[0], Pt) + date[1..];
    }

    [RelayCommand]
    private void AddTodo()
    {
        var text = NewTodo.Trim();
        if (text.Length == 0) return;
        var (task, due) = TodoItem.ParseDue(text, DateTime.Now);
        Todos.Add(new TodoItem(task, false, due));
        NewTodo = "";
        Persist();
    }

    [RelayCommand]
    private void ToggleTodo(TodoItem item)
    {
        item.Done = !item.Done;
        Persist();
    }

    [RelayCommand]
    private void RemoveTodo(TodoItem item)
    {
        Todos.Remove(item);
        Persist();
    }

    private void Persist()
    {
        RefreshOverdue();
        TodoStore.Save(Todos, _todosPath);
        TodoCounter = Counter(Todos);
        Agenda.Republish();
        _reminders.IsEnabled = Todos.Any(Pending);
    }

    private void FireReminders()
    {
        var due = DueNow(Todos, DateTime.Now);
        foreach (var t in due)
        {
            t.Reminded = true;
            Reminder?.Invoke(t);
        }
        if (due.Count > 0) Persist();
        else _reminders.IsEnabled = Todos.Any(Pending);
    }

    // Vencidas e abertas vão pro topo (ordem relativa mantida). Só muda quando um lembrete vence ou a lista muda.
    private void RefreshOverdue()
    {
        var now = DateTime.Now;
        foreach (var t in Todos) t.Overdue = t.IsOverdue(now);
        var ordered = Ordered(Todos, now);
        for (int i = 0; i < ordered.Count; i++)
        {
            var from = Todos.IndexOf(ordered[i]);
            if (from != i) Todos.Move(from, i);
        }
    }

    public static List<TodoItem> Ordered(IEnumerable<TodoItem> items, DateTime now) =>
        items.OrderBy(t => !t.IsOverdue(now)).ToList();

    // Tarefas abertas com horário no dia, como entradas da agenda.
    private IEnumerable<AgendaEvent> TasksOf(DateTime day) => TasksOf(Todos, day, DateTime.Now);

    public static List<AgendaEvent> TasksOf(IEnumerable<TodoItem> items, DateTime day, DateTime now) =>
        items.Where(t => !t.Done && t.Due?.Date == day.Date)
            .Select(t => new AgendaEvent(t.Text, t.Due!.Value, t.Due.Value, false, null) { IsTask = true, Late = t.Due <= now })
            .ToList();

    private static bool Pending(TodoItem t) => t.Due is not null && !t.Done && !t.Reminded;

    public static List<TodoItem> DueNow(IEnumerable<TodoItem> items, DateTime now) =>
        items.Where(t => Pending(t) && t.Due <= now).ToList();

    // Botão "+10 min" do alerta: volta a avisar daqui a 10 min.
    public void Snooze(TodoItem item, TimeSpan delay)
    {
        if (item.Done || !Todos.Contains(item)) return;
        item.Due = DateTime.Now + delay;
        item.Reminded = false;
        Persist();
    }

    // Botão "Feito" do alerta.
    public void MarkDone(TodoItem item)
    {
        if (item.Done || !Todos.Contains(item)) return;
        item.Done = true;
        Persist();
    }

    // Semana de segunda a domingo que contém `today`; `selected` só marca se não for hoje (hoje já tem destaque).
    public static List<WeekDay> BuildWeek(DateTime today, DateTime? selected = null, ISet<DateTime>? eventDays = null)
    {
        var monday = AgendaViewModel.WeekStart(today);
        return Enumerable.Range(0, 7)
            .Select(i => monday.AddDays(i))
            .Select((d, i) => new WeekDay(Labels[i], d.Day, d == today.Date, d,
                eventDays?.Contains(d) == true, d == selected?.Date && d != today.Date))
            .ToList();
    }

    public static string Counter(IEnumerable<TodoItem> items)
    {
        var list = items.ToList();
        return $"{list.Count(i => i.Done)}/{list.Count}";
    }
}
