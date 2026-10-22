using CouchShell.Services;
using static CouchShell.Services.ControllerInputMapper;

namespace CouchShell.Tests;

public class ControllerInputMapperTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // A mapper that has seen one idle poll, as it would right after the controller connects.
    private static ControllerInputMapper Connected()
    {
        var mapper = new ControllerInputMapper();
        mapper.Update(0, 0, 0, Ms(0));
        return mapper;
    }

    [Fact]
    public void DpadPressMovesOnceThenRepeatsAfterDelay()
    {
        var mapper = Connected();
        Assert.Equal([NavAction.Right], mapper.Update(DpadRight, 0, 0, Ms(10)));
        Assert.Empty(mapper.Update(DpadRight, 0, 0, Ms(10 + 399)));  // still inside RepeatDelay
        Assert.Equal([NavAction.Right], mapper.Update(DpadRight, 0, 0, Ms(10 + 400)));
        Assert.Empty(mapper.Update(DpadRight, 0, 0, Ms(10 + 400 + 119)));
        Assert.Equal([NavAction.Right], mapper.Update(DpadRight, 0, 0, Ms(10 + 400 + 120)));
    }

    [Fact]
    public void ReleaseThenPressMovesImmediately()
    {
        var mapper = Connected();
        mapper.Update(DpadLeft, 0, 0, Ms(10));
        Assert.Empty(mapper.Update(0, 0, 0, Ms(20)));
        Assert.Equal([NavAction.Left], mapper.Update(DpadLeft, 0, 0, Ms(30)));
    }

    [Fact]
    public void ChangingDirectionWhileHeldMovesImmediately()
    {
        var mapper = Connected();
        mapper.Update(DpadLeft, 0, 0, Ms(10));
        Assert.Equal([NavAction.Up], mapper.Update(DpadUp, 0, 0, Ms(20)));
    }

    [Fact]
    public void StickInsideDeadzoneDoesNothing()
    {
        var mapper = Connected();
        Assert.Empty(mapper.Update(0, StickDeadzone, -StickDeadzone, Ms(10)));
    }

    [Theory]
    [InlineData(20000, 0, NavAction.Right)]
    [InlineData(-20000, 0, NavAction.Left)]
    [InlineData(0, 20000, NavAction.Up)]
    [InlineData(0, -20000, NavAction.Down)]
    [InlineData(20000, 15000, NavAction.Right)]  // diagonal: the bigger tilt wins
    [InlineData(short.MinValue, 0, NavAction.Left)]  // full tilt doesn't overflow
    public void StickBeyondDeadzoneMoves(short x, short y, NavAction expected)
    {
        var mapper = Connected();
        Assert.Equal([expected], mapper.Update(0, x, y, Ms(10)));
    }

    [Fact]
    public void DpadWinsOverStick()
    {
        var mapper = Connected();
        Assert.Equal([NavAction.Down], mapper.Update(DpadDown, 20000, 0, Ms(10)));
    }

    [Fact]
    public void ButtonsFireOncePerPress()
    {
        var mapper = Connected();
        Assert.Equal([NavAction.Select], mapper.Update(ButtonA, 0, 0, Ms(10)));
        Assert.Empty(mapper.Update(ButtonA, 0, 0, Ms(2000)));  // holding A doesn't repeat
        Assert.Empty(mapper.Update(0, 0, 0, Ms(2010)));
        Assert.Equal([NavAction.Back], mapper.Update(ButtonB, 0, 0, Ms(2020)));
    }

    [Fact]
    public void ButtonHeldDuringReconnectDoesNotFire()
    {
        var mapper = Connected();
        mapper.Reset();  // what ControllerService does on disconnect
        Assert.Empty(mapper.Update(ButtonA, 0, 0, Ms(10)));
        mapper.Update(0, 0, 0, Ms(20));
        Assert.Equal([NavAction.Select], mapper.Update(ButtonA, 0, 0, Ms(30)));
    }
}
