using System.Windows.Media;

namespace ZenVF.Services;

/// <summary>
/// One CCD row of the per-core chart legend. The legend is drawn in WPF
/// rather than by OxyPlot so each CCD gets its own line; OxyPlot's legend
/// only wraps by width and ran sixteen entries together.
/// </summary>
public sealed class CoreLegendGroup
{
    public required string Name { get; init; }
    public required IReadOnlyList<CoreLegendItem> Items { get; init; }
}

/// <summary>A core's colour swatch and label, matching its chart line.</summary>
public sealed class CoreLegendItem
{
    public required string Label { get; init; }
    public required Brush Swatch { get; init; }
}
