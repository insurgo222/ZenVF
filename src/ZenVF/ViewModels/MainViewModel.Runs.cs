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

/// <summary>Sweep parameters, starting / stopping hardware runs, end-of-run restore check, progress and live data.</summary>
public sealed partial class MainViewModel
{
    private void ValidateInputs()
    {
        if (_suppressValidation)
        {
            return;
        }

        SweepSettings? settings = BuildSettings();
        if (settings is null || _topology is null)
        {
            IsInputValid = false;
            ValidationMessage = "请输入有效的整数参数。";
            StepCountText = "—";
            PlanSummaryText = "检查输入参数";
            SingleCorePlanText = "检查每核单独测试参数";
            if (!IsRunning)
            {
                ProgressTimeText = "预计时间 —";
            }

            return;
        }

        string? error = ValidateSettings(settings);
        ValidationMessage = error ?? string.Empty;
        IsInputValid = error is null;
        _lastSettings = settings;
        StepCountText = error is null ? settings.StepCount.ToString() : "—";
        PlanSummaryText = error is null
            ? $"{settings.StepCount} 档 · {settings.SelectedCcds.Count} 个 CCD · 预估约 {FormatDuration(settings.ApproximateDurationSeconds)}"
            : "检查输入参数";

        if (error is not null)
        {
            SingleCorePlanText = "检查每核单独测试参数";
            if (!IsRunning)
            {
                ProgressTimeText = "请检查输入参数";
            }

            return;
        }

        int coreCount = CountSelectedCores(settings.SelectedCcds);
        SingleCorePlanText =
            $"{settings.StepCount} 档 × {coreCount} 核 · 预估约 {FormatDuration(settings.ApproximateSingleCoreDurationSeconds(coreCount))}";

        if (!IsRunning)
        {
            // The old estimate used to sit under the buttons. It now lives
            // under the progress bar and becomes a live ETA once a run starts.
            ProgressTimeText = PlanSummaryText;
        }
    }

    private string? ValidateSettings(SweepSettings settings)
    {
        // For an unrecognised model the only safe ceiling is what the CPU was
        // already running before ZenVF touched it.
        uint referenceCeiling = _smu?.RestoreTarget ?? _originalFMaxCeiling;
        int? currentActual = referenceCeiling > 0 ? ActualFromCeiling(referenceCeiling) : null;
        return settings.Validate(_cpu?.StockFMaxMHz, _cpu?.BaseClockMHz, currentActual);
    }

    private SweepSettings? BuildSettings()
    {
        if (!TryParse(StartFMaxText, out int start) ||
            !TryParse(EndFMaxText, out int end) ||
            !TryParse(StepText, out int step) ||
            !TryParse(WarmupText, out int warmup) ||
            !TryParse(SampleText, out int sample))
        {
            return null;
        }

        return new SweepSettings
        {
            StartFMax = start,
            EndFMax = end,
            StepMHz = step,
            WarmupSeconds = warmup,
            SampleSeconds = sample,
            SelectedCcds = CcdOptions.Where(c => c.IsSelected).Select(c => c.Index).ToHashSet()
        };
    }

    private int CountSelectedCores(IEnumerable<int> selectedCcds)
    {
        if (_topology is null)
        {
            return 0;
        }

        HashSet<int> selected = selectedCcds.ToHashSet();
        return _topology.Ccds
            .Where(ccd => selected.Contains(ccd.Index))
            .Sum(ccd => ccd.PhysicalCoreCount);
    }

