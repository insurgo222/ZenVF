namespace ZenVF.Models;

public sealed class SweepSettings
{
    // These are user-facing actual boost frequencies. SweepEngine converts
    // them to the SMU's FMax ceiling (actual + CPU ceiling offset) on write.
    public int StartFMax { get; set; }
    public int EndFMax { get; set; }
    public int StepMHz { get; set; } = 100;
    // 9800X3D testing showed only about 0.01 V difference between 5+5 and
    // 10+10, so 5+5 is the default. Longer times remain available for noisy
    // systems or for a final, more conservative run.
    public int WarmupSeconds { get; set; } = 5;
    public int SampleSeconds { get; set; } = 5;
    public HashSet<int> SelectedCcds { get; set; } = new();
    public bool RestoreAfterTest { get; set; } = true;

    public int StepCount => BuildFrequencies().Count;

    public int ApproximateDurationSeconds =>
        StepCount * (Math.Max(0, WarmupSeconds) + Math.Max(1, SampleSeconds));

    /// <summary>
    /// Rough runtime for the single-core sweep: every frequency step is
    /// repeated once per physical core, plus a short settling pause.
    /// </summary>
    public int ApproximateSingleCoreDurationSeconds(int coreCount) =>
        StepCount
        * Math.Max(0, coreCount)
        * (Math.Max(0, WarmupSeconds) + Math.Max(1, SampleSeconds) + 1);

    public IReadOnlyList<int> BuildFrequencies()
    {
        List<int> values = new();
        if (StepMHz <= 0 || StartFMax < EndFMax)
        {
            return values;
        }

        for (int f = StartFMax; f >= EndFMax; f -= StepMHz)
        {
            values.Add(f);
        }

        if (values.Count == 0 || values[^1] != EndFMax)
        {
            values.Add(EndFMax);
        }

        return values;
    }

    public string? Validate(int? stockFMax, int? baseClockMHz = null)
    {
        if (StepMHz < 1)
        {
            return "步进必须大于 0 MHz。";
        }

        if (StartFMax <= 0 || EndFMax <= 0)
        {
            return "频率必须大于 0 MHz。";
        }

        if (StartFMax < EndFMax)
        {
            return "起始频率不能低于结束频率。";
        }

        if (StartFMax - EndFMax < StepMHz)
        {
            return "起始和结束频率之间至少要有一个步进。";
        }

        if (WarmupSeconds is < 0 or > 60)
        {
            return "预热时间应在 0–60 秒之间。";
        }

        if (SampleSeconds is < 1 or > 120)
        {
            return "采样时间应在 1–120 秒之间。";
        }

        if (SelectedCcds.Count == 0)
        {
            return "请至少选择一个 CCD。";
        }

        if (stockFMax is int stock)
        {
            if (StartFMax > stock + 200)
            {
                return $"起始频率超出建议上限（默认最高 +200 MHz = {stock + 200} MHz）。";
            }

            int minEnd = baseClockMHz ?? (stock - 1000);
            if (EndFMax < minEnd)
            {
                return $"结束频率低于建议下限（{minEnd} MHz）。";
            }
        }

        return null;
    }
}

/// <summary>
/// Shared sanity checks for hardware sensor values. Some drivers report
/// voltage in millivolts instead of volts; a single 1100 mV sample mixed with
/// 1.1 V samples makes the chart axis collapse. Normalize before plotting.
/// </summary>
public static class SensorValueSanity
{
    public const double MinVoltage = 0.30;
    public const double MaxVoltage = 2.00;

    public static double NormalizeVoltage(double value)
    {
        // Accept both 1.10 (V) and 1100 (mV), but do not turn 1.10 into 0.0011.
        return value is >= 300 and <= 2000 ? value / 1000.0 : value;
    }

    public static bool IsPlausibleVoltage(double value) =>
        value is >= MinVoltage and <= MaxVoltage;

    public static bool IsPlausibleClock(double value) =>
        value is >= 100 and <= 10000;
}

