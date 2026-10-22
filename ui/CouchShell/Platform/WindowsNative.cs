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
}
