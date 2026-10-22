using HDMICaptain.Core;

namespace HDMICaptain.Core.Tests;

public class LaunchOptionsTests
{
    [Fact]
    public void ParsesAppleStyleArguments()
    {
        var o = LaunchOptions.Parse(
            ["-testPattern", "smpteBars", "-qaAction", "screenshot", "-qaShowStats", "YES", "-qaSoakMinutes", "30", "-debugUnlockPro", "YES"],
            allowQa: true);

        Assert.Equal(TestPatternKind.SmpteBars, o.TestPattern);
        Assert.Equal("screenshot", o.QaAction);
        Assert.True(o.QaShowStats);
        Assert.Equal(30, o.QaSoakMinutes);
        Assert.True(o.DebugUnlockPro);
        Assert.False(o.QaPinControls);
    }

    [Fact]
    public void QaSwitchesAreIgnoredOutsideDebug()
    {
        var o = LaunchOptions.Parse(["-testPattern", "grid", "-debugUnlockPro", "YES", "-qaAction", "screenshot"], allowQa: false);

        Assert.Equal(TestPatternKind.Grid, o.TestPattern);
        Assert.False(o.DebugUnlockPro);
        Assert.Null(o.QaAction);
    }

    [Theory]
    [InlineData("NO", false)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    public void FlagValues(string value, bool expected) =>
        Assert.Equal(expected, LaunchOptions.Parse(["-qaPinControls", value], allowQa: true).QaPinControls);

    [Fact]
    public void BareSwitchMeansYesAndUnknownPatternIsNull()
    {
        var o = LaunchOptions.Parse(["-qaShowStats", "-testPattern", "plaid"], allowQa: true);

        Assert.True(o.QaShowStats);
        Assert.Null(o.TestPattern);
    }

    [Fact]
    public void InvalidSoakMinutesIsNull() =>
        Assert.Null(LaunchOptions.Parse(["-qaSoakMinutes", "-5"], allowQa: true).QaSoakMinutes);
}
