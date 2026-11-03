using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using OxyPlot;
using ZenVF.Interop;
using ZenVF.Models;
using ZenVF.Services;

namespace ZenVF.ViewModels;

/// <summary>Hardware detection, SMU connect / reconnect, startup eCLK reading, FMax takeover and restore, PawnIO installer, shutdown.</summary>
public sealed partial class MainViewModel
{
    private async Task InitializeHardwareAsync()
    {
        StageText = "正在检测 CPU、PawnIO 与 SMU…";
        Log(LogLevel.Info, "正在检测硬件…");

        (CpuDescriptor cpu, CpuTopology topology) = await Task.Run(() =>
        {
            CpuDescriptor detected = CpuDescriptor.Detect();
            CpuTopology layout = CpuTopology.Detect(detected);
            detected.CcdCount = layout.CcdCount;
            return (detected, layout);
        });
        _cpu = cpu;
        _topology = topology;

        CpuName = _cpu.Name;
        ArchitectureText = _cpu.IsZen5 ? "Zen 5" : _cpu.IsZen4 ? "Zen 4" : _cpu.ArchitectureLabel;
        CoreText = $"{_cpu.PhysicalCores}C / {_cpu.LogicalCores}T";
        CcdText = _cpu.CcdCount == 1 ? "1 CCD" : $"{_cpu.CcdCount} CCD";
        Has3DCache = _topology.Ccds.Any(c => c.Is3DCache) ||
                     _cpu.Name.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        BuildCcdOptions();

        SmuConnection connection = await Task.Run(() => ConnectSmu(cpu, topology));
        AdoptConnection(connection);
        ApplySmuState(connection);
        ApplyDefaultRange();

        await MeasureStartupEclkAsync();
        LogOriginalFMax();
        ApplyReadyStatus(connection.Error);
    }

    /// <summary>
    /// Connects the SMU and starts the sensors. Runs off the UI thread and is
    /// reused by the "reconnect" command, so a busy PCI lock or a freshly
    /// installed PawnIO no longer needs a restart.
    /// </summary>
    private static SmuConnection ConnectSmu(CpuDescriptor cpu, CpuTopology topology)
    {
        SmuController? smu = null;
        string? error = null;
        if (!cpu.IsAmd)
        {
            error = "未检测到 AMD 处理器。";
        }
        else if (!cpu.IsSupported)
        {
            error = $"不支持的处理器（{cpu.Codename}，Family 0x{cpu.Family:X2} Model 0x{cpu.Model:X2}）。仅支持已验证的 Zen 4 / Zen 5 型号，不会访问 SMU。";
        }
        else
        {
            smu = new SmuController(cpu);
            if (!smu.Initialize(out string initError))
            {
                error = initError;
            }
        }

        LhmSensorService sensors = new(topology, smu, cpu.IsZen4 || cpu.IsZen5);
        sensors.Start();
        SensorSnapshot snapshot = sensors.Sample();
        PboAssessment? assessment = smu?.IsAvailable == true ? smu.AssessPbo(snapshot) : null;
        return new SmuConnection(smu, sensors, error, assessment);
    }

    private void AdoptConnection(SmuConnection connection)
    {
        _smu = connection.Smu;
        _sensors = connection.Sensors;
        _originalFMaxCeiling = connection.Smu?.LastKnownFMaxCeiling ?? 0;
    }

