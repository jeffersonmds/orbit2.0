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

public sealed record WeekDay(string Label, int Day, bool IsToday);

public sealed partial class HojeViewModel : TabViewModel
{
    private static readonly CultureInfo Pt = new("pt-BR");
    private static readonly string[] Labels = { "Seg", "Ter", "Qua", "Qui", "Sex", "Sáb", "Dom" };

    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly string _todosPath;

    public override string Id => "hoje";
    public override string Title => "Hoje";

    public WeatherViewModel Weather { get; }
    public PomodoroViewModel Pomodoro { get; }
    public ObservableCollection<WeekDay> Week { get; } = new();
    public ObservableCollection<TodoItem> Todos { get; } = new();

    [ObservableProperty] private string _time = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private string _newTodo = "";
    [ObservableProperty] private string _todoCounter = "0/0";

    public HojeViewModel(ApiService api, PomodoroViewModel pomodoro, string todosPath)
    {
        Weather = new WeatherViewModel(api);
        Pomodoro = pomodoro;
        _todosPath = todosPath;
        foreach (var t in TodoStore.Load(todosPath)) Todos.Add(t);
        TodoCounter = Counter(Todos);
        _clock.Tick += (_, _) => UpdateClock();
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
        Week.Clear();
        foreach (var d in BuildWeek(DateTime.Today)) Week.Add(d);
        _clock.Start();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        Time = now.ToString("HH:mm", Pt);
        Date = Pt.TextInfo.ToTitleCase(now.ToString("dddd, d 'de' MMMM", Pt));
    }

    [RelayCommand]
    private void AddTodo()
    {
        var text = NewTodo.Trim();
        if (text.Length == 0) return;
        Todos.Add(new TodoItem(text, false));
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
        TodoStore.Save(Todos, _todosPath);
        TodoCounter = Counter(Todos);
    }

    // Semana de segunda a domingo que contém `today`.
    public static List<WeekDay> BuildWeek(DateTime today)
    {
        int offset = ((int)today.DayOfWeek + 6) % 7; // segunda = 0
        var monday = today.Date.AddDays(-offset);
        return Enumerable.Range(0, 7)
            .Select(i => monday.AddDays(i))
            .Select((d, i) => new WeekDay(Labels[i], d.Day, d == today.Date))
            .ToList();
    }

    public static string Counter(IEnumerable<TodoItem> items)
    {
        var list = items.ToList();
        return $"{list.Count(i => i.Done)}/{list.Count}";
    }
}
