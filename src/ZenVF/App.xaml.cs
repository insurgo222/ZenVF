using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ZenVF.Models;
using ZenVF.Services;

namespace ZenVF;

public partial class App : Application
{
    private AppOptions _options = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        _options = AppOptions.Parse(e.Args);

        if (!_options.Demo && !_options.SkipElevation && !IsAdministrator())
        {
            MessageBoxResult choice = MessageBox.Show(
                "ZenVF 需要管理员权限才能加载 PawnIO 并写入 AMD SMU 的 PBO FMax。\n\n" +
                "是：以管理员身份重新启动\n" +
                "否：以演示模式运行（不访问硬件）\n" +
                "取消：退出",
                "需要管理员权限",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Warning);

            if (choice == MessageBoxResult.Cancel)
            {
                Shutdown(0);
                return;
            }

            if (choice == MessageBoxResult.Yes)
            {
                if (TryRestartElevated(e.Args))
                {
                    Shutdown(0);
                    return;
                }

                MessageBox.Show("无法以管理员身份重新启动。将以演示模式继续。", "ZenVF", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            _options = new AppOptions
            {
                Demo = true,
                ScreenshotPath = _options.ScreenshotPath,
                ScreenshotTab = _options.ScreenshotTab,
                SkipElevation = _options.SkipElevation,
                CpuOverride = _options.CpuOverride,
                ThemeOverride = _options.ThemeOverride,
                AccentOverride = _options.AccentOverride,
                DemoEclk = _options.DemoEclk,
                ScreenshotCore = _options.ScreenshotCore,
                ScreenshotTheme = _options.ScreenshotTheme
            };
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        ThemeManager.Initialize(_options);

        if (_options.ScreenshotPath is not null)
        {
            // RenderTargetBitmap can pick up a corrupted hardware glyph atlas
            // when the demo window is occluded. Screenshot mode is not
            // performance-sensitive, so render it entirely in software.
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
        }

        MainWindow window = new(_options);
        MainWindow = window;
        window.Show();
    }

    private static bool IsAdministrator()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryRestartElevated(string[] args)
    {
        try
        {
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
            {
                return false;
            }

            string arguments = string.Join(" ", args.Select(QuoteArgument));
            ProcessStartInfo startInfo = new(executable, arguments)
            {
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(startInfo);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string QuoteArgument(string argument)
    {
        return argument.Contains(' ') ? $"\"{argument.Replace("\"", "\\\"")}\"" : argument;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZenVF");
            Directory.CreateDirectory(folder);
            File.AppendAllText(Path.Combine(folder, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Never mask the original exception with a logging failure.
        }

        MessageBox.Show("ZenVF 遇到未处理的错误：\n\n" + e.Exception.Message +
                        "\n\n若正在扫描，请重启系统以清除可能残留的 FMax 设置。",
            "ZenVF 错误", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        Shutdown(1);
    }
}