    /// <summary>Status texts, logs, FMax readouts and PBO assessment for a connection result.</summary>
    private void ApplySmuState(SmuConnection connection)
    {
        if (_cpu is null)
        {
            return;
        }

        PawnIoStatusText = PawnIoNative.IsInstalled
            ? $"PawnIO {PawnIoNative.InstalledVersion ?? ""}".Trim()
            : "PawnIO 未安装";
        SmuStatusText = _smu?.IsAvailable == true
            ? $"SMU 0x{_smu.SmuVersion:X8} · {_smu.PlatformName}"
            : "SMU 不可用";
        DriverWarningVisible = !PawnIoNative.IsInstalled;
        DriverWarningText = "未检测到 PawnIO。安装后点“重新连接”即可读取 CPU 频率、电压并修改最高频率。";
        IsHardwareReady = _smu?.IsAvailable == true;

        if (!PawnIoNative.IsInstalled)
        {
            Log(LogLevel.Error, "未安装 PawnIO。请点击“安装 PawnIO”，安装完成后点“重新连接”。");
        }
        else if (_smu?.IsAvailable != true)
        {
            Log(LogLevel.Error, $"SMU 初始化失败：{connection.Error ?? "未知错误"}");
        }
        else
        {
            Log(LogLevel.Success, $"SMU 已连接：{_smu.PlatformName}，版本 0x{_smu.SmuVersion:X8}。");
        }

        if (_sensors is { IsAvailable: false })
        {
            Log(LogLevel.Warning, $"传感器不可用：{_sensors.Error}");
        }
        else if (_smu?.HasPerCoreVoltageTable == true)
        {
            string verified = _smu.PmTableVersion == 0x00620205
                ? "已实测与 HWiNFO 逐位一致"
                : "布局按 0x00620205 推定";
            Log(LogLevel.Success,
                $"电压源：SMU PM table 0x{_smu.PmTableVersion:X8}，每核 = HWiNFO Core VID，单核测试 = HWiNFO CPU VDDCR_VDD（{verified}）。");
        }
        else if (_cpu.IsZen4 || _cpu.IsZen5)
        {
            uint pmVersion = _smu?.PmTableVersion ?? 0;
            Log(LogLevel.Warning, $"SMU PM table 不可用（版本 0x{pmVersion:X8}），将回退到 LHM Core VID（SVI3 修正）；建议仍用 HWiNFO 对照。");
        }

        int? stockActual = _cpu.StockFMaxMHz;
        int currentActual = _originalFMaxCeiling > 0 ? _cpu.ToActualFMax(_originalFMaxCeiling) : 0;
        StockFMaxText = stockActual is int stock ? $"{stock} MHz" : "—";
        CurrentFMaxText = currentActual > 0 ? $"{currentActual} MHz" : "—";
        OriginalFMaxText = currentActual > 0 ? $"{currentActual} MHz" : "—";
        ScalarText = _smu?.PboScalar is float scalar ? scalar.ToString("0.00") : "—";

        if (connection.Assessment is not null)
        {
            ApplyAssessment(connection.Assessment);
        }
        else
        {
            PboState = "unknown";
            PboSummary = _smu?.IsAvailable == true ? "PBO 状态未知" : "SMU 不可用";
            PboDetail = connection.Error ?? "无法读取 PBO 状态。";
            PboEvidenceText = connection.Error is null ? string.Empty : "• " + connection.Error;
        }
    }

    /// <summary>Default sweep range and its hint, from the model table or the current FMax.</summary>
    private void ApplyDefaultRange()
    {
        if (_cpu is null)
        {
            return;
        }

        int? stockActual = _cpu.StockFMaxMHz;
        int currentActual = _originalFMaxCeiling > 0 ? _cpu.ToActualFMax(_originalFMaxCeiling) : 0;
        int baseline = stockActual ?? (currentActual > 0 ? currentActual : 5000);
        int start = currentActual > 0 ? currentActual : baseline;
        if (stockActual is int s)
        {
            start = Math.Min(start, s + 200);
        }

        int end = _cpu.BaseClockMHz ?? Math.Max(3000, baseline - 500);
        if (end >= start)
        {
            end = Math.Max(3000, start - 1000);
        }

        _suppressValidation = true;
        StartFMaxText = start.ToString();
        EndFMaxText = end.ToString();
        StepText = "100";
        WarmupText = "5";
        SampleText = "5";
        _suppressValidation = false;

        if (stockActual is int knownStock)
        {
            int minEnd = _cpu.BaseClockMHz ?? (knownStock - 1000);
            RangeHintText = $"可测 {minEnd}–{knownStock + 200} MHz · 默认 {start}→{end}";
        }
        else
        {
            RangeHintText = currentActual > 0
                ? $"未识别型号 · 上限为当前 {currentActual} MHz · 默认 {start}→{end}"
                : $"未识别型号 · 无法确定上限";
            Log(LogLevel.Warning,
                $"未在型号表中找到“{_cpu.Name}”的默认最高频率；起始频率不允许超过当前最高频率{(currentActual > 0 ? $" {currentActual} MHz" : string.Empty)}。");
        }
    }

