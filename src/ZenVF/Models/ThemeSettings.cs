namespace ZenVF.Models;

public enum ThemeMode
{
    System,
    Light,
    Dark
}

public sealed class ThemeSettings
{
    public ThemeMode Mode { get; set; } = ThemeMode.System;
    public string Accent { get; set; } = string.Empty;
}
