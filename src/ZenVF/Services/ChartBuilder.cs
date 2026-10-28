using System.Globalization;
using System.Text;
using OxyPlot;
using WpfColor = System.Windows.Media.Color;
using OxyPlot.Annotations;
using OxyPlot.Axes;
using OxyPlot.Legends;
using OxyPlot.Series;
using ZenVF.Models;

namespace ZenVF.Services;

public static class ChartBuilder
{
    private static OxyColor Background = OxyColor.Parse("#0E1317");
    private static OxyColor PlotBackground = OxyColor.Parse("#0E1317");
    private static OxyColor Grid = OxyColor.Parse("#1E262C");
    private static OxyColor MinorGrid = OxyColor.Parse("#171E23");
    private static OxyColor AxisLine = OxyColor.Parse("#3A4650");
    private static OxyColor Text = OxyColor.Parse("#E6EDF2");
    private static OxyColor Muted = OxyColor.Parse("#9AA8B3");
    private static OxyColor Yellow = OxyColor.Parse("#C87533");
    private static OxyColor Blue = OxyColor.Parse("#5BC0BE");
    private static OxyColor BlueLight = OxyColor.Parse("#8AD8D6");
    private static OxyColor MarkerStrokeColor = OxyColor.Parse("#0E1317");
    private static OxyColor LegendBackground = OxyColor.FromAColor(220, Background);
    private static OxyColor LegendBorder = Grid;
    private static OxyColor LegendTextColor = Text;

    // Die-world series inks: copper, oxide, passivation, gold, violet and
    // steel. Adjacent entries alternate warm/cool so overlapping per-core
    // lines stay separable.
    private static OxyColor[] CorePalette =
    {
        OxyColor.Parse("#5BC0BE"),
        OxyColor.Parse("#C87533"),
        OxyColor.Parse("#7FBF9A"),
        OxyColor.Parse("#D8A84A"),
        OxyColor.Parse("#8E9BFF"),
        OxyColor.Parse("#E0715B"),
        OxyColor.Parse("#B48FD8"),
        OxyColor.Parse("#75A7D8"),
        OxyColor.Parse("#C7B45A"),
        OxyColor.Parse("#6FCFC9"),
        OxyColor.Parse("#D88FA8"),
        OxyColor.Parse("#9ACB8A"),
        OxyColor.Parse("#D0A46A"),
        OxyColor.Parse("#A8B3C4"),
        OxyColor.Parse("#8AD8FF"),
        OxyColor.Parse("#C9A227")
    };

    private static OxyColor[] CcdPalette =
    {
        Blue,
        OxyColor.Parse("#C87533"),
        OxyColor.Parse("#7FBF9A"),
        OxyColor.Parse("#D8A84A"),
        OxyColor.Parse("#B48FD8"),
        OxyColor.Parse("#75A7D8")
    };

    public static void ApplyTheme(bool dark, WpfColor accent)
    {
        OxyColor accentColor = OxyColor.FromRgb(accent.R, accent.G, accent.B);
        if (dark)
        {
            Background = OxyColor.Parse("#0E1317");
            PlotBackground = OxyColor.Parse("#0E1317");
            Grid = OxyColor.Parse("#1E262C");
            MinorGrid = OxyColor.Parse("#171E23");
            AxisLine = OxyColor.Parse("#3A4650");
            Text = OxyColor.Parse("#E6EDF2");
            Muted = OxyColor.Parse("#9AA8B3");
            Yellow = OxyColor.Parse("#C87533");
            MarkerStrokeColor = OxyColor.Parse("#0E1317");
            CorePalette = new[]
            {
                accentColor,
                OxyColor.Parse("#C87533"),
                OxyColor.Parse("#7FBF9A"),
                OxyColor.Parse("#D8A84A"),
                OxyColor.Parse("#8E9BFF"),
                OxyColor.Parse("#E0715B"),
                OxyColor.Parse("#B48FD8"),
                OxyColor.Parse("#75A7D8"),
                OxyColor.Parse("#C7B45A"),
                OxyColor.Parse("#6FCFC9"),
                OxyColor.Parse("#D88FA8"),
                OxyColor.Parse("#9ACB8A"),
                OxyColor.Parse("#D0A46A"),
                OxyColor.Parse("#A8B3C4"),
                OxyColor.Parse("#8AD8FF"),
                OxyColor.Parse("#C9A227")
            };
        }
        else
        {
            Background = OxyColor.Parse("#F7F8F8");
            PlotBackground = OxyColor.Parse("#FBFCFC");
            Grid = OxyColor.Parse("#DDE3E7");
            MinorGrid = OxyColor.Parse("#EDF0F2");
            AxisLine = OxyColor.Parse("#A9B4BC");
            Text = OxyColor.Parse("#10161B");
            Muted = OxyColor.Parse("#55636E");
            Yellow = OxyColor.Parse("#8A6100");
            MarkerStrokeColor = OxyColor.Parse("#FBFCFC");
            CorePalette = new[]
            {
                accentColor,
                OxyColor.Parse("#9A4E12"),
                OxyColor.Parse("#2F7A55"),
                OxyColor.Parse("#8A6100"),
                OxyColor.Parse("#4A5BB5"),
                OxyColor.Parse("#A63A2B"),
                OxyColor.Parse("#7A4D99"),
                OxyColor.Parse("#2D5F8A"),
                OxyColor.Parse("#6B6B00"),
                OxyColor.Parse("#0B7A74"),
                OxyColor.Parse("#A33A5E"),
                OxyColor.Parse("#3F7A3A"),
                OxyColor.Parse("#8A6420"),
                OxyColor.Parse("#5B6670"),
                OxyColor.Parse("#3A6FA0"),
                OxyColor.Parse("#8A6A10")
            };
        }

        Blue = accentColor;
        BlueLight = Blend(accentColor, OxyColors.White, dark ? 0.30 : 0.38);
        CcdPalette = new[]
        {
            Blue,
            dark ? OxyColor.Parse("#C87533") : OxyColor.Parse("#9A4E12"),
            dark ? OxyColor.Parse("#7FBF9A") : OxyColor.Parse("#2F7A55"),
            dark ? OxyColor.Parse("#D8A84A") : OxyColor.Parse("#8A6100"),
            dark ? OxyColor.Parse("#B48FD8") : OxyColor.Parse("#7A4D99"),
            dark ? OxyColor.Parse("#75A7D8") : OxyColor.Parse("#2D5F8A")
        };
        LegendBackground = OxyColor.FromAColor(dark ? (byte)220 : (byte)235, Background);
        LegendBorder = Grid;
        LegendTextColor = Text;
        PointLabelColor = Text;
    }