    private void LogOriginalFMax()
    {
        if (_cpu is null || _originalFMaxCeiling == 0)
        {
            return;
        }

        Log(LogLevel.Info,
            $"原始 FMax：SMU 上限 {_originalFMaxCeiling} MHz，实际最高频率 {_cpu.ToActualFMax(_originalFMaxCeiling)} MHz。最高频率接管未打开。");
        if (_cpu.FMaxCeilingOffsetMHz > 0)
        {
            Log(LogLevel.Info,
                $"{_cpu.ModelToken ?? _cpu.Name}：SMU FMax 上限比实际最高加速高 {_cpu.FMaxCeilingOffsetMHz} MHz；界面与扫描范围均使用实际值。");
        }
    }

    private void ApplyReadyStatus(string? error)
    {
        if (_smu?.IsAvailable == true)
        {
            StatusText = "就绪 · 请先打开“最高频率接管”";
            StageText = "打开开关后会保存当前频率，结束时自动恢复";
        }
        else
        {
            StatusText = "SMU 不可用 · 无法开始扫描";
            StageText = error ?? "请安装 PawnIO 后点“重新连接”";
        }

        IsPboArmed = false;
        OnPropertyChanged(nameof(CanTogglePbo));
        OnPropertyChanged(nameof(PboToggleHint));
        OnPropertyChanged(nameof(CanInstallPawnIo));
        OnPropertyChanged(nameof(CanReconnectSmu));
        OnPropertyChanged(nameof(ShowReconnectSmu));
        RefreshCommands();
    }

