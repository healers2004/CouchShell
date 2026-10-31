namespace CouchShell.Services;

// What the user asked for, independent of whether it came from a controller or a keyboard.
public enum NavAction { Up, Down, Left, Right, Select, Back, VolumeDown, VolumeUp, Mute }

// Turns raw controller readings into navigation actions: D-pad or stick to a direction,
// stick deadzone, key repeat while a direction is held, and one Select/Back per button press.
// No Win32 or WPF in here, so it's unit tested directly.
public class ControllerInputMapper
{
    // XInput button bits (XINPUT_GAMEPAD_* in xinput.h).
    public const ushort DpadUp = 0x0001;
    public const ushort DpadDown = 0x0002;
    public const ushort DpadLeft = 0x0004;
    public const ushort DpadRight = 0x0008;
    public const ushort ButtonA = 0x1000;
    public const ushort ButtonB = 0x2000;
    public const ushort ButtonY = 0x8000;
    public const ushort LeftShoulder = 0x0100;
    public const ushort RightShoulder = 0x0200;

    // Microsoft's recommended left-stick deadzone. Worn sticks rest slightly off-centre, so
    // anything this close to the middle counts as centred.
    public const int StickDeadzone = 7849;

    // Holding a direction moves once, then repeats after a pause, like a held keyboard key.
    public static readonly TimeSpan RepeatDelay = TimeSpan.FromMilliseconds(400);
    public static readonly TimeSpan RepeatInterval = TimeSpan.FromMilliseconds(120);

    private NavAction? _heldDirection;  // null when no direction is held
    private TimeSpan _nextRepeat;
    private ushort _previousButtons;

    public ControllerInputMapper() => Reset();

    // Call once per poll with the current reading and a steadily increasing clock.
    // Returns the actions to perform now (usually none).
    public List<NavAction> Update(ushort buttons, short stickX, short stickY, TimeSpan now)
    {
        var actions = new List<NavAction>();

        NavAction? direction = ReadDirection(buttons, stickX, stickY);
        if (direction != _heldDirection)
        {
            // A new direction (or a change of direction) moves straight away.
            _heldDirection = direction;
            if (direction is NavAction pressed)
            {
                actions.Add(pressed);
                _nextRepeat = now + RepeatDelay;
            }
        }
        else if (direction is NavAction held && now >= _nextRepeat)
        {
            actions.Add(held);
            _nextRepeat = now + RepeatInterval;
        }

        // A and B fire once, on the poll where the button goes from up to down.
        if (JustPressed(buttons, ButtonA)) actions.Add(NavAction.Select);
        if (JustPressed(buttons, ButtonB)) actions.Add(NavAction.Back);
        if (JustPressed(buttons, LeftShoulder)) actions.Add(NavAction.VolumeDown);
        if (JustPressed(buttons, RightShoulder)) actions.Add(NavAction.VolumeUp);
        if (JustPressed(buttons, ButtonY)) actions.Add(NavAction.Mute);
        _previousButtons = buttons;

        return actions;
    }

    // Forget held input, e.g. after the controller disconnects. Treating every button as
    // already held means an A held while reconnecting won't fire until pressed again.
    public void Reset()
    {
        _heldDirection = null;
        _previousButtons = ushort.MaxValue;
    }

    private bool JustPressed(ushort buttons, ushort button) =>
        (buttons & button) != 0 && (_previousButtons & button) == 0;

    private static NavAction? ReadDirection(ushort buttons, short stickX, short stickY)
    {
        // The D-pad wins over the stick.
        if ((buttons & DpadUp) != 0) return NavAction.Up;
        if ((buttons & DpadDown) != 0) return NavAction.Down;
        if ((buttons & DpadLeft) != 0) return NavAction.Left;
        if ((buttons & DpadRight) != 0) return NavAction.Right;

        // Widen to int first: Math.Abs(short.MinValue) doesn't fit in a short.
        int x = stickX;
        int y = stickY;
        if (Math.Max(Math.Abs(x), Math.Abs(y)) <= StickDeadzone) return null;

        // Follow the axis tilted furthest, so a slightly diagonal push still moves one way.
        if (Math.Abs(x) >= Math.Abs(y)) return x > 0 ? NavAction.Right : NavAction.Left;
        return y > 0 ? NavAction.Up : NavAction.Down;  // XInput: positive Y is up
    }
}
