using System.Diagnostics;
using System.Windows.Threading;
using CouchShell.Platform;

namespace CouchShell.Services;

// Polls the controller on a background thread, so the UI thread never waits on XInput, and
// hands each action to the UI thread through its Dispatcher. Handles connect, disconnect and
// reconnect; the keyboard keeps working whether or not a controller is present.
public sealed class ControllerService : IDisposable
{
    // About 60 polls a second while a controller is connected.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(16);

    // Reading an empty XInput slot is slow, so look for a (re)connected controller only once a second.
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    private readonly Dispatcher _dispatcher;
    private readonly ControllerInputMapper _mapper = new();
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    // Raised on the UI thread.
    public event Action<NavAction>? ActionPressed;

    public ControllerService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    // While set, the controller drives the web app instead of CouchShell's menus. volatile: it's
    // set on the UI thread and read on the polling thread.
    private volatile WebInputSession? _webSession;

    public void SetWebSession(WebInputSession? session) => _webSession = session;

    public void Start() => _loop = Task.Run(() => PollLoopAsync(_stop.Token));

    private async Task PollLoopAsync(CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        int slot = -1;  // XInput slot of the controller in use, or -1 when none is connected
        WebInputSession? runningSession = null;

        while (!token.IsCancellationRequested)
        {
            if (slot < 0)
            {
                slot = FindController();
                if (slot >= 0) Debug.WriteLine($"Controller connected in slot {slot}");
            }

            WebInputSession? session = _webSession;
            if (!ReferenceEquals(session, runningSession))
            {
                // Handing over: let go of anything the old session held, and don't let a button
                // that's still down count as a new press.
                runningSession?.Release();
                runningSession = session;
                _mapper.Reset();
            }

            TimeSpan delay;
            if (slot >= 0 && WindowsNative.TryGetGamepad(slot, out var pad))
            {
                if (session != null)
                {
                    session.Update(pad, clock.Elapsed);
                }
                else
                {
                    foreach (NavAction action in _mapper.Update(pad.Buttons, pad.ThumbLX, pad.ThumbLY, clock.Elapsed))
                    {
                        // Queue it for the UI thread without waiting (`_ =` discards the result).
                        _ = _dispatcher.InvokeAsync(() => ActionPressed?.Invoke(action));
                    }
                }
                delay = PollInterval;
            }
            else
            {
                if (slot >= 0) Debug.WriteLine("Controller disconnected");
                slot = -1;
                _mapper.Reset();  // nothing stays "held" across a disconnect
                session?.Release();
                delay = ScanInterval;
            }

            try
            {
                await Task.Delay(delay, token);
            }
            catch (OperationCanceledException)
            {
                return;  // Dispose() was called
            }
        }
    }

    // Returns the first slot with a controller in it, or -1.
    private static int FindController()
    {
        for (int slot = 0; slot < WindowsNative.XInputMaxControllers; slot++)
        {
            if (WindowsNative.TryGetGamepad(slot, out _)) return slot;
        }
        return -1;
    }

    // Stops polling. Waits briefly for the loop so it can't touch the window after it closes.
    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The loop ended with an error; there's nothing left to stop.
        }
        _stop.Dispose();
    }
}
