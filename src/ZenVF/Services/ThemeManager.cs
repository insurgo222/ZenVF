using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using ZenVF.Models;
using ThemeMode = ZenVF.Models.ThemeMode;

namespace ZenVF.Services;

public readonly record struct ThemeAccent(string Name, string Hex, Color Color);

/// <summary>
/// Applies the Fluent 2 light/dark palette, follows the Windows app theme when
/// requested, and persists the user's theme/accent choice.
/// </summary>
public static class ThemeManager
{
    public static event EventHandler? ThemeChanged;

    private static readonly string SettingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZenVF");
    private static readonly string SettingsPath = Path.Combine(SettingsFolder, "theme.json");
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly ThemeAccent[] Accents =
    {
        new("氧化青", "#5BC0BE", Color.FromRgb(0x5B, 0xC0, 0xBE)),
        new("深青", "#0E6B6B", Color.FromRgb(0x0E, 0x6B, 0x6B)),
        new("铜", "#C87533", Color.FromRgb(0xC8, 0x75, 0x33)),
        new("硅蓝", "#5B8FC9", Color.FromRgb(0x5B, 0x8F, 0xC9)),
        new("钝化绿", "#6FAE8B", Color.FromRgb(0x6F, 0xAE, 0x8B)),
        new("光刻紫", "#A183C9", Color.FromRgb(0xA1, 0x83, 0xC9)),
        new("金", "#C9A227", Color.FromRgb(0xC9, 0xA2, 0x27)),
        new("警示橙", "#D9822B", Color.FromRgb(0xD9, 0x82, 0x2B)),
        new("故障红", "#C0503F", Color.FromRgb(0xC0, 0x50, 0x3F)),
        new("石墨", "#7A8790", Color.FromRgb(0x7A, 0x87, 0x90))
    };

    private static DispatcherTimer? _systemTimer;
    private static bool _initialized;
    private static bool _accentIsDefault = true;
    private static Color _accent = Color.FromRgb(0x0E, 0x6B, 0x6B);

    public static ThemeMode Mode { get; private set; } = ThemeMode.System;
    public static bool IsDark { get; private set; }
    public static Color Accent => _accent;
    public static string AccentHex => $"#{_accent.R:X2}{_accent.G:X2}{_accent.B:X2}";
    public static IReadOnlyList<ThemeAccent> AccentPresets => Accents;

    public static void Initialize(AppOptions options)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        ThemeSettings settings = LoadSettings();
        Mode = settings.Mode;
        bool hasSavedAccent = TryParseColor(settings.Accent, out Color savedAccent);

        if (!string.IsNullOrWhiteSpace(options.ThemeOverride)
            && Enum.TryParse(options.ThemeOverride, true, out ThemeMode overrideMode))
        {
            Mode = overrideMode;
        }

        IsDark = ResolveDark(Mode);
        _accentIsDefault = !hasSavedAccent;
        _accent = hasSavedAccent
            ? savedAccent
            : IsDark
                ? Color.FromRgb(0x5B, 0xC0, 0xBE)
                : Color.FromRgb(0x0E, 0x6B, 0x6B);

        if (TryParseColor(options.AccentOverride, out Color overrideAccent))
        {
            _accent = overrideAccent;
            _accentIsDefault = false;
        }

