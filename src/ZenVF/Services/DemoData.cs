using ZenVF.Models;

namespace ZenVF.Services;

public static class DemoData
{
    public const string CpuName = "AMD Ryzen 9 9950X3D 16-Core Processor";
    private const int DemoCoreCount = 16;

    /// <summary>
    /// Debug/demo-only eCLK ratio used by <c>--demo-eclk</c> to exercise the
    /// adjusted labels and exports without real hardware.
    /// </summary>
    public static double EclkRatio { get; set; } = 1.0;

    public static CpuDescriptor CreateCpu()
    {
        return new CpuDescriptor
        {
            Name = CpuName,
            Vendor = "AuthenticAMD",
            Family = 0x1A,
            Model = 0x44,
            Stepping = 0,
            IsAmd = true,
            IsZen4 = false,
            IsZen5 = true,
            IsApu = false,
            PhysicalCores = 16,
            LogicalCores = 32,
            CcdCount = 2,
            ArchitectureLabel = "Zen 5",
            Codename = "Granite Ridge (demo)",
            ModelToken = "9950X3D",
            StockFMaxMHz = 5700,
            BaseClockMHz = 4300,
            FMaxCeilingOffsetMHz = 0
        };
    }

    public static CpuTopology CreateTopology()
    {
        return new CpuTopology
        {
            Ccds = new List<CcdInfo>
            {
                new()
                {
                    Index = 0,
                    Name = "CCD0",
                    ProcessorMask = 0x0000FFFF,
                    PhysicalCoreCount = 8,
                    LogicalProcessors = Enumerable.Range(0, 16).ToList(),
                    CoreLogicalProcessors = Enumerable.Range(0, 8)
                        .Select(i => (IReadOnlyList<int>)new[] { i * 2, (i * 2) + 1 })
                        .ToList(),
                    Is3DCache = true
                },
                new()
                {
                    Index = 1,
                    Name = "CCD1",
                    ProcessorMask = 0xFFFF0000,
                    PhysicalCoreCount = 8,
                    LogicalProcessors = Enumerable.Range(16, 16).ToList(),
                    CoreLogicalProcessors = Enumerable.Range(0, 8)
                        .Select(i => (IReadOnlyList<int>)new[] { 16 + (i * 2), 16 + (i * 2) + 1 })
                        .ToList(),
                    Is3DCache = false
                }
            }
        };
    }

    public static SweepSettings CreateSettings()
    {
        return new SweepSettings
        {
            StartFMax = 5900,
            EndFMax = 4300,
            StepMHz = 100,
            WarmupSeconds = 5,
            SampleSeconds = 5,
            SelectedCcds = new HashSet<int> { 0, 1 },
            RestoreAfterTest = true
        };
    }

    /// <summary>
    /// Creates one full set of per-core voltages for a demo FMax step. The
    /// per-core offsets change with frequency, so the highest-voltage core is
    /// deliberately not the same core at every step.
    /// </summary>
    public static FrequencyCoreVoltages CreateCoreVoltages(int requestedFMax)
    {
        // The chart plots the measured effective clock, not the FMax setpoint.
        double targetClock = 4300 + ((requestedFMax - 4300) * (1200.0 / 1600.0));
        double distance = targetClock - 4300;
        double packagePower = 88 + (distance * 0.045);
        double tctl = 52 + (distance * 0.014);
        List<CoreVoltageSample> cores = new();

        for (int coreId = 1; coreId <= DemoCoreCount; coreId++)
        {
            int ccd = coreId <= 8 ? 0 : 1;
            double clock = targetClock - 8 - (ccd * 6)
                + (Math.Sin((requestedFMax * 0.021) + (coreId * 0.83) + ccd) * 4)
                + ((coreId % 3) * 1.5);
            double voltage = 0.940
                + (0.00024 * distance)
                + (0.00000003 * distance * distance)
                + (ccd * 0.0105);

            // Per-core V/F spread. The slow sine terms make the worst core
            // move between steps, which is exactly what the per-frequency
            // max-core selection is meant to expose.
            voltage += 0.0018
                + (0.0046 * Math.Sin((coreId * 1.37) + (requestedFMax * 0.0087)))
                + (0.0018 * Math.Cos((coreId * 0.71) - (requestedFMax * 0.0043)));

            double temperature = tctl + (ccd * 1.2)
                + (Math.Sin((requestedFMax * 0.01) + coreId) * 0.7);
            cores.Add(new CoreVoltageSample
            {
                CoreId = coreId,
                CcdIndex = ccd,
                ClockMHz = Math.Round(clock * EclkRatio, 0),
                Voltage = Math.Round(voltage, 4),
                Temperature = Math.Round(temperature, 1)
            });
        }

        return new FrequencyCoreVoltages
        {
            RequestedFMax = requestedFMax,
            EffectiveFMax = requestedFMax,
            RequestedFMaxCeiling = requestedFMax,
            EffectiveFMaxCeiling = requestedFMax,
            PackagePower = Math.Round(packagePower, 1),
            Tctl = Math.Round(tctl, 1),
            Cores = cores
        };
    }

