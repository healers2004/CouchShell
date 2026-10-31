using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CouchShell.Platform;
using CouchShell.Services;
using CouchShell.ViewModels;

namespace CouchShell.Views;

public partial class MainWindow : Window
{
    private readonly ControllerService _controller;
    private readonly DispatcherTimer _appCheckTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly OverlayViewModel _overlay = new();
    private InputOverlayWindow? _overlayWindow;
    private readonly UiSounds _sounds = new();
    private bool _isUserMovingFocus;  // only a person moving focus gets a tick, not a program
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();

        Loaded += (_, _) =>
        {
            FocusTile(null);
            // Skipped along with other animations, if Windows has them turned off.
            if (SystemParameters.ClientAreaAnimation) PlaySound(UiSound.Startup);
            else Splash.Visibility = Visibility.Collapsed;
        };
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewGotKeyboardFocus += (_, e) =>
        {
            if (_isUserMovingFocus) PlaySound(UiSound.Move);
            if (e.NewFocus is Button { DataContext: TileViewModel tile }) ViewModel.OnTileFocused(tile);
            if (e.NewFocus is Button { DataContext: SearchKey }) ViewModel.OnSearchKeyFocused();
            // Only a person moving focus: when a screen rebuilds, Windows briefly moves focus
            // elsewhere by itself, and that mustn't switch screens.
            if (_isUserMovingFocus && e.NewFocus is Button { DataContext: TabViewModel tab }) ViewModel.ShowScreen(tab.Name);
        };

        _controller = new ControllerService(Dispatcher);
        _controller.ActionPressed += OnControllerAction;
        _controller.Start();

        UpdateClock();
        _appCheckTimer.Tick += async (_, _) =>
        {
            UpdateClock();
            await ViewModel.CheckRunningAppAsync();
        };
        _appCheckTimer.Start();

        DataContextChanged += (_, _) =>
        {
            ViewModel.FocusRequested += FocusTile;
            ViewModel.WebControlChanged += OnWebControlChanged;
        };
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _appCheckTimer.Stop();
            _controller.Dispose();
            _overlayWindow?.Close();
            _sounds.Dispose();
        };
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void OnClosing(object? sender, CancelEventArgs e) => _isClosing = true;

    // A web app started or ended: hand the controller over, and show or hide the overlay.
    private void OnWebControlChanged(WebControlStyle? style)
    {
        if (style is not WebControlStyle webStyle)
        {
            _controller.SetWebSession(null);
            _overlay.IsHintVisible = false;
            _overlay.IsKeyboardVisible = false;
            UpdateOverlayVisibility();
            return;
        }

        _controller.SetWebSession(new WebInputSession(webStyle, ViewModel.PointerSpeed, Dispatcher, OnWebCommand));
        _overlay.ResetSelection();
        _overlay.ShowHintFor(webStyle, ViewModel.CanLeaveRunningApp);
        UpdateOverlayVisibility();
    }

    private void OnWebCommand(InputCommand command)
    {
        switch (command)
        {
            case InputCommand.SetKeyboardOpen open:
                _overlay.IsKeyboardVisible = open.Open;
                UpdateOverlayVisibility();
                break;
            case InputCommand.KeyboardMove move:
                PlaySound(UiSound.Move);
                _overlay.Move(move.Direction);
                break;
            case InputCommand.KeyboardPress:
                PlaySound(UiSound.Select);
                PressKey(_overlay.SelectedKey);
                break;
            case InputCommand.ToggleControlsBar:
                _overlay.IsHintVisible = !_overlay.IsHintVisible;
                UpdateOverlayVisibility();
                break;
            case InputCommand.KeyboardShift:
                _overlay.ToggleShift();
                break;
            case InputCommand.Volume volume:
                ViewModel.ChangeVolume(volume.Action);
                break;
            case InputCommand.CloseApp:
                ViewModel.CloseRunningApp();
                break;
            case InputCommand.LeaveRunning:
                ViewModel.LeaveRunningApp();
                break;
        }
    }

    private void PressKey(KeyViewModel key)
    {
        switch (key.Kind)
        {
            case KeyKind.Character:
                WindowsNative.TypeCharacter(key.Character);
                break;
            case KeyKind.Text:
                foreach (char character in key.Label) WindowsNative.TypeCharacter(character);
                break;
            case KeyKind.Shift:
                _overlay.ToggleShift();
                break;
            case KeyKind.Space:
                WindowsNative.TapKey(WebInputMapper.KeySpace);
                break;
            case KeyKind.Backspace:
                WindowsNative.TapKey(WebInputMapper.KeyBackspace);
                break;
            case KeyKind.Enter:
                WindowsNative.TapKey(WebInputMapper.KeyEnter);
                break;
        }
    }

    private void UpdateOverlayVisibility()
    {
        if (_overlay.IsHintVisible || _overlay.IsKeyboardVisible)
        {
            _overlayWindow ??= new InputOverlayWindow { DataContext = _overlay };
            _overlayWindow.Show();
        }
        else
        {
            _overlayWindow?.Hide();
        }
    }

    private void PlaySound(UiSound sound)
    {
        if (ViewModel.SoundPack != MainViewModel.SoundsOff) _sounds.Play(sound, ViewModel.SoundPack);
    }

    private void UpdateClock() => Clock.Text = DateTime.Now.ToString("t", CultureInfo.CurrentCulture);

    // A focused tile scrolls its whole row into view, so the row's title stays visible. A row too
    // tall for the screen falls through to WPF's default, which scrolls to just the tile.
    private void OnTileBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        DependencyObject? current = e.TargetObject;
        while (current != null && current is not StackPanel { DataContext: TileRow })
        {
            current = VisualTreeHelper.GetParent(current);
        }
        // The target is the row itself when this is the request made just below.
        if (current is StackPanel row && !ReferenceEquals(row, e.TargetObject) && row.ActualHeight <= TileScroller.ViewportHeight)
        {
            e.Handled = true;
            row.BringIntoView();
        }
    }

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
        else if (!e.IsRepeat && e.Key == Key.Enter)
        {
            PlaySound(UiSound.Select);
        }
        else if (!e.IsRepeat && e.Key == Key.Escape)
        {
            PlaySound(UiSound.Back);
        }
    }

    // WPF's directional focus picks the nearest element in that direction.
    private void MoveFocusFromCurrent(FocusNavigationDirection direction)
    {
        if (Keyboard.FocusedElement is UIElement focused)
        {
            // Going up from the tiles lands on the open screen's tab, rather than whichever tab
            // happens to be nearest, which would switch screens.
            if (direction == FocusNavigationDirection.Up && focused is Button { DataContext: TileViewModel } &&
                focused.PredictFocus(direction) is Button { DataContext: TabViewModel })
            {
                PlaySound(UiSound.Move);
                FocusTile("tab:" + ViewModel.CurrentScreen);
                return;
            }
            _isUserMovingFocus = true;
            focused.MoveFocus(new TraversalRequest(direction));
            _isUserMovingFocus = false;
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
                PlaySound(UiSound.Select);
                // Same as Enter: run the focused button's command.
                if (Keyboard.FocusedElement is Button { Command: ICommand command } button &&
                    command.CanExecute(button.CommandParameter))
                {
                    command.Execute(button.CommandParameter);
                }
                break;
            case NavAction.Back:
                PlaySound(UiSound.Back);
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
            target ??= buttons.Find(button => button.Tag is string tag && !tag.StartsWith("tab:") && !tag.StartsWith("confirm:") && !tag.StartsWith("key:"));
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
