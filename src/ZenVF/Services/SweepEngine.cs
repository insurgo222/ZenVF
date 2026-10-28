using System.Diagnostics;
using ZenVF.Interop;
using ZenVF.Models;

namespace ZenVF.Services;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error,
    Debug
}

public sealed class LogEntry
{
    public DateTime Time { get; init; } = DateTime.Now;
    public LogLevel Level { get; init; }
    public required string Message { get; init; }
    public string TimeText => Time.ToString("HH:mm:ss");
    public string LevelText => Level switch
    {
        LogLevel.Info => "INFO",
        LogLevel.Success => " OK ",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Debug => "DBG ",
        _ => "INFO"
    };
}

public sealed class SweepProgress
{
    public int StepIndex { get; init; }
    public int StepCount { get; init; }
    /// <summary>Actual maximum boost target for this step, in MHz.</summary>
    public int Frequency { get; init; }
    /// <summary>Raw SMU FMax ceiling written for this step, in MHz.</summary>
    public int FrequencyCeiling { get; init; }
    public required string Stage { get; init; }
    public double Fraction { get; init; }
    public TimeSpan Elapsed { get; init; }

    public int Percent => StepCount <= 0 ? 0 : (int)Math.Round(Math.Clamp(Fraction, 0, 1) * 100);
}

public sealed class SweepEngine
{
    private const double MaxSafeTemperature = 105.0;
    // A small positive clock overshoot is normal boost behaviour and should
    // not interrupt a run with a warning. Keep the warning for real SMU/PBO
    // failures, where the measured clock is far above the requested ceiling.
    private const int ClockOvershootToleranceMHz = 250;
    private const int FMaxReadbackToleranceMHz = 50;

