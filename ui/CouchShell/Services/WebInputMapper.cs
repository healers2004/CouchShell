namespace CouchShell.Services;

// How the controller drives a web app. Pointer works on any site. Keys suits sites built for TVs,
// which navigate with arrow keys.
public enum WebControlStyle { Pointer, Keys, WaitingForLaunch }

// What the controller asked for while a web app runs.
public abstract record InputCommand
{
    public sealed record MoveCursor(int Dx, int Dy) : InputCommand;
    public sealed record Scroll(int Delta) : InputCommand;
    public sealed record MouseButton(bool Down) : InputCommand;
    public sealed record TapKey(ushort VirtualKey) : InputCommand;
    public sealed record Volume(NavAction Action) : InputCommand;
    public sealed record SetKeyboardOpen(bool Open) : InputCommand;
    public sealed record KeyboardMove(NavAction Direction) : InputCommand;
    public sealed record KeyboardPress : InputCommand;
    public sealed record KeyboardShift : InputCommand;
    public sealed record ToggleControlsBar : InputCommand;
    public sealed record CloseApp : InputCommand;
    public sealed record LeaveRunning : InputCommand;
}

// Turns raw controller readings into InputCommands. No Win32 or WPF in here, so it's unit tested
// directly. Only used for web apps: games read the controller themselves.
public sealed class WebInputMapper(WebControlStyle style, int pointerSpeedPercent = 100)
{
    public const ushort ButtonX = 0x4000;
    public const ushort Start = 0x0010;
    public const ushort View = 0x0020;  // "Back" on older controllers
    public const ushort RightStickPress = 0x0080;

    public const ushort KeyLeft = 0x25;
    public const ushort KeyUp = 0x26;
    public const ushort KeyRight = 0x27;
    public const ushort KeyDown = 0x28;
    public const ushort KeyEnter = 0x0D;
    public const ushort KeyEscape = 0x1B;
    public const ushort KeyBackspace = 0x08;
    public const ushort KeySpace = 0x20;
    public const ushort KeyF = 0x46;
    public const ushort KeyBrowserBack = 0xA6;

    private const double FullTilt = 32767;
    private const double MaxCursorSpeed = 1500;  // pixels per second at full tilt, at 100% speed
    private const double MaxScrollSpeed = 1500;  // wheel units per second; one notch is 120
    private const double RightStickDeadzone = 8689;  // Microsoft's recommended value

    // Long enough that exiting can't happen by accident.
    public static readonly TimeSpan ExitHoldTime = TimeSpan.FromSeconds(1);

    private readonly ControllerInputMapper _buttons = new();
    private ushort _previousButtons = ushort.MaxValue;  // a button held at the start doesn't count
    private TimeSpan _lastUpdate;
    private bool _hasLastUpdate;
    private bool _mouseDown;
    private bool _keyboardOpen;
    private TimeSpan? _viewPressedAt;
    private bool _exitSent;
    private double _cursorX;  // fractions of a pixel, carried to the next update
    private double _cursorY;
    private double _scroll;

    public WebControlStyle Style { get; } = style;

    public List<InputCommand> Update(ushort buttons, short leftX, short leftY, short rightX, short rightY, TimeSpan now)
    {
        var commands = new List<InputCommand>();

        double seconds = _hasLastUpdate ? Math.Clamp((now - _lastUpdate).TotalSeconds, 0, 0.1) : 0;
        _lastUpdate = now;
        _hasLastUpdate = true;

        // In pointer mode the left stick is the cursor, so it must not also count as a direction.
        bool stickIsDirection = Style == WebControlStyle.Keys || _keyboardOpen;
        foreach (NavAction action in _buttons.Update(buttons, stickIsDirection ? leftX : (short)0, stickIsDirection ? leftY : (short)0, now))
        {
            HandleAction(action, commands);
        }

        if (JustPressed(buttons, Start))
        {
            _keyboardOpen = !_keyboardOpen;
            commands.Add(new InputCommand.SetKeyboardOpen(_keyboardOpen));
        }
        if (JustPressed(buttons, RightStickPress))
        {
            commands.Add(new InputCommand.ToggleControlsBar());
        }
        if (JustPressed(buttons, ButtonX))
        {
            commands.Add(new InputCommand.TapKey(KeySpace));
        }
        if (_mouseDown && (buttons & ControllerInputMapper.ButtonA) == 0)
        {
            _mouseDown = false;
            commands.Add(new InputCommand.MouseButton(false));
        }

        UpdateExitHold(buttons, now, commands);
        _previousButtons = buttons;

        if (Style != WebControlStyle.Keys && !_keyboardOpen)
        {
            MoveCursor(leftX, leftY, seconds, commands);
            ScrollPage(rightX, rightY, seconds, commands);
        }
        return commands;
    }

