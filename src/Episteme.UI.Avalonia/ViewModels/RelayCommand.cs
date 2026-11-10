using System.Windows.Input;

namespace Episteme.UI.Avalonia.ViewModels;

/// <summary>ICommand with async execution and re-entry protection; errors surface via OnError.</summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<object?, Task> _run;
    private bool _busy;

    public Action<Exception>? OnError { get; set; }
    public event EventHandler? CanExecuteChanged;

    public AsyncRelayCommand(Func<object?, Task> run) => _run = run;
    public AsyncRelayCommand(Func<Task> run) : this(_ => run()) { }

    public bool CanExecute(object? parameter) => !_busy;

    public async void Execute(object? parameter)
    {
        if (_busy) return;
        _busy = true;
        CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await _run(parameter); }
        catch (Exception ex) { OnError?.Invoke(ex); }
        finally
        {
            _busy = false;
            CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
