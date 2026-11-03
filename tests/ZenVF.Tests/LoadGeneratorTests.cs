using ZenVF.Models;
using ZenVF.Services;

namespace ZenVF.Tests;

public class LoadGeneratorTests
{
    [Fact]
    public void ActiveThreadCountIsExactAcrossRestarts()
    {
        // Logical CPU 0 always exists; one SMT-free "core" keeps the test light.
        PhysicalCoreRef core = new(0, 0, 0, 0, new[] { 0 });
        using LoadGenerator load = new();

        for (int run = 0; run < 3; run++)
        {
            Assert.Empty(load.StartSingleCore(core));
            Assert.True(SpinWait.SpinUntil(() => load.ActiveThreads == 1, 2000), $"run {run}: thread did not start");

            load.Stop();
            Assert.Equal(0, load.ActiveThreads);
        }
    }

    [Fact]
    public void StopWithoutStartLeavesZero()
    {
        using LoadGenerator load = new();

        load.Stop();

        Assert.Equal(0, load.ActiveThreads);
    }
}