    // Lets go of anything held, for when the session ends or the controller disconnects.
    public List<InputCommand> Release()
    {
        var commands = new List<InputCommand>();
        if (_mouseDown) commands.Add(new InputCommand.MouseButton(false));
        if (_keyboardOpen) commands.Add(new InputCommand.SetKeyboardOpen(false));
        _mouseDown = false;
        _keyboardOpen = false;
        _viewPressedAt = null;
        _exitSent = false;
        _previousButtons = ushort.MaxValue;
        _buttons.Reset();
        return commands;
    }

    private void HandleAction(NavAction action, List<InputCommand> commands)
    {
        switch (action)
        {
            case NavAction.VolumeDown or NavAction.VolumeUp:
                commands.Add(new InputCommand.Volume(action));
                break;
            case NavAction.Mute:  // Y: Shift on the keyboard, and fullscreen on a video
                commands.Add(_keyboardOpen ? new InputCommand.KeyboardShift() : new InputCommand.TapKey(KeyF));
                break;
            case NavAction.Up or NavAction.Down or NavAction.Left or NavAction.Right:
                commands.Add(_keyboardOpen ? new InputCommand.KeyboardMove(action) : new InputCommand.TapKey(ArrowKey(action)));
                break;
            case NavAction.Select:
                if (_keyboardOpen)
                {
                    commands.Add(new InputCommand.KeyboardPress());
                }
                else if (Style == WebControlStyle.Keys)
                {
                    commands.Add(new InputCommand.TapKey(KeyEnter));
                }
                else
                {
                    _mouseDown = true;  // held until A is released, so dragging works
                    commands.Add(new InputCommand.MouseButton(true));
                }
                break;
            case NavAction.Back:
                ushort key = _keyboardOpen ? KeyBackspace : Style == WebControlStyle.Keys ? KeyEscape : KeyBrowserBack;
                commands.Add(new InputCommand.TapKey(key));
                break;
        }
    }

    private static ushort ArrowKey(NavAction direction) => direction switch
    {
        NavAction.Up => KeyUp,
        NavAction.Down => KeyDown,
        NavAction.Left => KeyLeft,
        _ => KeyRight,
    };

    private bool JustPressed(ushort buttons, ushort button) =>
        (buttons & button) != 0 && (_previousButtons & button) == 0;

    private void UpdateExitHold(ushort buttons, TimeSpan now, List<InputCommand> commands)
    {
        if ((buttons & View) == 0)
        {
            // Let go before the hold time: that was a tap, which leaves the app running.
            if (_viewPressedAt != null && !_exitSent) commands.Add(new InputCommand.LeaveRunning());
            _viewPressedAt = null;
            _exitSent = false;
            return;
        }
        if (JustPressed(buttons, View)) _viewPressedAt = now;
        if (_viewPressedAt is TimeSpan pressed && !_exitSent && now - pressed >= ExitHoldTime)
        {
            _exitSent = true;
            commands.Add(new InputCommand.CloseApp());
        }
    }

    private void MoveCursor(short x, short y, double seconds, List<InputCommand> commands)
    {
        double speed = TiltSpeed(x, y, ControllerInputMapper.StickDeadzone) * MaxCursorSpeed * pointerSpeedPercent / 100 * seconds;
        if (speed <= 0) return;
        double length = Math.Sqrt((double)x * x + (double)y * y);
        _cursorX += x / length * speed;
        _cursorY -= y / length * speed;  // stick up is positive, but screen Y grows downward
        int dx = (int)_cursorX;
        int dy = (int)_cursorY;
        _cursorX -= dx;
        _cursorY -= dy;
        if (dx != 0 || dy != 0) commands.Add(new InputCommand.MoveCursor(dx, dy));
    }

    private void ScrollPage(short x, short y, double seconds, List<InputCommand> commands)
    {
        double speed = TiltSpeed(0, y, RightStickDeadzone) * MaxScrollSpeed * seconds;
        if (speed <= 0) return;
        _scroll += y > 0 ? speed : -speed;  // the wheel's positive direction is up, like the stick
        int delta = (int)_scroll;
        _scroll -= delta;
        if (delta != 0) commands.Add(new InputCommand.Scroll(delta));
    }

    // 0 inside the deadzone, up to 1 at full tilt. Squared, so small tilts give fine control.
    private static double TiltSpeed(short x, short y, double deadzone)
    {
        double length = Math.Sqrt((double)x * x + (double)y * y);
        if (length <= deadzone) return 0;
        double amount = Math.Min(1, (length - deadzone) / (FullTilt - deadzone));
        return amount * amount;
    }
}