    public async Task<SweepRunResult> RunAsync(
        SmuController smu,
        ISensorService sensors,
        CpuTopology topology,
        CpuDescriptor cpu,
        SweepSettings settings,
        uint originalFMaxCeiling,
        Action<LogEntry> log,
        Action<SweepProgress> progress,
        Action<FrequencyCoreVoltages> coreVoltagesReady,
        Action<SweepPoint> pointReady,
        CancellationToken cancellationToken)
    {
        DateTime started = DateTime.Now;
        Stopwatch stopwatch = Stopwatch.StartNew();
        const string label = "全核";
        string prefix = label + " ";
        List<SweepSeries> series = settings.SelectedCcds
            .OrderBy(i => i)
            .Select(i => new SweepSeries { CcdIndex = i, Label = $"CCD{i} | MAX CORE" })
            .ToList();

        SweepRunResult result = new()
        {
            CpuName = cpu.Name,
            StockFMax = cpu.StockFMaxMHz,
            StockFMaxCeiling = cpu.StockFMaxCeilingMHz,
            FMaxCeilingOffsetMHz = cpu.FMaxCeilingOffsetMHz,
            Settings = settings,
            Series = series,
            Started = started
        };

        List<int> frequencies = settings.BuildFrequencies().ToList();
        using LoadGenerator load = new();
        bool restoreDone = false;
        bool keepFinal = !settings.RestoreAfterTest;
        bool perCoreVoltageWarningShown = false;
        bool voltageSourceLogged = false;

        try
        {
            string ceilingNote = cpu.FMaxCeilingOffsetMHz > 0
                ? $"，SMU 上限自动加 {cpu.FMaxCeilingOffsetMHz} MHz"
                : string.Empty;
            log(new LogEntry { Level = LogLevel.Info, Message = $"{prefix}扫描计划：{frequencies.Count} 个实际最高加速点（{frequencies[0]} → {frequencies[^1]} MHz{ceilingNote}），{settings.WarmupSeconds}s 预热 / {settings.SampleSeconds}s 采样。" });
            log(new LogEntry { Level = LogLevel.Info, Message = $"{prefix}负载模式：128-bit SSE2（Prime95 SSE 风格，不启用 AVX/AVX2）。" });
            foreach (string warning in load.Start(topology, settings.SelectedCcds))
            {
                log(new LogEntry { Level = LogLevel.Warning, Message = warning });
            }

            await Task.Delay(350, cancellationToken).ConfigureAwait(false);
            log(new LogEntry { Level = LogLevel.Info, Message = $"已启动 {load.ActiveThreads} 个{label} SSE2 负载线程（每个逻辑处理器一个）。" });
            if (load.LastError is string loadError)
            {
                log(new LogEntry { Level = LogLevel.Warning, Message = $"部分负载线程异常：{loadError}" });
            }

            await Task.Delay(550, cancellationToken).ConfigureAwait(false);

            for (int step = 0; step < frequencies.Count; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requestedActual = frequencies[step];
                uint requestedCeiling = cpu.ToCeilingFMax(requestedActual);
                progress(new SweepProgress
                {
                    StepIndex = step + 1,
                    StepCount = frequencies.Count,
                    Frequency = requestedActual,
                    FrequencyCeiling = (int)requestedCeiling,
                    Stage = "设置频率",
                    Fraction = (step / (double)frequencies.Count) + (0.05 / frequencies.Count),
                    Elapsed = stopwatch.Elapsed
                });

                SmuWriteResult write = smu.SetFMaxCeiling(requestedCeiling);
                if (!write.Success)
                {
                    throw new InvalidOperationException(write.Message);
                }

                bool readBackSucceeded = write.ReadBackFMax > 0;
                uint effectiveCeiling = readBackSucceeded ? write.ReadBackFMax : requestedCeiling;
                int effectiveActual = cpu.ToActualFMax(effectiveCeiling);
                if (!readBackSucceeded)
                {
                    log(new LogEntry
                    {
                        Level = LogLevel.Warning,
                        Message = $"FMax 写入成功但回读失败；本档按请求值继续：实际最高 {requestedActual} MHz（SMU 上限 {requestedCeiling} MHz）。"
                    });
                }
                else if (Math.Abs(effectiveActual - requestedActual) > FMaxReadbackToleranceMHz)
                {
                    log(new LogEntry
                    {
                        Level = LogLevel.Warning,
                        Message = $"FMax 回读偏差：请求实际最高 {requestedActual} MHz（SMU 上限 {requestedCeiling} MHz），回读实际最高 {effectiveActual} MHz（SMU 上限 {effectiveCeiling} MHz）。"
                    });
                }
                else
                {
                    log(new LogEntry
                    {
                        Level = LogLevel.Info,
                        Message = $"FMax 回读：实际最高 {effectiveActual} MHz（SMU 上限 {effectiveCeiling} MHz）。"
                    });
                }

                await WarmupAsync(
                        settings.WarmupSeconds,
                        step,
                        frequencies.Count,
                        requestedActual,
                        (int)requestedCeiling,
                        label,
                        stopwatch,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);

                Dictionary<int, List<SensorCoreSample>> coreSamples = new();
                List<double> packagePowers = new();
                List<double> tctlValues = new();
                DateTime sampleStart = DateTime.Now;

                while ((DateTime.Now - sampleStart).TotalSeconds < settings.SampleSeconds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SensorSnapshot snapshot = sensors.Sample();
                    if (snapshot.HasData)
                    {
                        if (!voltageSourceLogged && !string.IsNullOrWhiteSpace(snapshot.VoltageSource))
                        {
                            voltageSourceLogged = true;
                            double[] validCoreVoltages = snapshot.Cores
                                .Select(c => c.Vid)
                                .Where(SensorValueSanity.IsPlausibleVoltage)
                                .ToArray();
                            string range = validCoreVoltages.Length > 0
                                ? $" 首轮范围 {validCoreVoltages.Min():0.000}–{validCoreVoltages.Max():0.000} V（{validCoreVoltages.Length} 核）"
                                : string.Empty;
                            log(new LogEntry
                            {
                                Level = LogLevel.Info,
                                Message = $"每核电压源：{snapshot.VoltageSource}。{range}"
                            });
                        }

                        if (!snapshot.HasPerCoreVoltage && !perCoreVoltageWarningShown)
                        {
                            perCoreVoltageWarningShown = true;
                            log(new LogEntry
                            {
                                Level = LogLevel.Warning,
                                Message = "传感器未提供完整的每核电压；最高电压核心可能退化为按封装电压/核心序号选择，请用 HWiNFO 对照。"
                            });
                        }

                        foreach (SensorCoreSample coreSample in snapshot.Cores)
                        {
                            if (coreSample.EffectiveClock <= 0 && coreSample.Vid <= 0.15)
                            {
                                continue;
                            }

                            if (!coreSamples.TryGetValue(coreSample.CoreId, out List<SensorCoreSample>? list))
                            {
                                list = new List<SensorCoreSample>();
                                coreSamples[coreSample.CoreId] = list;
                            }

                            list.Add(coreSample);
                        }

                        if (snapshot.PackagePower > 0)
                        {
                            packagePowers.Add(snapshot.PackagePower);
                        }

                        if (snapshot.Tctl > 0)
                        {
                            tctlValues.Add(snapshot.Tctl);
                            if (snapshot.Tctl >= MaxSafeTemperature)
                            {
                                throw new InvalidOperationException($"温度达到 {snapshot.Tctl:0.0} °C，已中止扫描并准备恢复 FMax。");
                            }
                        }
                    }

                    double sampleFraction = (DateTime.Now - sampleStart).TotalSeconds / Math.Max(1, settings.SampleSeconds);
                    progress(new SweepProgress
                    {
                        StepIndex = step + 1,
                        StepCount = frequencies.Count,
                        Frequency = requestedActual,
                        FrequencyCeiling = (int)requestedCeiling,
                        Stage = snapshot.Tctl > 0 ? $"采样 · {label} · {snapshot.Tctl:0} °C" : $"采样 · {label}",
                        Fraction = ((step + 0.15 + (0.8 * Math.Clamp(sampleFraction, 0, 1))) / frequencies.Count),
                        Elapsed = stopwatch.Elapsed
                    });

                    int remaining = (int)((settings.SampleSeconds - (DateTime.Now - sampleStart).TotalSeconds) * 1000);
                    await Task.Delay(Math.Clamp(remaining, 150, 250), cancellationToken).ConfigureAwait(false);
                }

                double power = packagePowers.Count > 0 ? packagePowers.Average() : 0;
                double tctl = tctlValues.Count > 0 ? tctlValues.Max() : 0;
                bool anyPoint = false;

                // Reduce every core independently, then keep the full set so the
                // secondary chart can show per-core voltages at this FMax step.
                List<CoreVoltageSample> coreMedians = new();
                foreach ((int coreId, List<SensorCoreSample> list) in coreSamples.OrderBy(pair => pair.Key))
                {
                    if (list.Count == 0 || !settings.SelectedCcds.Contains(list[0].CcdIndex))
                    {
                        continue;
                    }

                    List<double> clockValues = list
                        .Select(s => s.EffectiveClock)
                        .Where(SensorValueSanity.IsPlausibleClock)
                        .ToList();
                    List<double> voltageValues = list
                        .Select(s => SensorValueSanity.NormalizeVoltage(s.Vid))
                        .Where(SensorValueSanity.IsPlausibleVoltage)
                        .ToList();
                    if (clockValues.Count == 0 || voltageValues.Count == 0)
                    {
                        continue;
                    }

                    double clock = Median(clockValues);
                    double voltage = Median(voltageValues);
                    double temperature = Median(list.Where(s => s.Temperature > 0).Select(s => s.Temperature));
                    if (!SensorValueSanity.IsPlausibleClock(clock)
                        || !SensorValueSanity.IsPlausibleVoltage(voltage))
                    {
                        continue;
                    }

                    coreMedians.Add(new CoreVoltageSample
                    {
                        CoreId = coreId,
                        CcdIndex = list[0].CcdIndex,
                        ClockMHz = Math.Round(clock, 1),
                        Voltage = Math.Round(voltage, 4),
                        Temperature = Math.Round(temperature, 1)
                    });
                }

                if (coreMedians.Count > 0)
                {
                    FrequencyCoreVoltages frequencyCores = new()
                    {
                        RequestedFMax = requestedActual,
                        EffectiveFMax = effectiveActual,
                        RequestedFMaxCeiling = (int)requestedCeiling,
                        EffectiveFMaxCeiling = (int)effectiveCeiling,
                        PackagePower = Math.Round(power, 1),
                        Tctl = Math.Round(tctl, 1),
                        Cores = coreMedians
                    };
                    result.CoreVoltages.Add(frequencyCores);
                    coreVoltagesReady(frequencyCores);

                    foreach (SweepSeries ccdSeries in series)
                    {
                        CoreVoltageSample? worstCore = coreMedians
                            .Where(c => c.CcdIndex == ccdSeries.CcdIndex)
                            .OrderByDescending(c => c.Voltage)
                            .ThenBy(c => c.CoreId)
                            .FirstOrDefault();
                        if (worstCore is null)
                        {
                            continue;
                        }

                        SweepPoint point = new()
                        {
                            CcdIndex = ccdSeries.CcdIndex,
                            CoreId = worstCore.CoreId,
                            RequestedFMax = requestedActual,
                            EffectiveFMax = effectiveActual,
                            RequestedFMaxCeiling = (int)requestedCeiling,
                            EffectiveFMaxCeiling = (int)effectiveCeiling,
                            ClockMHz = worstCore.ClockMHz,
                            Voltage = worstCore.Voltage,
                            Temperature = worstCore.Temperature,
                            PackagePower = frequencyCores.PackagePower,
                            SampleCount = coreSamples[worstCore.CoreId].Count,
                            Timestamp = DateTime.Now
                        };
                        ccdSeries.Add(point);
                        pointReady(point);
                        anyPoint = true;

                        log(new LogEntry
                        {
                            Level = LogLevel.Success,
                            Message = $"CCD{ccdSeries.CcdIndex} · Core #{worstCore.DisplayCoreId}  FMax 实际 {requestedActual} MHz（SMU 上限 {requestedCeiling} MHz） → {point.ClockMHz:0} MHz @ {point.Voltage:0.000} V  ({point.Temperature:0.0} °C, {point.PackagePower:0.0} W)"
                        });
                    }

                    CoreVoltageSample? maxCore = frequencyCores.MaxVoltageCore;
                    if (maxCore is not null)
                    {
                        log(new LogEntry
                        {
                            Level = LogLevel.Info,
                            Message = $"{prefix}最高电压核心：Core #{maxCore.DisplayCoreId} · CCD{maxCore.CcdIndex} · {maxCore.Voltage:0.000} V @ {maxCore.ClockMHz:0} MHz。"
                        });
                    }
                }

                if (coreMedians.Count == 0)
                {
                    log(new LogEntry { Level = LogLevel.Warning, Message = $"FMax 实际 {requestedActual} MHz（SMU 上限 {requestedCeiling} MHz）：没有拿到有效的核心频率/电压样本。" });
                }
                else if (!anyPoint)
                {
                    log(new LogEntry { Level = LogLevel.Warning, Message = $"FMax 实际 {requestedActual} MHz（SMU 上限 {requestedCeiling} MHz）：核心数据存在，但无法映射到所选 CCD。" });
                }

                if (tctl > 0)
                {
                    log(new LogEntry { Level = LogLevel.Debug, Message = $"封装温度峰值 {tctl:0.0} °C。" });
                }

                progress(new SweepProgress
                {
                    StepIndex = step + 1,
                    StepCount = frequencies.Count,
                    Frequency = requestedActual,
                    FrequencyCeiling = (int)requestedCeiling,
                    Stage = "已完成",
                    Fraction = (step + 1) / (double)frequencies.Count,
                    Elapsed = stopwatch.Elapsed
                });
            }

            result.Finished = DateTime.Now;
            result.Note = keepFinal ? $"{prefix}测试结束，保留最终 FMax。" : null;
            log(new LogEntry { Level = LogLevel.Success, Message = $"{prefix}测试完成，用时 {(DateTime.Now - started).TotalSeconds:0} 秒。" });
            return result;
        }
        finally
        {
            load.Stop();
            if (!keepFinal && originalFMaxCeiling is >= 400 and <= 8000)
            {
                SmuWriteResult restore = smu.SetFMaxCeiling(originalFMaxCeiling);
                restoreDone = restore.Success;
                int originalActual = cpu.ToActualFMax(originalFMaxCeiling);
                log(new LogEntry
                {
                    Level = restore.Success ? LogLevel.Success : LogLevel.Error,
                    Message = restore.Success
                        ? $"已恢复原始 FMax：SMU 上限 {originalFMaxCeiling} MHz（实际最高 {originalActual} MHz）。"
                        : $"恢复原始 FMax 失败：{restore.Message} 请立即重启系统。"
                });
            }
            else if (!keepFinal && !restoreDone)
            {
                log(new LogEntry { Level = LogLevel.Error, Message = "没有可用的原始 FMax，未能恢复。请重启系统。" });
            }

            result.RestoreSucceeded = keepFinal ? null : restoreDone;
        }
    }