public sealed class SweepPoint
{
    public int CcdIndex { get; init; }
    /// <summary>LibreHardwareMonitor sensor index; 1-based.</summary>
    public int CoreId { get; init; }
    /// <summary>Zero-based core number used by the UI, logs and CSV.</summary>
    public int DisplayCoreId => CoreId > 0 ? CoreId - 1 : CoreId;
    /// <summary>Actual maximum boost target entered by the user, in MHz.</summary>
    public int RequestedFMax { get; init; }
    /// <summary>Actual maximum boost read back from the SMU, in MHz.</summary>
    public int EffectiveFMax { get; init; }
    /// <summary>Raw SMU FMax ceiling written for this step, in MHz.</summary>
    public int RequestedFMaxCeiling { get; init; }
    /// <summary>Raw SMU FMax ceiling read back for this step, in MHz.</summary>
    public int EffectiveFMaxCeiling { get; init; }
    public double ClockMHz { get; init; }
    public double Voltage { get; init; }
    public double Temperature { get; init; }
    public double PackagePower { get; init; }
    public int SampleCount { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

public sealed class SweepSeries
{
    public required int CcdIndex { get; init; }
    public required string Label { get; init; }
    public List<SweepPoint> Points { get; } = new();

    public void Add(SweepPoint point)
    {
        Points.Add(point);
        Points.Sort((a, b) => a.RequestedFMax.CompareTo(b.RequestedFMax));
    }
}

public sealed class SweepRunResult
{
    public required string CpuName { get; init; }
    /// <summary>Default actual maximum boost of this CPU model, in MHz.</summary>
    public required int? StockFMax { get; init; }
    /// <summary>Default SMU FMax ceiling of this CPU model, in MHz.</summary>
    public int? StockFMaxCeiling { get; init; }
    /// <summary>Difference between the SMU ceiling and the actual boost ceiling.</summary>
    public int FMaxCeilingOffsetMHz { get; init; }
    public required SweepSettings Settings { get; init; }
    /// <summary>
    /// Per-CCD maximum-voltage curves for an all-core sweep. Empty for a
    /// single-core sweep, which is charted directly from <see cref="CoreVoltages"/>.
    /// </summary>
    public required List<SweepSeries> Series { get; init; }
    /// <summary>
    /// One entry per tested FMax step, containing the median voltage of every
    /// core. For an all-core run it feeds the per-core chart and the per-core
    /// voltage window; for a single-core run every sample was collected with
    /// only that core loaded.
    /// </summary>
    public List<FrequencyCoreVoltages> CoreVoltages { get; } = new();
    public DateTime Started { get; init; } = DateTime.Now;
    public DateTime? Finished { get; set; }
    public string? Note { get; set; }
    /// <summary>Whether the original SMU FMax ceiling was restored after the run; null when kept intentionally.</summary>
    public bool? RestoreSucceeded { get; set; }
    /// <summary>
    /// Estimated external clock ratio: highest runtime Max of the Core #N
    /// display clock read at startup divided by the current PBO maximum actual
    /// frequency. 1.0 means eCLK is off or not detectable; values above 1.0 mean
    /// the CPU core clock is driven by an external BCLK.
    /// </summary>
    public double? EclkRatio { get; set; }
    /// <summary>Number of startup Core #N samples used for the eCLK estimate.</summary>
    public int? EclkSampleCount { get; set; }

    public bool IsEclkDetected => EclkRatio is > 1.0;
    public double EclkBclkMHz => Math.Round((EclkRatio ?? 1.0) * 100.0, 3);

    /// <summary>
    /// Converts the nominal FMax setpoint to the actual frequency seen when
    /// an external clock generator scales the core clock. Rounds the product
    /// to the nearest MHz so small bus-speed jitter does not drop a step by
    /// one. For example, 5425 × 1.0369 becomes 5625 MHz.
    /// </summary>
    public int ToEclkAdjustedFMax(int nominalFMax)
    {
        double ratio = EclkRatio ?? 1.0;
        return (int)Math.Round(nominalFMax * ratio, MidpointRounding.AwayFromZero);
    }
}

public sealed class CoreVoltageSample
{
    /// <summary>LibreHardwareMonitor sensor index; 1-based.</summary>
    public int CoreId { get; init; }
    /// <summary>Zero-based core number used by the UI, logs and CSV.</summary>
    public int DisplayCoreId => CoreId > 0 ? CoreId - 1 : CoreId;
    public int CcdIndex { get; init; }
    /// <summary>First logical processor of the physical core used by the single-core load; -1 for all-core samples.</summary>
    public int LogicalProcessor { get; init; } = -1;
    public double ClockMHz { get; init; }
    public double Voltage { get; init; }
    public double Temperature { get; init; }
    public int SampleCount { get; init; }
}

public sealed class FrequencyCoreVoltages
{
    /// <summary>Actual maximum boost target for this step, in MHz.</summary>
    public int RequestedFMax { get; init; }
    /// <summary>Actual maximum boost read back from the SMU, in MHz.</summary>
    public int EffectiveFMax { get; init; }
    /// <summary>Raw SMU FMax ceiling written for this step, in MHz.</summary>
    public int RequestedFMaxCeiling { get; init; }
    /// <summary>Raw SMU FMax ceiling read back for this step, in MHz.</summary>
    public int EffectiveFMaxCeiling { get; init; }
    public double PackagePower { get; init; }
    public double Tctl { get; init; }
    public List<CoreVoltageSample> Cores { get; init; } = new();

    public CoreVoltageSample? MaxVoltageCore =>
        Cores.Where(c => SensorValueSanity.IsPlausibleVoltage(c.Voltage))
            .OrderByDescending(c => c.Voltage)
            .ThenBy(c => c.CoreId)
            .FirstOrDefault();
}

public sealed class SensorCoreSample
{
    /// <summary>LibreHardwareMonitor sensor index; 1-based.</summary>
    public int CoreId { get; init; }
    /// <summary>Zero-based core number used by the UI, logs and CSV.</summary>
    public int DisplayCoreId => CoreId > 0 ? CoreId - 1 : CoreId;
    public int CcdIndex { get; init; }
    /// <summary>LibreHardwareMonitor's discrete core clock sensor ("Core #N"), before eCLK correction.</summary>
    public double CoreClock { get; init; }
    /// <summary>Runtime maximum of the "Core #N" display clock sensor (HWiNFO Max column).</summary>
    public double CoreClockMax { get; init; }
    /// <summary>Nominal FMax setpoint paired with this sample, used to estimate eCLK.</summary>
    public double NominalClockMHz { get; init; }
    public double EffectiveClock { get; init; }
    public double Vid { get; init; }
    public double Temperature { get; init; }
}

public sealed class CcdSensorSample
{
    public int CcdIndex { get; init; }
    /// <summary>LibreHardwareMonitor sensor index; 1-based.</summary>
    public int CoreId { get; init; }
    /// <summary>Zero-based core number used by the UI, logs and CSV.</summary>
    public int DisplayCoreId => CoreId > 0 ? CoreId - 1 : CoreId;
    public double ClockMHz { get; init; }
    public double Voltage { get; init; }
    public double Temperature { get; init; }
}

public sealed class SensorSnapshot
{
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public List<SensorCoreSample> Cores { get; init; } = new();
    public List<CcdSensorSample> Ccds { get; init; } = new();
    public double Tctl { get; init; }
    public double PackagePower { get; init; }
    public double PptLimit { get; init; }
    public double TdcLimit { get; init; }
    public double EdcLimit { get; init; }
    public double AverageEffectiveClock { get; init; }
    /// <summary>LibreHardwareMonitor "Bus Speed" sensor, in MHz.</summary>
    public double BusSpeedMHz { get; init; }
    public bool HasPerCoreVoltage { get; init; }
    public string VoltageSource { get; init; } = "未知";
    /// <summary>SVI3 VDDCR_VDD package-rail voltage used by the per-core isolated test.</summary>
    public double VddcrVdd { get; init; }
    public string VddcrVddSource { get; init; } = "未知";
    public bool HasData => Cores.Count > 0 || Tctl > 0;
}

public sealed class PboAssessment
{
    public bool? Enabled { get; init; }
    public required string Summary { get; init; }
    public required string Detail { get; init; }
    public List<string> Evidence { get; init; } = new();
    /// <summary>Current actual maximum boost, in MHz.</summary>
    public int? CurrentFMax { get; init; }
    public float? Scalar { get; init; }
}