    /// <summary>
    /// Retries the SMU connection without restarting ZenVF: after another
    /// monitor held the PCI lock at startup, or after PawnIO was installed.
    /// Keeps the user's sweep range; re-runs eCLK detection, which needs the
    /// real FMax as its reference.
    /// </summary>
    private async Task ReconnectSmuAsync()
    {
        if (!CanReconnectSmu || _cpu is null || _topology is null)
        {
            return;
        }

        if (_smu?.HasPendingRestore == true)
        {
            // Never drop a controller that still owes an FMax restore.
            Log(LogLevel.Error, "当前 SMU 连接仍有待恢复的 FMax，不能重新连接。");
            return;
        }

        IsReconnecting = true;
        StatusText = "正在重新连接 SMU…";
        StageText = "正在检测 PawnIO 与 SMU";
        Log(LogLevel.Info, "正在重新连接 SMU…");
        try
        {
            CpuDescriptor cpu = _cpu;
            CpuTopology topology = _topology;
            SmuConnection connection = await Task.Run(() => ConnectSmu(cpu, topology));

            SmuController? oldSmu = _smu;
            ISensorService? oldSensors = _sensors;
            AdoptConnection(connection);
            oldSensors?.Dispose();
            oldSmu?.Dispose();

            ApplySmuState(connection);
            if (_smu?.IsAvailable == true)
            {
                await MeasureStartupEclkAsync();
                LogOriginalFMax();
            }

            ApplyReadyStatus(connection.Error);
            ValidateInputs();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"重新连接失败：{ex.Message}");
            StatusText = "SMU 不可用 · 无法开始扫描";
        }
        finally
        {
            IsReconnecting = false;
        }
    }

    private async Task MeasureStartupEclkAsync()
    {
        if (IsDemo || _sensors is null || _cpu is null)
        {
            return;
        }

        if (!_sensors.IsAvailable)
        {
            Log(LogLevel.Warning, "eCLK 启动读取跳过：LHM 传感器不可用。");
            return;
        }

        int pboMaxMHz = _originalFMaxCeiling > 0
            ? _cpu.ToActualFMax(_originalFMaxCeiling)
            : _cpu.StockFMaxMHz ?? 0;
        if (pboMaxMHz <= 0)
        {
            Log(LogLevel.Warning, "eCLK 启动读取跳过：无法确定当前 PBO 最大频率。");
            return;
        }

        string previousStage = StageText;
        string previousStatus = StatusText;
        StageText = $"正在读取启动显示频率（约 {EclkStartupSampleMs / 1000} 秒）…";
        StatusText = "正在读取启动时的最高核心频率…";
        Log(LogLevel.Info,
            $"启动 eCLK 读取：连续采样约 {EclkStartupSampleMs / 1000} 秒，读取 LHM Core #N 的运行时最大值（HWiNFO Max 列）与 Bus Speed 最大值；PBO 最大频率 {pboMaxMHz} MHz。");

        List<SensorCoreSample> samples = new();
        double busSpeedMHz = 0;
        try
        {
            async Task CollectAsync(int durationMs)
            {
                DateTime start = DateTime.Now;
                do
                {
                    SensorSnapshot snapshot = _sensors.Sample();
                    if (snapshot.HasData)
                    {
                        if (SensorValueSanity.IsPlausibleClock(snapshot.BusSpeedMHz)
                            && snapshot.BusSpeedMHz > busSpeedMHz)
                        {
                            busSpeedMHz = snapshot.BusSpeedMHz;
                        }

                        samples.AddRange(snapshot.Cores.Where(core =>
                        {
                            double displayMax = Math.Max(core.CoreClock, core.CoreClockMax);
                            return SensorValueSanity.IsPlausibleClock(displayMax)
                                && displayMax >= 800;
                        }));
                    }

                    await Task.Delay(50);
                }
                while ((DateTime.Now - start).TotalMilliseconds < durationMs && !_disposed);
            }

            // Take the maximum over a short window so the bus/clock maximum
            // converges instead of depending on whichever 50 ms sample happened
            // first. If the runtime Max is still below PBO after that, a very
            // short single-core wake is used to give the Core #N Max sensor a
            // real boost clock; this is not the all-core stress test.
            await CollectAsync(EclkStartupSampleMs);
            EclkDetector.Estimate initial = EclkDetector.DetectStartup(samples, pboMaxMHz, busSpeedMHz);
            if (!initial.Detected)
            {
                PhysicalCoreRef? probeCore = _topology?.GetPhysicalCores().FirstOrDefault();
                if (probeCore is not null)
                {
                    StageText = "正在用单核唤醒刷新 Core #N 最大值…";
                    Log(LogLevel.Info,
                        "启动 Max 尚未达到 PBO 上限，使用约 0.8 秒单核唤醒刷新 Core #N Max；不写 FMax，不使用 Effective，不运行全核烤机。");
                    using LoadGenerator probe = new();
                    probe.StartSingleCore(probeCore);
                    await CollectAsync(800);
                    await Task.Run(probe.Stop);
                    await CollectAsync(100);
                }
            }

            EclkDetector.Estimate estimate = EclkDetector.DetectStartup(samples, pboMaxMHz, busSpeedMHz);
            _startupEclkPeakMHz = estimate.PeakClockMHz;
            _startupEclkPboMaxMHz = estimate.PboMaxMHz;
            _startupEclkBusMHz = estimate.BusSpeedMHz;
            if (estimate.Detected)
            {
                _startupEclkRatio = estimate.Ratio;
                _startupEclkSamples = estimate.SampleCount;
                ApplyEclk(null);
                Log(LogLevel.Success,
                    $"eCLK 启动读取完成：Core #N 运行最大值 {estimate.PeakClockMHz:0.0} MHz，Bus Speed {estimate.BusSpeedMHz:0.###} MHz，PBO 最大频率 {estimate.PboMaxMHz:0} MHz ≈ {estimate.Ratio:0.00000}（BCLK2 ≈ {estimate.BclkMHz:0.###} MHz，{estimate.CoreCount} 核 / {estimate.SampleCount} 个样本）。");
            }
            else
            {
                _startupEclkRatio = null;
                _startupEclkSamples = 0;
                ApplyEclk(null);
                Log(LogLevel.Info,
                    $"eCLK 启动读取未检测：Core #N 运行最大值 {estimate.PeakClockMHz:0.0} MHz，Bus Speed {estimate.BusSpeedMHz:0.###} MHz，PBO 最大频率 {estimate.PboMaxMHz:0} MHz ≈ {estimate.PeakClockMHz / Math.Max(1, estimate.PboMaxMHz):0.00000}。频率标签按设定频率显示。");
            }
        }
        catch (Exception ex)
        {
            _startupEclkRatio = null;
            _startupEclkSamples = 0;
            ApplyEclk(null);
            Log(LogLevel.Warning, $"eCLK 启动读取失败：{ex.Message}。频率标签按设定频率显示。");
        }
        finally
        {
            StageText = previousStage;
            StatusText = previousStatus;
        }
    }

    private void ApplyAssessment(PboAssessment assessment)
    {
        PboState = assessment.Enabled switch
        {
            true => "on",
            false => "off",
            _ => "unknown"
        };
        PboSummary = assessment.Summary;
        PboDetail = assessment.Detail;
        PboEvidenceText = assessment.Evidence.Count > 0
            ? string.Join("\n", assessment.Evidence.Take(2).Select(e => "• " + e))
            : "• 无可用证据";
        if (assessment.CurrentFMax is int current)
        {
            CurrentFMaxText = $"{current} MHz";
        }

        if (assessment.Scalar is float scalar)
        {
            ScalarText = scalar.ToString("0.00");
        }
    }

    private async Task TogglePboAsync()
    {
        if (IsDemo)
        {
            IsPboArmed = !IsPboArmed;
            OnPropertyChanged(nameof(PboToggleHint));
            Log(LogLevel.Info, IsPboArmed ? "DEMO 最高频率接管已打开。" : "DEMO 最高频率接管已关闭。");
            return;
        }

        if (_smu is null || !_smu.IsAvailable)
        {
            Log(LogLevel.Error, "SMU 不可用，无法切换最高频率接管。");
            return;
        }

        if (!IsPboArmed)
        {
            uint currentCeiling;
            if (_smu.RestoreTarget is uint pending)
            {
                // A previous restore did not complete. The value on the SMU
                // now is ZenVF's own test value, not the user's original, so
                // keep the value recorded before the first write.
                currentCeiling = pending;
                Log(LogLevel.Warning,
                    $"上次未能确认恢复原始 FMax；继续沿用写入前记录的原始值 {ActualFromCeiling(pending)} MHz（SMU 上限 {pending} MHz）。");
            }
            else if (!_smu.TryGetFMaxCeiling(out currentCeiling))
            {
                Log(LogLevel.Error, "无法读取当前 FMax，接管失败。");
                return;
            }

            _originalFMaxCeiling = currentCeiling;
            int currentActual = ActualFromCeiling(currentCeiling);
            OriginalFMaxText = $"{currentActual} MHz";
            CurrentFMaxText = $"{currentActual} MHz";
            IsPboArmed = true;
            StatusText = "就绪 · 最高频率接管已打开";
            StageText = $"已保存原始最高频率 {currentActual} MHz";
            OnPropertyChanged(nameof(PboToggleHint));
            Log(LogLevel.Success,
                $"最高频率接管已打开：原始最高频率 {currentActual} MHz（SMU 上限 {currentCeiling} MHz）。");
            await RefreshPboStatusAsync();
        }
        else
        {
            IsPboArmed = false;
            bool restored = await RestoreOriginalFMaxAsync("手动关闭最高频率接管");
            StatusText = restored
                ? "就绪 · 最高频率接管未打开"
                : "恢复原始频率失败，请重启系统";
            StageText = restored
                ? (_originalFMaxCeiling > 0
                    ? $"原始最高频率 {ActualFromCeiling(_originalFMaxCeiling)} MHz"
                    : "等待打开")
                : "请立即重启系统";
            Log(restored ? LogLevel.Info : LogLevel.Error,
                restored ? "最高频率接管已关闭。" : "最高频率接管已关闭，但恢复原始频率失败。");
        }
    }

    private async Task<bool> RestoreOriginalFMaxAsync(string reason)
    {
        if (_smu?.IsAvailable != true)
        {
            return false;
        }

        if (_smu.RestoreTarget is not uint target)
        {
            // ZenVF has not changed FMax (or already restored it). Writing
            // anything here would overwrite changes made by other tools.
            Log(LogLevel.Info, $"{reason}：本程序未修改 FMax，无需恢复。");
            return true;
        }

        SmuWriteResult restore = await Task.Run(_smu.RestoreFMax);
        if (!restore.Success)
        {
            if (restore.ReadBackFMax > 0)
            {
                CurrentFMaxText = $"{ActualFromCeiling(restore.ReadBackFMax)} MHz";
            }

            Log(LogLevel.Error, $"{reason}：恢复 FMax 失败 — {restore.Message}");
            return false;
        }

        int restoredActual = ActualFromCeiling(target);
        CurrentFMaxText = $"{restoredActual} MHz";
        Log(LogLevel.Success,
            $"{reason}：已恢复原始 FMax —— SMU 上限 {target} MHz，实际最高加速 {restoredActual} MHz。");
        return true;
    }

    /// <summary>
    /// After a run the engine has already tried to restore. If the SMU still
    /// reports a pending restore, try once more before telling the user to reboot.
    /// </summary>
    private async Task<bool> EnsureRestoredAfterRunAsync()
    {
        if (_smu?.IsAvailable != true)
        {
            return false;
        }

        if (!_smu.HasPendingRestore)
        {
            return true;
        }

        return await RestoreOriginalFMaxAsync("测试结束后重试恢复");
    }

    /// <summary>
    /// Best-effort synchronous restore for crash, session-end and process-exit
    /// paths. Only writes when ZenVF itself changed FMax. Never throws.
    /// </summary>
    public void EmergencyRestore()
    {
        try
        {
            _cts?.Cancel();
        }
        catch
        {
            // The token source may already be disposed.
        }

        try
        {
            if (!IsDemo && _smu?.IsAvailable == true && _smu.HasPendingRestore)
            {
                _smu.RestoreFMax();
            }
        }
        catch
        {
            // Nothing more can be done on these paths.
        }
    }

    private async Task RefreshSmuStateAsync()
    {
        if (_smu?.IsAvailable != true)
        {
            return;
        }

        await Task.Run(() =>
        {
            if (_smu.TryGetFMaxCeiling(out uint currentCeiling))
            {
                int currentActual = ActualFromCeiling(currentCeiling);
                Dispatch(() =>
                {
                    CurrentFMaxText = $"{currentActual} MHz";
                    OnPropertyChanged(nameof(PboToggleHint));
                });
            }

            SensorSnapshot snapshot = _sensors?.Sample() ?? new SensorSnapshot();
            PboAssessment assessment = _smu.AssessPbo(snapshot);
            Dispatch(() => ApplyAssessment(assessment));
        });
    }

    private async Task RefreshPboStatusAsync()
    {
        if (_smu?.IsAvailable != true)
        {
            return;
        }

        PboAssessment assessment = await Task.Run(() => _smu.AssessPbo(_sensors?.Sample()));
        ApplyAssessment(assessment);
    }

    private async Task InstallPawnIoAsync()
    {
        string path = GetPawnIoInstallerPath();
        if (!File.Exists(path))
        {
            Log(LogLevel.Error, "安装程序不存在：PawnIO_setup.exe");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Log(LogLevel.Info, "已启动 PawnIO 安装程序。安装完成后点“重新连接”。");
            StatusText = "已启动 PawnIO 安装程序";
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"无法启动 PawnIO 安装程序：{ex.Message}");
        }

        await Task.CompletedTask;
    }

    private static string GetPawnIoInstallerPath()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "PawnIO_setup.exe");
        return File.Exists(root) ? root : Path.Combine(AppContext.BaseDirectory, "Assets", "PawnIO_setup.exe");
    }

    public void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _cts?.Cancel();
            _runTask?.Wait(5000);
        }
        catch
        {
            // Ignore shutdown wait failures.
        }

        // Only restores when ZenVF itself left FMax changed; an FMax set by
        // another tool while ZenVF was open is left alone.
        EmergencyRestore();

        _smu?.Dispose();
        _sensors?.Dispose();
        _cts?.Dispose();
    }

    private sealed record SmuConnection(
        SmuController? Smu,
        LhmSensorService Sensors,
        string? Error,
        PboAssessment? Assessment);
}
