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

/// <summary>Chart and legend rebuilding, chart subtitle, eCLK display.</summary>
public sealed partial class MainViewModel
{
    public void RefreshChartsForTheme()
    {
        RebuildCharts(_currentRun, IsDemo);
        RebuildSingleCoreChart(IsDemo);
    }

    private void RebuildCharts(SweepRunResult? result, bool isDemo)
    {
        PlotModel = ChartBuilder.Build(result, isDemo);
        PerCorePlotModel = ChartBuilder.BuildPerCore(result, isDemo);
        PerCoreLegend = ChartBuilder.BuildCoreLegend(PerCorePlotModel);
    }

    private void RebuildSingleCoreChart(bool isDemo)
    {
        SingleCorePlotModel = ChartBuilder.BuildPerCore(_singleCoreRun, isDemo, true);
        SingleCoreLegend = ChartBuilder.BuildCoreLegend(SingleCorePlotModel);
    }

    private void RefreshHasResult()
    {
        HasResult = (_currentRun?.CoreVoltages.Count > 0)
            || (_singleCoreRun?.CoreVoltages.Count > 0);
        OnPropertyChanged(nameof(CanShowCoreVoltages));
    }

    private void ApplySessionEclk(SweepRunResult result)
    {
        if (result.EclkRatio is null && _startupEclkRatio is double ratio)
        {
            result.EclkRatio = ratio;
            result.EclkSampleCount = _startupEclkSamples;
        }
    }

    private void ApplyEclk(SweepRunResult? result)
    {
        double? ratio = result?.EclkRatio ?? _startupEclkRatio;
        int sampleCount = result?.EclkSampleCount ?? _startupEclkSamples;
        if (ratio is double detectedRatio && detectedRatio > 1.0)
        {
            IsEclkDetected = true;
            EclkText = $"eCLK {FormatEclkBclk(detectedRatio)} MHz";
            string sourceText = _startupEclkPeakMHz > 0
                ? $"Core #N 运行最大值 {_startupEclkPeakMHz:0.0} MHz / PBO 最大频率 {_startupEclkPboMaxMHz:0} MHz"
                : $"eCLK 倍率 {detectedRatio:0.00000}";
            string busText = _startupEclkBusMHz > 0
                ? $"，Bus Speed {_startupEclkBusMHz:0.###} MHz"
                : string.Empty;
            EclkTooltip =
                $"已检测到外部时钟发生器（eCLK，启动显示频率读取值）。\n" +
                $"{sourceText}，BCLK2 ≈ {detectedRatio * 100:0.000} MHz{busText}，{sampleCount} 个样本。\n" +
                "仅使用 LHM Core #N 的运行时最大值（HWiNFO Max 列），不使用 Core #N (Effective)，也不使用烤机频率。\n" +
                "图表横轴使用实测有效频率；每核电压窗口的档位/标题按“设定频率 × eCLK 倍率”显示实际频率，窗口内不显示实时有效频率。";
            return;
        }

        IsEclkDetected = false;
        EclkText = "eCLK —";
        EclkTooltip = "未检测到 eCLK，或 LHM 未提供启动时的 Core #N 显示频率。频率标签按设定频率显示。";
    }

    private void UpdateChartSubtitle(SweepRunResult? result)
    {
        result ??= _currentRun;
        int allSteps = result?.CoreVoltages.Count ?? 0;
        int allCores = CountDistinctCores(result);
        int singleSteps = _singleCoreRun?.CoreVoltages.Count ?? 0;
        int singleCores = CountDistinctCores(_singleCoreRun);
        int? stockActual = result?.StockFMax ?? _singleCoreRun?.StockFMax;
        string stock = stockActual is int stockValue
            ? $" · 基准 {stockValue} MHz"
            : string.Empty;

        string cpuName = result?.CpuName ?? _singleCoreRun?.CpuName ?? CpuName;
        string subtitle;
        if (allSteps > 0)
        {
            subtitle = $"{cpuName} · 全核 {allSteps} 档 · {allCores} 核{stock}";
        }
        else if (singleSteps > 0)
        {
            subtitle = $"{cpuName} · 单核 {singleSteps} 档 · {singleCores} 核{stock}";
        }
        else
        {
            subtitle = $"{cpuName} · 等待扫描数据{stock}";
        }

        if (allSteps > 0 && singleSteps > 0)
        {
            subtitle += $" · 单核 {singleSteps} 档";
        }

        ChartSubtitle = subtitle;
    }

    private static int CountDistinctCores(SweepRunResult? result)
    {
        return result?.CoreVoltages
            .SelectMany(f => f.Cores)
            .Select(c => (c.CcdIndex, c.CoreId))
            .Distinct()
            .Count() ?? 0;
    }

    private static string FormatEclkBclk(double ratio)
    {
        return (ratio * 100.0).ToString("0.###", CultureInfo.InvariantCulture);
    }
}