        ApplyTheme(notify: false);
        UpdateSystemWatcher();
    }

    public static void SetMode(ThemeMode mode, bool persist = true)
    {
        if (Mode == mode && _initialized)
        {
            return;
        }

        Mode = mode;
        if (_accentIsDefault)
        {
            _accent = ResolveDark(mode)
                ? Color.FromRgb(0x5B, 0xC0, 0xBE)
                : Color.FromRgb(0x0E, 0x6B, 0x6B);
        }

        if (persist)
        {
            SaveSettings();
        }

        ApplyTheme();
        UpdateSystemWatcher();
    }

    public static void SetAccent(Color color, bool persist = true)
    {
        if (_accent == color && _initialized && !_accentIsDefault)
        {
            return;
        }

        _accent = color;
        _accentIsDefault = false;
        if (persist)
        {
            SaveSettings();
        }

        ApplyTheme();
    }

    public static bool SetAccentHex(string? hex, bool persist = true)
    {
        if (!TryParseColor(hex, out Color color))
        {
            return false;
        }

        SetAccent(color, persist);
        return true;
    }

    public static bool TryParseColor(string? text, out Color color)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            color = default;
            return false;
        }

        try
        {
            object? value = ColorConverter.ConvertFromString(text.Trim());
            if (value is Color parsed)
            {
                color = parsed;
                return true;
            }
        }
        catch
        {
            // Invalid user input is reported by the caller.
        }

        color = default;
        return false;
    }

    public static ThemeMode GetSystemTheme()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            object? value = key?.GetValue("AppsUseLightTheme");
            if (value is not null)
            {
                return Convert.ToInt32(value) == 1 ? ThemeMode.Light : ThemeMode.Dark;
            }
        }
        catch
        {
            // Fall back to dark, which is also ZenVF's default.
        }

        return ThemeMode.Dark;
    }

    private static bool ResolveDark(ThemeMode mode)
    {
        return mode switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => GetSystemTheme() == ThemeMode.Dark
        };
    }

    private static void ApplyTheme(bool notify = true)
    {
        Application? app = Application.Current;
        if (app is null)
        {
            return;
        }

        IsDark = ResolveDark(Mode);
        if (_accentIsDefault)
        {
            _accent = IsDark
                ? Color.FromRgb(0x5B, 0xC0, 0xBE)
                : Color.FromRgb(0x0E, 0x6B, 0x6B);
        }

        SwapPaletteDictionary(IsDark);
        ApplyAccentResources(IsDark, _accent);
        ChartBuilder.ApplyTheme(IsDark, _accent);

        if (notify)
        {
            ThemeChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    private static void SwapPaletteDictionary(bool dark)
    {
        Application app = Application.Current;
        ResourceDictionary? current = app.Resources.MergedDictionaries
            .FirstOrDefault(dictionary =>
            {
                string? source = dictionary.Source?.OriginalString;
                return source is not null
                    && (source.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)
                        || source.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase));
            });

        ResourceDictionary palette = new()
        {
            Source = new Uri(
                $"pack://application:,,,/ZenVF;component/Themes/{(dark ? "Dark" : "Light")}.xaml",
                UriKind.Absolute)
        };

        if (current is not null)
        {
            int index = app.Resources.MergedDictionaries.IndexOf(current);
            app.Resources.MergedDictionaries.RemoveAt(index);
            app.Resources.MergedDictionaries.Insert(index, palette);
        }
        else
        {
            app.Resources.MergedDictionaries.Add(palette);
        }
    }

    private static void ApplyAccentResources(bool dark, Color accent)
    {
        Color hover = dark ? Lighten(accent, 0.18) : Darken(accent, 0.10);
        Color pressed = dark ? Darken(accent, 0.12) : Darken(accent, 0.22);
        Color text = ContrastColor(accent);
        Color soft = Color.FromArgb(dark ? (byte)36 : (byte)28, accent.R, accent.G, accent.B);

        SetBrush("AccentBrush", accent);
        SetBrush("AccentHoverBrush", hover);
        SetBrush("AccentPressedBrush", pressed);
        SetBrush("AccentTextBrush", text);
        SetBrush("AccentSoftBrush", soft);
        SetBrush("SelectionBrush", Color.FromArgb(84, accent.R, accent.G, accent.B));
        SetBrush("FocusBrush", Color.FromArgb(204, accent.R, accent.G, accent.B));
    }

    private static void SetBrush(string key, Color color)
    {
        Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static Color Lighten(Color color, double amount)
    {
        return Blend(color, Colors.White, Math.Clamp(amount, 0, 1));
    }

    private static Color Darken(Color color, double amount)
    {
        return Blend(color, Colors.Black, Math.Clamp(amount, 0, 1));
    }

    private static Color Blend(Color from, Color to, double amount)
    {
        return Color.FromArgb(
            from.A,
            (byte)Math.Round(from.R + ((to.R - from.R) * amount)),
            (byte)Math.Round(from.G + ((to.G - from.G) * amount)),
            (byte)Math.Round(from.B + ((to.B - from.B) * amount)));
    }

    private static Color ContrastColor(Color color)
    {
        double luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;
        return luminance > 0.62 ? Color.FromRgb(0, 0, 0) : Colors.White;
    }

    private static void UpdateSystemWatcher()
    {
        if (Mode != ThemeMode.System)
        {
            _systemTimer?.Stop();
            return;
        }

        if (_systemTimer is null)
        {
            _systemTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5)
            };
            _systemTimer.Tick += (_, _) =>
            {
                if (Mode != ThemeMode.System)
                {
                    return;
                }

                bool systemDark = GetSystemTheme() == ThemeMode.Dark;
                if (systemDark != IsDark)
                {
                    ApplyTheme();
                }
            };
        }

        _systemTimer.Start();
    }

    private static ThemeSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                string json = File.ReadAllText(SettingsPath);
                ThemeSettings? settings = JsonSerializer.Deserialize<ThemeSettings>(json, JsonOptions);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch
        {
            // A corrupt settings file must never prevent startup.
        }

        return new ThemeSettings();
    }

    private static void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(SettingsFolder);
            ThemeSettings settings = new()
            {
                Mode = Mode,
                Accent = _accentIsDefault ? string.Empty : AccentHex
            };
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Theme persistence is best-effort.
        }
    }
}
