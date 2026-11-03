using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using OxyPlot;
using ZenVF.Interop;
using ZenVF.Models;
using ZenVF.Services;

namespace ZenVF.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    /// <summary>Startup eCLK window: the maximum Core #N / Bus Speed reading within this time is used.</summary>
    private const int EclkStartupSampleMs = 3000;

    private readonly AppOptions _options;
    private readonly SweepEngine _engine = new();
    private readonly RelayCommand _stopCommand;
    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _singleCoreStartCommand;
    private readonly AsyncRelayCommand _togglePboCommand;
    private readonly AsyncRelayCommand _installPawnIoCommand;
    private readonly AsyncRelayCommand _reconnectSmuCommand;
    private bool _isReconnecting;

    private CpuDescriptor? _cpu;
    private CpuTopology? _topology;
    private SmuController? _smu;
    private ISensorService? _sensors;
    private SweepRunResult? _currentRun;
    private SweepRunResult? _singleCoreRun;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private uint _originalFMaxCeiling;
    private bool _isRunning;
    private bool _isInitializing = true;
    private bool _isPboArmed;
    private bool _isInputValid;
    private bool _isHardwareReady;
    private bool _disposed;
    private bool _suppressValidation;
    private int _progressPercent;
    private string _statusText = "初始化中…";
    private string _stageText = "正在加载 CPU 与传感器信息";
    private string _validationMessage = string.Empty;
    private string _cpuName = "检测中…";
    private string _cpuDisplayName = "检测中…";
    private string _architectureText = "—";
    private string _coreText = "—";
    private string _ccdText = "—";
    private string _stockFMaxText = "—";
    private string _currentFMaxText = "—";
    private string _originalFMaxText = "—";
    private string _scalarText = "—";
    private string _pboSummary = "正在检测…";
    private string _pboDetail = "读取 SMU 状态";
    private string _pboEvidenceText = string.Empty;
    private string _pboState = "unknown";
    private string _startFMaxText = "5425";
    private string _endFMaxText = "4700";
    private string _stepText = "100";
    private string _warmupText = "5";
    private string _sampleText = "5";
    private string _stepCountText = "—";
    private string _planSummaryText = "—";
    private string _singleCorePlanText = "每核单独测试参数待检查";
    private string _progressTimeText = "预计时间 —";
    private string _rangeHintText = "—";
    private string _chartSubtitle = "等待扫描数据";
    private string _pawnIoStatusText = "PawnIO —";
    private string _smuStatusText = "SMU —";
    private string _adminText = "—";
    private string _eclkText = "eCLK —";
    private string _eclkTooltip = "尚未检测到外部时钟发生器。启动时会读取 Core #N 的运行时最大值。";
    private bool _isEclkDetected;
    private double? _startupEclkRatio;
    private int _startupEclkSamples;
    private double _startupEclkPeakMHz;
    private double _startupEclkPboMaxMHz;
    private double _startupEclkBusMHz;
    private string _driverWarningText = string.Empty;
    private bool _driverWarningVisible;
    private bool _isDemo;
    private bool _hasResult;
    private bool _has3DCache;
    private PlotModel _plotModel;
    private PlotModel _perCorePlotModel;
    private PlotModel _singleCorePlotModel;
    private IReadOnlyList<CoreLegendGroup> _perCoreLegend = Array.Empty<CoreLegendGroup>();
    private IReadOnlyList<CoreLegendGroup> _singleCoreLegend = Array.Empty<CoreLegendGroup>();
    private SweepSettings? _lastSettings;
    private double _runPlannedSeconds;

    public MainViewModel(AppOptions options)
    {
        _options = options;
        _isDemo = options.Demo;
        DemoData.EclkRatio = options.DemoEclk ? 1.05 : 1.0;
        _plotModel = ChartBuilder.Build(null, _isDemo);
        _perCorePlotModel = ChartBuilder.BuildPerCore(null, _isDemo);
        _singleCorePlotModel = ChartBuilder.BuildPerCore(null, _isDemo, true);

        _startCommand = new AsyncRelayCommand(StartAsync, () => CanStart);
        _singleCoreStartCommand = new AsyncRelayCommand(StartSingleCoreSweepAsync, () => CanStartSingleCore);
        _stopCommand = new RelayCommand(Stop, () => IsRunning);
        ClearLogCommand = new RelayCommand(() => LogItems.Clear());
        _togglePboCommand = new AsyncRelayCommand(TogglePboAsync, () => CanTogglePbo && !IsRunning);
        _installPawnIoCommand = new AsyncRelayCommand(InstallPawnIoAsync, () => CanInstallPawnIo);
        _reconnectSmuCommand = new AsyncRelayCommand(ReconnectSmuAsync, () => CanReconnectSmu);

        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            WindowsPrincipal principal = new(identity);
            IsElevated = principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            IsElevated = false;
        }

        AdminText = IsElevated ? "管理员" : "标准用户";
    }

    public static string AppVersion => AppInfo.Version;

    public string VersionText => "v" + AppVersion;

    public ObservableCollection<CcdOption> CcdOptions { get; } = new();

    /// <summary>Columns for the CCD card grid: a lone CCD card spans the full rail width.</summary>
    public int CcdColumns => CcdOptions.Count <= 1 ? 1 : 2;
    public ObservableCollection<LogEntry> LogItems { get; } = new();

    public event EventHandler? SingleCoreSweepStarted;

    public RelayCommand StopCommand => _stopCommand;
    public AsyncRelayCommand StartCommand => _startCommand;
    public AsyncRelayCommand SingleCoreStartCommand => _singleCoreStartCommand;
    public AsyncRelayCommand TogglePboCommand => _togglePboCommand;
    public AsyncRelayCommand InstallPawnIoCommand => _installPawnIoCommand;
    public AsyncRelayCommand ReconnectSmuCommand => _reconnectSmuCommand;
    public RelayCommand ClearLogCommand { get; }
    public bool IsElevated { get; }

    public bool IsDemo
    {
        get => _isDemo;
        private set => Set(ref _isDemo, value);
    }

    public bool IsInitializing
    {
        get => _isInitializing;
        private set
        {
            if (Set(ref _isInitializing, value))
            {
                OnPropertyChanged(nameof(ShowReconnectSmu));
                OnPropertyChanged(nameof(CanReconnectSmu));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStartSingleCore));
                RefreshCommands();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (Set(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStartSingleCore));
                OnPropertyChanged(nameof(CanExport));
                OnPropertyChanged(nameof(CanShowCoreVoltages));
                OnPropertyChanged(nameof(StartButtonText));
                OnPropertyChanged(nameof(SingleCoreButtonText));
                RefreshCommands();
            }
        }
    }

    public bool IsPboArmed
    {
        get => _isPboArmed;
        private set
        {
            if (Set(ref _isPboArmed, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStartSingleCore));
                OnPropertyChanged(nameof(PboToggleHint));
                RefreshCommands();
            }
        }
    }

    public bool IsHardwareReady
    {
        get => _isHardwareReady;
        private set
        {
            if (Set(ref _isHardwareReady, value))
            {
                OnPropertyChanged(nameof(ShowReconnectSmu));
                OnPropertyChanged(nameof(CanReconnectSmu));
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStartSingleCore));
                RefreshCommands();
            }
        }
    }

    public bool CanTogglePbo => _smu?.IsAvailable == true || IsDemo;
    public bool CanInstallPawnIo => !IsDemo && !PawnIoNative.IsInstalled && File.Exists(GetPawnIoInstallerPath());

    /// <summary>Offered while the SMU is unavailable on a supported CPU and nothing else is running.</summary>
    public bool CanReconnectSmu =>
        !IsDemo && !IsInitializing && !IsRunning && !_isReconnecting
        && _cpu?.IsSupported == true && _smu?.IsAvailable != true;

    /// <summary>Shows the reconnect button (enabled state comes from <see cref="CanReconnectSmu"/>).</summary>
    public bool ShowReconnectSmu =>
        !IsDemo && !IsInitializing && _cpu?.IsSupported == true && _smu?.IsAvailable != true;

    public bool IsReconnecting
    {
        get => _isReconnecting;
        private set
        {
            if (Set(ref _isReconnecting, value))
            {
                OnPropertyChanged(nameof(CanReconnectSmu));
                RefreshCommands();
            }
        }
    }
    public bool IsInputValid
    {
        get => _isInputValid;
        private set
        {
            if (Set(ref _isInputValid, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStartSingleCore));
                RefreshCommands();
            }
        }
    }

    public bool CanStart => IsHardwareReady && IsPboArmed && IsInputValid && !IsRunning && !IsInitializing;
    public bool CanStartSingleCore => IsHardwareReady && IsPboArmed && IsInputValid && !IsRunning && !IsInitializing;
    public bool CanExport => HasResult && !IsRunning;
    public bool CanShowCoreVoltages =>
        HasResult && !IsRunning
        && (_currentRun?.CoreVoltages.Count > 0 || _singleCoreRun?.CoreVoltages.Count > 0);
    public string StartButtonText => IsRunning ? "测试中…" : "开始全核测试";
    public string SingleCoreButtonText => IsRunning ? "测试中…" : "开始每核单独测试";

    public bool HasResult
    {
        get => _hasResult;
        private set
        {
            if (Set(ref _hasResult, value))
            {
                OnPropertyChanged(nameof(CanExport));
                OnPropertyChanged(nameof(CanShowCoreVoltages));
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string StageText
    {
        get => _stageText;
        private set => Set(ref _stageText, value);
    }

    public string ValidationMessage
    {
        get => _validationMessage;
        private set
        {
            if (Set(ref _validationMessage, value))
            {
                OnPropertyChanged(nameof(HasValidationMessage));
            }
        }
    }

    public bool HasValidationMessage => !string.IsNullOrWhiteSpace(ValidationMessage);

    public int ProgressPercent
    {
        get => _progressPercent;
        private set => Set(ref _progressPercent, value);
    }

    public string CpuName
    {
        get => _cpuName;
        private set
        {
            if (Set(ref _cpuName, value))
            {
                CpuDisplayName = ShortenCpuName(value);
            }
        }
    }

    public string CpuDisplayName
    {
        get => _cpuDisplayName;
        private set => Set(ref _cpuDisplayName, value);
    }

    public string ArchitectureText
    {
        get => _architectureText;
        private set => Set(ref _architectureText, value);
    }

    public string CoreText
    {
        get => _coreText;
        private set => Set(ref _coreText, value);
    }

    public string CcdText
    {
        get => _ccdText;
        private set => Set(ref _ccdText, value);
    }

    public string StockFMaxText
    {
        get => _stockFMaxText;
        private set => Set(ref _stockFMaxText, value);
    }

    public string CurrentFMaxText
    {
        get => _currentFMaxText;
        private set => Set(ref _currentFMaxText, value);
    }

    public string OriginalFMaxText
    {
        get => _originalFMaxText;
        private set => Set(ref _originalFMaxText, value);
    }

    public string ScalarText
    {
        get => _scalarText;
        private set => Set(ref _scalarText, value);
    }

    public string PboSummary
    {
        get => _pboSummary;
        private set => Set(ref _pboSummary, value);
    }

    public string PboDetail
    {
        get => _pboDetail;
        private set => Set(ref _pboDetail, value);
    }

    public string PboEvidenceText
    {
        get => _pboEvidenceText;
        private set => Set(ref _pboEvidenceText, value);
    }

    public string PboState
    {
        get => _pboState;
        private set => Set(ref _pboState, value);
    }

    public string PboToggleHint
    {
        get
        {
            if (!CanTogglePbo)
            {
                return "SMU 不可用，无法修改频率";
            }

            if (IsDemo)
            {
                return IsPboArmed ? "演示模式 · 已打开（不会修改硬件）" : "演示模式 · 未打开";
            }

            if (!IsPboArmed)
            {
                return "未打开 · 不会修改 CPU 频率";
            }

            if (_originalFMaxCeiling == 0)
            {
                return "已打开 · 正在读取原始频率";
            }

            int actual = ActualFromCeiling(_originalFMaxCeiling);
            return $"已打开 · 结束时会恢复到 {actual} MHz";
        }
    }

    public string StartFMaxText
    {
        get => _startFMaxText;
        set
        {
            if (Set(ref _startFMaxText, value))
            {
                ValidateInputs();
            }
        }
    }

    public string EndFMaxText
    {
        get => _endFMaxText;
        set
        {
            if (Set(ref _endFMaxText, value))
            {
                ValidateInputs();
            }
        }
    }

    public string StepText
    {
        get => _stepText;
        set
        {
            if (Set(ref _stepText, value))
            {
                ValidateInputs();
            }
        }
    }

    public string WarmupText
    {
        get => _warmupText;
        set
        {
            if (Set(ref _warmupText, value))
            {
                ValidateInputs();
            }
        }
    }

    public string SampleText
    {
        get => _sampleText;
        set
        {
            if (Set(ref _sampleText, value))
            {
                ValidateInputs();
            }
        }
    }

    public string PlanSummaryText
    {
        get => _planSummaryText;
        private set => Set(ref _planSummaryText, value);
    }

    public string SingleCorePlanText
    {
        get => _singleCorePlanText;
        private set => Set(ref _singleCorePlanText, value);
    }

    public string StepCountText
    {
        get => _stepCountText;
        private set => Set(ref _stepCountText, value);
    }

    public string ProgressTimeText
    {
        get => _progressTimeText;
        private set => Set(ref _progressTimeText, value);
    }

    public string RangeHintText
    {
        get => _rangeHintText;
        private set => Set(ref _rangeHintText, value);
    }

    public string ChartSubtitle
    {
        get => _chartSubtitle;
        private set => Set(ref _chartSubtitle, value);
    }

    public string PawnIoStatusText
    {
        get => _pawnIoStatusText;
        private set => Set(ref _pawnIoStatusText, value);
    }

    public string SmuStatusText
    {
        get => _smuStatusText;
        private set => Set(ref _smuStatusText, value);
    }

    public string AdminText
    {
        get => _adminText;
        private set => Set(ref _adminText, value);
    }

    public string AdminTooltip => IsElevated
        ? "当前以管理员身份运行，可访问 PawnIO 并写入 SMU。"
        : "当前为标准用户；需要管理员权限才能加载 PawnIO 并写入 SMU。";

    public bool IsEclkDetected
    {
        get => _isEclkDetected;
        private set => Set(ref _isEclkDetected, value);
    }

    public string EclkText
    {
        get => _eclkText;
        private set => Set(ref _eclkText, value);
    }

    public string EclkTooltip
    {
        get => _eclkTooltip;
        private set => Set(ref _eclkTooltip, value);
    }

    public string DriverWarningText
    {
        get => _driverWarningText;
        private set => Set(ref _driverWarningText, value);
    }

    public bool DriverWarningVisible
    {
        get => _driverWarningVisible;
        private set => Set(ref _driverWarningVisible, value);
    }

    public bool Has3DCache
    {
        get => _has3DCache;
        private set => Set(ref _has3DCache, value);
    }

    public PlotModel PlotModel
    {
        get => _plotModel;
        private set => Set(ref _plotModel, value);
    }

    public PlotModel PerCorePlotModel
    {
        get => _perCorePlotModel;
        private set => Set(ref _perCorePlotModel, value);
    }

    public PlotModel SingleCorePlotModel
    {
        get => _singleCorePlotModel;
        private set => Set(ref _singleCorePlotModel, value);
    }

    /// <summary>Per-core chart legend, one row per CCD.</summary>
    public IReadOnlyList<CoreLegendGroup> PerCoreLegend
    {
        get => _perCoreLegend;
        private set => Set(ref _perCoreLegend, value);
    }

    /// <summary>Single-core chart legend, one row per CCD.</summary>
    public IReadOnlyList<CoreLegendGroup> SingleCoreLegend
    {
        get => _singleCoreLegend;
        private set => Set(ref _singleCoreLegend, value);
    }

    public SweepRunResult? CurrentRun => _currentRun;
    public SweepRunResult? SingleCoreRun => _singleCoreRun;

    public async Task InitializeAsync()
    {
        Log(LogLevel.Info, $"ZenVF {AppVersion}（构建 {BuildStamp()}）— AMD Zen 4 / Zen 5 V/F 扫描工具（默认 5 秒预热 + 5 秒采样）");
        if (!IsDemo)
        {
            Log(LogLevel.Info, IsElevated ? "进程已提权。" : "进程未提权，硬件写入可能不可用。");
        }

        if (IsDemo)
        {
            SetupDemo();
        }
        else
        {
            await InitializeHardwareAsync();
        }

        IsInitializing = false;
        ValidateInputs();
        RefreshCommands();
    }

    private void BuildCcdOptions()
    {
        CcdOptions.Clear();
        if (_topology is null)
        {
            return;
        }

        foreach (CcdInfo ccd in _topology.Ccds)
        {
            CcdOption option = new()
            {
                Index = ccd.Index,
                Name = ccd.Name,
                PhysicalCores = ccd.PhysicalCoreCount,
                Is3DCache = ccd.Is3DCache,
                IsSelected = true
            };
            option.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(CcdOption.IsSelected))
                {
                    ValidateInputs();
                }
            };
            CcdOptions.Add(option);
        }

        OnPropertyChanged(nameof(CcdColumns));
    }

    public void Log(LogLevel level, string message)
    {
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Dispatch(() => AddLog(new LogEntry { Level = level, Message = message }));
            return;
        }

        AddLog(new LogEntry { Level = level, Message = message });
    }

    private void AddLog(LogEntry entry)
    {
        LogItems.Add(entry);
        while (LogItems.Count > 600)
        {
            LogItems.RemoveAt(0);
        }
    }

    public string BuildLogText()
    {
        return string.Join(Environment.NewLine, LogItems.Select(l => $"[{l.Time:HH:mm:ss}] {l.LevelText} {l.Message}"));
    }

    private static void Dispatch(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action);
        }
    }

    private void RefreshCommands()
    {
        _startCommand.RaiseCanExecuteChanged();
        _singleCoreStartCommand.RaiseCanExecuteChanged();
        _stopCommand.RaiseCanExecuteChanged();
        _togglePboCommand.RaiseCanExecuteChanged();
        _installPawnIoCommand.RaiseCanExecuteChanged();
        _reconnectSmuCommand.RaiseCanExecuteChanged();
    }

    private int ActualFromCeiling(uint ceilingFMax) =>
        _cpu?.ToActualFMax(ceilingFMax) ?? (int)ceilingFMax;

    private uint CeilingFromActual(int actualFMax) =>
        _cpu?.ToCeilingFMax(actualFMax) ?? (uint)actualFMax;

    private static bool TryParse(string text, out int value) => int.TryParse(text.Trim(), out value);

    private static string ShortenCpuName(string name)
    {
        string[] suffixes =
        {
            " 16-Core Processor", " 12-Core Processor", " 8-Core Processor", " 6-Core Processor",
            " 4-Core Processor", " 24-Core Processor", " 32-Core Processor", " w/ Radeon", " with Radeon"
        };
        foreach (string suffix in suffixes)
        {
            int index = name.IndexOf(suffix, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
            {
                return name[..index].Trim();
            }
        }

        return name;
    }

    private static string FormatDuration(int seconds)
    {
        if (seconds < 60)
        {
            return $"{seconds} 秒";
        }

        int minutes = seconds / 60;
        int remaining = seconds % 60;
        return remaining == 0 ? $"{minutes} 分" : $"{minutes} 分 {remaining} 秒";
    }

    /// <summary>
    /// Write time of the running assembly, so a log shows which build was
    /// tested (the version number alone does not change between local builds).
    /// </summary>
    private static string BuildStamp()
    {
        try
        {
            string path = typeof(MainViewModel).Assembly.Location;
            if (string.IsNullOrEmpty(path))
            {
                path = Environment.ProcessPath ?? string.Empty;
            }

            return File.Exists(path)
                ? File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                : "未知";
        }
        catch
        {
            return "未知";
        }
    }

    private static string FormatClock(TimeSpan value)
    {
        int totalSeconds = Math.Max(0, (int)Math.Round(value.TotalSeconds));
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
    }

    public void Dispose()
    {
        Shutdown();
    }
}
