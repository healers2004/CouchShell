using System.Windows.Threading;
using CouchShell.Platform;

namespace CouchShell.Services;

// The controller's connection to a running web app. Mouse and key commands are sent straight from
// the polling thread so the pointer stays smooth; anything touching the UI goes through the Dispatcher.
public sealed class WebInputSession(WebControlStyle style, int pointerSpeedPercent, Dispatcher dispatcher, Action<InputCommand> onUiCommand)
{
    private readonly WebInputMapper _mapper = new(style, pointerSpeedPercent);

    internal void Update(in WindowsNative.XInputGamepad pad, TimeSpan now) =>
        Run(_mapper.Update(pad.Buttons, pad.ThumbLX, pad.ThumbLY, pad.ThumbRX, pad.ThumbRY, now));

    public void Release() => Run(_mapper.Release());

    private void Run(List<InputCommand> commands)
    {
        foreach (InputCommand command in commands)
        {
            switch (command)
            {
                case InputCommand.MoveCursor move:
                    WindowsNative.MoveMouse(move.Dx, move.Dy);
                    break;
                case InputCommand.Scroll scroll:
                    WindowsNative.ScrollMouse(scroll.Delta);
                    break;
                case InputCommand.MouseButton button:
                    WindowsNative.SetLeftMouseButton(button.Down);
                    break;
                case InputCommand.TapKey key:
                    WindowsNative.TapKey(key.VirtualKey);
                    break;
                default:
                    _ = dispatcher.InvokeAsync(() => onUiCommand(command));
                    break;
            }
        }
    }
}
