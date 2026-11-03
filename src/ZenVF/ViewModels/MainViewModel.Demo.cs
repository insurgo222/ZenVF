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

/// <summary>Demo mode: sample data and simulated runs that never touch hardware.</summary>
public sealed partial class MainViewModel
{
    private void SetupDemo()
    {
        _cpu = DemoData.CreateCpu();
        _topology = DemoData.CreateTopology();
        _sensors = new DemoSensorService();
        _sensors.Start();
        _smu = null;
        _originalFMaxCeiling = 5900;
        _isPboArmed = true;
        OnPropertyChanged(nameof(IsPboArmed));
        OnPropertyChanged(nameof(CanTogglePbo));
        OnPropertyChanged(nameof(PboToggleHint));

        CpuName = _cpu.Name;
        ArchitectureText = "Zen 5";
        CoreText = $"{_cpu.PhysicalCores}C / {_cpu.LogicalCores}T";
        CcdText = "2 CCD";
        Has3DCache = true;
        StockFMaxText = "5700 MHz";
        CurrentFMaxText = "5900 MHz";
        OriginalFMaxText = "5900 MHz";
        ScalarText = "1.00";
        PboState = "on";
        PboSummary = "PBO 已开启";
        PboDetail = "演示模式：模拟 PBO 已开启，不会修改硬件。";
        PboEvidenceText = string.Join("\n", DemoData.CreatePboAssessment().Evidence.Take(2).Select(e => "• " + e));
        PawnIoStatusText = "PawnIO 演示";
        SmuStatusText = "RSMU 演示连接";
        DriverWarningVisible = false;
        IsHardwareReady = true;

        BuildCcdOptions();
        _suppressValidation = true;
        SweepSettings settings = DemoData.CreateSettings();
        StartFMaxText = settings.StartFMax.ToString();
        EndFMaxText = settings.EndFMax.ToString();
        StepText = settings.StepMHz.ToString();
        WarmupText = settings.WarmupSeconds.ToString();
        SampleText = settings.SampleSeconds.ToString();
        _suppressValidation = false;

        _currentRun = DemoData.CreateRunResult();
        _singleCoreRun = DemoData.CreateSingleCoreSweepResult(settings, _topology);
        ApplyEclk(_currentRun.IsEclkDetected ? _currentRun : _singleCoreRun);
        HasResult = _currentRun.CoreVoltages.Count > 0
            || _singleCoreRun.CoreVoltages.Count > 0;
        RebuildCharts(_currentRun, true);
        RebuildSingleCoreChart(true);
        UpdateChartSubtitle(_currentRun);
        RangeHintText = "可测 4300–5900 MHz · 默认 5900→4300";
        StatusText = "演示模式 · 不会修改硬件";
        StageText = "演示曲线已加载";
        OnPropertyChanged(nameof(CanShowCoreVoltages));
        Log(LogLevel.Warning, "DEMO 模式：不会修改硬件，也不会启动真实负载。");
        Log(LogLevel.Info, "已载入 9950X3D 演示 V/F 曲线（每档每 CCD 最高电压核心）。");
        Log(LogLevel.Info, $"已载入单核演示扫描（{_singleCoreRun.CoreVoltages.Count} 档 × {_singleCoreRun.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本）。");
        Log(LogLevel.Info, "最高频率接管：已打开（演示）。");
    }

    private async Task RunDemoSingleCoreSweepAsync(SweepSettings settings)
    {
        if (_topology is null)
        {
            Log(LogLevel.Error, "演示拓扑未就绪。");
            return;
        }

        List<PhysicalCoreRef> cores = _topology.GetPhysicalCores(settings.SelectedCcds).ToList();
        if (cores.Count == 0)
        {
            Log(LogLevel.Error, "所选 CCD 没有可测试的物理核心。");
            return;
        }

        _runPlannedSeconds = 0;
        IsRunning = true;
        ProgressPercent = 0;
        ProgressTimeText = "预计剩余 --:--";
        HasResult = _currentRun?.CoreVoltages.Count > 0;
        _cts = new CancellationTokenSource();
        _singleCoreRun = new SweepRunResult
        {
            CpuName = DemoData.CpuName,
            StockFMax = _cpu?.StockFMaxMHz ?? 5700,
            StockFMaxCeiling = _cpu?.StockFMaxCeilingMHz,
            FMaxCeilingOffsetMHz = _cpu?.FMaxCeilingOffsetMHz ?? 0,
            Settings = settings,
            Series = new List<SweepSeries>(),
            Started = DateTime.Now
        };
        ApplyEclk(null);
        RebuildSingleCoreChart(true);
        ChartSubtitle = "DEMO SINGLE-CORE SWEEP";
        StatusText = "演示单核测试中…";
        StageText = "DEMO · 准备单核负载";
        SingleCoreSweepStarted?.Invoke(this, EventArgs.Empty);
        Log(LogLevel.Info,
            $"DEMO 单核测试开始：{settings.StepCount} 个档位 × {cores.Count} 个核心，每个档位依次只测一个核心，不写入任何硬件。");

        List<int> frequencies = settings.BuildFrequencies().ToList();
        Stopwatch overall = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < frequencies.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                int requestedFMax = frequencies[i];
                FrequencyCoreVoltages frequency = DemoData.CreateSingleCoreFrequency(requestedFMax, cores);
                _singleCoreRun.CoreVoltages.Add(frequency);
                RefreshHasResult();
                RebuildSingleCoreChart(true);
                UpdateChartSubtitle(_currentRun);
                double fraction = (i + 1) / (double)frequencies.Count;
                ProgressPercent = (int)Math.Round(fraction * 100);
                ProgressTimeText = FormatProgressTime(fraction, overall.Elapsed, 0);
                StageText = $"DEMO · {requestedFMax} MHz · {cores.Count} 核 · 单核";
                await Task.Delay(150, _cts.Token);
            }

