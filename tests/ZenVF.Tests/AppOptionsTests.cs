using ZenVF.Models;

namespace ZenVF.Tests;

public class AppOptionsTests
{
    [Fact]
    public void ScreenshotTakesPathAndImpliesDemo()
    {
        var o = AppOptions.Parse(["--screenshot", "out.png", "--screenshot-tab", "2"]);
        Assert.Equal("out.png", o.ScreenshotPath);
        Assert.Equal(2, o.ScreenshotTab);
        Assert.True(o.Demo);
    }

    [Fact]
    public void ScreenshotCoreTakesPathDirectly()
    {
        var o = AppOptions.Parse(["--demo", "--screenshot-core", "out.png", "--screenshot-tab", "2"]);
        Assert.True(o.ScreenshotCore);
        Assert.Equal("out.png", o.ScreenshotPath);
        Assert.Equal(2, o.ScreenshotTab);
    }

    [Fact]
    public void ScreenshotThemeTakesPathDirectly()
    {
        var o = AppOptions.Parse(["--demo", "--screenshot-theme", "out.png"]);
        Assert.True(o.ScreenshotTheme);
        Assert.Equal("out.png", o.ScreenshotPath);
    }

    [Fact]
    public void ScreenshotCoreStillWorksWithSeparateScreenshotFlag()
    {
        var o = AppOptions.Parse(["--screenshot-core", "--screenshot", "out.png"]);
        Assert.True(o.ScreenshotCore);
        Assert.Equal("out.png", o.ScreenshotPath);

        var reversed = AppOptions.Parse(["--screenshot", "out.png", "--screenshot-core"]);
        Assert.True(reversed.ScreenshotCore);
        Assert.Equal("out.png", reversed.ScreenshotPath);
    }

    [Fact]
    public void ScreenshotCoreWithoutPathLeavesPathNull()
    {
        var o = AppOptions.Parse(["--screenshot-core", "--screenshot-tab", "1"]);
        Assert.True(o.ScreenshotCore);
        Assert.Null(o.ScreenshotPath);
        Assert.Equal(1, o.ScreenshotTab);
    }
}
