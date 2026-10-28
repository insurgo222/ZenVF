using ZenVF.Models;

namespace ZenVF.Services;

/// <summary>
/// Estimates the external clock (eCLK) ratio from the HWiNFO-style maximum
/// displayed CPU core clock read while the window is starting up, before the
/// user starts any test. The LHM "Core #N" sensor's runtime Max value is used;
/// "Core #N (Effective)" is deliberately ignored because it is a derived
/// effective clock and drifts under load.
///
/// The denominator is the current PBO maximum actual frequency, e.g. 5425 MHz
/// for a 9800X3D with PBO +200. The highest recorded display value is used
/// directly; there is no percentile, median, consistency check or stress-test
/// sample. The resulting BCLK is carried up to two decimal places, matching
/// the precision exposed by BIOS eCLK controls.
/// </summary>
public static class EclkDetector
{
    public const double MinimumRatio = 1.0025;
    public const double MaximumRatio = 1.350;

    public readonly record struct Estimate(
        bool Detected,
        double Ratio,
        double BclkMHz,
        int CoreCount,
        int SampleCount,
        double PeakClockMHz,
        double PboMaxMHz,
        double BusSpeedMHz);

    public static Estimate DetectStartup(
        IEnumerable<SensorCoreSample> samples,
        double pboMaxMHz,
        double busSpeedMHz = 0,
        double minimumRatio = MinimumRatio)
    {
        List<SensorCoreSample> valid = samples
            .Where(s => SensorValueSanity.IsPlausibleClock(DisplayMax(s)))
            .Where(s => DisplayMax(s) >= 800)
            .ToList();

        if (valid.Count == 0 || pboMaxMHz <= 0)
        {
            return new Estimate(false, 1.0, 100.0, 0, valid.Count, 0, Math.Max(0, pboMaxMHz), busSpeedMHz);
        }

        double peakClockMHz = valid.Max(DisplayMax);
        double coreRatio = peakClockMHz / pboMaxMHz;

        // LHM's Core #N clock can remain at the nominal P-state value when an
        // external clock is active, while its Bus Speed sensor still reflects
        // the real BCLK. Take the larger of the two valid sources so neither a
        // stale Core #N value nor a single low bus sample lowers eCLK.
        double busRatio = busSpeedMHz > 0 ? busSpeedMHz / 100.0 : 0;
        double rawRatio = coreRatio;
        if (busRatio >= minimumRatio && busRatio > rawRatio)
        {
            rawRatio = busRatio;
        }

        bool detected = rawRatio >= minimumRatio && rawRatio <= MaximumRatio;

        int distinctCores = valid
            .Select(s => (s.CcdIndex, s.CoreId))
            .Distinct()
            .Count();

        if (!detected)
        {
            return new Estimate(false, 1.0, 100.0, distinctCores, valid.Count, peakClockMHz, pboMaxMHz, busSpeedMHz);
        }

        // BIOS exposes eCLK/BCLK2 with two decimal places. Any fraction in the
        // third decimal carries into the second one ("有就入"), so 103.688 or
        // 103.689 both become 103.69 instead of falling back to 103.68.
        double bclkMHz = Math.Ceiling((rawRatio * 100.0 * 100.0) - 1e-9) / 100.0;
        if (bclkMHz / 100.0 > MaximumRatio)
        {
            return new Estimate(false, 1.0, 100.0, distinctCores, valid.Count, peakClockMHz, pboMaxMHz, busSpeedMHz);
        }

        double ratio = Math.Round(bclkMHz / 100.0, 5);
        return new Estimate(
            true,
            ratio,
            bclkMHz,
            distinctCores,
            valid.Count,
            peakClockMHz,
            pboMaxMHz,
            busSpeedMHz);
    }

    private static double DisplayMax(SensorCoreSample sample) =>
        Math.Max(sample.CoreClock, sample.CoreClockMax);
}