    private static OxyColor Blend(OxyColor from, OxyColor to, double amount)
    {
        return OxyColor.FromArgb(
            from.A,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    public sealed record ChartSeriesTag(
        string Label,
        string HoverTitle,
        OxyColor BaseColor,
        double StrokeThickness = 1.8,
        double MarkerSize = 3.4,
        IReadOnlyList<int>? PointCoreIds = null,
        int? CcdIndex = null);

    public static OxyColor PointLabelColor { get; private set; } = OxyColor.Parse("#E1E2E9");

    public static string CcdColorHex(int ccdIndex)
    {
        if (CcdPalette.Length == 0)
        {
            return "#5BC0BE";
        }

        OxyColor color = CcdPalette[Math.Abs(ccdIndex) % CcdPalette.Length];
        return $"#{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>Point label that can be dimmed together with its CCD line.</summary>
    public sealed class CcdTextAnnotation : TextAnnotation
    {
        public int CcdIndex { get; init; }
    }

    public static PlotModel Build(SweepRunResult? result, bool isDemo)
    {
        PlotModel model = new()
        {
            Background = Background,
            PlotAreaBackground = PlotBackground,
            PlotAreaBorderColor = AxisLine,
            PlotAreaBorderThickness = new OxyThickness(1),
            TextColor = Text,
            TitleColor = Text,
            DefaultFontSize = 12,
            IsLegendVisible = true
        };

        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.TopLeft,
            LegendPlacement = LegendPlacement.Inside,
            LegendOrientation = LegendOrientation.Horizontal,
            LegendBackground = LegendBackground,
            LegendBorder = LegendBorder,
            LegendBorderThickness = 1,
            LegendFont = "Bahnschrift",
            LegendFontSize = 12,
            LegendTextColor = LegendTextColor,
            LegendMargin = 14
        });

        List<SweepPoint> allPoints = result?.Series
            .SelectMany(s => s.Points)
            .Where(p => SensorValueSanity.IsPlausibleVoltage(p.Voltage)
                        && SensorValueSanity.IsPlausibleClock(p.ClockMHz))
            .ToList() ?? new();
        List<CoreVoltageSample> allCorePoints = result?.CoreVoltages
            .SelectMany(f => f.Cores)
            .Where(c => SensorValueSanity.IsPlausibleVoltage(c.Voltage)
                        && SensorValueSanity.IsPlausibleClock(c.ClockMHz))
            .ToList() ?? new();

        // A V/F curve plots the clock the core actually achieved, not the FMax
        // setpoint that was written to the SMU. When per-core data is present,
        // use the entire core population for the axis ranges.
        double minFrequency = allCorePoints.Count > 0
            ? allCorePoints.Min(p => p.ClockMHz)
            : allPoints.Count > 0 ? allPoints.Min(p => p.ClockMHz) : 4400;
        double maxFrequency = allCorePoints.Count > 0
            ? allCorePoints.Max(p => p.ClockMHz)
            : allPoints.Count > 0 ? allPoints.Max(p => p.ClockMHz) : 5600;
        if (maxFrequency - minFrequency < 500)
        {
            double centre = (minFrequency + maxFrequency) / 2;
            minFrequency = centre - 400;
            maxFrequency = centre + 400;
        }

        double minVoltage = allCorePoints.Count > 0
            ? allCorePoints.Min(p => p.Voltage)
            : allPoints.Count > 0 ? allPoints.Min(p => p.Voltage) : 0.95;
        double maxVoltage = allCorePoints.Count > 0
            ? allCorePoints.Max(p => p.Voltage)
            : allPoints.Count > 0 ? allPoints.Max(p => p.Voltage) : 1.28;

        double xMin = Math.Floor((minFrequency - 70) / 50) * 50;
        double xMax = Math.Ceiling((maxFrequency + 70) / 50) * 50;
        double yMin = Math.Max(0.50, Math.Floor((minVoltage - 0.025) * 20) / 20);
        double yMax = Math.Min(2.00, Math.Ceiling((maxVoltage + 0.035) * 20) / 20);

        // Guard against a single bad sensor sample or a millivolt/volt mix
        // collapsing the axes. The chart must always receive min < max.
        if (xMax - xMin < 500)
        {
            double centre = (xMin + xMax) / 2;
            xMin = centre - 500;
            xMax = centre + 500;
        }

        if (yMax - yMin < 0.12)
        {
            double centre = (yMin + yMax) / 2;
            yMin = Math.Max(0.50, centre - 0.12);
            yMax = Math.Min(2.00, centre + 0.12);
        }

        if (yMax <= yMin + 0.02)
        {
            yMin = 0.90;
            yMax = 1.30;
        }

        LinearAxis xAxis = new()
        {
            Position = AxisPosition.Bottom,
            Title = "EFFECTIVE FREQUENCY (MHz)",
            TitleFont = "Bahnschrift",
            TitleFontSize = 12,
            TitleColor = Muted,
            TitlePosition = 0.5,
            AxisTitleDistance = 10,
            Font = "Cascadia Mono",
            FontSize = 12,
            TextColor = Text,
            TicklineColor = AxisLine,
            AxislineColor = AxisLine,
            AxislineStyle = LineStyle.Solid,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = Grid,
            MinorGridlineStyle = LineStyle.Solid,
            MinorGridlineColor = MinorGrid,
            MajorStep = 200,
            MinorStep = 100,
            Minimum = xMin,
            Maximum = xMax,
            IsZoomEnabled = true,
            IsPanEnabled = true
        };

        LinearAxis yAxis = new()
        {
            Position = AxisPosition.Left,
            Title = "VOLTAGE (V)",
            TitleFont = "Bahnschrift",
            TitleFontSize = 12,
            TitleColor = Muted,
            AxisTitleDistance = 10,
            Font = "Cascadia Mono",
            FontSize = 12,
            TextColor = Text,
            TicklineColor = AxisLine,
            AxislineColor = AxisLine,
            AxislineStyle = LineStyle.Solid,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = Grid,
            MinorGridlineStyle = LineStyle.Solid,
            MinorGridlineColor = MinorGrid,
            MajorStep = 0.05,
            MinorStep = 0.025,
            Minimum = yMin,
            Maximum = yMax,
            StringFormat = "0.00",
            IsZoomEnabled = true,
            IsPanEnabled = true
        };

        model.Axes.Add(xAxis);
        model.Axes.Add(yAxis);

        if (result is not null)
        {
            // The main chart now gets one max-voltage-core trajectory per CCD.
            // A single global worst core hides cross-CCD differences, especially
            // on dual-CCD parts where the voltage leader can change per step.
            SortedDictionary<int, List<(FrequencyCoreVoltages Frequency, CoreVoltageSample Core)>> ccdMaxCores = new();
            foreach (FrequencyCoreVoltages frequency in result.CoreVoltages)
            {
                foreach (IGrouping<int, CoreVoltageSample> ccdGroup in frequency.Cores
                             .Where(c => SensorValueSanity.IsPlausibleVoltage(c.Voltage)
                                         && SensorValueSanity.IsPlausibleClock(c.ClockMHz))
                             .GroupBy(c => c.CcdIndex)
                             .OrderBy(g => g.Key))
                {
                    CoreVoltageSample? core = ccdGroup
                        .OrderByDescending(c => c.Voltage)
                        .ThenBy(c => c.DisplayCoreId)
                        .FirstOrDefault();
                    if (core is null)
                    {
                        continue;
                    }

                    if (!ccdMaxCores.TryGetValue(ccdGroup.Key, out var points))
                    {
                        points = new List<(FrequencyCoreVoltages Frequency, CoreVoltageSample Core)>();
                        ccdMaxCores[ccdGroup.Key] = points;
                    }

                    points.Add((frequency, core));
                }
            }

            if (ccdMaxCores.Count > 0)
            {
                foreach (KeyValuePair<int, List<(FrequencyCoreVoltages Frequency, CoreVoltageSample Core)>> ccd in ccdMaxCores)
                {
                    int ccdIndex = ccd.Key;
                    List<(FrequencyCoreVoltages Frequency, CoreVoltageSample Core)> maxCorePoints = ccd.Value
                        .OrderBy(pair => pair.Core.ClockMHz)
                        .ToList();
                    List<int> pointCoreIds = maxCorePoints
                        .Select(pair => pair.Core.DisplayCoreId)
                        .ToList();
                    OxyColor color = CcdColor(ccdIndex);
                    string label = $"CCD{ccdIndex} MAX CORE";
                    LineSeries maxLine = new()
                    {
                        Title = label,
                        Color = color,
                        StrokeThickness = 2.4,
                        MarkerType = MarkerType.Circle,
                        MarkerSize = 5.2,
                        MarkerFill = color,
                        MarkerStroke = MarkerStrokeColor,
                        MarkerStrokeThickness = 1,
                        TrackerFormatString = $"{label}\n{{2:0}} MHz  ·  {{4:0.000}} V",
                        TrackerKey = $"maxcore{ccdIndex}",
                        CanTrackerInterpolatePoints = true,
                        Tag = new ChartSeriesTag(
                            label,
                            $"CCD{ccdIndex} · MAX CORE",
                            color,
                            2.4,
                            5.2,
                            pointCoreIds,
                            ccdIndex)
                    };

                    foreach (var pair in maxCorePoints)
                    {
                        maxLine.Points.Add(new DataPoint(pair.Core.ClockMHz, pair.Core.Voltage));
                    }

                    model.Series.Add(maxLine);

                    if (maxCorePoints.Count <= 48)
                    {
                        // The legend identifies the CCD, so the point labels only
                        // need the short core number. Labels are staggered above
                        // and below the curve to reduce collisions when the two
                        // CCD trajectories are close together.
                        bool labelAbove = ccdIndex % 2 == 0;
                        foreach (var pair in maxCorePoints)
                        {
                            model.Annotations.Add(new CcdTextAnnotation
                            {
                                CcdIndex = ccdIndex,
                                Text = $"#{pair.Core.DisplayCoreId}",
                                TextPosition = new DataPoint(
                                    pair.Core.ClockMHz,
                                    pair.Core.Voltage + (labelAbove ? 0.0055 : -0.0055)),
                                TextColor = PointLabelColor,
                                Font = "Bahnschrift",
                                FontSize = 8.5,
                                TextHorizontalAlignment = HorizontalAlignment.Center,
                                TextVerticalAlignment = labelAbove
                                    ? VerticalAlignment.Bottom
                                    : VerticalAlignment.Top
                            });
                        }
                    }
                }
            }
            else
            {
                foreach (SweepSeries series in result.Series.OrderBy(s => s.CcdIndex))
                {
                    OxyColor color = CcdColor(series.CcdIndex);
                    string label = series.Label;
                    LineSeries line = new()
                    {
                        Title = label,
                        Color = color,
                        StrokeThickness = 2.2,
                        MarkerType = MarkerType.Circle,
                        MarkerSize = 4.8,
                        MarkerFill = color,
                        MarkerStroke = MarkerStrokeColor,
                        MarkerStrokeThickness = 1,
                        TrackerFormatString = $"{label}\n{{2:0}} MHz  ·  {{4:0.000}} V",
                        TrackerKey = $"ccd{series.CcdIndex}",
                        CanTrackerInterpolatePoints = true,
                        Tag = new ChartSeriesTag(label, label, color, 2.2, 4.8, null, series.CcdIndex)
                    };

                    foreach (SweepPoint point in series.Points
                                 .Where(p => SensorValueSanity.IsPlausibleVoltage(p.Voltage)
                                             && SensorValueSanity.IsPlausibleClock(p.ClockMHz))
                                 .OrderBy(p => p.ClockMHz))
                    {
                        line.Points.Add(new DataPoint(point.ClockMHz, point.Voltage));
                    }

                    model.Series.Add(line);
                }
            }
        }

        if (allPoints.Count == 0 && allCorePoints.Count == 0)
        {
            ApplyEmptyState(model, xAxis, yAxis, xMin, xMax, yMin, yMax);
        }
        else if (result?.StockFMax is int stock)
        {
            string label = result.StockFMaxCeiling is int ceiling && ceiling != stock
                ? $"STOCK {stock} MHz  ·  SMU CEILING {ceiling} MHz"
                : $"STOCK {stock} MHz";

            model.Annotations.Add(new TextAnnotation
            {
                Text = label,
                TextPosition = new DataPoint(xMax - ((xMax - xMin) * 0.015), yMin + ((yMax - yMin) * 0.055)),
                TextColor = OxyColor.FromAColor(110, Yellow),
                Font = "Bahnschrift",
                FontSize = 9,
                TextHorizontalAlignment = HorizontalAlignment.Right,
                TextVerticalAlignment = VerticalAlignment.Bottom
            });
        }

        return model;
    }

    public static PlotModel BuildPerCore(SweepRunResult? result, bool isDemo, bool singleCoreLoad = false)
    {
        PlotModel model = new()
        {
            Background = Background,
            PlotAreaBackground = PlotBackground,
            PlotAreaBorderColor = AxisLine,
            PlotAreaBorderThickness = new OxyThickness(1),
            TextColor = Text,
            TitleColor = Text,
            DefaultFontSize = 12,
            IsLegendVisible = true
        };

        model.Legends.Add(new Legend
        {
            LegendPosition = LegendPosition.TopCenter,
            LegendPlacement = LegendPlacement.Outside,
            LegendOrientation = LegendOrientation.Horizontal,
            LegendBackground = Background,
            LegendBorder = OxyColors.Transparent,
            LegendBorderThickness = 0,
            LegendFont = "Bahnschrift",
            LegendFontSize = 9.5,
            LegendTextColor = Text,
            LegendMargin = 2,
            LegendPadding = 2,
            LegendSymbolLength = 12,
            LegendItemSpacing = 3,
            LegendColumnSpacing = 10
        });

        List<CoreVoltageSample> allCorePoints = result?.CoreVoltages
            .SelectMany(f => f.Cores)
            .Where(c => SensorValueSanity.IsPlausibleVoltage(c.Voltage)
                        && SensorValueSanity.IsPlausibleClock(c.ClockMHz))
            .ToList() ?? new();

        double minFrequency = allCorePoints.Count > 0 ? allCorePoints.Min(p => p.ClockMHz) : 4400;
        double maxFrequency = allCorePoints.Count > 0 ? allCorePoints.Max(p => p.ClockMHz) : 5600;
        if (maxFrequency - minFrequency < 500)
        {
            double centre = (minFrequency + maxFrequency) / 2;
            minFrequency = centre - 400;
            maxFrequency = centre + 400;
        }

        double minVoltage = allCorePoints.Count > 0 ? allCorePoints.Min(p => p.Voltage) : 0.95;
        double maxVoltage = allCorePoints.Count > 0 ? allCorePoints.Max(p => p.Voltage) : 1.28;

        double xMin = Math.Floor((minFrequency - 70) / 50) * 50;
        double xMax = Math.Ceiling((maxFrequency + 70) / 50) * 50;
        double yMin = Math.Max(0.50, Math.Floor((minVoltage - 0.025) * 20) / 20);
        double yMax = Math.Min(2.00, Math.Ceiling((maxVoltage + 0.035) * 20) / 20);

        if (xMax - xMin < 500)
        {
            double centre = (xMin + xMax) / 2;
            xMin = centre - 500;
            xMax = centre + 500;
        }

        if (yMax - yMin < 0.12)
        {
            double centre = (yMin + yMax) / 2;
            yMin = Math.Max(0.50, centre - 0.12);
            yMax = Math.Min(2.00, centre + 0.12);
        }

        if (yMax <= yMin + 0.02)
        {
            yMin = 0.90;
            yMax = 1.30;
        }

        LinearAxis xAxis = new()
        {
            Position = AxisPosition.Bottom,
            Title = "EFFECTIVE FREQUENCY (MHz)",
            TitleFont = "Bahnschrift",
            TitleFontSize = 12,
            TitleColor = Muted,
            TitlePosition = 0.5,
            AxisTitleDistance = 10,
            Font = "Cascadia Mono",
            FontSize = 12,
            TextColor = Text,
            TicklineColor = AxisLine,
            AxislineColor = AxisLine,
            AxislineStyle = LineStyle.Solid,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = Grid,
            MinorGridlineStyle = LineStyle.Solid,
            MinorGridlineColor = MinorGrid,
            MajorStep = 200,
            MinorStep = 100,
            Minimum = xMin,
            Maximum = xMax,
            IsZoomEnabled = true,
            IsPanEnabled = true
        };

        LinearAxis yAxis = new()
        {
            Position = AxisPosition.Left,
            Title = "VOLTAGE (V)",
            TitleFont = "Bahnschrift",
            TitleFontSize = 12,
            TitleColor = Muted,
            AxisTitleDistance = 10,
            Font = "Cascadia Mono",
            FontSize = 12,
            TextColor = Text,
            TicklineColor = AxisLine,
            AxislineColor = AxisLine,
            AxislineStyle = LineStyle.Solid,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = Grid,
            MinorGridlineStyle = LineStyle.Solid,
            MinorGridlineColor = MinorGrid,
            MajorStep = 0.05,
            MinorStep = 0.025,
            Minimum = yMin,
            Maximum = yMax,
            StringFormat = "0.00",
            IsZoomEnabled = true,
            IsPanEnabled = true
        };

        model.Axes.Add(xAxis);
        model.Axes.Add(yAxis);

        if (result is not null)
        {
            int colorIndex = 0;
            foreach (IGrouping<(int CcdIndex, int CoreId), CoreVoltageSample> group in allCorePoints
                         .GroupBy(c => (c.CcdIndex, c.CoreId))
                         .OrderBy(g => g.Key.CcdIndex)
                         .ThenBy(g => g.Key.CoreId))
            {
                List<CoreVoltageSample> samples = group
                    .OrderBy(c => c.ClockMHz)
                    .ThenBy(c => c.Voltage)
                    .ToList();
                if (samples.Count == 0)
                {
                    continue;
                }

                CoreVoltageSample first = samples[0];
                OxyColor color = CorePalette[colorIndex++ % CorePalette.Length];
                string label = $"CCD{first.CcdIndex} Core{first.DisplayCoreId}";
                LineSeries line = new()
                {
                    Title = label,
                    Color = color,
                    StrokeThickness = 1.8,
                    MarkerType = MarkerType.Circle,
                    MarkerSize = 3.4,
                    MarkerFill = color,
                    MarkerStroke = OxyColors.Transparent,
                    MarkerStrokeThickness = 0,
                    TrackerFormatString = $"CCD{first.CcdIndex} · Core {first.DisplayCoreId}\n{{2:0}} MHz  ·  {{4:0.000}} V",
                    TrackerKey = $"core{first.CcdIndex}_{first.CoreId}",
                    CanTrackerInterpolatePoints = true,
                    Tag = new ChartSeriesTag(
                        label,
                        $"CCD{first.CcdIndex} · Core {first.DisplayCoreId}",
                        color,
                        CcdIndex: first.CcdIndex)
                };

                foreach (CoreVoltageSample core in samples)
                {
                    line.Points.Add(new DataPoint(core.ClockMHz, core.Voltage));
                }

                model.Series.Add(line);
            }
        }

        if (model.Series.Count == 0)
        {
            ApplyEmptyState(model, xAxis, yAxis, xMin, xMax, yMin, yMax);
        }
        else if (singleCoreLoad)
        {
            model.Annotations.Add(new TextAnnotation
            {
                Text = "SINGLE-CORE LOAD",
                TextPosition = new DataPoint(xMin + ((xMax - xMin) * 0.015), yMin + ((yMax - yMin) * 0.055)),
                TextColor = OxyColor.FromAColor(150, BlueLight),
                Font = "Bahnschrift",
                FontSize = 9,
                TextHorizontalAlignment = HorizontalAlignment.Left,
                TextVerticalAlignment = VerticalAlignment.Bottom
            });
        }

        if (model.Series.Count > 0 && result?.StockFMax is int stock)
        {
            string label = result.StockFMaxCeiling is int ceiling && ceiling != stock
                ? $"STOCK {stock} MHz  ·  SMU CEILING {ceiling} MHz"
                : $"STOCK {stock} MHz";

            model.Annotations.Add(new TextAnnotation
            {
                Text = label,
                TextPosition = new DataPoint(xMax - ((xMax - xMin) * 0.015), yMin + ((yMax - yMin) * 0.055)),
                TextColor = OxyColor.FromAColor(110, Yellow),
                Font = "Bahnschrift",
                FontSize = 9,
                TextHorizontalAlignment = HorizontalAlignment.Right,
                TextVerticalAlignment = VerticalAlignment.Bottom
            });
        }

        return model;
    }

    public static PlotModel BuildCoreVoltages(SweepRunResult? result, int requestedFMax, bool isDemo)
    {
        PlotModel model = new()
        {
            Background = Background,
            PlotAreaBackground = PlotBackground,
            PlotAreaBorderColor = AxisLine,
            PlotAreaBorderThickness = new OxyThickness(1),
            TextColor = Text,
            TitleColor = Text,
            DefaultFontSize = 12,
            IsLegendVisible = false
        };

        FrequencyCoreVoltages? frequency = result?.CoreVoltages
            .OrderBy(f => Math.Abs(f.RequestedFMax - requestedFMax))
            .FirstOrDefault();
        if (frequency is null || frequency.Cores.Count == 0)
        {
            AddEmptyAxes(model, "无每核电压数据", isDemo
                ? "演示数据不包含每核电压"
                : "完成一次扫描后查看每核电压");
            return model;
        }

        int adjustedFMax = result?.ToEclkAdjustedFMax(frequency.RequestedFMax) ?? frequency.RequestedFMax;

        List<CoreVoltageSample> cores = frequency.Cores
            .Where(c => SensorValueSanity.IsPlausibleVoltage(c.Voltage))
            .OrderBy(c => c.CoreId)
            .ToList();
        if (cores.Count == 0)
        {
            AddEmptyAxes(model, "无每核电压数据", "核心 VID 传感器没有读数");
            return model;
        }

        CoreVoltageSample? maxCore = frequency.MaxVoltageCore;
        double baseline = Math.Max(0.50, Math.Floor((cores.Min(c => c.Voltage) - 0.04) * 20) / 20);
        double voltageMax = Math.Ceiling((cores.Max(c => c.Voltage) + 0.035) * 50) / 50;
        if (voltageMax <= baseline + 0.02)
        {
            voltageMax = baseline + 0.10;
        }

        LinearAxis xAxis = new()
        {
            Position = AxisPosition.Bottom,
            Title = "VOLTAGE (V)",
            TitleFont = "Bahnschrift",
            TitleFontSize = 12,
            TitleColor = Muted,
            AxisTitleDistance = 10,
            Font = "Cascadia Mono",
            FontSize = 11,
            TextColor = Text,
            TicklineColor = AxisLine,
            AxislineColor = AxisLine,
            MajorGridlineStyle = LineStyle.Solid,
            MajorGridlineColor = Grid,
            MinorGridlineStyle = LineStyle.Solid,
            MinorGridlineColor = MinorGrid,
            MajorStep = 0.02,
            MinorStep = 0.01,
            Minimum = baseline,
            Maximum = voltageMax,
            StringFormat = "0.00",
            IsZoomEnabled = false,
            IsPanEnabled = false
        };

        CategoryAxis yAxis = new()
        {
            Position = AxisPosition.Left,
            Title = "CORE / CCD",
            TitleFont = "Bahnschrift",
            TitleFontSize = 12,
            TitleColor = Muted,
            AxisTitleDistance = 10,
            Font = "Cascadia Mono",
            FontSize = 10,
            TextColor = Text,
            TicklineColor = AxisLine,
            AxislineColor = AxisLine,
            IsZoomEnabled = false,
            IsPanEnabled = false
        };

        foreach (CoreVoltageSample core in cores)
        {
            yAxis.Labels.Add($"Core #{core.DisplayCoreId}  ·  CCD{core.CcdIndex}");
        }

        model.Axes.Add(xAxis);
        model.Axes.Add(yAxis);

        BarSeries bars = new()
        {
            Title = "CORE VID",
            FillColor = Blue,
            StrokeThickness = 0,
            BarWidth = 0.68,
            BaseValue = baseline,
            LabelFormatString = "{0:0.000} V",
            LabelPlacement = LabelPlacement.Outside,
            LabelColor = Text,
            LabelMargin = 3,
            Font = "Cascadia Mono",
            FontSize = 9.5,
            TrackerFormatString = $"{adjustedFMax} MHz\n{{1}}  ·  {{2:0.000}} V"
        };

        for (int i = 0; i < cores.Count; i++)
        {
            CoreVoltageSample core = cores[i];
            OxyColor color = maxCore is not null && core.CoreId == maxCore.CoreId
                ? Yellow
                : core.CcdIndex % 2 == 0 ? Blue : BlueLight;
            bars.Items.Add(new BarItem(core.Voltage, i) { Color = color });
        }

        model.Series.Add(bars);

        return model;
    }

    private static void AddEmptyAxes(PlotModel model, string title, string hint)
    {
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Bottom,
            Minimum = 0,
            Maximum = 1,
            IsZoomEnabled = false,
            IsPanEnabled = false,
            TextColor = OxyColors.Transparent,
            TicklineColor = OxyColors.Transparent,
            AxislineColor = OxyColors.Transparent
        });
        model.Axes.Add(new LinearAxis
        {
            Position = AxisPosition.Left,
            Minimum = 0,
            Maximum = 1,
            IsZoomEnabled = false,
            IsPanEnabled = false,
            TextColor = OxyColors.Transparent,
            TicklineColor = OxyColors.Transparent,
            AxislineColor = OxyColors.Transparent
        });
        model.Annotations.Add(new TextAnnotation
        {
            Text = title,
            TextPosition = new DataPoint(0.5, 0.54),
            TextColor = OxyColor.FromAColor(150, Muted),
            Font = "Microsoft YaHei UI",
            FontSize = 20,
            TextHorizontalAlignment = HorizontalAlignment.Center,
            TextVerticalAlignment = VerticalAlignment.Middle
        });
        model.Annotations.Add(new TextAnnotation
        {
            Text = hint,
            TextPosition = new DataPoint(0.5, 0.46),
            TextColor = OxyColor.FromAColor(110, Muted),
            Font = "Microsoft YaHei UI",
            FontSize = 11,
            TextHorizontalAlignment = HorizontalAlignment.Center,
            TextVerticalAlignment = VerticalAlignment.Middle
        });
    }

    public static void ExportPng(PlotModel model, string path, int width = 1600, int height = 900)
    {
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        OxyPlot.Wpf.PngExporter.Export(model, path, width, height, 144);
    }

    public static void ExportCsv(
        SweepRunResult? result,
        string path,
        SweepRunResult? singleCore = null)
    {
        StringBuilder builder = new();
        builder.AppendLine("# ZenVF data export");
        string cpuName = result?.CpuName ?? singleCore?.CpuName ?? "";
        int? stockFMax = result?.StockFMax ?? singleCore?.StockFMax;
        int? stockCeiling = result?.StockFMaxCeiling ?? singleCore?.StockFMaxCeiling;
        int ceilingOffset = result?.FMaxCeilingOffsetMHz ?? singleCore?.FMaxCeilingOffsetMHz ?? 0;
        builder.AppendLine($"# CPU,{Escape(cpuName)}");
        builder.AppendLine($"# StockActualFMaxMHz,{stockFMax?.ToString(CultureInfo.InvariantCulture) ?? ""}");
        builder.AppendLine($"# StockCeilingFMaxMHz,{stockCeiling?.ToString(CultureInfo.InvariantCulture) ?? ""}");
        builder.AppendLine($"# FMaxCeilingOffsetMHz,{ceilingOffset.ToString(CultureInfo.InvariantCulture)}");
        double? eclkRatio = result?.EclkRatio ?? singleCore?.EclkRatio;
        builder.AppendLine($"# EclkRatio,{(eclkRatio.HasValue ? eclkRatio.Value.ToString("0.00000", CultureInfo.InvariantCulture) : "")}");
        builder.AppendLine($"# EclkBclkMHz,{(eclkRatio.HasValue ? (eclkRatio.Value * 100).ToString("0.000", CultureInfo.InvariantCulture) : "")}");

        if (result is not null)
        {
            builder.AppendLine($"# Started,{result.Started:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"# Finished,{(result.Finished.HasValue ? result.Finished.Value.ToString("yyyy-MM-dd HH:mm:ss") : "")}");
            builder.AppendLine("# ccd,core_id,requested_actual_fmax_mhz,effective_actual_fmax_mhz,requested_ceiling_fmax_mhz,effective_ceiling_fmax_mhz,effective_clock_mhz,voltage_v,temperature_c,package_power_w,samples,timestamp,eclk_actual_fmax_mhz");
            foreach (SweepSeries series in result.Series.OrderBy(s => s.CcdIndex))
            {
                foreach (SweepPoint point in series.Points.OrderByDescending(p => p.RequestedFMax))
                {
                    builder.AppendLine(string.Join(",",
                        point.CcdIndex.ToString(CultureInfo.InvariantCulture),
                        point.DisplayCoreId.ToString(CultureInfo.InvariantCulture),
                        point.RequestedFMax.ToString(CultureInfo.InvariantCulture),
                        point.EffectiveFMax.ToString(CultureInfo.InvariantCulture),
                        point.RequestedFMaxCeiling.ToString(CultureInfo.InvariantCulture),
                        point.EffectiveFMaxCeiling.ToString(CultureInfo.InvariantCulture),
                        point.ClockMHz.ToString("0.0", CultureInfo.InvariantCulture),
                        point.Voltage.ToString("0.0000", CultureInfo.InvariantCulture),
                        point.Temperature.ToString("0.0", CultureInfo.InvariantCulture),
                        point.PackagePower.ToString("0.0", CultureInfo.InvariantCulture),
                        point.SampleCount.ToString(CultureInfo.InvariantCulture),
                        point.Timestamp.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                        result.ToEclkAdjustedFMax(point.RequestedFMax).ToString(CultureInfo.InvariantCulture)));
                }
            }

            builder.AppendLine();
            builder.AppendLine("# per_core_voltages");
            builder.AppendLine("# requested_actual_fmax_mhz,effective_actual_fmax_mhz,requested_ceiling_fmax_mhz,effective_ceiling_fmax_mhz,ccd,core_id,effective_clock_mhz,voltage_v,temperature_c,package_power_w,tctl_c,eclk_actual_fmax_mhz");
            foreach (FrequencyCoreVoltages frequency in result.CoreVoltages.OrderByDescending(f => f.RequestedFMax))
            {
                foreach (CoreVoltageSample core in frequency.Cores
                             .OrderBy(c => c.CcdIndex)
                             .ThenBy(c => c.CoreId))
                {
                    builder.AppendLine(string.Join(",",
                        frequency.RequestedFMax.ToString(CultureInfo.InvariantCulture),
                        frequency.EffectiveFMax.ToString(CultureInfo.InvariantCulture),
                        frequency.RequestedFMaxCeiling.ToString(CultureInfo.InvariantCulture),
                        frequency.EffectiveFMaxCeiling.ToString(CultureInfo.InvariantCulture),
                        core.CcdIndex.ToString(CultureInfo.InvariantCulture),
                        core.DisplayCoreId.ToString(CultureInfo.InvariantCulture),
                        core.ClockMHz.ToString("0.0", CultureInfo.InvariantCulture),
                        core.Voltage.ToString("0.0000", CultureInfo.InvariantCulture),
                        core.Temperature.ToString("0.0", CultureInfo.InvariantCulture),
                        frequency.PackagePower.ToString("0.0", CultureInfo.InvariantCulture),
                        frequency.Tctl.ToString("0.0", CultureInfo.InvariantCulture),
                        result.ToEclkAdjustedFMax(frequency.RequestedFMax).ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        if (singleCore is not null)
        {
            builder.AppendLine();
            builder.AppendLine("# single_core_per_core_voltages");
            builder.AppendLine("# requested_actual_fmax_mhz,effective_actual_fmax_mhz,requested_ceiling_fmax_mhz,effective_ceiling_fmax_mhz,ccd,core_id,logical_cpu,effective_clock_mhz,voltage_v,temperature_c,package_power_w,tctl_c,samples,eclk_actual_fmax_mhz");
            foreach (FrequencyCoreVoltages frequency in singleCore.CoreVoltages.OrderByDescending(f => f.RequestedFMax))
            {
                foreach (CoreVoltageSample core in frequency.Cores
                             .OrderBy(c => c.CcdIndex)
                             .ThenBy(c => c.CoreId))
                {
                    builder.AppendLine(string.Join(",",
                        frequency.RequestedFMax.ToString(CultureInfo.InvariantCulture),
                        frequency.EffectiveFMax.ToString(CultureInfo.InvariantCulture),
                        frequency.RequestedFMaxCeiling.ToString(CultureInfo.InvariantCulture),
                        frequency.EffectiveFMaxCeiling.ToString(CultureInfo.InvariantCulture),
                        core.CcdIndex.ToString(CultureInfo.InvariantCulture),
                        core.DisplayCoreId.ToString(CultureInfo.InvariantCulture),
                        core.LogicalProcessor.ToString(CultureInfo.InvariantCulture),
                        core.ClockMHz.ToString("0.0", CultureInfo.InvariantCulture),
                        core.Voltage.ToString("0.0000", CultureInfo.InvariantCulture),
                        core.Temperature.ToString("0.0", CultureInfo.InvariantCulture),
                        frequency.PackagePower.ToString("0.0", CultureInfo.InvariantCulture),
                        frequency.Tctl.ToString("0.0", CultureInfo.InvariantCulture),
                        core.SampleCount.ToString(CultureInfo.InvariantCulture),
                        singleCore.ToEclkAdjustedFMax(frequency.RequestedFMax).ToString(CultureInfo.InvariantCulture)));
                }
            }
        }

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    public static void ExportMarkdown(
        SweepRunResult? result,
        string path,
        SweepRunResult? singleCore = null)
    {
        SweepRunResult? primary = result ?? singleCore;
        StringBuilder builder = new();
        builder.AppendLine("# ZenVF V/F 测试报告");
        builder.AppendLine();
        builder.AppendLine($"> 生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}  ");
        builder.AppendLine($"> 数据来源：ZenVF {typeof(ChartBuilder).Assembly.GetName().Version?.ToString(3) ?? "1.0.7"}");
        builder.AppendLine();

        builder.AppendLine("## 测试概要");
        builder.AppendLine();
        builder.AppendLine("| 项目 | 值 |");
        builder.AppendLine("| --- | --- |");
        if (primary is not null)
        {
            builder.AppendLine($"| CPU | {Md(primary.CpuName)} |");
            builder.AppendLine($"| 扫描范围 | {primary.Settings.StartFMax} → {primary.Settings.EndFMax} MHz |");
            builder.AppendLine($"| 步进 | {primary.Settings.StepMHz} MHz |");
            builder.AppendLine($"| 预热 / 采样 | {primary.Settings.WarmupSeconds} s / {primary.Settings.SampleSeconds} s |");
            builder.AppendLine($"| CCD | {FormatCcds(primary.Settings.SelectedCcds)} |");
            builder.AppendLine($"| 原始 SMU FMax 上限 | {primary.StockFMaxCeiling?.ToString(CultureInfo.InvariantCulture) ?? "—"} MHz |");
            builder.AppendLine($"| FMax 上限偏移 | {primary.FMaxCeilingOffsetMHz} MHz |");
            SweepRunResult? eclkSource = result?.IsEclkDetected == true
                ? result
                : singleCore?.IsEclkDetected == true ? singleCore : null;
            builder.AppendLine(eclkSource?.EclkRatio is double eclk
                ? $"| eCLK | {eclk * 100:0.###} MHz（×{eclk:0.00000}） |"
                : "| eCLK | 未检测 |");
        }
        else
        {
            builder.AppendLine("| CPU | — |");
        }

        builder.AppendLine($"| 全核测试 | {FormatRunSummary(result)} |");
        builder.AppendLine($"| 每核单独测试 | {FormatRunSummary(singleCore)} |");
        builder.AppendLine($"| 全核测试 FMax 恢复 | {RestoreText(result)} |");
        builder.AppendLine($"| 每核单独 FMax 恢复 | {RestoreText(singleCore)} |");
        if (!string.IsNullOrWhiteSpace(primary?.Note))
        {
            builder.AppendLine($"| 备注 | {Md(primary.Note)} |");
        }

        builder.AppendLine();
        if (result is not null)
        {
            AppendAllCoreMarkdown(builder, result);
        }

        if (singleCore is not null)
        {
            AppendSingleCoreMarkdown(builder, singleCore);
        }

        builder.AppendLine("## 说明");
        builder.AppendLine();
        builder.AppendLine("- 档位为界面中的实际最高加速频率；SMU 上限为写入/回读的原始 FMax 上限。");
        builder.AppendLine("- 检测到 eCLK 时，档位旁的“eCLK 实际”按 `设定频率 × eCLK 倍率` 计算；图表横轴仍使用实测有效频率。");
        builder.AppendLine("- 实测频率来自 LibreHardwareMonitor 的有效频率中位数，不是 FMax 设定值。");
        builder.AppendLine("- 每核单独测试的电压为 `CPU VDDCR_VDD` 封装遥测；同一档位内各核心共享该电压。");
        builder.AppendLine("- 完整逐点数据请使用“导出 CSV”。");
        builder.AppendLine();

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, builder.ToString(), new UTF8Encoding(true));
    }

    private static void AppendAllCoreMarkdown(StringBuilder builder, SweepRunResult run)
    {
        builder.AppendLine("## 全核测试");
        builder.AppendLine();
        builder.AppendLine("### 每档最高电压核心");
        builder.AppendLine();

        List<SweepPoint> points = run.Series
            .SelectMany(series => series.Points)
            .OrderByDescending(point => point.RequestedFMax)
            .ThenBy(point => point.CcdIndex)
            .ToList();
        if (points.Count == 0)
        {
            builder.AppendLine("_无数据_");
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("| 档位 (MHz) | eCLK 实际 (MHz) | 回读 (MHz) | SMU 上限 (MHz) | CCD | 核心 | 实测频率 (MHz) | 电压 (V) | 温度 (°C) | 封装功耗 (W) | 样本 |");
            builder.AppendLine("| ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (SweepPoint point in points)
            {
                builder.AppendLine($"| {point.RequestedFMax} | {run.ToEclkAdjustedFMax(point.RequestedFMax)} | {point.EffectiveFMax} | {point.EffectiveFMaxCeiling} | CCD{point.CcdIndex} | {point.DisplayCoreId} | {FormatClock(point.ClockMHz)} | {FormatVoltage(point.Voltage)} | {FormatTemperature(point.Temperature)} | {FormatPower(point.PackagePower)} | {point.SampleCount} |");
            }

            builder.AppendLine();
        }

        builder.AppendLine("### 每核 V/F 汇总");
        builder.AppendLine();
        AppendCoreSummary(builder, run, includeLogicalProcessor: false);
    }

    private static void AppendSingleCoreMarkdown(StringBuilder builder, SweepRunResult run)
    {
        builder.AppendLine("## 每核单独测试");
        builder.AppendLine();
        builder.AppendLine("> 电压来源：`CPU VDDCR_VDD`（SMU PM table SVI3 遥测）。同一档位内各核心共享封装电压。");
        builder.AppendLine();
        builder.AppendLine("### 每档汇总");
        builder.AppendLine();

        List<FrequencyCoreVoltages> frequencies = run.CoreVoltages
            .OrderByDescending(frequency => frequency.RequestedFMax)
            .ToList();
        if (frequencies.Count == 0)
        {
            builder.AppendLine("_无数据_");
            builder.AppendLine();
        }
        else
        {
            builder.AppendLine("| 档位 (MHz) | eCLK 实际 (MHz) | 回读 (MHz) | SMU 上限 (MHz) | 核心数 | 封装功耗 (W) | Tctl (°C) |");
            builder.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (FrequencyCoreVoltages frequency in frequencies)
            {
                builder.AppendLine($"| {frequency.RequestedFMax} | {run.ToEclkAdjustedFMax(frequency.RequestedFMax)} | {frequency.EffectiveFMax} | {frequency.EffectiveFMaxCeiling} | {frequency.Cores.Count} | {FormatPower(frequency.PackagePower)} | {FormatTemperature(frequency.Tctl)} |");
            }

            builder.AppendLine();
        }

        builder.AppendLine("### 每核 V/F 汇总");
        builder.AppendLine();
        AppendCoreSummary(builder, run, includeLogicalProcessor: true);
    }

    private static void AppendCoreSummary(
        StringBuilder builder,
        SweepRunResult run,
        bool includeLogicalProcessor)
    {
        List<IGrouping<(int CcdIndex, int CoreId), (FrequencyCoreVoltages Frequency, CoreVoltageSample Core)>> groups =
            run.CoreVoltages
                .SelectMany(frequency => frequency.Cores.Select(core => (Frequency: frequency, Core: core)))
                .GroupBy(item => (item.Core.CcdIndex, item.Core.CoreId))
                .OrderBy(group => group.Key.CcdIndex)
                .ThenBy(group => group.Key.CoreId)
                .ToList();

        if (groups.Count == 0)
        {
            builder.AppendLine("_无数据_");
            builder.AppendLine();
            return;
        }

        if (includeLogicalProcessor)
        {
            builder.AppendLine("| CCD | 核心 | 逻辑处理器 | 档位 | 实测频率范围 (MHz) | 电压范围 (V) | 温度范围 (°C) |");
            builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        }
        else
        {
            builder.AppendLine("| CCD | 核心 | 档位 | 实测频率范围 (MHz) | 电压范围 (V) | 温度范围 (°C) |");
            builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        }

        foreach (IGrouping<(int CcdIndex, int CoreId), (FrequencyCoreVoltages Frequency, CoreVoltageSample Core)> group in groups)
        {
            List<double> clocks = group
                .Select(item => item.Core.ClockMHz)
                .Where(SensorValueSanity.IsPlausibleClock)
                .ToList();
            List<double> voltages = group
                .Select(item => item.Core.Voltage)
                .Where(SensorValueSanity.IsPlausibleVoltage)
                .ToList();
            List<double> temperatures = group
                .Select(item => item.Core.Temperature)
                .Where(value => value > 0)
                .ToList();
            int logicalProcessor = group
                .Select(item => item.Core.LogicalProcessor)
                .Where(value => value >= 0)
                .DefaultIfEmpty(-1)
                .First();
            int steps = group
                .Select(item => item.Frequency.RequestedFMax)
                .Distinct()
                .Count();

            if (includeLogicalProcessor)
            {
                builder.AppendLine($"| CCD{group.Key.CcdIndex} | {group.First().Core.DisplayCoreId} | {(logicalProcessor >= 0 ? logicalProcessor.ToString(CultureInfo.InvariantCulture) : "—")} | {steps} | {Range(clocks, "0")} | {Range(voltages, "0.000")} | {Range(temperatures, "0.0")} |");
            }
            else
            {
                builder.AppendLine($"| CCD{group.Key.CcdIndex} | {group.First().Core.DisplayCoreId} | {steps} | {Range(clocks, "0")} | {Range(voltages, "0.000")} | {Range(temperatures, "0.0")} |");
            }
        }

        builder.AppendLine();
    }

    private static string FormatRunSummary(SweepRunResult? run)
    {
        if (run is null)
        {
            return "未运行";
        }

        int steps = run.CoreVoltages.Count;
        int samples = run.CoreVoltages.Sum(frequency => frequency.Cores.Count);
        string end = run.Finished.HasValue
            ? run.Finished.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : "未完成";
        return $"{steps} 档 / {samples} 个核心样本 · {run.Started.ToString("MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} → {end}";
    }

    private static string RestoreText(SweepRunResult? run)
    {
        if (run is null)
        {
            return "未运行";
        }

        return run.RestoreSucceeded switch
        {
            true => "成功",
            false => "失败",
            null => run.Settings.RestoreAfterTest ? "未记录" : "保留当前设置"
        };
    }

    private static string FormatCcds(IReadOnlyCollection<int> ccds)
    {
        return ccds.Count == 0
            ? "—"
            : string.Join("、", ccds.OrderBy(ccd => ccd).Select(ccd => $"CCD{ccd}"));
    }

    private static string Range(IReadOnlyCollection<double> values, string format)
    {
        if (values.Count == 0)
        {
            return "—";
        }

        double minimum = values.Min();
        double maximum = values.Max();
        if (minimum.Equals(maximum))
        {
            return minimum.ToString(format, CultureInfo.InvariantCulture);
        }

        return $"{minimum.ToString(format, CultureInfo.InvariantCulture)}–{maximum.ToString(format, CultureInfo.InvariantCulture)}";
    }

    private static string FormatClock(double value) =>
        SensorValueSanity.IsPlausibleClock(value)
            ? value.ToString("0", CultureInfo.InvariantCulture)
            : "—";

    private static string FormatVoltage(double value) =>
        SensorValueSanity.IsPlausibleVoltage(value)
            ? value.ToString("0.000", CultureInfo.InvariantCulture)
            : "—";

    private static string FormatTemperature(double value) =>
        value > 0 ? value.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    private static string FormatPower(double value) =>
        value > 0 ? value.ToString("0.0", CultureInfo.InvariantCulture) : "—";

    private static string Md(string? text) =>
        (text ?? string.Empty).Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ").Trim();

    private static void ApplyEmptyState(
        PlotModel model,
        LinearAxis xAxis,
        LinearAxis yAxis,
        double xMin,
        double xMax,
        double yMin,
        double yMax)
    {
        model.IsLegendVisible = false;
        xAxis.Title = null;
        xAxis.TextColor = OxyColors.Transparent;
        yAxis.Title = null;
        yAxis.TextColor = OxyColors.Transparent;

        model.Annotations.Add(new TextAnnotation
        {
            Text = "等待测试数据",
            TextPosition = new DataPoint((xMin + xMax) / 2, (yMin + yMax) / 2),
            TextColor = Muted,
            Font = "Microsoft YaHei UI",
            FontSize = 18,
            TextHorizontalAlignment = HorizontalAlignment.Center,
            TextVerticalAlignment = VerticalAlignment.Middle
        });
    }

    private static OxyColor CcdColor(int ccdIndex)
    {
        return CcdPalette[Math.Abs(ccdIndex) % CcdPalette.Length];
    }

    private static string Escape(string text) => text.Replace(",", ";").Replace("\r", " ").Replace("\n", " ");
}
