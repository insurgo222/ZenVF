namespace ZenVF;

public static class AppInfo
{
    /// <summary>Product version from the assembly (set once, in ZenVF.csproj).</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
