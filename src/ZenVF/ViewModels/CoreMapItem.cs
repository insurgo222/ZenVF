using System.Collections.ObjectModel;

namespace ZenVF.ViewModels;

/// <summary>
/// One CCD row in the main-window floorplan strip. Cores stay in topology
/// order so a block's position never moves between runs.
/// </summary>
public sealed class CoreMapGroup : ObservableObject
{
    private string _accent = string.Empty;

    public required int CcdIndex { get; init; }
    public required string Name { get; init; }
    public required string Meta { get; init; }
    public required string Accent
    {
        get => _accent;
        set => Set(ref _accent, value);
    }

    public ObservableCollection<CoreMapItem> Cores { get; } = new();
}

/// <summary>
/// A physical core block. <see cref="Heat"/> is the row-relative voltage,
/// used as the opacity of the copper bar so the custom text stays readable
/// in both themes.
/// </summary>
public sealed class CoreMapItem : ObservableObject
{
    private string _voltageText = "—";
    private string _clockText = "—";
    private string _tooltip = string.Empty;
    private double _heat;
    private bool _isMax;
    private bool _isLoaded;

    public required int CoreId { get; init; }
    public required int CcdIndex { get; init; }
    public required string Label { get; init; }

    public string VoltageText
    {
        get => _voltageText;
        set => Set(ref _voltageText, value);
    }

    public string ClockText
    {
        get => _clockText;
        set => Set(ref _clockText, value);
    }

    public string Tooltip
    {
        get => _tooltip;
        set => Set(ref _tooltip, value);
    }

    public double Heat
    {
        get => _heat;
        set => Set(ref _heat, Math.Clamp(value, 0, 1));
    }

    public bool IsMax
    {
        get => _isMax;
        set => Set(ref _isMax, value);
    }

    public bool IsLoaded
    {
        get => _isLoaded;
        set => Set(ref _isLoaded, value);
    }
}
