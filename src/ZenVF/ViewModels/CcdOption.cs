using ZenVF.Models;

namespace ZenVF.ViewModels;

public sealed class CcdOption : ObservableObject
{
    private bool _isSelected = true;

    public required int Index { get; init; }
    public required string Name { get; init; }
    public required int PhysicalCores { get; init; }
    public bool Is3DCache { get; init; }
    public string Detail => Is3DCache ? "3D V-Cache" : $"{PhysicalCores} 核";

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}


