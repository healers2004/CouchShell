using System.Windows.Input;

namespace CouchShell.ViewModels;

// Minimal ICommand: lets XAML buttons and key bindings call a view-model method.
public class RelayCommand(Action<object?> execute) : ICommand
{
    // Commands are always enabled for now, so CanExecuteChanged never fires.
    public event EventHandler? CanExecuteChanged { add { } remove { } }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
