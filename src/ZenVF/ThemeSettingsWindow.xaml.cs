using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShapePath = System.Windows.Shapes.Path;
using ZenVF.Services;
using AppThemeMode = ZenVF.Models.ThemeMode;

namespace ZenVF;

public partial class ThemeSettingsWindow : Window
{
    private readonly Dictionary<string, (Button Button, ShapePath Check)> _accentButtons =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _syncing;

    public ThemeSettingsWindow()
    {
        InitializeComponent();
        BuildAccentSwatches();
        SyncFromTheme();
        ThemeManager.ThemeChanged += OnThemeChanged;
        Closed += OnClosed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowThemeHelper.Apply(this);
    }

    private void BuildAccentSwatches()
    {
        foreach (ThemeAccent preset in ThemeManager.AccentPresets)
        {
            Grid content = new()
            {
                Width = 30,
                Height = 30
            };

            Border circle = new()
            {
                Width = 30,
                Height = 30,
                CornerRadius = new CornerRadius(15),
                Background = new SolidColorBrush(preset.Color)
            };

            ShapePath check = new()
            {
                Data = Geometry.Parse("M 4,9 L 8,13 L 16,5"),
                Stroke = new SolidColorBrush(ContrastColor(preset.Color)),
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };

            content.Children.Add(circle);
            content.Children.Add(check);

            Button button = new()
            {
                Style = (Style)FindResource("AccentSwatchButton"),
                Tag = preset.Hex,
                ToolTip = preset.Name,
                Content = content
            };
            AutomationProperties.SetName(button, preset.Name);
            AutomationProperties.SetHelpText(button, preset.Hex);
            button.Click += Accent_Click;
            AccentPanel.Children.Add(button);
            _accentButtons[preset.Hex] = (button, check);
        }
    }

    private void SyncFromTheme()
    {
        _syncing = true;
        SystemModeRadio.IsChecked = ThemeManager.Mode == AppThemeMode.System;
        LightModeRadio.IsChecked = ThemeManager.Mode == AppThemeMode.Light;
        DarkModeRadio.IsChecked = ThemeManager.Mode == AppThemeMode.Dark;

        Brush selectedBrush = (Brush)FindResource("TextBrush");
        foreach ((string hex, (Button button, ShapePath check)) in _accentButtons)
        {
            bool selected = string.Equals(hex, ThemeManager.AccentHex, StringComparison.OrdinalIgnoreCase);
            button.BorderBrush = selected ? selectedBrush : Brushes.Transparent;
            check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        }

        if (!AccentHexBox.IsKeyboardFocusWithin)
        {
            AccentHexBox.Text = ThemeManager.AccentHex;
        }

        _syncing = false;
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (_syncing || sender is not RadioButton { Tag: string tag })
        {
            return;
        }

        if (Enum.TryParse(tag, true, out AppThemeMode mode))
        {
            ThemeManager.SetMode(mode);
        }
    }

    private void Accent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string hex })
        {
            return;
        }

        ThemeManager.SetAccentHex(hex);
        AccentStatusText.Text = string.Empty;
        SyncFromTheme();
    }

    private void ApplyAccent_Click(object sender, RoutedEventArgs e)
    {
        if (!ThemeManager.SetAccentHex(AccentHexBox.Text))
        {
            AccentStatusText.Text = "请输入有效的颜色，例如 #5BC0BE。";
            AccentStatusText.Foreground = (Brush)FindResource("RedBrush");
            return;
        }

        AccentStatusText.Text = "已应用自定义主题颜色。";
        AccentStatusText.Foreground = (Brush)FindResource("GreenBrush");
        SyncFromTheme();
    }

    private void AccentHex_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyAccent_Click(sender, e);
            e.Handled = true;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        WindowThemeHelper.Apply(this);
        SyncFromTheme();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // The window can already be closing.
            }
        }
    }

    private void Done_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private static Color ContrastColor(Color color)
    {
        double luminance = ((0.2126 * color.R) + (0.7152 * color.G) + (0.0722 * color.B)) / 255.0;
        return luminance > 0.62 ? Color.FromRgb(0, 0, 0) : Colors.White;
    }
}