    public static SweepPoint? CreatePointForCcd(FrequencyCoreVoltages frequency, int ccdIndex)
    {
        CoreVoltageSample? worstCore = frequency.Cores
            .Where(c => c.CcdIndex == ccdIndex)
            .OrderByDescending(c => c.Voltage)
            .ThenBy(c => c.CoreId)
            .FirstOrDefault();
        if (worstCore is null)
        {
            return null;
        }

        return new SweepPoint
        {
            CcdIndex = ccdIndex,
            CoreId = worstCore.CoreId,
            RequestedFMax = frequency.RequestedFMax,
            EffectiveFMax = frequency.EffectiveFMax,
            RequestedFMaxCeiling = frequency.RequestedFMaxCeiling,
            EffectiveFMaxCeiling = frequency.EffectiveFMaxCeiling,
            ClockMHz = worstCore.ClockMHz,
            Voltage = worstCore.Voltage,
            Temperature = worstCore.Temperature,
            PackagePower = frequency.PackagePower,
            SampleCount = 18,
            Timestamp = DateTime.Now
        };
    }

    public static SweepRunResult CreateSingleCoreSweepResult(
        SweepSettings settings,
        CpuTopology topology)
    {
        SweepRunResult result = new()
        {
            CpuName = CpuName,
            StockFMax = 5700,
            StockFMaxCeiling = 5700,
            FMaxCeilingOffsetMHz = 0,
            Settings = settings,
            Series = new List<SweepSeries>(),
            Started = DateTime.Now.AddMinutes(-2),
            Finished = DateTime.Now,
            Note = "演示数据 · 非实测"
        };

        List<PhysicalCoreRef> cores = topology.GetPhysicalCores(settings.SelectedCcds).ToList();
        foreach (int frequency in settings.BuildFrequencies())
        {
            result.CoreVoltages.Add(CreateSingleCoreFrequency(frequency, cores));
        }

        ApplyDemoEclk(result);
        return result;
    }

    /// <summary>
    /// One frequency step of the single-core demo sweep. Every core is sampled
    /// at the same FMax setpoint, but only one core is loaded at a time; the
    /// voltage curve is intentionally lower than the all-core demo data.
    /// </summary>
    public static FrequencyCoreVoltages CreateSingleCoreFrequency(
        int requestedFMax,
        IReadOnlyList<PhysicalCoreRef> cores)
    {
        List<CoreVoltageSample> samples = new();
        double peakTemperature = 0;

        foreach (PhysicalCoreRef core in cores.OrderBy(c => c.CcdIndex).ThenBy(c => c.DisplayCoreId))
        {
            double clock = requestedFMax
                - 12
                - ((core.CoreIndexInCcd % 4) * 3)
                + (Math.Sin((core.DisplayCoreId * 0.91) + (requestedFMax * 0.0027)) * 2.8);
            double distance = Math.Max(0, clock - 4300);
            double voltage = 0.902
                + (0.000218 * distance)
                + (0.000000018 * distance * distance)
                + (core.CcdIndex * 0.009)
                + (0.0016 * Math.Sin((core.DisplayCoreId * 1.17) + (requestedFMax * 0.004)))
                + (0.0010 * Math.Cos(core.DisplayCoreId * 0.63));
            double temperature = 52
                + (core.CcdIndex * 1.0)
                + (Math.Sin((core.DisplayCoreId * 0.71) + 1.1) * 1.4);
            peakTemperature = Math.Max(peakTemperature, temperature);

            samples.Add(new CoreVoltageSample
            {
                CoreId = core.DisplayCoreId + 1,
                CcdIndex = core.CcdIndex,
                LogicalProcessor = core.LogicalProcessor,
                ClockMHz = Math.Round(clock * EclkRatio, 1),
                Voltage = Math.Round(voltage, 4),
                Temperature = Math.Round(temperature, 1),
                SampleCount = 14
            });
        }

        double distanceFromBase = Math.Max(0, requestedFMax - 4300);
        return new FrequencyCoreVoltages
        {
            RequestedFMax = requestedFMax,
            EffectiveFMax = requestedFMax,
            RequestedFMaxCeiling = requestedFMax,
            EffectiveFMaxCeiling = requestedFMax,
            PackagePower = Math.Round(27 + (distanceFromBase * 0.009), 1),
            Tctl = Math.Round(peakTemperature + 2.4, 1),
            Cores = samples
        };
    }