    private async Task StartAsync()
    {
        if (!ValidateStart(out SweepSettings? settings))
        {
            return;
        }

        if (IsDemo)
        {
            await RunDemoSweepAsync(settings!);
            return;
        }

        if (!TryPrepareHardwareRun("无法读取当前 FMax，已取消全核测试。"))
        {
            return;
        }

        _runPlannedSeconds = settings!.ApproximateDurationSeconds;
        IsRunning = true;
        ProgressPercent = 0;
        ProgressTimeText = $"预计剩余 {FormatClock(TimeSpan.FromSeconds(_runPlannedSeconds))} · 已用 00:00";
        HasResult = _singleCoreRun?.CoreVoltages.Count > 0;
        _cts = new CancellationTokenSource();
        _currentRun = SweepRunResult.Create(_cpu, settings!, SweepRunResult.MaxCoreSeries(settings!), DateTime.Now);
        ApplyEclk(null);
        RebuildCharts(_currentRun, false);
        ChartSubtitle = "全核测试中…";
        StatusText = "全核测试 · 正在设置频率";
        StageText = "准备全核负载";
        Log(LogLevel.Info,
            $"开始全核测试：{settings!.StepCount} 个频率档位（{settings.StartFMax}→{settings.EndFMax} MHz，写入上限 {CeilingFromActual(settings.StartFMax)}→{CeilingFromActual(settings.EndFMax)} MHz），CCD {string.Join(", ", settings.SelectedCcds.OrderBy(i => i))}，全核负载。");

        RunOutcome outcome = RunOutcome.Failed;
        string completedStage = string.Empty;
        try
        {
            Task<SweepRunResult> runTask = _engine.RunAsync(
                _smu,
                _sensors,
                _topology,
                _cpu,
                settings,
                entry => Dispatch(() => AddLog(entry)),
                progress => Dispatch(() => ApplyProgress(progress)),
                cores => Dispatch(() => AddCoreVoltages(cores)),
                point => Dispatch(() => AddLivePoint(point)),
                _cts.Token);
            _runTask = runTask;

            SweepRunResult result = await runTask;
            ApplySessionEclk(result);
            _currentRun = result;
            ApplyEclk(result.IsEclkDetected ? result : _singleCoreRun);
            RefreshHasResult();
            RebuildCharts(result, false);
            UpdateChartSubtitle(result);
            outcome = RunOutcome.Completed;
            completedStage = $"完成 · {result.CoreVoltages.Count} 个频率档位";
            StatusText = "已完成 · 正在确认原始频率";
            StageText = completedStage;
            ProgressPercent = 100;
            ProgressTimeText = $"总用时 {FormatClock((result.Finished ?? DateTime.Now) - result.Started)}";
            Log(LogLevel.Success, "全核测试结束。可查看图表、每核电压或导出 CSV。");
        }
        catch (OperationCanceledException)
        {
            outcome = RunOutcome.Stopped;
            StatusText = "已停止 · 正在恢复原始频率";
            StageText = "用户中止";
            ProgressPercent = 0;
            ProgressTimeText = "已停止";
            Log(LogLevel.Warning, "全核测试已由用户停止。");
        }
        catch (Exception ex)
        {
            StatusText = "全核测试失败 · 正在恢复原始频率";
            StageText = "发生错误";
            ProgressTimeText = "已中止";
            Log(LogLevel.Error, ex.Message);
        }
        finally
        {
            await FinishRunAsync(outcome, "已完成", "全核测试失败", completedStage);
        }
    }

    private async Task StartSingleCoreSweepAsync()
    {
        if (!ValidateStart(out SweepSettings? settings))
        {
            return;
        }

        if (IsDemo)
        {
            await RunDemoSingleCoreSweepAsync(settings!);
            return;
        }

        if (!TryPrepareHardwareRun("无法读取当前最高频率，已取消单核测试。"))
        {
            return;
        }

        IsRunning = true;
        ProgressPercent = 0;
        ProgressTimeText = "预计剩余 --:--";
        HasResult = _currentRun?.CoreVoltages.Count > 0;
        _cts = new CancellationTokenSource();
        _singleCoreRun = SweepRunResult.Create(_cpu, settings!, new List<SweepSeries>(), DateTime.Now);
        ApplyEclk(null);
        RebuildSingleCoreChart(false);
        ChartSubtitle = "单核测试中…";
        StatusText = "单核测试 · 正在设置频率";
        StageText = "准备单核负载";
        int coreCount = CountSelectedCores(settings!.SelectedCcds);
        _runPlannedSeconds = settings.ApproximateSingleCoreDurationSeconds(coreCount);
        ProgressTimeText = $"预计剩余 {FormatClock(TimeSpan.FromSeconds(_runPlannedSeconds))} · 已用 00:00";
        Log(LogLevel.Info,
            $"开始每核单独测试：{settings.StepCount} 个频率档位（{settings.StartFMax}→{settings.EndFMax} MHz），每档依次测试 {coreCount} 个物理核心，每核 {settings.WarmupSeconds} 秒预热 / {settings.SampleSeconds} 秒采样。");
        SingleCoreSweepStarted?.Invoke(this, EventArgs.Empty);

        RunOutcome outcome = RunOutcome.Failed;
        string completedStage = string.Empty;
        try
        {
            Task<SweepRunResult> runTask = _engine.RunSingleCoreSweepAsync(
                _smu,
                _sensors,
                _topology,
                _cpu,
                settings,
                entry => Dispatch(() => AddLog(entry)),
                progress => Dispatch(() => ApplySingleCoreProgress(progress)),
                frequency => Dispatch(() => AddSingleCoreSweep(frequency)),
                _cts.Token);
            _runTask = runTask;

            SweepRunResult result = await runTask;
            ApplySessionEclk(result);
            _singleCoreRun = result;
            ApplyEclk(result.IsEclkDetected ? result : _currentRun);
            RefreshHasResult();
            RebuildSingleCoreChart(false);
            UpdateChartSubtitle(_currentRun);
            outcome = RunOutcome.Completed;
            completedStage = $"完成 · {result.CoreVoltages.Count} 个频率档位 · {result.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本";
            StatusText = "单核完成 · 正在确认原始频率";
            StageText = completedStage;
            ProgressPercent = 100;
            ProgressTimeText = $"总用时 {FormatClock((result.Finished ?? DateTime.Now) - result.Started)}";
            Log(LogLevel.Success, "单核测试结束。可查看“单核 V/F”图，或导出 CSV。");
        }
        catch (OperationCanceledException)
        {
            outcome = RunOutcome.Stopped;
            StatusText = "已停止 · 正在恢复原始频率";
            StageText = "用户中止单核测试";
            ProgressPercent = 0;
            ProgressTimeText = "已停止";
            Log(LogLevel.Warning, "单核测试已由用户停止。");
        }
        catch (Exception ex)
        {
            StatusText = "单核测试失败 · 正在恢复原始频率";
            StageText = "发生错误";
            ProgressTimeText = "已中止";
            Log(LogLevel.Error, ex.Message);
        }
        finally
        {
            await FinishRunAsync(outcome, "单核完成", "单核测试失败", completedStage);
        }
    }

