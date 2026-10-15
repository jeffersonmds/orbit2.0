using CommunityToolkit.Mvvm.ComponentModel;

namespace Orbit.Tabs.Hoje;

public sealed partial class TodoItem : ObservableObject
{
    public TodoItem(string text, bool done)
    {
        Text = text;
        _done = done;
    }

    public string Text { get; }

    [ObservableProperty] private bool _done;
}