            _singleCoreRun.Finished = DateTime.Now;
            _singleCoreRun.Note = "演示数据 · 非实测";
            DemoData.ApplyDemoEclk(_singleCoreRun);
            ApplyEclk(_singleCoreRun.IsEclkDetected ? _singleCoreRun : _currentRun);
            RebuildSingleCoreChart(true);
            RefreshHasResult();
            StatusText = "演示单核测试完成 · 未写入硬件";
            StageText = $"DEMO · {_singleCoreRun.CoreVoltages.Count} 个档位 · {_singleCoreRun.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本";
            ProgressPercent = 100;
            ProgressTimeText = $"总用时 {FormatClock((_singleCoreRun.Finished ?? DateTime.Now) - _singleCoreRun.Started)}";
            UpdateChartSubtitle(_currentRun);
            Log(LogLevel.Success,
                $"DEMO 单核测试完成：{_singleCoreRun.CoreVoltages.Count} 个档位、{_singleCoreRun.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本。");
        }
        catch (OperationCanceledException)
        {
            StatusText = "演示单核测试已停止";
            ProgressTimeText = "已停止";
            Log(LogLevel.Warning, "单核测试已由用户停止。");
        }
        finally
        {
            IsRunning = false;
            // The subtitle is set to "…测试中…" at start and is otherwise only
            // replaced when a step completes; a stop/failure before the first
            // step left it stuck.
            UpdateChartSubtitle(_currentRun);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunDemoSweepAsync(SweepSettings settings)
    {
        _runPlannedSeconds = 0;
        IsRunning = true;
        ProgressPercent = 0;
        ProgressTimeText = "预计剩余 --:--";
        HasResult = _singleCoreRun?.CoreVoltages.Count > 0;
        _cts = new CancellationTokenSource();
        _currentRun = new SweepRunResult
        {
            CpuName = DemoData.CpuName,
            StockFMax = _cpu?.StockFMaxMHz ?? 5700,
            StockFMaxCeiling = _cpu?.StockFMaxCeilingMHz,
            FMaxCeilingOffsetMHz = _cpu?.FMaxCeilingOffsetMHz ?? 0,
            Settings = settings,
            Series = settings.SelectedCcds.OrderBy(i => i).Select(i => new SweepSeries
            {
                CcdIndex = i,
                Label = $"CCD{i} | DEMO"
            }).ToList(),
            Started = DateTime.Now
        };
        ApplyEclk(null);
        RebuildCharts(_currentRun, true);
        ChartSubtitle = "DEMO SWEEP RUNNING";
        StatusText = "演示全核测试中…";
        Log(LogLevel.Info, "DEMO 全核测试开始，不写入任何硬件。");

        List<int> frequencies = settings.BuildFrequencies().ToList();
        Stopwatch overall = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < frequencies.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                FrequencyCoreVoltages frequency = FilterRowToCcds(
                    DemoData.CreateCoreVoltages(frequencies[i]),
                    settings.SelectedCcds);
                _currentRun.CoreVoltages.Add(frequency);
                foreach (SweepSeries series in _currentRun.Series)
                {
                    SweepPoint? point = DemoData.CreatePointForCcd(frequency, series.CcdIndex);
                    if (point is not null)
                    {
                        series.Add(point);
                    }
                }

                RebuildCharts(_currentRun, true);
                OnPropertyChanged(nameof(CanShowCoreVoltages));
                double fraction = (i + 1) / (double)frequencies.Count;
                ProgressPercent = (int)Math.Round(fraction * 100);
                ProgressTimeText = FormatProgressTime(fraction, overall.Elapsed, 0);
                StageText = $"DEMO · {frequencies[i]} MHz · 全核";
                UpdateChartSubtitle(_currentRun);
                await Task.Delay(100, _cts.Token);
            }

            _currentRun.Finished = DateTime.Now;
            _currentRun.Note = "演示数据 · 非实测";
            DemoData.ApplyDemoEclk(_currentRun);
            ApplyEclk(_currentRun.IsEclkDetected ? _currentRun : _singleCoreRun);
            RebuildCharts(_currentRun, true);
            HasResult = true;
            StatusText = "演示全核测试完成 · 未写入硬件";
            StageText = "DEMO · 完成";
            ProgressPercent = 100;
            ProgressTimeText = $"总用时 {FormatClock((_currentRun.Finished ?? DateTime.Now) - _currentRun.Started)}";
            Log(LogLevel.Success, $"DEMO 全核测试完成，{_currentRun.CoreVoltages.Count} 个频率档位、{_currentRun.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本。");
        }
        catch (OperationCanceledException)
        {
            StatusText = "演示全核测试已停止";
            ProgressTimeText = "已停止";
            Log(LogLevel.Warning, "全核测试已由用户停止。");
        }
        finally
        {
            IsRunning = false;
            // The subtitle is set to "…测试中…" at start and is otherwise only
            // replaced when a step completes; a stop/failure before the first
            // step left it stuck.
            UpdateChartSubtitle(_currentRun);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static FrequencyCoreVoltages FilterRowToCcds(
        FrequencyCoreVoltages row,
        IReadOnlySet<int> selectedCcds) => new()
    {
        RequestedFMax = row.RequestedFMax,
        EffectiveFMax = row.EffectiveFMax,
        RequestedFMaxCeiling = row.RequestedFMaxCeiling,
        EffectiveFMaxCeiling = row.EffectiveFMaxCeiling,
        PackagePower = row.PackagePower,
        Tctl = row.Tctl,
        Cores = row.Cores.Where(c => selectedCcds.Contains(c.CcdIndex)).ToList()
    };
}