    /// <summary>
    /// Checks shared by both hardware runs: hardware is ready and the current
    /// FMax can be read; records it as the original if none is known yet.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.MemberNotNullWhen(true, nameof(_smu), nameof(_topology), nameof(_cpu), nameof(_sensors))]
    private bool TryPrepareHardwareRun(string readFailedMessage)
    {
        if (_smu is null || _topology is null || _cpu is null || _sensors is null)
        {
            Log(LogLevel.Error, "硬件未就绪。");
            return false;
        }

        if (!_smu.TryGetFMaxCeiling(out uint currentCeiling))
        {
            Log(LogLevel.Error, readFailedMessage);
            return false;
        }

        if (_originalFMaxCeiling == 0)
        {
            _originalFMaxCeiling = currentCeiling;
            OriginalFMaxText = $"{ActualFromCeiling(currentCeiling)} MHz";
            OnPropertyChanged(nameof(PboToggleHint));
        }

        return true;
    }

    /// <summary>How a hardware run ended; decides the restore check and the final status.</summary>
    private enum RunOutcome
    {
        Completed,
        Stopped,
        Failed
    }

    /// <summary>
    /// Common end of a hardware run. Every outcome goes through
    /// EnsureRestoredAfterRunAsync, which retries a
    /// pending restore and is a no-op when nothing is pending. The outcome
    /// comes from the run itself, not from parsing the status text (the old
    /// prefix checks skipped the retry when a completed all-core run had
    /// already failed to restore).
    /// </summary>
    private async Task FinishRunAsync(
        RunOutcome outcome,
        string completedLabel,
        string failedLabel,
        string completedStage)
    {
        IsRunning = false;
        // The subtitle is set to "…测试中…" at start and is otherwise only
        // replaced when a step completes; a stop/failure before the first
        // step left it stuck.
        UpdateChartSubtitle(_currentRun);
        _cts?.Dispose();
        _cts = null;
        _runTask = null;
        await RefreshSmuStateAsync();

        bool restored = await EnsureRestoredAfterRunAsync();
        string label = outcome switch
        {
            RunOutcome.Completed => completedLabel,
            RunOutcome.Stopped => "已停止",
            _ => failedLabel
        };
        StatusText = restored
            ? $"{label} · 已恢复原始频率"
            : $"{label} · 恢复原始频率失败，请重启系统";
        StageText = restored
            ? (outcome == RunOutcome.Completed ? completedStage : "已恢复原始频率")
            : "恢复失败 · 请立即重启系统";
    }

    private bool ValidateStart(out SweepSettings? settings)
    {
        settings = BuildSettings();
        if (settings is null)
        {
            ValidationMessage = "请输入有效的整数参数。";
            return false;
        }

        string? error = ValidateSettings(settings);
        if (error is not null)
        {
            ValidationMessage = error;
            StatusText = "无法开始 · 参数无效";
            Log(LogLevel.Warning, error);
            return false;
        }

        if (!IsPboArmed)
        {
            StatusText = "无法开始 · 请先打开最高频率接管";
            Log(LogLevel.Warning, "请先打开“最高频率接管”开关。");
            return false;
        }

        if (!IsHardwareReady)
        {
            StatusText = "无法开始 · 驱动或 SMU 不可用";
            return false;
        }

        ValidationMessage = string.Empty;
        return true;
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        StatusText = "正在停止…";
        StageText = "等待当前采样结束，恢复原始频率";
        ProgressTimeText = "正在停止…";
        Log(LogLevel.Warning, "正在停止测试…");
        _cts?.Cancel();
    }

