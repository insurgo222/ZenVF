namespace ZenVF.Models;

public sealed class AppOptions
{
    public bool Demo { get; init; }
    public string? ScreenshotPath { get; init; }
    public int ScreenshotTab { get; init; }
    public bool SkipElevation { get; init; }
    public string? ThemeOverride { get; init; }
    public string? AccentOverride { get; init; }
    public bool DemoEclk { get; init; }
    public bool ScreenshotCore { get; init; }
    public bool ScreenshotTheme { get; init; }

    public static AppOptions Parse(string[] args)
    {
        bool demo = false;
        bool demoEclk = false;
        bool screenshotCore = false;
        bool screenshotTheme = false;
        bool skipElevation = false;
        string? screenshot = null;
        int screenshotTab = 0;
        string? themeOverride = null;
        string? accentOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--demo":
                case "-d":
                    demo = true;
                    break;
                case "--demo-eclk":
                    demo = true;
                    demoEclk = true;
                    break;
                case "--no-elevate":
                    skipElevation = true;
                    break;
                case "--screenshot":
                    screenshot = TakePath(args, ref i) ?? screenshot;
                    break;
                case "--screenshot-tab":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int tabIndex) && tabIndex >= 0)
                    {
                        screenshotTab = tabIndex;
                        i++;
                    }
                    break;
                // Both accept the output path directly (README usage); the path may
                // also come from a separate --screenshot <path>.
                case "--screenshot-core":
                    screenshotCore = true;
                    demo = true;
                    screenshot = TakePath(args, ref i) ?? screenshot;
                    break;
                case "--screenshot-theme":
                    screenshotTheme = true;
                    demo = true;
                    screenshot = TakePath(args, ref i) ?? screenshot;
                    break;
                case "--theme":
                    if (i + 1 < args.Length)
                    {
                        themeOverride = args[++i];
                    }
                    break;
                case "--accent":
                    if (i + 1 < args.Length)
                    {
                        accentOverride = args[++i];
                    }
                    break;
            }
        }

        return new AppOptions
        {
            Demo = demo || screenshot is not null,
            ScreenshotPath = screenshot,
            ScreenshotTab = screenshotTab,
            SkipElevation = skipElevation,
            ThemeOverride = themeOverride,
            AccentOverride = accentOverride,
            DemoEclk = demoEclk,
            ScreenshotCore = screenshotCore,
            ScreenshotTheme = screenshotTheme
        };
    }

    // Consumes args[i + 1] as a path when it is present and not another flag.
    private static string? TakePath(string[] args, ref int i)
    {
        if (i + 1 < args.Length && !args[i + 1].StartsWith("-", StringComparison.Ordinal))
        {
            return args[++i];
        }

        return null;
    }
}
