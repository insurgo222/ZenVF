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

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppOptions _options;
    private readonly SweepEngine _engine = new();
    private readonly DispatcherTimer _telemetryTimer;
    private readonly RelayCommand _stopCommand;
    private readonly AsyncRelayCommand _startCommand;
    private readonly AsyncRelayCommand _singleCoreStartCommand;
    private readonly AsyncRelayCommand _togglePboCommand;
    private readonly AsyncRelayCommand _installPawnIoCommand;

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
    private bool _telemetryBusy;
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
    private string _packagePowerText = "—";
    private string _tctlText = "—";
    private string _averageClockText = "—";
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
    private SweepSettings? _lastSettings;
    private double _runPlannedSeconds;
    private readonly List<CoreMapItem> _coreMapItems = new();
    private string _coreMapCaption = "等待测试数据";

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

        _telemetryTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(700)
        };
        _telemetryTimer.Tick += async (_, _) => await RefreshTelemetryAsync();

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

    public ObservableCollection<CcdOption> CcdOptions { get; } = new();
    public ObservableCollection<CoreMapGroup> CoreMap { get; } = new();
    public ObservableCollection<LogEntry> LogItems { get; } = new();

    public string CoreMapCaption
    {
        get => _coreMapCaption;
        private set => Set(ref _coreMapCaption, value);
    }

    public event EventHandler? SingleCoreSweepStarted;

    public RelayCommand StopCommand => _stopCommand;
    public AsyncRelayCommand StartCommand => _startCommand;
    public AsyncRelayCommand SingleCoreStartCommand => _singleCoreStartCommand;
    public AsyncRelayCommand TogglePboCommand => _togglePboCommand;
    public AsyncRelayCommand InstallPawnIoCommand => _installPawnIoCommand;
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
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStartSingleCore));
                RefreshCommands();
            }
        }
    }

    public bool CanTogglePbo => _smu?.IsAvailable == true || IsDemo;
    public bool CanInstallPawnIo => !IsDemo && !PawnIoNative.IsInstalled && File.Exists(GetPawnIoInstallerPath());
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
    public bool CanShowCoreVoltages => HasResult && _currentRun?.CoreVoltages.Count > 0 && !IsRunning;
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

    public string PackagePowerText
    {
        get => _packagePowerText;
        private set => Set(ref _packagePowerText, value);
    }

    public string TctlText
    {
        get => _tctlText;
        private set => Set(ref _tctlText, value);
    }

    public string AverageClockText
    {
        get => _averageClockText;
        private set => Set(ref _averageClockText, value);
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

    public SweepRunResult? CurrentRun => _currentRun;
    public SweepRunResult? SingleCoreRun => _singleCoreRun;

    public async Task InitializeAsync()
    {
        Log(LogLevel.Info, "ZenVF 1.0.7 — AMD Zen 4 / Zen 5 V/F 扫描工具（默认 5 秒预热 + 5 秒采样）");
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
        _telemetryTimer.Start();
        await RefreshTelemetryAsync();
        ValidateInputs();
        RefreshCommands();
    }

    private void SetupDemo()
    {
        _cpu = DemoData.CreateCpu();
        _topology = DemoData.CreateTopology();
        _sensors = new DemoSensorService();
        _sensors.Start();
        _smu = null;
        _originalFMaxCeiling = 5900;
        _isPboArmed = true;
        OnPropertyChanged(nameof(IsPboArmed));
        OnPropertyChanged(nameof(CanTogglePbo));
        OnPropertyChanged(nameof(PboToggleHint));

        CpuName = _cpu.Name;
        ArchitectureText = "Zen 5";
        CoreText = $"{_cpu.PhysicalCores}C / {_cpu.LogicalCores}T";
        CcdText = "2 CCD";
        Has3DCache = true;
        StockFMaxText = "5700 MHz";
        CurrentFMaxText = "5900 MHz";
        OriginalFMaxText = "5900 MHz";
        ScalarText = "1.00";
        PboState = "on";
        PboSummary = "PBO 已开启";
        PboDetail = "演示模式：模拟 PBO 已开启，不会修改硬件。";
        PboEvidenceText = string.Join("\n", DemoData.CreatePboAssessment().Evidence.Take(2).Select(e => "• " + e));
        PawnIoStatusText = "PawnIO 演示";
        SmuStatusText = "RSMU 演示连接";
        DriverWarningVisible = false;
        IsHardwareReady = true;

        BuildCcdOptions();
        _suppressValidation = true;
        SweepSettings settings = DemoData.CreateSettings();
        StartFMaxText = settings.StartFMax.ToString();
        EndFMaxText = settings.EndFMax.ToString();
        StepText = settings.StepMHz.ToString();
        WarmupText = settings.WarmupSeconds.ToString();
        SampleText = settings.SampleSeconds.ToString();
        _suppressValidation = false;

        _currentRun = DemoData.CreateRunResult();
        _singleCoreRun = DemoData.CreateSingleCoreSweepResult(settings, _topology);
        ApplyEclk(_currentRun.IsEclkDetected ? _currentRun : _singleCoreRun);
        HasResult = _currentRun.CoreVoltages.Count > 0
            || _singleCoreRun.CoreVoltages.Count > 0;
        RebuildCharts(_currentRun, true);
        RebuildSingleCoreChart(true);
        UpdateChartSubtitle(_currentRun);
        RangeHintText = "可测 4300–5900 MHz · 默认 5900→4300";
        StatusText = "演示模式 · 不会修改硬件";
        StageText = "演示曲线已加载";
        OnPropertyChanged(nameof(CanShowCoreVoltages));
        Log(LogLevel.Warning, "DEMO 模式：不会修改硬件，也不会启动真实负载。");
        Log(LogLevel.Info, "已载入 9950X3D 演示 V/F 曲线（每档每 CCD 最高电压核心）。");
        Log(LogLevel.Info, $"已载入单核演示扫描（{_singleCoreRun.CoreVoltages.Count} 档 × {_singleCoreRun.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本）。");
        Log(LogLevel.Info, "最高频率接管：已打开（演示）。");
        BuildCoreMap();
        UpdateCoreMap(_currentRun.CoreVoltages.LastOrDefault());
    }

    private async Task InitializeHardwareAsync()
    {
        StageText = "正在检测 CPU、PawnIO 与 SMU…";
        Log(LogLevel.Info, "正在检测硬件…");

        HardwareInitResult init = await Task.Run(() =>
        {
            CpuDescriptor cpu = CpuDescriptor.Detect(_options.CpuOverride);
            CpuTopology topology = CpuTopology.Detect(cpu);
            cpu.CcdCount = topology.CcdCount;

            SmuController? smu = null;
            string? smuError = null;
            if (cpu.IsAmd)
            {
                smu = new SmuController(cpu);
                if (!smu.Initialize(out string error))
                {
                    smuError = error;
                }
            }
            else
            {
                smuError = "未检测到 AMD 处理器。";
            }

            LhmSensorService sensors = new(topology, smu, cpu.IsZen4 || cpu.IsZen5);
            sensors.Start();
            SensorSnapshot snapshot = sensors.Sample();
            PboAssessment? assessment = smu?.IsAvailable == true ? smu.AssessPbo(snapshot) : null;

            return new HardwareInitResult(cpu, topology, smu, sensors, smuError, assessment, smu?.LastKnownFMaxCeiling ?? 0);
        });

        _cpu = init.Cpu;
        _topology = init.Topology;
        _smu = init.Smu;
        _sensors = init.Sensors;
        _originalFMaxCeiling = init.OriginalFMaxCeiling;

        CpuName = _cpu.Name;
        ArchitectureText = _cpu.IsZen5 ? "Zen 5" : _cpu.IsZen4 ? "Zen 4" : _cpu.ArchitectureLabel;
        CoreText = $"{_cpu.PhysicalCores}C / {_cpu.LogicalCores}T";
        CcdText = _cpu.CcdCount == 1 ? "1 CCD" : $"{_cpu.CcdCount} CCD";
        Has3DCache = _topology.Ccds.Any(c => c.Is3DCache) ||
                     _cpu.Name.Contains("X3D", StringComparison.OrdinalIgnoreCase);
        BuildCcdOptions();

        PawnIoStatusText = PawnIoNative.IsInstalled
            ? $"PawnIO {PawnIoNative.InstalledVersion ?? ""}".Trim()
            : "PawnIO 未安装";
        SmuStatusText = _smu?.IsAvailable == true
            ? $"SMU 0x{_smu.SmuVersion:X8} · {_smu.PlatformName}"
            : "SMU 不可用";
        DriverWarningVisible = !PawnIoNative.IsInstalled;
        DriverWarningText = "未检测到 PawnIO。安装后才能读取 CPU 频率、电压并修改最高频率。";
        IsHardwareReady = _smu?.IsAvailable == true;

        if (!PawnIoNative.IsInstalled)
        {
            Log(LogLevel.Error, "未安装 PawnIO。请点击“安装 PawnIO”，安装完成后重启 ZenVF。");
        }
        else if (_smu?.IsAvailable != true)
        {
            Log(LogLevel.Error, $"SMU 初始化失败：{init.SmuError ?? "未知错误"}");
        }
        else
        {
            Log(LogLevel.Success, $"SMU 已连接：{_smu.PlatformName}，版本 0x{_smu.SmuVersion:X8}。");
        }

        if (_sensors is { IsAvailable: false })
        {
            Log(LogLevel.Warning, $"传感器不可用：{_sensors.Error}");
        }
        else if (_smu?.HasPerCoreVoltageTable == true)
        {
            Log(LogLevel.Success, $"每核电压源：SMU PM table 0x{_smu.PmTableVersion:X8}（已绕过 LHM 错误的 Zen 4/5 VID）。");
        }
        else if (_cpu.IsZen4 || _cpu.IsZen5)
        {
            uint pmVersion = _smu?.PmTableVersion ?? 0;
            Log(LogLevel.Warning, $"SMU PM table 不可用（版本 0x{pmVersion:X8}），将回退到 LHM Core VID（SVI3 修正）；建议仍用 HWiNFO 对照。");
        }

        int? stockActual = _cpu.StockFMaxMHz;
        int currentActual = init.OriginalFMaxCeiling > 0 ? _cpu.ToActualFMax(init.OriginalFMaxCeiling) : 0;
        StockFMaxText = stockActual is int stock ? $"{stock} MHz" : "—";
        CurrentFMaxText = currentActual > 0 ? $"{currentActual} MHz" : "—";
        OriginalFMaxText = init.OriginalFMaxCeiling > 0 ? $"{currentActual} MHz" : "—";
        ScalarText = _smu?.PboScalar is float scalar ? scalar.ToString("0.00") : "—";

        if (init.Assessment is not null)
        {
            ApplyAssessment(init.Assessment);
        }
        else
        {
            PboState = "unknown";
            PboSummary = _smu?.IsAvailable == true ? "PBO 状态未知" : "SMU 不可用";
            PboDetail = init.SmuError ?? "无法读取 PBO 状态。";
            PboEvidenceText = init.SmuError is null ? string.Empty : "• " + init.SmuError;
        }

        int baseline = stockActual ?? (currentActual > 0 ? currentActual : 5000);
        int start = currentActual > 0 ? currentActual : baseline;
        if (stockActual is int s)
        {
            start = Math.Min(start, s + 200);
        }

        int end = _cpu.BaseClockMHz ?? Math.Max(3000, baseline - 500);
        if (end >= start)
        {
            end = Math.Max(3000, start - 1000);
        }

        _suppressValidation = true;
        StartFMaxText = start.ToString();
        EndFMaxText = end.ToString();
        StepText = "100";
        WarmupText = "5";
        SampleText = "5";
        _suppressValidation = false;

        if (stockActual is int knownStock)
        {
            int minEnd = _cpu.BaseClockMHz ?? (knownStock - 1000);
            RangeHintText = $"可测 {minEnd}–{knownStock + 200} MHz · 默认 {start}→{end}";
        }
        else
        {
            RangeHintText = $"默认 {start}→{end} MHz";
        }

        await MeasureStartupEclkAsync();

        if (init.OriginalFMaxCeiling > 0)
        {
            Log(LogLevel.Info,
                $"原始 FMax：SMU 上限 {init.OriginalFMaxCeiling} MHz，实际最高频率 {currentActual} MHz。最高频率接管未打开。");
            if (_cpu.FMaxCeilingOffsetMHz > 0)
            {
                Log(LogLevel.Info,
                    $"{_cpu.ModelToken ?? _cpu.Name}：SMU FMax 上限比实际最高加速高 {_cpu.FMaxCeilingOffsetMHz} MHz；界面与扫描范围均使用实际值。");
            }
        }

        if (_smu?.IsAvailable == true)
        {
            StatusText = "就绪 · 请先打开“最高频率接管”";
            StageText = "打开开关后会保存当前频率，结束时自动恢复";
            IsPboArmed = false;
        }
        else
        {
            StatusText = "SMU 不可用 · 无法开始扫描";
            StageText = init.SmuError ?? "请安装 PawnIO 并重试";
            IsPboArmed = false;
        }

        OnPropertyChanged(nameof(CanTogglePbo));
        OnPropertyChanged(nameof(PboToggleHint));
        OnPropertyChanged(nameof(CanInstallPawnIo));

        BuildCoreMap();
    }

    private async Task MeasureStartupEclkAsync()
    {
        if (IsDemo || _sensors is null || _cpu is null)
        {
            return;
        }

        if (!_sensors.IsAvailable)
        {
            Log(LogLevel.Warning, "eCLK 启动读取跳过：LHM 传感器不可用。");
            return;
        }

        int pboMaxMHz = _originalFMaxCeiling > 0
            ? _cpu.ToActualFMax(_originalFMaxCeiling)
            : _cpu.StockFMaxMHz ?? 0;
        if (pboMaxMHz <= 0)
        {
            Log(LogLevel.Warning, "eCLK 启动读取跳过：无法确定当前 PBO 最大频率。");
            return;
        }

        string previousStage = StageText;
        string previousStatus = StatusText;
        StageText = "正在读取启动显示频率（约 5 秒）…";
        StatusText = "正在读取启动时的最高核心频率…";
        Log(LogLevel.Info,
            $"启动 eCLK 读取：连续采样约 5 秒，读取 LHM Core #N 的运行时最大值（HWiNFO Max 列）与 Bus Speed 最大值；PBO 最大频率 {pboMaxMHz} MHz。");

        List<SensorCoreSample> samples = new();
        double busSpeedMHz = 0;
        try
        {
            async Task CollectAsync(int durationMs)
            {
                DateTime start = DateTime.Now;
                do
                {
                    SensorSnapshot snapshot = _sensors.Sample();
                    if (snapshot.HasData)
                    {
                        if (SensorValueSanity.IsPlausibleClock(snapshot.BusSpeedMHz)
                            && snapshot.BusSpeedMHz > busSpeedMHz)
                        {
                            busSpeedMHz = snapshot.BusSpeedMHz;
                        }

                        samples.AddRange(snapshot.Cores.Where(core =>
                        {
                            double displayMax = Math.Max(core.CoreClock, core.CoreClockMax);
                            return SensorValueSanity.IsPlausibleClock(displayMax)
                                && displayMax >= 800;
                        }));
                    }

                    await Task.Delay(50);
                }
                while ((DateTime.Now - start).TotalMilliseconds < durationMs && !_disposed);
            }

            // Read for five seconds so the bus/clock maximum converges instead
            // of depending on whichever 50 ms sample happened first. If the
            // runtime Max is still below PBO after that, a very short
            // single-core wake is used to give the Core #N Max sensor a real
            // boost clock; this is not the all-core stress test.
            await CollectAsync(5000);
            EclkDetector.Estimate initial = EclkDetector.DetectStartup(samples, pboMaxMHz, busSpeedMHz);
            if (!initial.Detected)
            {
                PhysicalCoreRef? probeCore = _topology?.GetPhysicalCores().FirstOrDefault();
                if (probeCore is not null)
                {
                    StageText = "正在用单核唤醒刷新 Core #N 最大值…";
                    Log(LogLevel.Info,
                        "启动 Max 尚未达到 PBO 上限，使用约 0.8 秒单核唤醒刷新 Core #N Max；不写 FMax，不使用 Effective，不运行全核烤机。");
                    using LoadGenerator probe = new();
                    probe.StartSingleCore(probeCore);
                    await CollectAsync(800);
                    await Task.Run(probe.Stop);
                    await CollectAsync(100);
                }
            }

            EclkDetector.Estimate estimate = EclkDetector.DetectStartup(samples, pboMaxMHz, busSpeedMHz);
            _startupEclkPeakMHz = estimate.PeakClockMHz;
            _startupEclkPboMaxMHz = estimate.PboMaxMHz;
            _startupEclkBusMHz = estimate.BusSpeedMHz;
            if (estimate.Detected)
            {
                _startupEclkRatio = estimate.Ratio;
                _startupEclkSamples = estimate.SampleCount;
                ApplyEclk(null);
                Log(LogLevel.Success,
                    $"eCLK 启动读取完成：Core #N 运行最大值 {estimate.PeakClockMHz:0.0} MHz，Bus Speed {estimate.BusSpeedMHz:0.###} MHz，PBO 最大频率 {estimate.PboMaxMHz:0} MHz ≈ {estimate.Ratio:0.00000}（BCLK2 ≈ {estimate.BclkMHz:0.###} MHz，{estimate.CoreCount} 核 / {estimate.SampleCount} 个样本）。");
            }
            else
            {
                _startupEclkRatio = null;
                _startupEclkSamples = 0;
                ApplyEclk(null);
                Log(LogLevel.Info,
                    $"eCLK 启动读取未检测：Core #N 运行最大值 {estimate.PeakClockMHz:0.0} MHz，Bus Speed {estimate.BusSpeedMHz:0.###} MHz，PBO 最大频率 {estimate.PboMaxMHz:0} MHz ≈ {estimate.PeakClockMHz / Math.Max(1, estimate.PboMaxMHz):0.00000}。频率标签按设定频率显示。");
            }
        }
        catch (Exception ex)
        {
            _startupEclkRatio = null;
            _startupEclkSamples = 0;
            ApplyEclk(null);
            Log(LogLevel.Warning, $"eCLK 启动读取失败：{ex.Message}。频率标签按设定频率显示。");
        }
        finally
        {
            StageText = previousStage;
            StatusText = previousStatus;
        }
    }

    private void ApplyAssessment(PboAssessment assessment)
    {
        PboState = assessment.Enabled switch
        {
            true => "on",
            false => "off",
            _ => "unknown"
        };
        PboSummary = assessment.Summary;
        PboDetail = assessment.Detail;
        PboEvidenceText = assessment.Evidence.Count > 0
            ? string.Join("\n", assessment.Evidence.Take(2).Select(e => "• " + e))
            : "• 无可用证据";
        if (assessment.CurrentFMax is int current)
        {
            CurrentFMaxText = $"{current} MHz";
        }

        if (assessment.Scalar is float scalar)
        {
            ScalarText = scalar.ToString("0.00");
        }
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
    }

    private void BuildCoreMap()
    {
        CoreMap.Clear();
        _coreMapItems.Clear();
        if (_topology is null)
        {
            UpdateCoreMap(null);
            return;
        }

        foreach (CcdInfo ccd in _topology.Ccds.OrderBy(c => c.Index))
        {
            CoreMapGroup group = new()
            {
                CcdIndex = ccd.Index,
                Name = ccd.Name,
                Meta = ccd.Is3DCache ? "3D" : $"{ccd.PhysicalCoreCount} 核",
                Accent = ChartBuilder.CcdColorHex(ccd.Index)
            };

            foreach (PhysicalCoreRef core in _topology.GetPhysicalCores(new[] { ccd.Index }))
            {
                CoreMapItem item = new()
                {
                    CoreId = core.DisplayCoreId,
                    CcdIndex = ccd.Index,
                    Label = $"C{core.DisplayCoreId}"
                };
                group.Cores.Add(item);
                _coreMapItems.Add(item);
            }

            CoreMap.Add(group);
        }

        UpdateCoreMap(null);
    }

    private void UpdateCoreMap(FrequencyCoreVoltages? frequency, bool singleCore = false)
    {
        if (frequency is null || frequency.Cores.Count == 0)
        {
            foreach (CoreMapItem item in _coreMapItems)
            {
                item.VoltageText = "—";
                item.ClockText = "—";
                item.Tooltip = $"Core #{item.CoreId} · 等待本档读数";
                item.Heat = 0;
                item.IsMax = false;
                item.IsLoaded = false;
            }

            CoreMapCaption = "等待测试数据";
            return;
        }

        List<CoreVoltageSample> valid = frequency.Cores
            .Where(c => SensorValueSanity.IsPlausibleVoltage(c.Voltage))
            .ToList();
        double min = valid.Count > 0 ? valid.Min(c => c.Voltage) : 0;
        double max = valid.Count > 0 ? valid.Max(c => c.Voltage) : 0;
        int maxCoreId = frequency.MaxVoltageCore?.DisplayCoreId ?? -1;

        foreach (CoreMapItem item in _coreMapItems)
        {
            CoreVoltageSample? sample = frequency.Cores
                .FirstOrDefault(c => c.DisplayCoreId == item.CoreId);
            if (sample is null || !SensorValueSanity.IsPlausibleVoltage(sample.Voltage))
            {
                item.VoltageText = "—";
                item.ClockText = "—";
                item.Heat = 0;
                item.IsMax = false;
                item.IsLoaded = false;
                item.Tooltip = $"Core #{item.CoreId} · 本档无有效读数";
                continue;
            }

            double span = max - min;
            item.Heat = span > 0.0005
                ? 0.18 + (0.62 * ((sample.Voltage - min) / span))
                : 0.4;
            item.VoltageText = $"{sample.Voltage:0.000}";
            item.ClockText = sample.ClockMHz > 0 ? $"{sample.ClockMHz:0}" : "—";
            item.IsMax = sample.DisplayCoreId == maxCoreId;
            item.IsLoaded = sample.LogicalProcessor >= 0;
            item.Tooltip = $"Core #{item.CoreId} · CCD{item.CcdIndex}\n"
                + $"{sample.Voltage:0.000} V · {sample.ClockMHz:0} MHz · {sample.Temperature:0} °C";
        }

        CoreMapCaption = $"{(singleCore ? "单核" : "全核")} · {frequency.RequestedFMax} MHz · {valid.Count} 核";
    }

    private void ValidateInputs()
    {
        if (_suppressValidation)
        {
            return;
        }

        SweepSettings? settings = BuildSettings();
        if (settings is null || _topology is null)
        {
            IsInputValid = false;
            ValidationMessage = "请输入有效的整数参数。";
            StepCountText = "—";
            PlanSummaryText = "检查输入参数";
            SingleCorePlanText = "检查每核单独测试参数";
            if (!IsRunning)
            {
                ProgressTimeText = "预计时间 —";
            }

            return;
        }

        string? error = settings.Validate(_cpu?.StockFMaxMHz, _cpu?.BaseClockMHz);
        ValidationMessage = error ?? string.Empty;
        IsInputValid = error is null;
        _lastSettings = settings;
        StepCountText = error is null ? settings.StepCount.ToString() : "—";
        PlanSummaryText = error is null
            ? $"{settings.StepCount} 档 · {settings.SelectedCcds.Count} 个 CCD · 预估约 {FormatDuration(settings.ApproximateDurationSeconds)}"
            : "检查输入参数";

        if (error is not null)
        {
            SingleCorePlanText = "检查每核单独测试参数";
            if (!IsRunning)
            {
                ProgressTimeText = "请检查输入参数";
            }

            return;
        }

        int coreCount = CountSelectedCores(settings.SelectedCcds);
        SingleCorePlanText =
            $"{settings.StepCount} 档 × {coreCount} 核 · 预估约 {FormatDuration(settings.ApproximateSingleCoreDurationSeconds(coreCount))}";

        if (!IsRunning)
        {
            // The old estimate used to sit under the buttons. It now lives
            // under the progress bar and becomes a live ETA once a run starts.
            ProgressTimeText = PlanSummaryText;
        }
    }

    private SweepSettings? BuildSettings()
    {
        if (!TryParse(StartFMaxText, out int start) ||
            !TryParse(EndFMaxText, out int end) ||
            !TryParse(StepText, out int step) ||
            !TryParse(WarmupText, out int warmup) ||
            !TryParse(SampleText, out int sample))
        {
            return null;
        }

        return new SweepSettings
        {
            StartFMax = start,
            EndFMax = end,
            StepMHz = step,
            WarmupSeconds = warmup,
            SampleSeconds = sample,
            SelectedCcds = CcdOptions.Where(c => c.IsSelected).Select(c => c.Index).ToHashSet(),
            RestoreAfterTest = true
        };
    }

    private int CountSelectedCores(IEnumerable<int> selectedCcds)
    {
        if (_topology is null)
        {
            return 0;
        }

        HashSet<int> selected = selectedCcds.ToHashSet();
        return _topology.Ccds
            .Where(ccd => selected.Contains(ccd.Index))
            .Sum(ccd => ccd.PhysicalCoreCount);
    }

    private async Task StartAsync()
    {
        if (!ValidateStart(out SweepSettings? settings))
        {
            return;
        }

        if (IsDemo)
        {
            await RunDemoSweepAsync(settings!);
            return;
        }

        if (_smu is null || _topology is null || _cpu is null || _sensors is null)
        {
            Log(LogLevel.Error, "硬件未就绪。");
            return;
        }

        if (!_smu.TryGetFMaxCeiling(out uint currentCeiling))
        {
            Log(LogLevel.Error, "无法读取当前 FMax，已取消。");
            return;
        }

        if (_originalFMaxCeiling == 0)
        {
            _originalFMaxCeiling = currentCeiling;
            OriginalFMaxText = $"{ActualFromCeiling(currentCeiling)} MHz";
            OnPropertyChanged(nameof(PboToggleHint));
        }

        _runPlannedSeconds = settings!.ApproximateDurationSeconds;
        IsRunning = true;
        ProgressPercent = 0;
        ProgressTimeText = $"预计剩余 {FormatClock(TimeSpan.FromSeconds(_runPlannedSeconds))} · 已用 00:00";
        HasResult = _singleCoreRun?.CoreVoltages.Count > 0;
        _cts = new CancellationTokenSource();
        _currentRun = new SweepRunResult
        {
            CpuName = _cpu.Name,
            StockFMax = _cpu.StockFMaxMHz,
            StockFMaxCeiling = _cpu.StockFMaxCeilingMHz,
            FMaxCeilingOffsetMHz = _cpu.FMaxCeilingOffsetMHz,
            Settings = settings!,
            Series = settings!.SelectedCcds.OrderBy(i => i)
                .Select(i => new SweepSeries { CcdIndex = i, Label = $"CCD{i} | MAX CORE" })
                .ToList(),
            Started = DateTime.Now
        };
        ApplyEclk(null);
        RebuildCharts(_currentRun, false);
        UpdateCoreMap(null);
        ChartSubtitle = "全核测试中…";
        StatusText = "全核测试 · 正在设置频率";
        StageText = "准备全核负载";
        Log(LogLevel.Info,
            $"开始全核测试：{settings!.StepCount} 个频率档位（{settings.StartFMax}→{settings.EndFMax} MHz，写入上限 {CeilingFromActual(settings.StartFMax)}→{CeilingFromActual(settings.EndFMax)} MHz），CCD {string.Join(", ", settings.SelectedCcds.OrderBy(i => i))}，全核负载。");

        try
        {
            Task<SweepRunResult> runTask = _engine.RunAsync(
                _smu,
                _sensors,
                _topology,
                _cpu,
                settings,
                _originalFMaxCeiling,
                entry => Dispatch(() => AddLog(entry)),
                progress => Dispatch(() => ApplyProgress(progress)),
                cores => Dispatch(() => AddCoreVoltages(cores)),
                point => Dispatch(() => AddLivePoint(point)),
                _cts.Token);
            _runTask = runTask;

            SweepRunResult result = await runTask;
            ApplySessionEclk(result);
            _currentRun = result;
            ApplyEclk(result.IsEclkDetected ? result : _singleCoreRun);
            RefreshHasResult();
            RebuildCharts(result, false);
            UpdateChartSubtitle(result);
            StatusText = !result.Settings.RestoreAfterTest
                ? "已完成 · 保留最终频率"
                : result.RestoreSucceeded == false
                    ? "已完成 · 恢复原始频率失败，请重启系统"
                    : "已完成 · 已恢复原始频率";
            StageText = result.RestoreSucceeded == false
                ? "恢复失败 · 请立即重启系统"
                : $"完成 · {result.CoreVoltages.Count} 个频率档位";
            ProgressPercent = 100;
            ProgressTimeText = $"总用时 {FormatClock((result.Finished ?? DateTime.Now) - result.Started)}";
            Log(LogLevel.Success, "全核测试结束。可查看图表、每核电压或导出 CSV。");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止 · 正在恢复原始频率";
            StageText = "用户中止";
            ProgressPercent = 0;
            ProgressTimeText = "已停止";
            Log(LogLevel.Warning, "全核测试已由用户停止。");
        }
        catch (Exception ex)
        {
            StatusText = "全核测试失败 · 正在恢复原始频率";
            StageText = "发生错误";
            ProgressTimeText = "已中止";
            Log(LogLevel.Error, ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            _runTask = null;
            await RefreshSmuStateAsync();

            bool stopped = StatusText.StartsWith("已停止", StringComparison.Ordinal);
            bool failedRun = StatusText.StartsWith("全核测试失败", StringComparison.Ordinal);
            bool completedRestored = StatusText.StartsWith("已完成 · 已恢复", StringComparison.Ordinal);
            if (stopped || failedRun || completedRestored)
            {
                bool restored = false;
                if (_smu?.IsAvailable == true && _originalFMaxCeiling is >= 400 and <= 8000)
                {
                    restored = await Task.Run(() =>
                        _smu.TryGetFMaxCeiling(out uint currentCeiling)
                        && currentCeiling == _originalFMaxCeiling);
                }

                if (completedRestored)
                {
                    if (!restored)
                    {
                        StatusText = "已完成 · 恢复原始频率失败，请重启系统";
                        StageText = "恢复失败 · 请立即重启系统";
                    }
                }
                else
                {
                    string prefix = failedRun ? "全核测试失败" : "已停止";
                    StatusText = restored
                        ? $"{prefix} · 已恢复原始频率"
                        : $"{prefix} · 恢复原始频率失败，请重启系统";
                    StageText = restored ? "已恢复原始频率" : "请立即重启系统";
                }
            }
        }
    }

    private async Task StartSingleCoreSweepAsync()
    {
        if (!ValidateStart(out SweepSettings? settings))
        {
            return;
        }

        if (IsDemo)
        {
            await RunDemoSingleCoreSweepAsync(settings!);
            return;
        }

        if (_smu is null || _topology is null || _cpu is null || _sensors is null)
        {
            Log(LogLevel.Error, "硬件未就绪。");
            return;
        }

        if (!_smu.TryGetFMaxCeiling(out uint currentCeiling))
        {
            Log(LogLevel.Error, "无法读取当前最高频率，已取消单核测试。");
            return;
        }

        if (_originalFMaxCeiling == 0)
        {
            _originalFMaxCeiling = currentCeiling;
            OriginalFMaxText = $"{ActualFromCeiling(currentCeiling)} MHz";
            OnPropertyChanged(nameof(PboToggleHint));
        }

        IsRunning = true;
        ProgressPercent = 0;
        ProgressTimeText = "预计剩余 --:--";
        HasResult = _currentRun?.CoreVoltages.Count > 0;
        _cts = new CancellationTokenSource();
        _singleCoreRun = new SweepRunResult
        {
            CpuName = _cpu.Name,
            StockFMax = _cpu.StockFMaxMHz,
            StockFMaxCeiling = _cpu.StockFMaxCeilingMHz,
            FMaxCeilingOffsetMHz = _cpu.FMaxCeilingOffsetMHz,
            Settings = settings!,
            Series = new List<SweepSeries>(),
            Started = DateTime.Now
        };
        ApplyEclk(null);
        RebuildSingleCoreChart(false);
        ChartSubtitle = "单核测试中…";
        StatusText = "单核测试 · 正在设置频率";
        StageText = "准备单核负载";
        int coreCount = CountSelectedCores(settings!.SelectedCcds);
        _runPlannedSeconds = settings.ApproximateSingleCoreDurationSeconds(coreCount);
        ProgressTimeText = $"预计剩余 {FormatClock(TimeSpan.FromSeconds(_runPlannedSeconds))} · 已用 00:00";
        Log(LogLevel.Info,
            $"开始每核单独测试：{settings.StepCount} 个频率档位（{settings.StartFMax}→{settings.EndFMax} MHz），每档依次测试 {coreCount} 个物理核心，每核 {settings.WarmupSeconds} 秒预热 / {settings.SampleSeconds} 秒采样。");
        SingleCoreSweepStarted?.Invoke(this, EventArgs.Empty);

        try
        {
            Task<SweepRunResult> runTask = _engine.RunSingleCoreSweepAsync(
                _smu,
                _sensors,
                _topology,
                _cpu,
                settings,
                _originalFMaxCeiling,
                entry => Dispatch(() => AddLog(entry)),
                progress => Dispatch(() => ApplySingleCoreProgress(progress)),
                frequency => Dispatch(() => AddSingleCoreSweep(frequency)),
                _cts.Token);
            _runTask = runTask;

            SweepRunResult result = await runTask;
            ApplySessionEclk(result);
            _singleCoreRun = result;
            ApplyEclk(result.IsEclkDetected ? result : _currentRun);
            RefreshHasResult();
            RebuildSingleCoreChart(false);
            UpdateChartSubtitle(_currentRun);
            StatusText = result.RestoreSucceeded == false
                ? "单核完成 · 恢复原始频率失败，请重启系统"
                : "单核完成 · 已恢复原始频率";
            StageText = result.RestoreSucceeded == false
                ? "恢复失败 · 请立即重启系统"
                : $"完成 · {result.CoreVoltages.Count} 个频率档位 · {result.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本";
            ProgressPercent = 100;
            ProgressTimeText = $"总用时 {FormatClock((result.Finished ?? DateTime.Now) - result.Started)}";
            Log(LogLevel.Success, "单核测试结束。可查看“单核 V/F”图，或导出 CSV。");
        }
        catch (OperationCanceledException)
        {
            StatusText = "已停止 · 正在恢复原始频率";
            StageText = "用户中止单核测试";
            ProgressPercent = 0;
            ProgressTimeText = "已停止";
            Log(LogLevel.Warning, "单核测试已由用户停止。");
        }
        catch (Exception ex)
        {
            StatusText = "单核测试失败 · 正在恢复原始频率";
            StageText = "发生错误";
            ProgressTimeText = "已中止";
            Log(LogLevel.Error, ex.Message);
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
            _runTask = null;
            await RefreshSmuStateAsync();

            bool stopped = StatusText.StartsWith("已停止", StringComparison.Ordinal);
            bool failedRun = StatusText.StartsWith("单核测试失败", StringComparison.Ordinal);
            bool completed = StatusText.StartsWith("单核完成", StringComparison.Ordinal);
            if (!IsDemo && (stopped || failedRun || completed))
            {
                bool restored = false;
                if (_smu?.IsAvailable == true && _originalFMaxCeiling is >= 400 and <= 8000)
                {
                    restored = await Task.Run(() =>
                        _smu.TryGetFMaxCeiling(out uint currentCeiling)
                        && currentCeiling == _originalFMaxCeiling);
                }

                if (completed)
                {
                    StatusText = restored
                        ? "单核完成 · 已恢复原始频率"
                        : "单核完成 · 恢复原始频率失败，请重启系统";
                    StageText = restored ? StageText : "恢复失败 · 请立即重启系统";
                }
                else
                {
                    string prefix = failedRun ? "单核测试失败" : "已停止";
                    StatusText = restored
                        ? $"{prefix} · 已恢复原始频率"
                        : $"{prefix} · 恢复原始频率失败，请重启系统";
                    StageText = restored ? "已恢复原始频率" : "请立即重启系统";
                }
            }
        }
    }

    private async Task RunDemoSingleCoreSweepAsync(SweepSettings settings)
    {
        if (_topology is null)
        {
            Log(LogLevel.Error, "演示拓扑未就绪。");
            return;
        }

        List<PhysicalCoreRef> cores = _topology.GetPhysicalCores(settings.SelectedCcds).ToList();
        if (cores.Count == 0)
        {
            Log(LogLevel.Error, "所选 CCD 没有可测试的物理核心。");
            return;
        }

        _runPlannedSeconds = 0;
        IsRunning = true;
        ProgressPercent = 0;
        ProgressTimeText = "预计剩余 --:--";
        HasResult = _currentRun?.CoreVoltages.Count > 0;
        _cts = new CancellationTokenSource();
        _singleCoreRun = new SweepRunResult
        {
            CpuName = DemoData.CpuName,
            StockFMax = _cpu?.StockFMaxMHz ?? 5700,
            StockFMaxCeiling = _cpu?.StockFMaxCeilingMHz,
            FMaxCeilingOffsetMHz = _cpu?.FMaxCeilingOffsetMHz ?? 0,
            Settings = settings,
            Series = new List<SweepSeries>(),
            Started = DateTime.Now
        };
        ApplyEclk(null);
        RebuildSingleCoreChart(true);
        ChartSubtitle = "DEMO SINGLE-CORE SWEEP";
        StatusText = "演示单核测试中…";
        StageText = "DEMO · 准备单核负载";
        SingleCoreSweepStarted?.Invoke(this, EventArgs.Empty);
        Log(LogLevel.Info,
            $"DEMO 单核测试开始：{settings.StepCount} 个档位 × {cores.Count} 个核心，每个档位依次只测一个核心，不写入任何硬件。");

        List<int> frequencies = settings.BuildFrequencies().ToList();
        Stopwatch overall = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < frequencies.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                int requestedFMax = frequencies[i];
                FrequencyCoreVoltages frequency = DemoData.CreateSingleCoreFrequency(requestedFMax, cores);
                _singleCoreRun.CoreVoltages.Add(frequency);
                RefreshHasResult();
                RebuildSingleCoreChart(true);
                UpdateChartSubtitle(_currentRun);
                double fraction = (i + 1) / (double)frequencies.Count;
                ProgressPercent = (int)Math.Round(fraction * 100);
                ProgressTimeText = FormatProgressTime(fraction, overall.Elapsed, 0);
                StageText = $"DEMO · {requestedFMax} MHz · {cores.Count} 核 · 单核";
                await Task.Delay(150, _cts.Token);
            }

            _singleCoreRun.Finished = DateTime.Now;
            _singleCoreRun.Note = "演示数据 · 非实测";
            DemoData.ApplyDemoEclk(_singleCoreRun);
            ApplyEclk(_singleCoreRun.IsEclkDetected ? _singleCoreRun : _currentRun);
            RebuildSingleCoreChart(true);
            RefreshHasResult();
            StatusText = "演示单核测试完成 · 未写入硬件";
            StageText = $"DEMO · {_singleCoreRun.CoreVoltages.Count} 个档位 · {_singleCoreRun.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本";
            ProgressPercent = 100;
            ProgressTimeText = $"总用时 {FormatClock((_singleCoreRun.Finished ?? DateTime.Now) - _singleCoreRun.Started)}";
            UpdateChartSubtitle(_currentRun);
            Log(LogLevel.Success,
                $"DEMO 单核测试完成：{_singleCoreRun.CoreVoltages.Count} 个档位、{_singleCoreRun.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本。");
        }
        catch (OperationCanceledException)
        {
            StatusText = "演示单核测试已停止";
            ProgressTimeText = "已停止";
            Log(LogLevel.Warning, "单核测试已由用户停止。");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private bool ValidateStart(out SweepSettings? settings)
    {
        settings = BuildSettings();
        if (settings is null)
        {
            ValidationMessage = "请输入有效的整数参数。";
            return false;
        }

        string? error = settings.Validate(_cpu?.StockFMaxMHz, _cpu?.BaseClockMHz);
        if (error is not null)
        {
            ValidationMessage = error;
            StatusText = "无法开始 · 参数无效";
            Log(LogLevel.Warning, error);
            return false;
        }

        if (!IsPboArmed)
        {
            StatusText = "无法开始 · 请先打开最高频率接管";
            Log(LogLevel.Warning, "请先打开“最高频率接管”开关。");
            return false;
        }

        if (!IsHardwareReady)
        {
            StatusText = "无法开始 · 驱动或 SMU 不可用";
            return false;
        }

        ValidationMessage = string.Empty;
        return true;
    }

    private async Task RunDemoSweepAsync(SweepSettings settings)
    {
        _runPlannedSeconds = 0;
        IsRunning = true;
        ProgressPercent = 0;
        ProgressTimeText = "预计剩余 --:--";
        HasResult = _singleCoreRun?.CoreVoltages.Count > 0;
        _cts = new CancellationTokenSource();
        _currentRun = new SweepRunResult
        {
            CpuName = DemoData.CpuName,
            StockFMax = _cpu?.StockFMaxMHz ?? 5700,
            StockFMaxCeiling = _cpu?.StockFMaxCeilingMHz,
            FMaxCeilingOffsetMHz = _cpu?.FMaxCeilingOffsetMHz ?? 0,
            Settings = settings,
            Series = settings.SelectedCcds.OrderBy(i => i).Select(i => new SweepSeries
            {
                CcdIndex = i,
                Label = $"CCD{i} | DEMO"
            }).ToList(),
            Started = DateTime.Now
        };
        ApplyEclk(null);
        RebuildCharts(_currentRun, true);
        UpdateCoreMap(null);
        ChartSubtitle = "DEMO SWEEP RUNNING";
        StatusText = "演示全核测试中…";
        Log(LogLevel.Info, "DEMO 全核测试开始，不写入任何硬件。");

        List<int> frequencies = settings.BuildFrequencies().ToList();
        Stopwatch overall = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < frequencies.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();
                FrequencyCoreVoltages frequency = FilterRowToCcds(
                    DemoData.CreateCoreVoltages(frequencies[i]),
                    settings.SelectedCcds);
                _currentRun.CoreVoltages.Add(frequency);
                foreach (SweepSeries series in _currentRun.Series)
                {
                    SweepPoint? point = DemoData.CreatePointForCcd(frequency, series.CcdIndex);
                    if (point is not null)
                    {
                        series.Add(point);
                    }
                }

                RebuildCharts(_currentRun, true);
                UpdateCoreMap(frequency);
                OnPropertyChanged(nameof(CanShowCoreVoltages));
                double fraction = (i + 1) / (double)frequencies.Count;
                ProgressPercent = (int)Math.Round(fraction * 100);
                ProgressTimeText = FormatProgressTime(fraction, overall.Elapsed, 0);
                StageText = $"DEMO · {frequencies[i]} MHz · 全核";
                UpdateChartSubtitle(_currentRun);
                await Task.Delay(100, _cts.Token);
            }

            _currentRun.Finished = DateTime.Now;
            _currentRun.Note = "演示数据 · 非实测";
            DemoData.ApplyDemoEclk(_currentRun);
            ApplyEclk(_currentRun.IsEclkDetected ? _currentRun : _singleCoreRun);
            RebuildCharts(_currentRun, true);
            HasResult = true;
            StatusText = "演示全核测试完成 · 未写入硬件";
            StageText = "DEMO · 完成";
            ProgressPercent = 100;
            ProgressTimeText = $"总用时 {FormatClock((_currentRun.Finished ?? DateTime.Now) - _currentRun.Started)}";
            Log(LogLevel.Success, $"DEMO 全核测试完成，{_currentRun.CoreVoltages.Count} 个频率档位、{_currentRun.CoreVoltages.Sum(f => f.Cores.Count)} 个核心样本。");
        }
        catch (OperationCanceledException)
        {
            StatusText = "演示全核测试已停止";
            ProgressTimeText = "已停止";
            Log(LogLevel.Warning, "全核测试已由用户停止。");
        }
        finally
        {
            IsRunning = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static FrequencyCoreVoltages FilterRowToCcds(
        FrequencyCoreVoltages row,
        IReadOnlySet<int> selectedCcds) => new()
    {
        RequestedFMax = row.RequestedFMax,
        EffectiveFMax = row.EffectiveFMax,
        RequestedFMaxCeiling = row.RequestedFMaxCeiling,
        EffectiveFMaxCeiling = row.EffectiveFMaxCeiling,
        PackagePower = row.PackagePower,
        Tctl = row.Tctl,
        Cores = row.Cores.Where(c => selectedCcds.Contains(c.CcdIndex)).ToList()
    };

    private async Task TogglePboAsync()
    {
        if (IsDemo)
        {
            IsPboArmed = !IsPboArmed;
            OnPropertyChanged(nameof(PboToggleHint));
            Log(LogLevel.Info, IsPboArmed ? "DEMO 最高频率接管已打开。" : "DEMO 最高频率接管已关闭。");
            return;
        }

        if (_smu is null || !_smu.IsAvailable)
        {
            Log(LogLevel.Error, "SMU 不可用，无法切换最高频率接管。");
            return;
        }

        if (!IsPboArmed)
        {
            if (!_smu.TryGetFMaxCeiling(out uint currentCeiling))
            {
                Log(LogLevel.Error, "无法读取当前 FMax，接管失败。");
                return;
            }

            _originalFMaxCeiling = currentCeiling;
            int currentActual = ActualFromCeiling(currentCeiling);
            OriginalFMaxText = $"{currentActual} MHz";
            CurrentFMaxText = $"{currentActual} MHz";
            IsPboArmed = true;
            StatusText = "就绪 · 最高频率接管已打开";
            StageText = $"已保存原始最高频率 {currentActual} MHz";
            OnPropertyChanged(nameof(PboToggleHint));
            Log(LogLevel.Success,
                $"最高频率接管已打开：原始最高频率 {currentActual} MHz（SMU 上限 {currentCeiling} MHz）。");
            await RefreshPboStatusAsync();
        }
        else
        {
            IsPboArmed = false;
            bool restored = await RestoreOriginalFMaxAsync("手动关闭最高频率接管");
            StatusText = restored
                ? "就绪 · 最高频率接管未打开"
                : "恢复原始频率失败，请重启系统";
            StageText = restored
                ? (_originalFMaxCeiling > 0
                    ? $"原始最高频率 {ActualFromCeiling(_originalFMaxCeiling)} MHz"
                    : "等待打开")
                : "请立即重启系统";
            Log(restored ? LogLevel.Info : LogLevel.Error,
                restored ? "最高频率接管已关闭。" : "最高频率接管已关闭，但恢复原始频率失败。");
        }
    }

    private async Task<bool> RestoreOriginalFMaxAsync(string reason)
    {
        if (_smu?.IsAvailable != true || _originalFMaxCeiling is < 400 or > 8000)
        {
            return false;
        }

        SmuWriteResult restore = await Task.Run(() => _smu.SetFMaxCeiling(_originalFMaxCeiling));
        if (!restore.Success)
        {
            Log(LogLevel.Error, $"{reason}：恢复 FMax 失败 — {restore.Message}");
            return false;
        }

        bool verified = restore.ReadBackFMax == _originalFMaxCeiling;
        if (!verified)
        {
            verified = await Task.Run(() =>
                _smu.TryGetFMaxCeiling(out uint readBack) && readBack == _originalFMaxCeiling);
        }

        if (!verified)
        {
            if (restore.ReadBackFMax > 0)
            {
                CurrentFMaxText = $"{ActualFromCeiling(restore.ReadBackFMax)} MHz";
            }

            Log(LogLevel.Error,
                $"{reason}：恢复 FMax 写入成功但回读不一致（期望 SMU 上限 {_originalFMaxCeiling} MHz）— {restore.Message}");
            return false;
        }

        int restoredActual = ActualFromCeiling(_originalFMaxCeiling);
        CurrentFMaxText = $"{restoredActual} MHz";
        Log(LogLevel.Success,
            $"{reason}：已恢复原始 FMax —— SMU 上限 {_originalFMaxCeiling} MHz，实际最高加速 {restoredActual} MHz。");
        return true;
    }

    public void Stop()
    {
        if (!IsRunning)
        {
            return;
        }

        StatusText = "正在停止…";
        StageText = "等待当前采样结束，恢复原始频率";
        ProgressTimeText = "正在停止…";
        Log(LogLevel.Warning, "正在停止测试…");
        _cts?.Cancel();
    }

    public void RefreshChartsForTheme()
    {
        foreach (CoreMapGroup group in CoreMap)
        {
            group.Accent = ChartBuilder.CcdColorHex(group.CcdIndex);
        }

        RebuildCharts(_currentRun, IsDemo);
        RebuildSingleCoreChart(IsDemo);
    }

    private void ApplyProgress(SweepProgress progress)
    {
        UpdateProgressDisplay(progress, "全核测试", null, null);
    }

    private void ApplySingleCoreProgress(SweepProgress progress)
    {
        UpdateProgressDisplay(progress, "单核测试", null, null);
    }

    private void UpdateProgressDisplay(
        SweepProgress progress,
        string modeLabel,
        double? fractionOverride,
        TimeSpan? elapsedOverride)
    {
        double fraction = Math.Clamp(fractionOverride ?? progress.Fraction, 0, 1);
        TimeSpan elapsed = elapsedOverride ?? progress.Elapsed;
        ProgressPercent = (int)Math.Round(fraction * 100);
        StatusText = $"{modeLabel} · {progress.Frequency} MHz";
        StageText = $"{progress.Stage} · 第 {progress.StepIndex}/{progress.StepCount} 档";
        ProgressTimeText = FormatProgressTime(fraction, elapsed, _runPlannedSeconds);
    }

    private static string FormatProgressTime(double fraction, TimeSpan elapsed, double plannedSeconds)
    {
        if (elapsed <= TimeSpan.Zero || fraction <= 0.0001)
        {
            if (plannedSeconds > 0)
            {
                double initialRemaining = Math.Max(0, plannedSeconds - elapsed.TotalSeconds);
                return $"预计剩余 {FormatClock(TimeSpan.FromSeconds(initialRemaining))} · 已用 {FormatClock(elapsed)}";
            }

            return "预计剩余 --:--";
        }

        if (fraction >= 0.999)
        {
            return $"已用 {FormatClock(elapsed)}";
        }

        double measuredTotal = elapsed.TotalSeconds / fraction;
        double predictedTotal = measuredTotal;
        if (plannedSeconds > 0)
        {
            // Early in a run the partial fraction is dominated by warmup and
            // produces huge, unstable ETAs. Start from the planned duration,
            // then blend in the observed rate as enough progress accumulates.
            double weight = Math.Clamp((fraction - 0.05) / 0.45, 0, 0.85);
            predictedTotal = (plannedSeconds * (1 - weight)) + (measuredTotal * weight);
        }

        double remainingSeconds = Math.Max(0, predictedTotal - elapsed.TotalSeconds);
        return $"预计剩余 {FormatClock(TimeSpan.FromSeconds(remainingSeconds))} · 已用 {FormatClock(elapsed)}";
    }

    private void AddSingleCoreSweep(FrequencyCoreVoltages frequency)
    {
        if (_singleCoreRun is null)
        {
            return;
        }

        if (!_singleCoreRun.CoreVoltages.Contains(frequency))
        {
            _singleCoreRun.CoreVoltages.Add(frequency);
        }

        RefreshHasResult();
        RebuildSingleCoreChart(false);
        UpdateChartSubtitle(_currentRun);
        UpdateCoreMap(frequency, singleCore: true);
    }

    private void AddCoreVoltages(FrequencyCoreVoltages frequency)
    {
        if (_currentRun is null)
        {
            return;
        }

        // SweepEngine already stores the same instance in the run result. The
        // check also keeps demo/live callbacks safe if their ordering changes.
        if (!_currentRun.CoreVoltages.Contains(frequency))
        {
            _currentRun.CoreVoltages.Add(frequency);
        }

        HasResult = true;
        OnPropertyChanged(nameof(CanShowCoreVoltages));
        RebuildCharts(_currentRun, false);
        UpdateChartSubtitle(_currentRun);
        UpdateCoreMap(frequency);
    }

    private void AddLivePoint(SweepPoint point)
    {
        if (_currentRun is null)
        {
            return;
        }

        SweepSeries? series = _currentRun.Series.FirstOrDefault(s => s.CcdIndex == point.CcdIndex);
        if (series is null)
        {
            return;
        }

        if (!series.Points.Contains(point))
        {
            series.Add(point);
        }

        HasResult = true;
        OnPropertyChanged(nameof(CanShowCoreVoltages));
        RebuildCharts(_currentRun, false);
        UpdateChartSubtitle(_currentRun);
    }

    private void RebuildCharts(SweepRunResult? result, bool isDemo)
    {
        PlotModel = ChartBuilder.Build(result, isDemo);
        PerCorePlotModel = ChartBuilder.BuildPerCore(result, isDemo);
    }

    private void RebuildSingleCoreChart(bool isDemo)
    {
        SingleCorePlotModel = ChartBuilder.BuildPerCore(_singleCoreRun, isDemo, true);
    }

    private void RefreshHasResult()
    {
        HasResult = (_currentRun?.CoreVoltages.Count > 0)
            || (_singleCoreRun?.CoreVoltages.Count > 0);
    }

    private void ApplySessionEclk(SweepRunResult result)
    {
        if (result.EclkRatio is null && _startupEclkRatio is double ratio)
        {
            result.EclkRatio = ratio;
            result.EclkSampleCount = _startupEclkSamples;
        }
    }

    private void ApplyEclk(SweepRunResult? result)
    {
        double? ratio = result?.EclkRatio ?? _startupEclkRatio;
        int sampleCount = result?.EclkSampleCount ?? _startupEclkSamples;
        if (ratio is double detectedRatio && detectedRatio > 1.0)
        {
            IsEclkDetected = true;
            EclkText = $"eCLK {FormatEclkBclk(detectedRatio)} MHz";
            string sourceText = _startupEclkPeakMHz > 0
                ? $"Core #N 运行最大值 {_startupEclkPeakMHz:0.0} MHz / PBO 最大频率 {_startupEclkPboMaxMHz:0} MHz"
                : $"eCLK 倍率 {detectedRatio:0.00000}";
            string busText = _startupEclkBusMHz > 0
                ? $"，Bus Speed {_startupEclkBusMHz:0.###} MHz"
                : string.Empty;
            EclkTooltip =
                $"已检测到外部时钟发生器（eCLK，启动显示频率读取值）。\n" +
                $"{sourceText}，BCLK2 ≈ {detectedRatio * 100:0.000} MHz{busText}，{sampleCount} 个样本。\n" +
                "仅使用 LHM Core #N 的运行时最大值（HWiNFO Max 列），不使用 Core #N (Effective)，也不使用烤机频率。\n" +
                "图表横轴使用实测有效频率；每核电压窗口的档位/标题按“设定频率 × eCLK 倍率”显示实际频率，窗口内不显示实时有效频率。";
            return;
        }

        IsEclkDetected = false;
        EclkText = "eCLK —";
        EclkTooltip = "未检测到 eCLK，或 LHM 未提供启动时的 Core #N 显示频率。频率标签按设定频率显示。";
    }

    private void UpdateChartSubtitle(SweepRunResult? result)
    {
        result ??= _currentRun;
        int allSteps = result?.CoreVoltages.Count ?? 0;
        int allCores = CountDistinctCores(result);
        int singleSteps = _singleCoreRun?.CoreVoltages.Count ?? 0;
        int singleCores = CountDistinctCores(_singleCoreRun);
        int? stockActual = result?.StockFMax ?? _singleCoreRun?.StockFMax;
        string stock = stockActual is int stockValue
            ? $" · 基准 {stockValue} MHz"
            : string.Empty;

        string cpuName = result?.CpuName ?? _singleCoreRun?.CpuName ?? CpuName;
        string subtitle;
        if (allSteps > 0)
        {
            subtitle = $"{cpuName} · 全核 {allSteps} 档 · {allCores} 核{stock}";
        }
        else if (singleSteps > 0)
        {
            subtitle = $"{cpuName} · 单核 {singleSteps} 档 · {singleCores} 核{stock}";
        }
        else
        {
            subtitle = $"{cpuName} · 等待扫描数据{stock}";
        }

        if (allSteps > 0 && singleSteps > 0)
        {
            subtitle += $" · 单核 {singleSteps} 档";
        }

        ChartSubtitle = subtitle;
    }

    private static int CountDistinctCores(SweepRunResult? result)
    {
        return result?.CoreVoltages
            .SelectMany(f => f.Cores)
            .Select(c => (c.CcdIndex, c.CoreId))
            .Distinct()
            .Count() ?? 0;
    }

    private async Task RefreshSmuStateAsync()
    {
        if (_smu?.IsAvailable != true)
        {
            return;
        }

        await Task.Run(() =>
        {
            if (_smu.TryGetFMaxCeiling(out uint currentCeiling))
            {
                int currentActual = ActualFromCeiling(currentCeiling);
                Dispatch(() =>
                {
                    CurrentFMaxText = $"{currentActual} MHz";
                    OnPropertyChanged(nameof(PboToggleHint));
                });
            }

            SensorSnapshot snapshot = _sensors?.Sample() ?? new SensorSnapshot();
            PboAssessment assessment = _smu.AssessPbo(snapshot);
            Dispatch(() => ApplyAssessment(assessment));
        });
    }

    private async Task RefreshPboStatusAsync()
    {
        if (_smu?.IsAvailable != true)
        {
            return;
        }

        PboAssessment assessment = await Task.Run(() => _smu.AssessPbo(_sensors?.Sample()));
        ApplyAssessment(assessment);
    }

    private async Task RefreshTelemetryAsync()
    {
        if (_telemetryBusy || _sensors is null || IsInitializing)
        {
            return;
        }

        _telemetryBusy = true;
        try
        {
            SensorSnapshot snapshot = await Task.Run(() => _sensors.Sample());
            ApplyTelemetry(snapshot);
        }
        finally
        {
            _telemetryBusy = false;
        }
    }

    private void ApplyTelemetry(SensorSnapshot snapshot)
    {
        PackagePowerText = snapshot.PackagePower > 0 ? $"{snapshot.PackagePower:0.0} W" : "—";
        TctlText = snapshot.Tctl > 0 ? $"{snapshot.Tctl:0.0} °C" : "—";
        AverageClockText = snapshot.AverageEffectiveClock > 0 ? $"{snapshot.AverageEffectiveClock:0} MHz" : "—";
    }

    private async Task InstallPawnIoAsync()
    {
        string path = GetPawnIoInstallerPath();
        if (!File.Exists(path))
        {
            Log(LogLevel.Error, "安装程序不存在：PawnIO_setup.exe");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            Log(LogLevel.Info, "已启动 PawnIO 安装程序。安装完成后请重启 ZenVF。");
            StatusText = "已启动 PawnIO 安装程序";
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"无法启动 PawnIO 安装程序：{ex.Message}");
        }

        await Task.CompletedTask;
    }

    private static string GetPawnIoInstallerPath()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "PawnIO_setup.exe");
        return File.Exists(root) ? root : Path.Combine(AppContext.BaseDirectory, "Assets", "PawnIO_setup.exe");
    }

    public void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _telemetryTimer.Stop();
        try
        {
            _cts?.Cancel();
            _runTask?.Wait(5000);
        }
        catch
        {
            // Ignore shutdown wait failures.
        }

        try
        {
            if (!IsDemo && _smu?.IsAvailable == true && _originalFMaxCeiling is >= 400 and <= 8000)
            {
                if (!_smu.TryGetFMaxCeiling(out uint currentCeiling) || currentCeiling != _originalFMaxCeiling)
                {
                    _smu.SetFMaxCeiling(_originalFMaxCeiling);
                }
            }
        }
        catch
        {
            // Never block window shutdown.
        }

        _smu?.Dispose();
        _sensors?.Dispose();
        _cts?.Dispose();
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

    private static string FormatEclkBclk(double ratio)
    {
        return (ratio * 100.0).ToString("0.###", CultureInfo.InvariantCulture);
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

    private sealed record HardwareInitResult(
        CpuDescriptor Cpu,
        CpuTopology Topology,
        SmuController? Smu,
        LhmSensorService Sensors,
        string? SmuError,
        PboAssessment? Assessment,
        uint OriginalFMaxCeiling);
}
