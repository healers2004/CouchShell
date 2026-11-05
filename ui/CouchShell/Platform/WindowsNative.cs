using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

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

    // ---- Raw Input: the controller while another app is in front ----
    // On recent Windows (where Microsoft's GameInput is installed) XInput gives apps in the
    // background a controller with nothing pressed. CouchShell is in the background while an app is
    // open, so then it reads the controller's raw HID reports instead: Raw Input with INPUTSINK
    // still delivers them to a window that isn't in front. The HidP_ functions decode a report using
    // the layout the device describes ("preparsed data"), so there's no byte-level guessing.

    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidiPreparsedData = 0x20000005;
    private const uint RideviceInputSink = 0x00000100;
    private const ushort GenericDesktopPage = 0x01;
    private const ushort ButtonPage = 0x09;
    private const ushort JoystickUsage = 0x04;
    private const ushort GamepadUsage = 0x05;
    private const ushort UsageX = 0x30, UsageY = 0x31, UsageRx = 0x33, UsageRy = 0x34, UsageHat = 0x39;
    private const int HidpInput = 0;
    private const int HidpStatusSuccess = 0x00110000;
    private const int MaxButtons = 32;

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public uint Type;
        public uint Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    // HIDP_VALUE_CAPS, of which only these fields are read. The rest is padding up to 72 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HidValueCaps
    {
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, byte[]? data, ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, byte[]? data, ref uint size);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
        [Out] ushort[] usages, ref uint usageCount, byte[] preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        out uint value, byte[] preparsedData, byte[] report, uint reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetSpecificValueCaps(int reportType, ushort usagePage, ushort linkCollection, ushort usage,
        [Out] HidValueCaps[] caps, ref ushort capsCount, byte[] preparsedData);

    // Receives the controller's raw reports on a hidden window and keeps the latest state of each
    // controller. Created and fed on the UI thread; Current can be read from any thread.
    public sealed class RawGamepad : IDisposable
    {
        private readonly HwndSource _window;
        private readonly Dictionary<IntPtr, byte[]?> _layouts = [];  // per device; null: not a usable gamepad
        private readonly Dictionary<IntPtr, XInputGamepad> _states = [];
        private readonly object _gate = new();

        private RawGamepad(HwndSource window)
        {
            _window = window;
            _window.AddHook(OnMessage);
        }

        // Null if Windows won't register for raw controller input.
        public static RawGamepad? TryStart()
        {
            // Never shown: it exists only to receive WM_INPUT.
            var window = new HwndSource(new HwndSourceParameters("CouchShell controller") { Width = 0, Height = 0, WindowStyle = 0 });
            RawInputDevice[] devices =
            [
                new() { UsagePage = GenericDesktopPage, Usage = GamepadUsage, Flags = RideviceInputSink, Target = window.Handle },
                new() { UsagePage = GenericDesktopPage, Usage = JoystickUsage, Flags = RideviceInputSink, Target = window.Handle },
            ];
            if (RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>()))
            {
                return new RawGamepad(window);
            }
            window.Dispose();
            return null;
        }

        // Every controller's latest state combined, or null before any report has arrived.
        public XInputGamepad? Current
        {
            get
            {
                lock (_gate)
                {
                    return _states.Count == 0 ? null : Combine(_states.Values);
                }
            }
        }

        private IntPtr OnMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == WmInput) Read(lParam);
            return IntPtr.Zero;  // DefWindowProc still runs, as Raw Input requires
        }

        private void Read(IntPtr rawInput)
        {
            uint headerSize = (uint)Marshal.SizeOf<RawInputHeader>();
            uint size = 0;
            GetRawInputData(rawInput, RidInput, null, ref size, headerSize);
            var data = new byte[size];
            if (size == 0 || GetRawInputData(rawInput, RidInput, data, ref size, headerSize) != size) return;

            IntPtr device = MemoryMarshal.Read<IntPtr>(data.AsSpan(8));  // RAWINPUTHEADER.hDevice
            // RAWHID follows the header: report size, report count, then the reports. The last is newest.
            int reportSize = BitConverter.ToInt32(data, (int)headerSize);
            int reportCount = BitConverter.ToInt32(data, (int)headerSize + 4);
            if (reportSize <= 0 || reportCount <= 0) return;
            byte[] report = data.AsSpan((int)headerSize + 8 + (reportCount - 1) * reportSize, reportSize).ToArray();

            byte[]? layout = Layout(device);
            if (layout == null) return;
            XInputGamepad state = Decode(layout, report);
            lock (_gate) _states[device] = state;
        }

        private byte[]? Layout(IntPtr device)
        {
            if (_layouts.TryGetValue(device, out byte[]? known)) return known;
            uint size = 0;
            GetRawInputDeviceInfo(device, RidiPreparsedData, null, ref size);
            byte[]? layout = new byte[size];
            if (size == 0 || GetRawInputDeviceInfo(device, RidiPreparsedData, layout, ref size) != size) layout = null;
            _layouts[device] = layout;
            return layout;
        }

        public void Dispose() => _window.Dispose();
    }

    // Decodes one report with the standard layout Windows gives Xbox-style controllers.
    private static XInputGamepad Decode(byte[] layout, byte[] report)
    {
        var pressed = new ushort[MaxButtons];
        uint count = MaxButtons;
        if (HidP_GetUsages(HidpInput, ButtonPage, 0, pressed, ref count, layout, report, (uint)report.Length) != HidpStatusSuccess) count = 0;

        float Axis(ushort usage)
        {
            if (HidP_GetUsageValue(HidpInput, GenericDesktopPage, 0, usage, out uint value, layout, report, (uint)report.Length) != HidpStatusSuccess) return 0;
            (int min, int max) = Range(layout, usage);
            return max > min ? Math.Clamp((value - min) * 2f / (max - min) - 1f, -1f, 1f) : 0;
        }

        int? hat = null;
        if (HidP_GetUsageValue(HidpInput, GenericDesktopPage, 0, UsageHat, out uint hatValue, layout, report, (uint)report.Length) == HidpStatusSuccess)
        {
            (int min, _) = Range(layout, UsageHat);
            hat = (int)hatValue - min;
        }
        // HID's Y axes grow downwards; XInput's grow upwards.
        return FromHid(pressed.AsSpan(0, (int)count), hat, Axis(UsageX), -Axis(UsageY), Axis(UsageRx), -Axis(UsageRy));
    }

    // The value range a device declares for one control. A declared maximum below the minimum means
    // the field is unsigned and its top bit was read as a sign, so the bit size gives the real range.
    private static (int Min, int Max) Range(byte[] layout, ushort usage)
    {
        var caps = new HidValueCaps[1];
        ushort count = 1;
        if (HidP_GetSpecificValueCaps(HidpInput, GenericDesktopPage, 0, usage, caps, ref count, layout) != HidpStatusSuccess || count == 0)
        {
            return (0, 0);
        }
        HidValueCaps cap = caps[0];
        return cap.LogicalMax >= cap.LogicalMin ? (cap.LogicalMin, cap.LogicalMax) : (0, (int)((1L << cap.BitSize) - 1));
    }

    // Button numbers in the layout Windows gives Xbox-style controllers, as XInput bits:
    // 1 A, 2 B, 3 X, 4 Y, 5 LB, 6 RB, 7 View, 8 Menu, 9 left stick, 10 right stick.
    private static readonly ushort[] HidButtonBits = [0x1000, 0x2000, 0x4000, 0x8000, 0x0100, 0x0200, 0x0020, 0x0010, 0x0040, 0x0080];

    // The D-pad is a hat switch: 0 up, then clockwise in eighths; anything else is centred.
    private static readonly ushort[] HatBits = [0x0001, 0x0001 | 0x0008, 0x0008, 0x0002 | 0x0008, 0x0002, 0x0002 | 0x0004, 0x0004, 0x0001 | 0x0004];

    // A decoded report in XInput's form, so the rest of CouchShell needn't know where it came from.
    // Sticks are -1..1 with up positive.
    internal static XInputGamepad FromHid(ReadOnlySpan<ushort> pressedButtons, int? hat, float leftX, float leftY, float rightX, float rightY)
    {
        ushort buttons = 0;
        foreach (ushort number in pressedButtons)
        {
            if (number >= 1 && number <= HidButtonBits.Length) buttons |= HidButtonBits[number - 1];
        }
        if (hat is >= 0 and < 8) buttons |= HatBits[hat.Value];
        return new XInputGamepad
        {
            Buttons = buttons,
            ThumbLX = Scale(leftX),
            ThumbLY = Scale(leftY),
            ThumbRX = Scale(rightX),
            ThumbRY = Scale(rightY),
        };
    }

    // Several controllers at once: any button held on any of them, and each stick as pushed furthest.
    internal static XInputGamepad Combine(IEnumerable<XInputGamepad> pads)
    {
        var combined = new XInputGamepad();
        foreach (XInputGamepad pad in pads)
        {
            combined.Buttons |= pad.Buttons;
            combined.LeftTrigger = Math.Max(combined.LeftTrigger, pad.LeftTrigger);
            combined.RightTrigger = Math.Max(combined.RightTrigger, pad.RightTrigger);
            combined.ThumbLX = Furthest(combined.ThumbLX, pad.ThumbLX);
            combined.ThumbLY = Furthest(combined.ThumbLY, pad.ThumbLY);
            combined.ThumbRX = Furthest(combined.ThumbRX, pad.ThumbRX);
            combined.ThumbRY = Furthest(combined.ThumbRY, pad.ThumbRY);
        }
        return combined;
    }

    private static short Furthest(short a, short b) => Math.Abs((int)b) > Math.Abs((int)a) ? b : a;

    private static short Scale(float value) => (short)Math.Round(Math.Clamp(value, -1f, 1f) * short.MaxValue);

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

    // ---- Sending mouse and keyboard input, for controlling web apps ----

    private const uint InputMouse = 0;
    private const uint InputKeyboard = 1;
    private const uint MouseMove = 0x0001;
    private const uint MouseLeftDown = 0x0002;
    private const uint MouseLeftUp = 0x0004;
    private const uint MouseWheel = 0x0800;
    private const uint KeyExtended = 0x0001;
    private const uint KeyUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseInput
    {
        public int Dx;
        public int Dy;
        public uint Data;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    // A C union: both fields start at the same address.
    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    // Queues input as if it came from real devices. Returns how many events it accepted; fewer
    // means another program's input was blocking it, or the foreground app runs as administrator.
    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, Input[] inputs, int size);

    internal static uint SendMouse(int dx, int dy, uint data, uint flags) =>
        SendInput(1, [new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Dx = dx, Dy = dy, Data = data, Flags = flags } } }], Marshal.SizeOf<Input>());

    public static void MoveMouse(int dx, int dy) => SendMouse(dx, dy, 0, MouseMove);

    public static void ScrollMouse(int delta) => SendMouse(0, 0, unchecked((uint)delta), MouseWheel);

    public static void SetLeftMouseButton(bool down) => SendMouse(0, 0, 0, down ? MouseLeftDown : MouseLeftUp);

    // A press and release of one key.
    public static void TapKey(ushort virtualKey)
    {
        // Arrow keys are "extended": without this flag they'd be the number pad's arrows.
        uint extended = virtualKey is >= 0x25 and <= 0x28 ? KeyExtended : 0;
        Input[] inputs =
        [
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = extended } } },
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { VirtualKey = virtualKey, Flags = extended | KeyEventKeyUp } } },
        ];
        SendInput(2, inputs, Marshal.SizeOf<Input>());
    }

    // Types one character directly, whatever the keyboard layout is.
    public static void TypeCharacter(char character)
    {
        Input[] inputs =
        [
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyUnicode } } },
            new Input { Type = InputKeyboard, Data = new InputUnion { Keyboard = new KeyboardInput { ScanCode = character, Flags = KeyUnicode | KeyEventKeyUp } } },
        ];
        SendInput(2, inputs, Marshal.SizeOf<Input>());
    }

    // ---- Interface sounds ----

    private const uint WaveMapper = 0xFFFFFFFF;  // whichever output device is the default
    private const int MmSuccess = 0;

    // Describes 16-bit mono audio at 44.1 kHz, matching tools/make-sounds.ps1. Pack = 1: Win32 lays
    // this struct out with no padding.
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WaveFormat
    {
        public ushort Tag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint BytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public IntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public IntPtr Reserved;
    }

    [DllImport("winmm.dll")]
    private static extern int waveOutOpen(out IntPtr device, uint deviceId, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);

    [DllImport("winmm.dll")]
    private static extern int waveOutPrepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutUnprepareHeader(IntPtr device, IntPtr header, int size);

    [DllImport("winmm.dll")]
    private static extern int waveOutWrite(IntPtr device, IntPtr header, int size);

    // Stops whatever is playing at once.
    [DllImport("winmm.dll")]
    private static extern int waveOutReset(IntPtr device);

    [DllImport("winmm.dll")]
    private static extern int waveOutClose(IntPtr device);

    // A sound in memory that Windows can read directly. Windows keeps pointers into it while playing,
    // so it lives outside the garbage collector's reach, for the life of the program.
    public sealed record WaveClip(IntPtr Samples, int Length, IntPtr Header);

    public static WaveClip CreateWaveClip(ReadOnlySpan<byte> samples)
    {
        IntPtr memory = Marshal.AllocHGlobal(samples.Length);
        Marshal.Copy(samples.ToArray(), 0, memory, samples.Length);
        return new WaveClip(memory, samples.Length, Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>()));
    }

    // Returns IntPtr.Zero if there's no usable output device.
    public static IntPtr OpenWaveDevice()
    {
        var format = new WaveFormat
        {
            Tag = 1,  // uncompressed
            Channels = 1,
            SamplesPerSecond = 44100,
            BytesPerSecond = 88200,
            BlockAlign = 2,
            BitsPerSample = 16,
        };
        return waveOutOpen(out IntPtr device, WaveMapper, ref format, IntPtr.Zero, IntPtr.Zero, 0) == MmSuccess ? device : IntPtr.Zero;
    }

    public static void PrepareWaveClip(IntPtr device, WaveClip clip)
    {
        Marshal.StructureToPtr(new WaveHeader { Data = clip.Samples, BufferLength = (uint)clip.Length }, clip.Header, false);
        waveOutPrepareHeader(device, clip.Header, Marshal.SizeOf<WaveHeader>());
    }

    // Cuts off anything playing and starts this clip. Returns false if the device has gone.
    public static bool PlayWaveClip(IntPtr device, WaveClip clip)
    {
        waveOutReset(device);
        return waveOutWrite(device, clip.Header, Marshal.SizeOf<WaveHeader>()) == MmSuccess;
    }

    public static void CloseWaveDevice(IntPtr device, IEnumerable<WaveClip> clips)
    {
        waveOutReset(device);
        foreach (WaveClip clip in clips) waveOutUnprepareHeader(device, clip.Header, Marshal.SizeOf<WaveHeader>());
        waveOutClose(device);
    }

    // ---- App icons ----

    private const int IconOnly = 0x4;  // SIIGBF_ICONONLY

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    // Asks Windows for a picture of a shell item, including Store apps.
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);

    // Returns the item's icon, or null if it has none. Must run on an STA thread.
    public static BitmapSource? GetShellIcon(string path, int size)
    {
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out IShellItemImageFactory factory);
        if (factory.GetImage(new NativeSize { Width = size, Height = size }, IconOnly, out IntPtr bitmap) != 0) return null;
        try
        {
            BitmapSource source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(bitmap);  // the copy above is independent of this handle
        }
    }

    // ---- The overlay window ----

    private const int ExtendedStyleIndex = -20;  // GWL_EXSTYLE
    private const long ExStyleTransparent = 0x00000020;  // mouse clicks pass through
    private const long ExStyleToolWindow = 0x00000080;   // not in Alt+Tab
    private const long ExStyleNoActivate = 0x08000000;   // never takes focus

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);

    public static void MakeOverlay(IntPtr window)
    {
        long style = GetWindowLongPtr(window, ExtendedStyleIndex).ToInt64();
        SetWindowLongPtr(window, ExtendedStyleIndex, new IntPtr(style | ExStyleTransparent | ExStyleToolWindow | ExStyleNoActivate));
    }

    private const byte VirtualKeyMenu = 0x12;  // VK_MENU, the Alt key
    private const uint KeyEventKeyUp = 0x0002;

    // Queues a synthetic key event. It has no return value.
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
}
