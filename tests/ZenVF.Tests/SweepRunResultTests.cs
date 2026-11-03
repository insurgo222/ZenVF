using ZenVF.Models;

namespace ZenVF.Tests;

public class SweepRunResultTests
{
    [Fact]
    public void CreateCopiesCpuAndSettings()
    {
        CpuDescriptor cpu = new()
        {
            Name = "AMD Ryzen 7 9800X3D",
            Vendor = "AuthenticAMD",
            StockFMaxMHz = 5225,
            FMaxCeilingOffsetMHz = 25
        };
        SweepSettings settings = new() { StartFMax = 5425, EndFMax = 4700, SelectedCcds = new() { 0 } };
        DateTime started = new(2026, 11, 3, 9, 0, 0);

        SweepRunResult result = SweepRunResult.Create(cpu, settings, new List<SweepSeries>(), started);

        Assert.Equal("AMD Ryzen 7 9800X3D", result.CpuName);
        Assert.Equal(5225, result.StockFMax);
        Assert.Equal(5250, result.StockFMaxCeiling);
        Assert.Equal(25, result.FMaxCeilingOffsetMHz);
        Assert.Same(settings, result.Settings);
        Assert.Equal(started, result.Started);
        Assert.Empty(result.CoreVoltages);
    }

    [Fact]
    public void MaxCoreSeriesFollowsSelectedCcdsInOrder()
    {
        SweepSettings settings = new() { SelectedCcds = new() { 1, 0 } };

        List<SweepSeries> series = SweepRunResult.MaxCoreSeries(settings);

        Assert.Equal(new[] { 0, 1 }, series.Select(s => s.CcdIndex));
        Assert.Equal("CCD1 | MAX CORE", series[1].Label);
    }
}
