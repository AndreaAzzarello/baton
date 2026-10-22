using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Baton.App.Mvvm;

public abstract class ObservableObject : INotifyPropertyChanged
{
    // Every live view model, so a language change can refresh texts computed in code (weak: closed windows are not kept).
    private static readonly List<WeakReference<ObservableObject>> Live = [];

    protected ObservableObject()
    {
        lock (Live)
        {
            if (Live.Count > 0 && Live.Count % 4096 == 0)
                Live.RemoveAll(r => !r.TryGetTarget(out _));
            Live.Add(new WeakReference<ObservableObject>(this));
        }
    }

    /// <summary>Refreshes every binding of every live view model (after a language change).</summary>
    public static void RefreshAll()
    {
        List<ObservableObject> alive;
        lock (Live)
        {
            Live.RemoveAll(r => !r.TryGetTarget(out _));
            alive = [.. Live.Select(r => r.TryGetTarget(out var o) ? o : null).OfType<ObservableObject>()];
        }
        foreach (var item in alive)
            item.OnPropertyChanged(string.Empty);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>An empty name refreshes every binding to this object.</summary>
    protected void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
        : this(_ => execute(), canExecute is null ? null : _ => canExecute())
    {
    }

    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;

    public void Execute(object? parameter) => execute(parameter);
}
