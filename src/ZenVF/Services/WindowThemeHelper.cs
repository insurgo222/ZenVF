using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ZenVF.Services;

public static class WindowThemeHelper
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);

    public static void Apply(Window window)
    {
        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int dark = ThemeManager.IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));

        // DWMWCP_ROUND on Windows 11. Older Windows versions simply ignore it.
        int corner = 2;
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corner, sizeof(int));
    }
}