    public static PboAssessment CreatePboAssessment()
    {
        return new PboAssessment
        {
            Enabled = true,
            Summary = "PBO 已开启（演示）",
            Detail = "演示模式：展示 PBO 已开启时的界面状态。",
            Evidence = new List<string>
            {
                "当前最高频率 5900 MHz，高于默认 5700 MHz（演示）",
                "写入上限 5900 MHz（演示）",
                "PPT 限制 200 W 高于默认 162 W"
            },
            CurrentFMax = 5900,
            Scalar = 1.0f
        };
    }

    public static SweepRunResult CreateRunResult()
    {
        SweepSettings settings = CreateSettings();
        List<SweepSeries> series = new()
        {
            new SweepSeries { CcdIndex = 0, Label = "CCD0 | DEMO MAX" },
            new SweepSeries { CcdIndex = 1, Label = "CCD1 | DEMO MAX" }
        };

        SweepRunResult result = new()
        {
            CpuName = CpuName,
            StockFMax = 5700,
            StockFMaxCeiling = 5700,
            FMaxCeilingOffsetMHz = 0,
            Settings = settings,
            Series = series,
            Started = DateTime.Now.AddMinutes(-3),
            Finished = DateTime.Now,
            Note = "演示数据 · 非实测"
        };

        foreach (int f in settings.BuildFrequencies())
        {
            FrequencyCoreVoltages frequency = CreateCoreVoltages(f);
            result.CoreVoltages.Add(frequency);
            for (int ccd = 0; ccd < 2; ccd++)
            {
                SweepPoint? point = CreatePointForCcd(frequency, ccd);
                if (point is not null)
                {
                    series[ccd].Add(point);
                }
            }
        }

        ApplyDemoEclk(result);
        return result;
    }

    public static void ApplyDemoEclk(SweepRunResult result)
    {
        if (EclkRatio <= 1.0)
        {
            return;
        }

        result.EclkRatio = EclkRatio;
        result.EclkSampleCount = 136;
    }
}

public sealed class DemoSensorService : ISensorService
{
    private readonly Random _random = new(42);
    private double _phase;

    public bool IsAvailable => true;
    public string? Error => null;

    public void Start()
    {
    }

    public SensorSnapshot Sample()
    {
        _phase += 0.45;
        double baseClock = 5160 + (Math.Sin(_phase) * 18) - _random.Next(0, 14);
        double baseVoltage = 0.940 + (0.00024 * (baseClock - 4400)) + (0.00000003 * Math.Pow(baseClock - 4400, 2));
        List<SensorCoreSample> cores = new();

        for (int coreId = 1; coreId <= 16; coreId++)
        {
            int ccd = coreId <= 8 ? 0 : 1;
            double coreClock = baseClock - (ccd * 8) + (Math.Sin(coreId * 0.83) * 3) - _random.NextDouble() * 8;
            double effectiveClock = coreClock * DemoData.EclkRatio;
            double voltage = baseVoltage + (ccd * 0.0105)
                + 0.0018
                + (0.0046 * Math.Sin((coreId * 1.37) + (_phase * 0.12)))
                + (_random.NextDouble() * 0.0007);
            cores.Add(new SensorCoreSample
            {
                CoreId = coreId,
                CcdIndex = ccd,
                CoreClock = coreClock,
                EffectiveClock = effectiveClock,
                Vid = voltage,
                Temperature = 58 + (ccd * 1.5) + (_random.NextDouble() * 6)
            });
        }

        List<CcdSensorSample> ccdSamples = new();
        foreach (IGrouping<int, SensorCoreSample> group in cores.GroupBy(c => c.CcdIndex))
        {
            SensorCoreSample worst = group.OrderByDescending(c => c.Vid).First();
            ccdSamples.Add(new CcdSensorSample
            {
                CcdIndex = group.Key,
                CoreId = worst.CoreId,
                ClockMHz = worst.EffectiveClock,
                Voltage = worst.Vid,
                Temperature = worst.Temperature
            });
        }

        return new SensorSnapshot
        {
            Timestamp = DateTime.Now,
            Cores = cores,
            Ccds = ccdSamples,
            Tctl = 64 + (Math.Sin(_phase * 0.4) * 3),
            PackagePower = 138 + (Math.Sin(_phase * 0.3) * 8),
            PptLimit = 200,
            TdcLimit = 140,
            EdcLimit = 200,
            AverageEffectiveClock = cores.Average(c => c.EffectiveClock),
            HasPerCoreVoltage = true,
            VoltageSource = "DEMO",
            VddcrVdd = cores.Average(c => c.Vid),
            VddcrVddSource = "DEMO"
        };
    }

    public void Dispose()
    {
    }
}
