using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Microsoft.Win32;
using OxyPlot;
using OxyPlot.Series;
using ZenVF.Models;
using ZenVF.Services;
using ZenVF.ViewModels;

namespace ZenVF;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly AppOptions _options;
    private readonly bool _screenshotMode;
    private readonly string? _screenshotPath;
    private LineSeries? _hoveredMainLine;
    private LineSeries? _hoveredCoreLine;
    private LineSeries? _hoveredSingleCoreLine;

    public MainWindow(AppOptions options)
    {
        InitializeComponent();
        _options = options;
        _screenshotMode = options.ScreenshotPath is not null;
        _screenshotPath = options.ScreenshotPath;
        _vm = new MainViewModel(options);
        DataContext = _vm;
        _vm.LogItems.CollectionChanged += OnLogItemsChanged;
        _vm.SingleCoreSweepStarted += (_, _) => ChartTabs.SelectedIndex = 2;
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += OnClosed;

        // The custom hover card is the only tooltip. Remove OxyPlot's default
        // click-to-track command bindings so a click never opens a second
        // information overlay, but keep mouse-wheel zoom and left-drag pan available.
        ConfigurePassiveController(MainPlotView);
        ConfigurePassiveController(PerCorePlotView);
        ConfigurePassiveController(SingleCorePlotView);
        MainPlotView.PreviewMouseWheel += PlotView_PreviewMouseWheel;
        PerCorePlotView.PreviewMouseWheel += PlotView_PreviewMouseWheel;
        SingleCorePlotView.PreviewMouseWheel += PlotView_PreviewMouseWheel;
        MainPlotView.PreviewMouseLeftButtonDown += PlotView_PreviewMouseLeftButtonDown;
        PerCorePlotView.PreviewMouseLeftButtonDown += PlotView_PreviewMouseLeftButtonDown;
        SingleCorePlotView.PreviewMouseLeftButtonDown += PlotView_PreviewMouseLeftButtonDown;

        if (_screenshotMode)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = 0;
            Top = 0;
            Width = 1500;
            Height = 940;
            ShowInTaskbar = false;
        }
    }

    private static void ConfigurePassiveController(OxyPlot.Wpf.PlotView view)
    {
        PlotController controller = new();
        controller.UnbindAll();
        controller.BindMouseDown(OxyMouseButton.Left, PlotCommands.PanAt);
        controller.BindMouseWheel(PlotCommands.ZoomWheel);
        controller.BindMouseWheel(OxyModifierKeys.Control, PlotCommands.ZoomWheelFine);
        view.Controller = controller;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await _vm.InitializeAsync();
            ScrollLogToEnd();

            if (_screenshotMode && _screenshotPath is not null)
            {
                if (_options.ScreenshotCore && _vm.CurrentRun is not null)
                {
                    CoreVoltageWindow coreWindow = new(_vm.CurrentRun, _vm.IsDemo)
                    {
                        Owner = this,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 60,
                        Top = 60
                    };
                    coreWindow.Show();
                    await Task.Delay(800);
                    SaveWindowScreenshot(coreWindow, _screenshotPath);
                    coreWindow.Close();
                    Application.Current.Shutdown(0);
                    return;
                }

                if (_options.ScreenshotTheme)
                {
                    ThemeSettingsWindow themeWindow = new()
                    {
                        Owner = this,
                        WindowStartupLocation = WindowStartupLocation.Manual,
                        Left = 60,
                        Top = 60
                    };
                    themeWindow.Show();
                    await Task.Delay(800);
                    SaveWindowScreenshot(themeWindow, _screenshotPath);
                    themeWindow.Close();
                    Application.Current.Shutdown(0);
                    return;
                }

                if (_options.ScreenshotTab > 0 && _options.ScreenshotTab < ChartTabs.Items.Count)
                {
                    ChartTabs.SelectedIndex = _options.ScreenshotTab;
                }

                await Task.Delay(1500);
                SaveScreenshot(_screenshotPath);
                Application.Current.Shutdown(0);
            }
        }
        catch (Exception ex)
        {
            if (_screenshotMode && _screenshotPath is not null)
            {
                string logPath = Path.ChangeExtension(Path.GetFullPath(_screenshotPath), ".error.log");
                File.WriteAllText(logPath, ex.ToString());
                Application.Current.Shutdown(2);
                return;
            }

            MessageBox.Show("初始化失败：\n\n" + ex.Message, "ZenVF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void SaveScreenshot(string path)
    {
        SaveWindowScreenshot(this, path);
    }

    private static void SaveWindowScreenshot(Window window, string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (!fullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
        {
            fullPath += ".png";
        }

        string? directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        window.UpdateLayout();
        int width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth));
        int height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight));
        RenderTargetBitmap bitmap = new(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream stream = File.Create(fullPath);
        encoder.Save(stream);
    }

    private void OnLogItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, ScrollLogToEnd);
        }
    }

    private void ScrollLogToEnd()
    {
        if (_vm.LogItems.Count > 0)
        {
            LogList.ScrollIntoView(_vm.LogItems[^1]);
        }
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_vm.IsRunning)
        {
            MessageBoxResult result = MessageBox.Show(
                "测试仍在进行。关闭程序会停止测试并恢复原始频率。\n\n仍要关闭吗？",
                "ZenVF", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _vm.Shutdown();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowThemeHelper.Apply(this);
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        WindowThemeHelper.Apply(this);
        _vm.RefreshChartsForTheme();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeSettingsWindow window = new()
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (MaximizeButton is null)
        {
            return;
        }

        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ExportPng_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanExport)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出图表",
            Filter = "PNG 图片 (*.png)|*.png",
            FileName = DefaultFileName("png"),
            AddExtension = true,
            DefaultExt = ".png"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // Export the un-dimmed chart even if the pointer is still resting on
        // one of the curves.
        ResetAllHover();

        try
        {
            ChartBuilder.ExportPng(GetSelectedPlotModel(), dialog.FileName, 2000, 1125);
            _vm.Log(LogLevel.Success, $"PNG 已导出：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出 PNG 失败：\n\n" + ex.Message, "ZenVF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CoreVoltage_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentRun is null || _vm.CurrentRun.CoreVoltages.Count == 0)
        {
            return;
        }

        CoreVoltageWindow window = new(_vm.CurrentRun, _vm.IsDemo)
        {
            Owner = this
        };
        window.ShowDialog();
    }

    private void ExportCsv_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.CurrentRun is null && _vm.SingleCoreRun is null)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出 V/F 数据",
            Filter = "CSV 数据 (*.csv)|*.csv",
            FileName = DefaultFileName("csv"),
            AddExtension = true,
            DefaultExt = ".csv"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ChartBuilder.ExportCsv(_vm.CurrentRun, dialog.FileName, _vm.SingleCoreRun);
            _vm.Log(LogLevel.Success, $"CSV 已导出：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出 CSV 失败：\n\n" + ex.Message, "ZenVF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ExportMarkdown_Click(object sender, RoutedEventArgs e)
    {
        if (!_vm.CanExport)
        {
            return;
        }

        SaveFileDialog dialog = new()
        {
            Title = "导出 Markdown 报告",
            Filter = "Markdown 文件 (*.md)|*.md",
            FileName = DefaultFileName("md"),
            AddExtension = true,
            DefaultExt = ".md"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ChartBuilder.ExportMarkdown(_vm.CurrentRun, dialog.FileName, _vm.SingleCoreRun);
            _vm.Log(LogLevel.Success, $"Markdown 已导出：{dialog.FileName}");
        }
        catch (Exception ex)
        {
            MessageBox.Show("导出 Markdown 失败：\n\n" + ex.Message, "ZenVF", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        ZoomSelectedChart(1.25);
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
    {
        ZoomSelectedChart(0.8);
    }

    private void ResetZoom_Click(object sender, RoutedEventArgs e)
    {
        ResetAllHover();
        PlotModel model = GetSelectedPlotModel();
        model.ResetAllAxes();
        model.InvalidatePlot(false);
    }

    private void ZoomSelectedChart(double factor)
    {
        ResetAllHover();
        PlotModel model = GetSelectedPlotModel();
        model.ZoomAllAxes(factor);
        model.InvalidatePlot(false);
    }

    private PlotModel GetSelectedPlotModel()
    {
        return ChartTabs.SelectedIndex switch
        {
            1 => _vm.PerCorePlotModel,
            2 => _vm.SingleCorePlotModel,
            _ => _vm.PlotModel
        };
    }

    private void ChartTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded)
        {
            return;
        }

        ResetAllHover();
    }

    private void PlotView_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        ResetAllHover();
    }

    private void PlotView_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        ResetAllHover();
    }

    private void MainPlotView_MouseMove(object sender, MouseEventArgs e)
    {
        HandlePlotMouseMove(
            MainPlotView,
            e,
            MainHoverLayer,
            MainHoverTip,
            MainHoverSwatch,
            MainHoverTitle,
            MainHoverDetail,
            ref _hoveredMainLine);
    }

    private void MainPlotView_MouseLeave(object sender, MouseEventArgs e)
    {
        ResetHover(ref _hoveredMainLine, MainPlotView, MainHoverTip);
    }

    private void PerCorePlotView_MouseMove(object sender, MouseEventArgs e)
    {
        HandlePlotMouseMove(
            PerCorePlotView,
            e,
            CoreHoverLayer,
            CoreHoverTip,
            CoreHoverSwatch,
            CoreHoverTitle,
            CoreHoverDetail,
            ref _hoveredCoreLine);
    }

    private void PerCorePlotView_MouseLeave(object sender, MouseEventArgs e)
    {
        ResetHover(ref _hoveredCoreLine, PerCorePlotView, CoreHoverTip);
    }

    private void SingleCorePlotView_MouseMove(object sender, MouseEventArgs e)
    {
        HandlePlotMouseMove(
            SingleCorePlotView,
            e,
            SingleCoreHoverLayer,
            SingleCoreHoverTip,
            SingleCoreHoverSwatch,
            SingleCoreHoverTitle,
            SingleCoreHoverDetail,
            ref _hoveredSingleCoreLine);
    }

    private void SingleCorePlotView_MouseLeave(object sender, MouseEventArgs e)
    {
        ResetHover(ref _hoveredSingleCoreLine, SingleCorePlotView, SingleCoreHoverTip);
    }

    private static void HandlePlotMouseMove(
        OxyPlot.Wpf.PlotView view,
        MouseEventArgs e,
        Canvas layer,
        Border tip,
        Ellipse swatch,
        TextBlock title,
        TextBlock detail,
        ref LineSeries? hovered)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            ResetHover(ref hovered, view, tip);
            return;
        }

        if (view.ActualModel is not PlotModel model)
        {
            return;
        }

        if (hovered is not null && !model.Series.Contains(hovered))
        {
            hovered = null;
        }

        Point position = e.GetPosition(layer);
        LineSeries? nearest = FindNearestSeries(
            model,
            new ScreenPoint(position.X, position.Y),
            out TrackerHitResult? nearestHit);

        if (!ReferenceEquals(nearest, hovered))
        {
            hovered = nearest;
            ApplySeriesHighlight(model, nearest);
            view.InvalidatePlot(false);
        }

        if (nearest is not null && nearestHit is not null)
        {
            ShowHoverTip(nearest, nearestHit, tip, swatch, title, detail);
            PositionHoverTip(layer, tip, position);
        }
        else
        {
            tip.Visibility = Visibility.Collapsed;
        }
    }

    private static LineSeries? FindNearestSeries(
        PlotModel model,
        ScreenPoint position,
        out TrackerHitResult? nearestHit)
    {
        LineSeries? nearest = null;
        nearestHit = null;
        double nearestDistance = 14.0;

        foreach (LineSeries line in model.Series.OfType<LineSeries>())
        {
            if (!line.IsVisible || line.Tag is not ChartBuilder.ChartSeriesTag)
            {
                continue;
            }

            TrackerHitResult? hit = line.GetNearestPoint(position, line.CanTrackerInterpolatePoints);
            if (hit is null)
            {
                continue;
            }

            double dx = hit.Position.X - position.X;
            double dy = hit.Position.Y - position.Y;
            double distance = Math.Sqrt((dx * dx) + (dy * dy));
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearest = line;
                nearestHit = hit;
            }
        }

        return nearest;
    }

    private static void ShowHoverTip(
        LineSeries line,
        TrackerHitResult hit,
        Border tip,
        Ellipse swatch,
        TextBlock title,
        TextBlock detail)
    {
        if (line.Tag is not ChartBuilder.ChartSeriesTag tag)
        {
            tip.Visibility = Visibility.Collapsed;
            return;
        }

        swatch.Fill = ToBrush(tag.BaseColor);
        int pointIndex = FindNearestPointIndex(line, hit.DataPoint);
        DataPoint point = pointIndex >= 0 ? line.Points[pointIndex] : hit.DataPoint;
        string hoverTitle = tag.HoverTitle;
        if (tag.PointCoreIds is { Count: > 0 } coreIds
            && tag.CcdIndex is int ccdIndex
            && pointIndex >= 0
            && pointIndex < coreIds.Count)
        {
            hoverTitle = $"CCD{ccdIndex} · Core {coreIds[pointIndex]}";
        }

        title.Text = hoverTitle;
        detail.Text = $"{point.X:0} MHz   ·   {point.Y:0.000} V";
        tip.Visibility = Visibility.Visible;
    }

    private static int FindNearestPointIndex(LineSeries line, DataPoint target)
    {
        int nearestIndex = -1;
        double nearestDistance = double.MaxValue;
        for (int i = 0; i < line.Points.Count; i++)
        {
            DataPoint point = line.Points[i];
            double dx = point.X - target.X;
            double dy = point.Y - target.Y;
            double distance = (dx * dx) + (dy * dy);
            if (distance < nearestDistance)
            {
                nearestDistance = distance;
                nearestIndex = i;
            }
        }

        return nearestIndex;
    }

    private static void PositionHoverTip(Canvas layer, Border tip, Point cursor)
    {
        tip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Size size = tip.DesiredSize;
        double x = cursor.X + 18;
        double y = cursor.Y + 18;

        if (x + size.Width > layer.ActualWidth - 8)
        {
            x = cursor.X - size.Width - 18;
        }

        if (y + size.Height > layer.ActualHeight - 8)
        {
            y = cursor.Y - size.Height - 18;
        }

        x = Math.Max(8, Math.Min(x, Math.Max(8, layer.ActualWidth - size.Width - 8)));
        y = Math.Max(8, Math.Min(y, Math.Max(8, layer.ActualHeight - size.Height - 8)));
        Canvas.SetLeft(tip, x);
        Canvas.SetTop(tip, y);
    }

    private void ResetAllHover()
    {
        ResetHover(ref _hoveredMainLine, MainPlotView, MainHoverTip);
        ResetHover(ref _hoveredCoreLine, PerCorePlotView, CoreHoverTip);
        ResetHover(ref _hoveredSingleCoreLine, SingleCorePlotView, SingleCoreHoverTip);
    }

    private static void ResetHover(
        ref LineSeries? hovered,
        OxyPlot.Wpf.PlotView view,
        Border tip)
    {
        tip.Visibility = Visibility.Collapsed;
        if (hovered is null)
        {
            return;
        }

        hovered = null;
        if (view.ActualModel is PlotModel model)
        {
            ApplySeriesHighlight(model, null);
            view.InvalidatePlot(false);
        }
    }

    private static void ApplySeriesHighlight(PlotModel model, LineSeries? selected)
    {
        bool hasSelection = selected is not null && model.Series.Contains(selected);
        int? selectedCcd = hasSelection
                           && selected!.Tag is ChartBuilder.ChartSeriesTag { CcdIndex: int ccdIndex }
            ? ccdIndex
            : null;

        foreach (LineSeries line in model.Series.OfType<LineSeries>())
        {
            if (line.Tag is not ChartBuilder.ChartSeriesTag tag)
            {
                continue;
            }

            bool isSelected = hasSelection && ReferenceEquals(line, selected);
            bool isIdle = !hasSelection;
            OxyColor color = isIdle || isSelected
                ? tag.BaseColor
                : OxyColor.FromAColor(34, tag.BaseColor);

            line.Color = color;
            line.MarkerFill = color;
            line.StrokeThickness = isIdle
                ? tag.StrokeThickness
                : isSelected ? tag.StrokeThickness + 1.0 : Math.Max(0.8, tag.StrokeThickness - 0.9);
            line.MarkerSize = isIdle
                ? tag.MarkerSize
                : isSelected ? tag.MarkerSize + 2.0 : Math.Max(2.0, tag.MarkerSize - 0.9);
        }

        OxyColor annotationColor = ChartBuilder.PointLabelColor;
        foreach (ChartBuilder.CcdTextAnnotation annotation in model.Annotations.OfType<ChartBuilder.CcdTextAnnotation>())
        {
            bool isActive = selectedCcd is null || annotation.CcdIndex == selectedCcd.Value;
            annotation.TextColor = isActive ? annotationColor : OxyColor.FromAColor(34, annotationColor);
        }
    }

    private static Brush ToBrush(OxyColor color)
    {
        return new SolidColorBrush(Color.FromArgb(color.A, color.R, color.G, color.B));
    }

    private string DefaultFileName(string extension)
    {
        string cpu = _vm.CurrentRun?.CpuName ?? _vm.SingleCoreRun?.CpuName ?? "ZenVF";
        cpu = cpu.Replace("AMD ", string.Empty).Replace(" ", "_").Replace("/", "_");
        return $"ZenVF_{cpu}_{DateTime.Now:yyyyMMdd_HHmm}.{extension}";
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_vm.BuildLogText());
            _vm.Log(LogLevel.Info, "日志已复制到剪贴板。");
        }
        catch (Exception ex)
        {
            _vm.Log(LogLevel.Error, "复制日志失败：" + ex.Message);
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        _vm.LogItems.Clear();
    }
}
