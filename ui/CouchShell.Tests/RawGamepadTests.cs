using CouchShell.Platform;
using CouchShell.Services;
using Xunit;

namespace CouchShell.Tests;

public class RawGamepadTests
{
    private static WindowsNative.XInputGamepad Decode(ushort[] buttons, int? hat = null, float lx = 0, float ly = 0, float rx = 0, float ry = 0) =>
        WindowsNative.FromHid(buttons, hat, lx, ly, rx, ry);

    [Theory]
    [InlineData((ushort)1, ControllerInputMapper.ButtonA)]
    [InlineData((ushort)2, ControllerInputMapper.ButtonB)]
    [InlineData((ushort)7, WebInputMapper.View)]
    [InlineData((ushort)8, WebInputMapper.Start)]
    public void EachButtonNumberBecomesItsXInputBit(ushort number, ushort bit)
    {
        Assert.Equal(bit, Decode([number]).Buttons);
    }

    [Theory]
    [InlineData(0, ControllerInputMapper.DpadUp)]
    [InlineData(1, ControllerInputMapper.DpadUp | ControllerInputMapper.DpadRight)]
    public void TheHatSwitchIsTheDpad(int hat, ushort bits)
    {
        Assert.Equal(bits, Decode([], hat).Buttons);
    }

    [Fact]
    public void SticksScaleToXInputRange()
    {
        var pad = Decode([], lx: 1f, ly: 1f, rx: -0.5f, ry: 3f);
        Assert.Equal(short.MaxValue, pad.ThumbLX);
        Assert.Equal(short.MaxValue, pad.ThumbLY);  // up stays up
        Assert.Equal(-16384, pad.ThumbRX);
        Assert.Equal(short.MaxValue, pad.ThumbRY);  // out of range is clamped
    }

    [Fact]
    public void CombiningKeepsEveryButtonAndTheFurthestStick()
    {
        var zeros = new WindowsNative.XInputGamepad();  // XInput, blanked while an app is in front
        var raw = Decode([1], lx: -0.8f);
        var combined = WindowsNative.Combine([zeros, raw]);
        Assert.Equal(ControllerInputMapper.ButtonA, combined.Buttons);
        Assert.Equal(raw.ThumbLX, combined.ThumbLX);

        var other = Decode([2], lx: 0.2f);
        combined = WindowsNative.Combine([raw, other]);
        Assert.Equal(ControllerInputMapper.ButtonA | ControllerInputMapper.ButtonB, combined.Buttons);
        Assert.Equal(raw.ThumbLX, combined.ThumbLX);
    }
}
