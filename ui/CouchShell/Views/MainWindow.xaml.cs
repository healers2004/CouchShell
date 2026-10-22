using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CouchShell.Services;
using CouchShell.ViewModels;

namespace CouchShell.Views;

public partial class MainWindow : Window
{
    private readonly ControllerService _controller;

    public MainWindow()
    {
        InitializeComponent();
        // Put keyboard focus on the first tab at startup, so arrow keys work without pressing Tab first.
        Loaded += (_, _) => MoveFocus(new TraversalRequest(FocusNavigationDirection.First));

        _controller = new ControllerService(Dispatcher);
        _controller.ActionPressed += OnControllerAction;
        _controller.Start();
        Closed += (_, _) => _controller.Dispose();
    }

    // The controller moves the same keyboard focus the arrow keys do, so both behave the same:
    // WPF's directional focus picks the nearest element in that direction.
    private void OnControllerAction(NavAction action)
    {
        // Ignore the controller while another app is in front (e.g. a game, from Phase 5).
        if (!IsActive) return;

        if (Keyboard.FocusedElement is not UIElement focused)
        {
            MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            return;
        }

        switch (action)
        {
            case NavAction.Up:
                focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Up));
                break;
            case NavAction.Down:
                focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Down));
                break;
            case NavAction.Left:
                focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Left));
                break;
            case NavAction.Right:
                focused.MoveFocus(new TraversalRequest(FocusNavigationDirection.Right));
                break;
            case NavAction.Select:
                // Same as Enter: run the focused button's command.
                if (focused is Button { Command: ICommand command } button && command.CanExecute(button.CommandParameter))
                {
                    command.Execute(button.CommandParameter);
                }
                break;
            case NavAction.Back:
                // Same as Escape.
                ((MainViewModel)DataContext).GoHomeCommand.Execute(null);
                break;
        }
    }
}
