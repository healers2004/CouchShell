using CouchShell.Platform;
using CouchShell.Services;
using Xunit;

namespace CouchShell.Tests;

public class GameInputTests
{
    private static WindowsNative.XInputGamepad Convert(uint buttons, float lx = 0, float ly = 0, float rx = 0, float ry = 0, float lt = 0, float rt = 0) =>
        WindowsNative.ToXInput(new WindowsNative.GameInputGamepadState
        {
            Buttons = buttons, LeftX = lx, LeftY = ly, RightX = rx, RightY = ry, LeftTrigger = lt, RightTrigger = rt,
        });

    [Theory]
    [InlineData(0x00000004u, ControllerInputMapper.ButtonA)]
    [InlineData(0x00000008u, ControllerInputMapper.ButtonB)]
    [InlineData(0x00000020u, ControllerInputMapper.ButtonY)]
    [InlineData(0x00000001u, ControllerInputMapper.ButtonStart)]
    [InlineData(0x00000040u, ControllerInputMapper.DpadUp)]
    [InlineData(0x00000200u, ControllerInputMapper.DpadRight)]
    [InlineData(0x00000400u, ControllerInputMapper.LeftShoulder)]
    [InlineData(0x00000800u, ControllerInputMapper.RightShoulder)]
    [InlineData(0x00000002u, WebInputMapper.View)]
    [InlineData(0x00000010u, (ushort)0x4000)]  // X
    [InlineData(0x00002000u, (ushort)0x0080)]  // right stick press
    public void EachButtonBecomesItsXInputBit(uint gameInput, ushort xInput)
    {
        Assert.Equal(xInput, Convert(gameInput).Buttons);
    }

    [Fact]
    public void SeveralButtonsCombineAndUnknownOnesAreIgnored()
    {
        // A + B + a paddle-style button XInput has no bit for.
        Assert.Equal(ControllerInputMapper.ButtonA | ControllerInputMapper.ButtonB, Convert(0x0000000C | 0x00100000).Buttons);
    }

    [Fact]
    public void SticksAndTriggersScaleToXInputRanges()
    {
        var pad = Convert(0, lx: 1f, ly: -1f, rx: 0.5f, ry: 2f, lt: 1f, rt: 0.5f);
        Assert.Equal(short.MaxValue, pad.ThumbLX);
        Assert.Equal(-short.MaxValue, pad.ThumbLY);
        Assert.Equal(16384, pad.ThumbRX);
        Assert.Equal(short.MaxValue, pad.ThumbRY);  // out of range is clamped
        Assert.Equal(255, pad.LeftTrigger);
        Assert.Equal(128, pad.RightTrigger);
    }

    [Fact]
    public void UpOnTheStickStaysUp()
    {
        // Both APIs use positive Y for up, which the menus rely on.
        Assert.True(Convert(0, ly: 1f).ThumbLY > 0);
    }
}
