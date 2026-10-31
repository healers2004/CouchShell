using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CouchShell.Services;
using CouchShell.ViewModels;

namespace CouchShell.Views;

public partial class MainWindow : Window
{
    private readonly ControllerService _controller;
    private readonly DispatcherTimer _appCheckTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) => FocusTile(null);
        PreviewKeyDown += OnPreviewKeyDown;

        _controller = new ControllerService(Dispatcher);
        _controller.ActionPressed += OnControllerAction;
        _controller.Start();

        _appCheckTimer.Tick += async (_, _) => await ViewModel.CheckRunningAppAsync();
        _appCheckTimer.Start();

        DataContextChanged += (_, _) => ViewModel.FocusRequested += FocusTile;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _appCheckTimer.Stop();
            _controller.Dispose();
        };
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnClosing(object? sender, CancelEventArgs e) => _isClosing = true;

    // Handled here rather than left to WPF, so arrows take exactly the same path as the controller.
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        FocusNavigationDirection? direction = e.Key switch
        {
            Key.Up => FocusNavigationDirection.Up,
            Key.Down => FocusNavigationDirection.Down,
            Key.Left => FocusNavigationDirection.Left,
            Key.Right => FocusNavigationDirection.Right,
            _ => null,
        };
        if (direction is FocusNavigationDirection move)
        {
            MoveFocusFromCurrent(move);
            e.Handled = true;
        }
    }

    // WPF's directional focus picks the nearest element in that direction.
    private void MoveFocusFromCurrent(FocusNavigationDirection direction)
    {
        if (Keyboard.FocusedElement is UIElement focused)
        {
            focused.MoveFocus(new TraversalRequest(direction));
        }
        else
        {
            FocusTile(null);
        }
    }

    private void OnControllerAction(NavAction action)
    {
        if (!IsActive)
        {
            // Hidden means an app is in front, so leave it alone. Visible but not active means
            // Windows refused an earlier focus change: a controller press is a good reason to retry.
            if (IsVisible && !_isClosing) new WindowService(this).RestoreAndFocus();
            return;
        }

        switch (action)
        {
            case NavAction.Up:
                MoveFocusFromCurrent(FocusNavigationDirection.Up);
                break;
            case NavAction.Down:
                MoveFocusFromCurrent(FocusNavigationDirection.Down);
                break;
            case NavAction.Left:
                MoveFocusFromCurrent(FocusNavigationDirection.Left);
                break;
            case NavAction.Right:
                MoveFocusFromCurrent(FocusNavigationDirection.Right);
                break;
            case NavAction.Select:
                // Same as Enter: run the focused button's command.
                if (Keyboard.FocusedElement is Button { Command: ICommand command } button &&
                    command.CanExecute(button.CommandParameter))
                {
                    command.Execute(button.CommandParameter);
                }
                break;
            case NavAction.Back:
                ViewModel.BackCommand.Execute(null);  // same as Escape
                break;
            case NavAction.VolumeDown:
                ViewModel.VolumeDownCommand.Execute(null);
                break;
            case NavAction.VolumeUp:
                ViewModel.VolumeUpCommand.Execute(null);
                break;
            case NavAction.Mute:
                ViewModel.MuteCommand.Execute(null);
                break;
        }
    }

    // Focuses the button tagged `key`. Falls back to the first tile, then the open screen's tab.
    // Queued at low priority, because new tiles don't exist until WPF has laid them out.
    private void FocusTile(string? key)
    {
        Dispatcher.InvokeAsync(() =>
        {
            List<Button> buttons = FindButtons(this).ToList();
            Button? target = key == null ? null : buttons.Find(button => Equals(button.Tag, key));
            target ??= buttons.Find(button => button.Tag is string tag && !tag.StartsWith("tab:") && !tag.StartsWith("confirm:"));
            target ??= buttons.Find(button => Equals(button.Tag, "tab:" + ViewModel.CurrentScreen));
            target?.Focus();
        }, DispatcherPriority.Background);
    }

    private static IEnumerable<Button> FindButtons(DependencyObject parent)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button) yield return button;
            foreach (Button nested in FindButtons(child)) yield return nested;
        }
    }
}
