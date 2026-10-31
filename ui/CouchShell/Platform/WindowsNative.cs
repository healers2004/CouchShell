using System.Runtime.InteropServices;

namespace CouchShell.Platform;

// Every Win32 call the UI makes (P/Invoke) lives in this file.
internal static class WindowsNative
{
    // XInput supports four controllers, in slots 0-3.
    public const int XInputMaxControllers = 4;

    private const uint ErrorSuccess = 0;

    // Same field order and sizes as XINPUT_GAMEPAD in xinput.h, so the bytes line up.
    [StructLayout(LayoutKind.Sequential)]
    public struct XInputGamepad
    {
        public ushort Buttons;  // one bit per button; see ControllerInputMapper
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;   // left stick, -32768 (left) to 32767 (right)
        public short ThumbLY;   // left stick, -32768 (down) to 32767 (up)
        public short ThumbRX;
        public short ThumbRY;
    }

    // XINPUT_STATE: PacketNumber changes whenever any input on the controller changes.
    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    // xinput1_4.dll ships with Windows 8 and later. Returns ERROR_SUCCESS (0) with the state
    // filled in, or ERROR_DEVICE_NOT_CONNECTED (1167) when the slot is empty.
    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    // Reads the controller in `slot`. Returns false if no controller is connected there.
    public static bool TryGetGamepad(int slot, out XInputGamepad gamepad)
    {
        bool connected = XInputGetState((uint)slot, out XInputState state) == ErrorSuccess;
        gamepad = state.Gamepad;
        return connected;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // Returns the id of the thread that owns the window; the process id out-parameter is unused.
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    // Shares input state between two threads, which lets the second one take focus.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    // Returns false when Windows refuses, which it does for most background programs.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    // Windows only lets the foreground thread hand over focus, so attach to that thread for the
    // duration of the call. Returns whether `window` ended up in front.
    public static bool ForceForeground(IntPtr window)
    {
        if (TryTakeForeground(window)) return true;

        // Windows allows a focus change right after keyboard input, so a tap of Alt unlocks it.
        // Alt on its own types nothing.
        keybd_event(VirtualKeyMenu, 0, 0, UIntPtr.Zero);
        keybd_event(VirtualKeyMenu, 0, KeyEventKeyUp, UIntPtr.Zero);
        return TryTakeForeground(window);
    }

    private static bool TryTakeForeground(IntPtr window)
    {
        uint ours = GetCurrentThreadId();
        uint theirs = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        bool attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        BringWindowToTop(window);
        SetForegroundWindow(window);
        if (attached) AttachThreadInput(ours, theirs, false);
        return GetForegroundWindow() == window;
    }

    private const byte VirtualKeyMenu = 0x12;  // VK_MENU, the Alt key
    private const uint KeyEventKeyUp = 0x0002;

    // Queues a synthetic key event. It has no return value.
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
}
