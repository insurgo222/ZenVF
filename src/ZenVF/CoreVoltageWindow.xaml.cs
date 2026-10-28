using System.Windows;
using Microsoft.Win32;
using OxyPlot;
using ZenVF.Models;
using ZenVF.Services;

namespace ZenVF;

public partial class CoreVoltageWindow : Window
{
    private readonly SweepRunResult _result;
    private readonly bool _isDemo;
    private readonly List<FrequencyCoreVoltages> _frequencies;
    private int _index;
    private PlotModel _plot;

    public CoreVoltageWindow(SweepRunResult result, bool isDemo)
    {
        InitializeComponent();
        _result = result;
        _isDemo = isDemo;
        _frequencies = result.CoreVoltages
            .Where(f => f.Cores.Count > 0)
            .OrderByDescending(f => f.RequestedFMax)
            .ToList();
        _plot = ChartBuilder.BuildCoreVoltages(result, 0, isDemo);
        VoltagePlot.Model = _plot;
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += OnClosed;
        if (_frequencies.Count == 0)
        {
            FrequencyText.Text = "NO DATA";
            FrequencySubText.Text = "完成一次扫描后再打开此窗口";
            MaxCoreText.Text = "没有可用的每核电压数据。";
            CoreCountText.Text = "完成一次扫描后再打开此窗口。";
            EclkPill.Visibility = Visibility.Collapsed;
            PrevFrequencyButton.IsEnabled = false;
            NextFrequencyButton.IsEnabled = false;
            ExportButton.IsEnabled = false;
            return;
        }

        UpdateView();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowThemeHelper.Apply(this);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        WindowThemeHelper.Apply(this);
        if (_frequencies.Count == 0)
        {
            return;
        }

        FrequencyCoreVoltages frequency = _frequencies[_index];
        _plot = ChartBuilder.BuildCoreVoltages(_result, frequency.RequestedFMax, _isDemo);
        VoltagePlot.Model = _plot;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void UpdateView()
    {
        if (_frequencies.Count == 0)
        {
            return;
        }

        FrequencyCoreVoltages frequency = _frequencies[_index];
        _plot = ChartBuilder.BuildCoreVoltages(_result, frequency.RequestedFMax, _isDemo);
        VoltagePlot.Model = _plot;
        int adjustedFMax = _result.ToEclkAdjustedFMax(frequency.RequestedFMax);
        FrequencyText.Text = $"{adjustedFMax} MHz";
        FrequencySubText.Text = _result.IsEclkDetected ? "eCLK 校正后的核心频率" : "核心频率";
        EclkPill.Visibility = _result.IsEclkDetected ? Visibility.Visible : Visibility.Collapsed;
        EclkText.Text = _result.IsEclkDetected
            ? $"eCLK {_result.EclkBclkMHz:0.###} MHz"
            : "eCLK";

        CoreVoltageSample? max = frequency.MaxVoltageCore;
        MaxCoreText.Text = max is null
            ? "最高电压核心：—"
            : $"最高电压核心：Core #{max.DisplayCoreId} · CCD{max.CcdIndex} · {max.Voltage:0.000} V";
        CoreCountText.Text = $"{frequency.Cores.Count} 个核心 · 档位 {adjustedFMax} MHz"
            + $" · {frequency.PackagePower:0.0} W · Tctl {frequency.Tctl:0.0} °C";

        PrevFrequencyButton.IsEnabled = _index > 0;
        NextFrequencyButton.IsEnabled = _index < _frequencies.Count - 1;
        ExportButton.IsEnabled = true;
    }

    private void PrevFrequency_Click(object sender, RoutedEventArgs e)
    {
        if (_index <= 0)
        {
            return;
        }

        _index--;
        UpdateView();
    }

    private void NextFrequency_Click(object sender, RoutedEventArgs e)
    {
        if (_index >= _frequencies.Count - 1)
        {
            return;
        }

        _index++;
        UpdateView();
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_frequencies.Count == 0)
        {
            return;
        }

        FrequencyCoreVoltages frequency = _frequencies[_index];
        SaveFileDialog dialog = new()
        {
            Title = "导出每核电压图",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = $"ZenVF_CoreVoltage_{_result.ToEclkAdjustedFMax(frequency.RequestedFMax)}MHz_{DateTime.Now:yyyyMMdd_HHmm}.png",
            AddExtension = true,
            DefaultExt = ".png"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ChartBuilder.ExportPng(_plot, dialog.FileName, 1600, 1000);
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出每核电压图失败：\n\n" + ex.Message, "ZenVF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
