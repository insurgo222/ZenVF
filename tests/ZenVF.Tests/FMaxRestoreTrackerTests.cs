using ZenVF.Interop;

namespace ZenVF.Tests;

public class FMaxRestoreTrackerTests
{
    [Fact]
    public void FirstWriteCapturesTheCurrentCeiling()
    {
        FMaxRestoreTracker tracker = new();

        Assert.True(tracker.EnsureCaptured(() => 5450u));

        Assert.Equal(5450u, tracker.Target);
        Assert.True(tracker.HasPendingRestore);
    }

    [Fact]
    public void FailedReadRefusesTheWriteAndCapturesNothing()
    {
        FMaxRestoreTracker tracker = new();

        Assert.False(tracker.EnsureCaptured(() => null));

        Assert.Null(tracker.Target);
    }

    [Fact]
    public void LaterWritesNeverReplaceTheOriginal()
    {
        FMaxRestoreTracker tracker = new();
        tracker.EnsureCaptured(() => 5450u);
        tracker.RecordWrite(5300, success: true, readBack: 5300);

        int reads = 0;
        Assert.True(tracker.EnsureCaptured(() => { reads++; return 5300u; }));

        Assert.Equal(0, reads);
        Assert.Equal(5450u, tracker.Target);
    }

    [Fact]
    public void VerifiedRestoreClearsTheTarget()
    {
        FMaxRestoreTracker tracker = new();
        tracker.EnsureCaptured(() => 5450u);
        tracker.RecordWrite(5300, success: true, readBack: 5300);

        tracker.RecordWrite(5450, success: true, readBack: 5450);

        Assert.False(tracker.HasPendingRestore);
    }

    [Theory]
    [InlineData(false, 5450u)] // write failed
    [InlineData(true, 0u)]     // read-back failed
    [InlineData(true, 5425u)]  // read-back differs
    public void UnverifiedRestoreKeepsTheTarget(bool success, uint readBack)
    {
        FMaxRestoreTracker tracker = new();
        tracker.EnsureCaptured(() => 5450u);
        tracker.RecordWrite(5300, success: true, readBack: 5300);

        tracker.RecordWrite(5450, success, readBack);

        Assert.Equal(5450u, tracker.Target);
    }

    [Fact]
    public void AfterARestoreTheNextDirtyPeriodCapturesAFreshValue()
    {
        // Another tool may change FMax between ZenVF runs; the next run must
        // restore to that value, not to the one from the previous run.
        FMaxRestoreTracker tracker = new();
        tracker.EnsureCaptured(() => 5450u);
        tracker.RecordWrite(5450, success: true, readBack: 5450);

        tracker.EnsureCaptured(() => 5500u);

        Assert.Equal(5500u, tracker.Target);
    }
}