    private void ApplyProgress(SweepProgress progress)
    {
        UpdateProgressDisplay(progress, "全核测试", null, null);
    }

    private void ApplySingleCoreProgress(SweepProgress progress)
    {
        UpdateProgressDisplay(progress, "单核测试", null, null);
    }

    private void UpdateProgressDisplay(
        SweepProgress progress,
        string modeLabel,
        double? fractionOverride,
        TimeSpan? elapsedOverride)
    {
        // Progress callbacks are queued on the dispatcher; once Stop was
        // pressed (or the run ended) they must not overwrite "正在停止…".
        if (!IsRunning || _cts?.IsCancellationRequested == true)
        {
            return;
        }

        double fraction = Math.Clamp(fractionOverride ?? progress.Fraction, 0, 1);
        TimeSpan elapsed = elapsedOverride ?? progress.Elapsed;
        ProgressPercent = (int)Math.Round(fraction * 100);
        StatusText = $"{modeLabel} · {progress.Frequency} MHz";
        StageText = $"{progress.Stage} · 第 {progress.StepIndex}/{progress.StepCount} 档";
        ProgressTimeText = FormatProgressTime(fraction, elapsed, _runPlannedSeconds);
    }

    private static string FormatProgressTime(double fraction, TimeSpan elapsed, double plannedSeconds)
    {
        if (elapsed <= TimeSpan.Zero || fraction <= 0.0001)
        {
            if (plannedSeconds > 0)
            {
                double initialRemaining = Math.Max(0, plannedSeconds - elapsed.TotalSeconds);
                return $"预计剩余 {FormatClock(TimeSpan.FromSeconds(initialRemaining))} · 已用 {FormatClock(elapsed)}";
            }

            return "预计剩余 --:--";
        }

        if (fraction >= 0.999)
        {
            return $"已用 {FormatClock(elapsed)}";
        }

        double measuredTotal = elapsed.TotalSeconds / fraction;
        double predictedTotal = measuredTotal;
        if (plannedSeconds > 0)
        {
            // Early in a run the partial fraction is dominated by warmup and
            // produces huge, unstable ETAs. Start from the planned duration,
            // then blend in the observed rate as enough progress accumulates.
            double weight = Math.Clamp((fraction - 0.05) / 0.45, 0, 0.85);
            predictedTotal = (plannedSeconds * (1 - weight)) + (measuredTotal * weight);
        }

        double remainingSeconds = Math.Max(0, predictedTotal - elapsed.TotalSeconds);
        return $"预计剩余 {FormatClock(TimeSpan.FromSeconds(remainingSeconds))} · 已用 {FormatClock(elapsed)}";
    }

    private void AddSingleCoreSweep(FrequencyCoreVoltages frequency)
    {
        if (_singleCoreRun is null)
        {
            return;
        }

        if (!_singleCoreRun.CoreVoltages.Contains(frequency))
        {
            _singleCoreRun.CoreVoltages.Add(frequency);
        }

        RefreshHasResult();
        RebuildSingleCoreChart(false);
        UpdateChartSubtitle(_currentRun);
    }

    private void AddCoreVoltages(FrequencyCoreVoltages frequency)
    {
        if (_currentRun is null)
        {
            return;
        }

        // SweepEngine already stores the same instance in the run result. The
        // check also keeps demo/live callbacks safe if their ordering changes.
        if (!_currentRun.CoreVoltages.Contains(frequency))
        {
            _currentRun.CoreVoltages.Add(frequency);
        }

        HasResult = true;
        OnPropertyChanged(nameof(CanShowCoreVoltages));
        RebuildCharts(_currentRun, false);
        UpdateChartSubtitle(_currentRun);
    }

    private void AddLivePoint(SweepPoint point)
    {
        if (_currentRun is null)
        {
            return;
        }

        SweepSeries? series = _currentRun.Series.FirstOrDefault(s => s.CcdIndex == point.CcdIndex);
        if (series is null)
        {
            return;
        }

        if (!series.Points.Contains(point))
        {
            series.Add(point);
        }

        HasResult = true;
        OnPropertyChanged(nameof(CanShowCoreVoltages));
        RebuildCharts(_currentRun, false);
        UpdateChartSubtitle(_currentRun);
    }
}
