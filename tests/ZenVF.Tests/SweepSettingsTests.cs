using ZenVF.Models;

namespace ZenVF.Tests;

public class SweepSettingsTests
{
    [Fact]
    public void FrequenciesStepDownAndIncludeTheEnd()
    {
        Assert.Equal(new[] { 5000, 4900, 4800, 4700 }, Settings(5000, 4700).BuildFrequencies());
        Assert.Equal(new[] { 5000, 4900, 4800, 4750 }, Settings(5000, 4750).BuildFrequencies());
    }

    [Fact]
    public void ValidSettingsPass()
    {
        Assert.Null(Settings(5425, 4700).Validate(stockFMax: 5225, baseClockMHz: 4700));
    }

    [Fact]
    public void KnownModelIsCappedAtStockPlus200()
    {
        Assert.Null(Settings(5425, 4700).Validate(5225, 4700));
        Assert.NotNull(Settings(5426, 4700).Validate(5225, 4700));
    }

    [Fact]
    public void KnownModelEndIsBoundedByBaseClock()
    {
        Assert.NotNull(Settings(5225, 4600).Validate(5225, 4700));
    }

    [Fact]
    public void UnknownModelIsCappedAtTheCurrentFMax()
    {
        Assert.Null(Settings(5400, 4700).Validate(null, null, currentFMax: 5400));
        Assert.NotNull(Settings(5401, 4700).Validate(null, null, currentFMax: 5400));
    }

    [Fact]
    public void UnknownModelWithoutCurrentFMaxIsRejected()
    {
        Assert.NotNull(Settings(5000, 4700).Validate(null, null, currentFMax: null));
    }

    [Theory]
    [InlineData(4700, 5000, 100, 5, 5)]  // start below end
    [InlineData(5000, 4950, 100, 5, 5)]  // range smaller than one step
    [InlineData(5000, 4700, 0, 5, 5)]    // zero step
    [InlineData(5000, 4700, 100, 61, 5)] // warmup too long
    [InlineData(5000, 4700, 100, 5, 0)]  // no sampling
    public void InvalidInputsAreRejected(int start, int end, int step, int warmup, int sample)
    {
        SweepSettings settings = Settings(start, end);
        settings.StepMHz = step;
        settings.WarmupSeconds = warmup;
        settings.SampleSeconds = sample;

        Assert.NotNull(settings.Validate(5700, 4300));
    }

    [Fact]
    public void AtLeastOneCcdIsRequired()
    {
        SweepSettings settings = Settings(5000, 4700);
        settings.SelectedCcds.Clear();

        Assert.NotNull(settings.Validate(5700, 4300));
    }

    private static SweepSettings Settings(int start, int end) => new()
    {
        StartFMax = start,
        EndFMax = end,
        StepMHz = 100,
        WarmupSeconds = 5,
        SampleSeconds = 5,
        SelectedCcds = new HashSet<int> { 0 }
    };
}
