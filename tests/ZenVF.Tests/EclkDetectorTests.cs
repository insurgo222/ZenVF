using ZenVF.Models;
using ZenVF.Services;

namespace ZenVF.Tests;

public class EclkDetectorTests
{
    [Fact]
    public void PeakAtPboMaxIsNotEclk()
    {
        var estimate = EclkDetector.DetectStartup(Samples(5425), pboMaxMHz: 5425);

        Assert.False(estimate.Detected);
    }

    [Fact]
    public void PeakAbovePboMaxIsEclkRoundedUpToTwoDecimals()
    {
        // 5625 / 5425 = 1.036866 -> BCLK 103.6866 -> 103.69 (any third decimal carries).
        var estimate = EclkDetector.DetectStartup(Samples(5625), pboMaxMHz: 5425);

        Assert.True(estimate.Detected);
        Assert.Equal(103.69, estimate.BclkMHz, 2);
    }

    [Fact]
    public void BusSpeedAloneCanDetectEclk()
    {
        var estimate = EclkDetector.DetectStartup(Samples(5425), pboMaxMHz: 5425, busSpeedMHz: 102.0);

        Assert.True(estimate.Detected);
        Assert.Equal(102.0, estimate.BclkMHz, 2);
    }

    [Fact]
    public void NoSamplesDetectNothing()
    {
        Assert.False(EclkDetector.DetectStartup(Array.Empty<SensorCoreSample>(), 5425).Detected);
    }

    private static SensorCoreSample[] Samples(double peak) =>
    [
        new() { CoreId = 1, CcdIndex = 0, CoreClock = 4800, CoreClockMax = peak },
        new() { CoreId = 2, CcdIndex = 0, CoreClock = 4700, CoreClockMax = peak - 50 }
    ];
}