    /// <summary>
    /// Runs the same FMax sweep as the all-core test, but stresses exactly one
    /// physical core at a time. At every frequency step each selected core is
    /// loaded, warmed up and sampled separately, so the resulting per-core V/F
    /// points reflect single-core load rather than all-core load.
    /// </summary>
    public async Task<SweepRunResult> RunSingleCoreSweepAsync(
        SmuController smu,
        ISensorService sensors,
        CpuTopology topology,
        CpuDescriptor cpu,
        SweepSettings settings,
        uint originalFMaxCeiling,
        Action<LogEntry> log,
        Action<SweepProgress> progress,
        Action<FrequencyCoreVoltages> coreVoltagesReady,
        CancellationToken cancellationToken)
    {
        DateTime started = DateTime.Now;
        Stopwatch stopwatch = Stopwatch.StartNew();
        List<int> frequencies = settings.BuildFrequencies().ToList();
        List<PhysicalCoreRef> cores = topology.GetPhysicalCores(settings.SelectedCcds).ToList();
        SweepRunResult result = new()
        {
            CpuName = cpu.Name,
            StockFMax = cpu.StockFMaxMHz,
            StockFMaxCeiling = cpu.StockFMaxCeilingMHz,
            FMaxCeilingOffsetMHz = cpu.FMaxCeilingOffsetMHz,
            Settings = settings,
            Series = new List<SweepSeries>(),
            Started = started
        };

        using LoadGenerator load = new();
        bool restoreDone = false;
        bool keepFinal = !settings.RestoreAfterTest;
        bool? useVddcrVdd = null;
        int missingVddcrSamples = 0;

        try
        {
            if (frequencies.Count == 0)
            {
                throw new InvalidOperationException("没有可测试的频率档位。");
            }

            if (cores.Count == 0)
            {
                throw new InvalidOperationException("所选 CCD 没有可测试的物理核心。");
            }

            int estimatedSeconds = settings.ApproximateSingleCoreDurationSeconds(cores.Count);
            string ceilingNote = cpu.FMaxCeilingOffsetMHz > 0
                ? $"，SMU 上限自动加 {cpu.FMaxCeilingOffsetMHz} MHz"
                : string.Empty;
            log(new LogEntry
            {
                Level = LogLevel.Info,
                Message = $"每核单独测试计划：{frequencies.Count} 个频率档位（{settings.StartFMax}→{settings.EndFMax} MHz），每个档位依次测试 {cores.Count} 个物理核心，每核 {settings.WarmupSeconds}s 预热 / {settings.SampleSeconds}s 采样{ceilingNote}；预计约 {FormatDuration(estimatedSeconds)}。"
            });
            log(new LogEntry
            {
                Level = LogLevel.Info,
                Message = "负载模式：每次只对一个物理核心施加 128-bit SSE2 负载（该核心的全部 SMT 线程，通常为 2 个），让物理核心完整满载；同一档位内所有核心使用同一个 SMU FMax 上限，档位间按扫描范围逐级改变。"
            });

            for (int step = 0; step < frequencies.Count; step++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requestedActual = frequencies[step];
                uint requestedCeiling = cpu.ToCeilingFMax(requestedActual);
                double stepBase = step / (double)frequencies.Count;

                progress(new SweepProgress
                {
                    StepIndex = step + 1,
                    StepCount = frequencies.Count,
                    Frequency = requestedActual,
                    FrequencyCeiling = (int)requestedCeiling,
                    Stage = "设置频率",
                    Fraction = stepBase,
                    Elapsed = stopwatch.Elapsed
                });

                log(new LogEntry
                {
                    Level = LogLevel.Info,
                    Message = $"单核档位 {step + 1}/{frequencies.Count}：实际最高 {requestedActual} MHz（SMU 上限 {requestedCeiling} MHz），开始逐核测试。"
                });

                SmuWriteResult write = smu.SetFMaxCeiling(requestedCeiling);
                if (!write.Success)
                {
                    throw new InvalidOperationException(write.Message);
                }

                bool readBackSucceeded = write.ReadBackFMax > 0;
                uint effectiveCeiling = readBackSucceeded ? write.ReadBackFMax : requestedCeiling;
                int effectiveActual = cpu.ToActualFMax(effectiveCeiling);
                if (!readBackSucceeded)
                {
                    log(new LogEntry
                    {
                        Level = LogLevel.Warning,
                        Message = $"FMax 写入成功但回读失败；按请求值继续：实际最高 {requestedActual} MHz（SMU 上限 {requestedCeiling} MHz）。"
                    });
                }
                else if (Math.Abs(effectiveActual - requestedActual) > FMaxReadbackToleranceMHz)
                {
                    log(new LogEntry
                    {
                        Level = LogLevel.Warning,
                        Message = $"FMax 回读偏差：请求实际最高 {requestedActual} MHz（SMU 上限 {requestedCeiling} MHz），回读实际最高 {effectiveActual} MHz（SMU 上限 {effectiveCeiling} MHz）。"
                    });
                }
                else
                {
                    log(new LogEntry
                    {
                        Level = LogLevel.Info,
                        Message = $"FMax 回读：实际最高 {effectiveActual} MHz（SMU 上限 {effectiveCeiling} MHz）。"
                    });
                }

                List<CoreVoltageSample> stepCores = new();
                List<double> stepPowers = new();
                List<double> stepTctls = new();

                for (int coreIndex = 0; coreIndex < cores.Count; coreIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    PhysicalCoreRef core = cores[coreIndex];
                    string coreName = $"CCD{core.CcdIndex} Core #{core.DisplayCoreId}";
                    List<int> coreCpus = (core.LogicalProcessors ?? Array.Empty<int>())
                        .Where(cpu => cpu >= 0)
                        .Distinct()
                        .OrderBy(cpu => cpu)
                        .ToList();
                    if (coreCpus.Count == 0 && core.LogicalProcessor >= 0)
                    {
                        coreCpus.Add(core.LogicalProcessor);
                    }

                    string cpuText = coreCpus.Count > 0
                        ? string.Join(" + ", coreCpus.Select(cpu => $"CPU{cpu}"))
                        : "无可用逻辑处理器";
                    double coreBase = (step + (coreIndex / (double)cores.Count)) / frequencies.Count;

                    progress(new SweepProgress
                    {
                        StepIndex = step + 1,
                        StepCount = frequencies.Count,
                        Frequency = requestedActual,
                        FrequencyCeiling = (int)requestedCeiling,
                        Stage = $"准备 {coreName}",
                        Fraction = coreBase,
                        Elapsed = stopwatch.Elapsed
                    });

                    log(new LogEntry
                    {
                        Level = LogLevel.Info,
                        Message = $"逐核单核：档位 {requestedActual} MHz → {coreName}（{cpuText}，{coreCpus.Count} 个 SMT 线程）。"
                    });

                    try
                    {
                        foreach (string warning in load.StartSingleCore(core))
                        {
                            log(new LogEntry { Level = LogLevel.Warning, Message = warning });
                        }

                        await Task.Delay(350, cancellationToken).ConfigureAwait(false);
                        int expectedThreads = Math.Max(1, coreCpus.Count);
                        if (load.ActiveThreads < expectedThreads)
                        {
                            throw new InvalidOperationException(
                                $"{coreName} 的单核 SSE2 负载线程未能全部启动（{load.ActiveThreads}/{expectedThreads}）。");
                        }

                        if (!string.IsNullOrWhiteSpace(load.LastError))
                        {
                            log(new LogEntry { Level = LogLevel.Warning, Message = load.LastError });
                        }

                        string observedText = load.ObservedProcessors.Count > 0
                            ? string.Join(" + ", load.ObservedProcessors.Select(cpu => $"CPU{cpu}"))
                            : "未知";
                        log(new LogEntry
                        {
                            Level = LogLevel.Info,
                            Message = $"{coreName} 单核 SSE2 负载已启动（{load.ActiveThreads} 个线程，实际运行于 {observedText}）；开始预热。"
                        });

                        await WarmupSingleCoreSweepAsync(
                                settings.WarmupSeconds,
                                step,
                                frequencies.Count,
                                coreIndex,
                                cores.Count,
                                core,
                                requestedActual,
                                (int)requestedCeiling,
                                stopwatch,
                                progress,
                                cancellationToken)
                            .ConfigureAwait(false);

                        List<double> clockValues = new();
                        List<double> voltageValues = new();
                        List<double> temperatureValues = new();
                        List<double> packagePowers = new();
                        List<double> tctlValues = new();
                        SensorSnapshot? lastSnapshot = null;
                        DateTime sampleStart = DateTime.Now;

                        while ((DateTime.Now - sampleStart).TotalSeconds < settings.SampleSeconds)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            SensorSnapshot snapshot = sensors.Sample();
                            lastSnapshot = snapshot;
                            if (snapshot.HasData)
                            {
                                SensorCoreSample? target = FindTargetCore(snapshot, core);
                                if (target is not null)
                                {
                                    if (useVddcrVdd is null)
                                    {
                                        if (SensorValueSanity.IsPlausibleVoltage(snapshot.VddcrVdd))
                                        {
                                            useVddcrVdd = true;
                                            log(new LogEntry
                                            {
                                                Level = LogLevel.Info,
                                                Message = "每核单独测试电压源：CPU VDDCR_VDD（SMU PM table SVI3 遥测）。"
                                            });
                                        }
                                        else if (++missingVddcrSamples >= 5)
                                        {
                                            useVddcrVdd = false;
                                            log(new LogEntry
                                            {
                                                Level = LogLevel.Warning,
                                                Message = "每核单独测试连续多个采样未读到 CPU VDDCR_VDD，已回退到 Core VID；读数可能偏低，请用 HWiNFO 对照。"
                                            });
                                        }
                                    }

                                    if (SensorValueSanity.IsPlausibleClock(target.EffectiveClock))
                                    {
                                        clockValues.Add(target.EffectiveClock);
                                    }

                                    double voltage = useVddcrVdd switch
                                    {
                                        true => snapshot.VddcrVdd,
                                        false => target.Vid,
                                        _ => 0
                                    };
                                    voltage = SensorValueSanity.NormalizeVoltage(voltage);
                                    if (SensorValueSanity.IsPlausibleVoltage(voltage))
                                    {
                                        voltageValues.Add(voltage);
                                    }

                                    if (target.Temperature > 0)
                                    {
                                        temperatureValues.Add(target.Temperature);
                                    }
                                }

                                if (snapshot.PackagePower > 0)
                                {
                                    packagePowers.Add(snapshot.PackagePower);
                                }

                                if (snapshot.Tctl > 0)
                                {
                                    tctlValues.Add(snapshot.Tctl);
                                    if (snapshot.Tctl >= MaxSafeTemperature)
                                    {
                                        throw new InvalidOperationException($"温度达到 {snapshot.Tctl:0.0} °C，已中止单核测试并准备恢复频率。");
                                    }
                                }
                            }

                            double sampleFraction = (DateTime.Now - sampleStart).TotalSeconds
                                / Math.Max(1, settings.SampleSeconds);
                            string stage = snapshot.Tctl > 0
                                ? $"采样 · 单核 · {coreName} · {snapshot.Tctl:0} °C"
                                : $"采样 · 单核 · {coreName}";
                            progress(new SweepProgress
                            {
                                StepIndex = step + 1,
                                StepCount = frequencies.Count,
                                Frequency = requestedActual,
                                FrequencyCeiling = (int)requestedCeiling,
                                Stage = stage,
                                Fraction = (step + ((coreIndex + 0.15 + (0.80 * Math.Clamp(sampleFraction, 0, 1))) / cores.Count))
                                    / frequencies.Count,
                                Elapsed = stopwatch.Elapsed
                            });

                            int remaining = (int)((settings.SampleSeconds - (DateTime.Now - sampleStart).TotalSeconds) * 1000);
                            await Task.Delay(Math.Clamp(remaining, 150, 250), cancellationToken).ConfigureAwait(false);
                        }

                        if (clockValues.Count == 0 || voltageValues.Count == 0)
                        {
                            log(new LogEntry
                            {
                                Level = LogLevel.Warning,
                                Message = $"{coreName}：没有拿到有效的单核频率/电压样本，已跳过该核心。"
                            });
                        }
                        else
                        {
                            double clock = Median(clockValues);
                            double voltage = Median(voltageValues);
                            double temperature = Median(temperatureValues);
                            if (!SensorValueSanity.IsPlausibleClock(clock)
                                || !SensorValueSanity.IsPlausibleVoltage(voltage))
                            {
                                log(new LogEntry
                                {
                                    Level = LogLevel.Warning,
                                    Message = $"{coreName}：样本通过合理性检查失败，已跳过该核心。"
                                });
                            }
                            else
                            {
                                CoreVoltageSample sample = new()
                                {
                                    CoreId = core.DisplayCoreId + 1,
                                    CcdIndex = core.CcdIndex,
                                    LogicalProcessor = core.LogicalProcessor,
                                    ClockMHz = Math.Round(clock, 1),
                                    Voltage = Math.Round(voltage, 4),
                                    Temperature = Math.Round(temperature, 1),
                                    SampleCount = Math.Min(clockValues.Count, voltageValues.Count)
                                };
                                stepCores.Add(sample);
                                stepPowers.AddRange(packagePowers);
                                stepTctls.AddRange(tctlValues);

                                log(new LogEntry
                                {
                                    Level = LogLevel.Success,
                                    Message = $"档位 {requestedActual} MHz · {coreName} 单核 → {sample.ClockMHz:0} MHz @ {sample.Voltage:0.000} V  ({sample.Temperature:0.0} °C)"
                                });

                                if (sample.ClockMHz < requestedActual * 0.70)
                                {
                                    SensorCoreSample? top = lastSnapshot?.Cores
                                        .Where(c => SensorValueSanity.IsPlausibleClock(c.EffectiveClock))
                                        .OrderByDescending(c => c.EffectiveClock)
                                        .FirstOrDefault();
                                    string topText = top is null
                                        ? string.Empty
                                        : $" 当前有效频率最高的是 CCD{top.CcdIndex} Core #{top.DisplayCoreId}（{top.EffectiveClock:0} MHz）。";
                                    log(new LogEntry
                                    {
                                        Level = LogLevel.Warning,
                                        Message = $"{coreName} 实测有效频率 {sample.ClockMHz:0} MHz 明显低于档位 {requestedActual} MHz。{topText}若确认该核心已满载，请检查核心到 LHM 传感器的映射或负载线程绑定。"
                                    });
                                }
                                else if (sample.ClockMHz > requestedActual + ClockOvershootToleranceMHz)
                                {
                                    log(new LogEntry
                                    {
                                        Level = LogLevel.Warning,
                                        Message = $"{coreName} 实测有效频率 {sample.ClockMHz:0} MHz 明显高于档位 {requestedActual} MHz，SMU FMax 可能未生效；请检查回读值和 BIOS 的 PBO/FMax 设置。"
                                    });
                                }
                            }
                        }
                    }
                    finally
                    {
                        load.Stop();
                    }

                    progress(new SweepProgress
                    {
                        StepIndex = step + 1,
                        StepCount = frequencies.Count,
                        Frequency = requestedActual,
                        FrequencyCeiling = (int)requestedCeiling,
                        Stage = $"已完成 {coreName}",
                        Fraction = (step + ((coreIndex + 1) / (double)cores.Count)) / frequencies.Count,
                        Elapsed = stopwatch.Elapsed
                    });

                    if (coreIndex < cores.Count - 1)
                    {
                        await Task.Delay(300, cancellationToken).ConfigureAwait(false);
                    }
                }

                if (stepCores.Count > 0)
                {
                    FrequencyCoreVoltages row = new()
                    {
                        RequestedFMax = requestedActual,
                        EffectiveFMax = effectiveActual,
                        RequestedFMaxCeiling = (int)requestedCeiling,
                        EffectiveFMaxCeiling = (int)effectiveCeiling,
                        PackagePower = stepPowers.Count > 0 ? Math.Round(stepPowers.Average(), 1) : 0,
                        Tctl = stepTctls.Count > 0 ? Math.Round(stepTctls.Max(), 1) : 0,
                        Cores = stepCores
                    };
                    result.CoreVoltages.Add(row);
                    coreVoltagesReady(row);

                    CoreVoltageSample? maxCore = row.MaxVoltageCore;
                    log(new LogEntry
                    {
                        Level = LogLevel.Success,
                        Message = maxCore is not null
                            ? $"档位 {requestedActual} MHz 单核完成：{stepCores.Count}/{cores.Count} 个核心有数据，最高 CCD{maxCore.CcdIndex} Core #{maxCore.DisplayCoreId} {maxCore.Voltage:0.000} V。"
                            : $"档位 {requestedActual} MHz 单核完成：{stepCores.Count}/{cores.Count} 个核心有数据。"
                    });
                }
                else
                {
                    log(new LogEntry
                    {
                        Level = LogLevel.Warning,
                        Message = $"档位 {requestedActual} MHz：所有核心都没有有效数据，已跳过该档位。"
                    });
                }

                progress(new SweepProgress
                {
                    StepIndex = step + 1,
                    StepCount = frequencies.Count,
                    Frequency = requestedActual,
                    FrequencyCeiling = (int)requestedCeiling,
                    Stage = "档位完成",
                    Fraction = (step + 1) / (double)frequencies.Count,
                    Elapsed = stopwatch.Elapsed
                });
            }

            result.Finished = DateTime.Now;
            result.Note = keepFinal ? "测试结束，保留最终 FMax。" : null;
            log(new LogEntry
            {
                Level = LogLevel.Success,
                Message = $"每核单独测试完成：{result.CoreVoltages.Count}/{frequencies.Count} 个档位、{result.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本，用时 {FormatDuration((int)(DateTime.Now - started).TotalSeconds)}。"
            });
            return result;
        }
        finally
        {
            load.Stop();
            if (!keepFinal && originalFMaxCeiling is >= 400 and <= 8000)
            {
                SmuWriteResult restore = smu.SetFMaxCeiling(originalFMaxCeiling);
                restoreDone = restore.Success;
                int originalActual = cpu.ToActualFMax(originalFMaxCeiling);
                log(new LogEntry
                {
                    Level = restore.Success ? LogLevel.Success : LogLevel.Error,
                    Message = restore.Success
                        ? $"已恢复原始 FMax：SMU 上限 {originalFMaxCeiling} MHz（实际最高 {originalActual} MHz）。"
                        : $"恢复原始 FMax 失败：{restore.Message} 请立即重启系统。"
                });
            }
            else if (!keepFinal && !restoreDone)
            {
                log(new LogEntry { Level = LogLevel.Error, Message = "没有可用的原始 FMax，未能恢复。请重启系统。" });
            }

            result.RestoreSucceeded = keepFinal ? null : restoreDone;
        }
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds < 60)
        {
            return $"{Math.Max(0, seconds)} 秒";
        }

        int minutes = seconds / 60;
        int remainder = seconds % 60;
        return remainder == 0 ? $"{minutes} 分" : $"{minutes} 分 {remainder} 秒";
    }

    private static async Task WarmupSingleCoreSweepAsync(
        int seconds,
        int frequencyStep,
        int frequencyCount,
        int coreIndex,
        int coreCount,
        PhysicalCoreRef core,
        int frequencyActual,
        int frequencyCeiling,
        Stopwatch stopwatch,
        Action<SweepProgress> progress,
        CancellationToken cancellationToken)
    {
        if (seconds <= 0)
        {
            return;
        }

        DateTime start = DateTime.Now;
        while ((DateTime.Now - start).TotalSeconds < seconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double elapsed = (DateTime.Now - start).TotalSeconds;
            progress(new SweepProgress
            {
                StepIndex = frequencyStep + 1,
                StepCount = frequencyCount,
                Frequency = frequencyActual,
                FrequencyCeiling = frequencyCeiling,
                Stage = $"预热 · 单核 · CCD{core.CcdIndex} Core #{core.DisplayCoreId}",
                Fraction = (frequencyStep + ((coreIndex + 0.05 + (0.10 * Math.Clamp(elapsed / seconds, 0, 1))) / coreCount))
                    / frequencyCount,
                Elapsed = stopwatch.Elapsed
            });
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SensorCoreSample? FindTargetCore(SensorSnapshot snapshot, PhysicalCoreRef core)
    {
        return snapshot.Cores.FirstOrDefault(c =>
                   c.DisplayCoreId == core.DisplayCoreId && c.CcdIndex == core.CcdIndex)
               ?? snapshot.Cores.FirstOrDefault(c => c.DisplayCoreId == core.DisplayCoreId);
    }

    private static async Task WarmupAsync(
        int seconds,
        int step,
        int stepCount,
        int frequencyActual,
        int frequencyCeiling,
        string stageLabel,
        Stopwatch stopwatch,
        Action<SweepProgress> progress,
        CancellationToken cancellationToken)
    {
        if (seconds <= 0)
        {
            return;
        }

        DateTime start = DateTime.Now;
        while ((DateTime.Now - start).TotalSeconds < seconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double elapsed = (DateTime.Now - start).TotalSeconds;
            progress(new SweepProgress
            {
                StepIndex = step + 1,
                StepCount = stepCount,
                Frequency = frequencyActual,
                FrequencyCeiling = frequencyCeiling,
                Stage = $"预热 · {stageLabel}",
                Fraction = ((step + (0.05 + (0.1 * Math.Clamp(elapsed / seconds, 0, 1)))) / stepCount),
                Elapsed = stopwatch.Elapsed
            });
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
        }
    }

    private static double Median(IEnumerable<double> values)
    {
        double[] array = values.Where(v => !double.IsNaN(v) && !double.IsInfinity(v)).OrderBy(v => v).ToArray();
        if (array.Length == 0)
        {
            return 0;
        }

        int middle = array.Length / 2;
        return array.Length % 2 == 0 ? (array[middle - 1] + array[middle]) / 2 : array[middle];
    }
}
