using ZenVF.Services;

namespace ZenVF.Tests;

public class SweepEngineHelperTests
{
    [Fact]
    public void MajorityVoltageSourceWins()
    {
        Dictionary<string, int> counts = new() { ["LHM Core VID（SVI3 修正）"] = 6, ["SMU PM table"] = 5 };

        Assert.Equal("LHM Core VID（SVI3 修正）", SweepEngine.ChooseVoltageSource(counts));
    }

    [Fact]
    public void TieGoesToThePmTable()
    {
        Dictionary<string, int> counts = new() { ["LHM Core VID（SVI3 修正）"] = 5, ["SMU PM table"] = 5 };

        Assert.Equal("SMU PM table", SweepEngine.ChooseVoltageSource(counts));
    }

    [Fact]
    public void FailedPmTableLabelDoesNotWinATie()
    {
        Dictionary<string, int> counts = new() { ["LHM Core VID（PM table 读取失败）"] = 5, ["SMU PM table"] = 5 };

        Assert.Equal("SMU PM table", SweepEngine.ChooseVoltageSource(counts));
    }

    [Fact]
    public void NoSamplesMeansNoSource()
    {
        Assert.Null(SweepEngine.ChooseVoltageSource(new Dictionary<string, int>()));
    }

    [Fact]
    public void ThermalGuardAbortsOnTheThirdConsecutiveReading()
    {
        SweepEngine.ThermalGuard guard = new(105, "测试");

        guard.Check(105);
        guard.Check(106);
        Assert.Throws<InvalidOperationException>(() => guard.Check(105.5));
    }

    [Fact]
    public void ThermalGuardResetsWhenTemperatureDrops()
    {
        SweepEngine.ThermalGuard guard = new(105, "测试");

        guard.Check(105);
        guard.Check(105);
        guard.Check(100);
        guard.Check(105);
        guard.Check(105);
    }

    [Fact]
    public void ThermalGuardIgnoresMissingReadings()
    {
        SweepEngine.ThermalGuard guard = new(105, "测试");

        guard.Check(0);
        guard.Check(0);
        guard.Check(0);
        guard.Check(104.9);
    }

    [Fact]
    public void LimiterDescriptionNamesThePptWall()
    {
        Assert.Contains("PPT", SweepEngine.DescribeLimiter(power: 160, pptLimit: 162, tctl: 70, thermalLimit: 95));
    }

    [Fact]
    public void LimiterDescriptionNamesTemperatureNearTjmax()
    {
        Assert.Contains("温度", SweepEngine.DescribeLimiter(power: 100, pptLimit: 162, tctl: 94, thermalLimit: 95));
    }

    [Fact]
    public void LimiterDescriptionFallsBackToCurrent()
    {
        Assert.Contains("TDC/EDC", SweepEngine.DescribeLimiter(power: 100, pptLimit: 162, tctl: 70, thermalLimit: 95));
    }
}
