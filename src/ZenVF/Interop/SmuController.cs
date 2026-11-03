using System.Diagnostics;
using System.Threading;
using ZenVF.Models;

namespace ZenVF.Interop;

public sealed class FusedLimits
{
    public int? Ppt { get; init; }
    public int? Tdc { get; init; }
    public int? Edc { get; init; }
}

public sealed class SmuController : IDisposable
{
    public const uint DesktopGetFMax = 0x6E;
    public const uint DesktopSetFMax = 0x70;
    public const uint DesktopGetPboScalar = 0x6D;
    public const uint DesktopFusedPpt = 0xDC;
    public const uint DesktopFusedTdc = 0xDB;
    public const uint DesktopFusedEdc = 0xD9;

    public const uint ApuGetFMax = 0x42;
    public const uint ApuSetFMax = 0x47;
    public const uint ApuGetPboScalar = 0x0F;
    public const uint ApuFusedPpt = 0x11;
    public const uint ApuFusedTdc = 0x15;
    public const uint ApuFusedEdc = 0x16;

    private readonly PawnIoNative _pawnIo = new();
    private readonly CpuDescriptor _cpu;
    private readonly object _sync = new();
    private readonly object _pmTableSync = new();
    private readonly object _writeSync = new();
    private readonly FMaxRestoreTracker _restore = new();

    // Returned by SendRsmu when the Global\Access_PCI mutex could not be
    // taken (HRESULT_FROM_WIN32(ERROR_TIMEOUT)); distinct from access denied.
    private const int PciLockTimeoutHResult = unchecked((int)0x800705B4);
    private const int InitAttempts = 3;
    private int _lastRsmuHr;
    private FmaxCommands? _commands;
    private bool _initialized;
    private bool _disposed;
    private uint _pmTableVersion;
    private int _pmTableSize;
    private int _perCoreVoltageBase = -1;
    private int _vddcrVddVoltageIndex = -1;
    private int _vddcrVddFallbackIndex = -1;
    private int _vddcrVddSecondFallbackIndex = -1;

    public SmuController(CpuDescriptor cpu)
    {
        _cpu = cpu;
    }

    public bool IsAvailable => _commands is not null && _pawnIo.IsLoaded;
    public string? InitializationError { get; private set; }
    public string PlatformName => _commands?.Name ?? "未连接";
    public uint SmuVersion { get; private set; }
    public uint PmTableVersion => _pmTableVersion;
    public bool HasPerCoreVoltageTable => _perCoreVoltageBase >= 0 && _pmTableSize > 0;
    public float? PboScalar { get; private set; }
    public FusedLimits? Fused { get; private set; }
    /// <summary>Raw SMU FMax ceiling, in MHz. This can be one 25 MHz step above the actual boost limit.</summary>
    public uint? LastKnownFMaxCeiling { get; private set; }

    public bool Initialize(out string error)
    {
        lock (_sync)
        {
            if (_initialized)
            {
                error = InitializationError ?? string.Empty;
                return IsAvailable;
            }

            _initialized = true;
            try
            {
                if (!_cpu.IsSupported)
                {
                    InitializationError =
                        $"不支持的处理器（Family 0x{_cpu.Family:X2} Model 0x{_cpu.Model:X2}）。仅支持已验证的 Zen 4 / Zen 5 型号，不会向 SMU 发送命令。";
                    error = InitializationError;
                    return false;
                }

                if (!PawnIoNative.IsInstalled)
                {
                    InitializationError = "未安装 PawnIO 驱动。";
                    error = InitializationError;
                    return false;
                }

                using Stream? stream = typeof(SmuController).Assembly
                    .GetManifestResourceStream("ZenVF.Assets.RyzenSMU.bin");
                if (stream is null)
                {
                    InitializationError = "内置 RyzenSMU 模块缺失。";
                    error = InitializationError;
                    return false;
                }

                using MemoryStream memory = new();
                stream.CopyTo(memory);
                _pawnIo.LoadEmbeddedModule(memory.ToArray());

                SmuVersion = ReadUInt("ioctl_get_smu_version");

                // Only the command set that belongs to this platform is used.
                // Probing the other set would send command ids whose meaning
                // on this SMU is unknown.
                FmaxCommands candidate = _cpu.IsApu ? FmaxCommands.Apu : FmaxCommands.Desktop;

                // Other monitors (HWiNFO starting or exiting, MSI Center ...)
                // can hold the PCI bus mutex for several seconds. Without a
                // retry, one busy moment at startup left the SMU unusable for
                // the whole session and was reported as a BIOS lock.
                uint lastValue = 0;
                for (int attempt = 1;
                     attempt <= InitAttempts && (_commands is null || _pmTableVersion == 0);
                     attempt++)
                {
                    if (attempt > 1)
                    {
                        Thread.Sleep(1000);
                    }

                    if (_pmTableVersion == 0)
                    {
                        ResolvePmTable();
                    }

                    if (_commands is null)
                    {
                        if (TryReadFMax(candidate, out uint value) && value is >= 400 and <= 8000)
                        {
                            _commands = candidate;
                            LastKnownFMaxCeiling = value;
                        }
                        else
                        {
                            lastValue = value;
                        }
                    }
                }

                if (_commands is null)
                {
                    InitializationError = _lastRsmuHr == PciLockTimeoutHResult
                        ? $"PCI 总线锁一直被其他硬件监控程序占用（例如 HWiNFO 正在启动或退出、MSI Center 等），共尝试 {InitAttempts} 次仍未拿到。请稍候或关闭这些程序，再点 SMU 状态行右侧的“重新连接”，无需重启 ZenVF。"
                        : _lastRsmuHr != 0
                            ? $"RSMU（{candidate.Name}）读取 FMax 的命令失败（HRESULT 0x{_lastRsmuHr:X8}）。"
                            : $"RSMU（{candidate.Name}）返回的 FMax 无效（{lastValue}）。BIOS 可能锁定了 PBO，或该平台的命令不兼容。";
                    error = InitializationError;
                    return false;
                }

                PboScalar = TryReadScalar(_commands);
                Fused = TryReadFused(_commands);
                InitializationError = null;
                error = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                InitializationError = ex.Message;
                error = ex.Message;
                return false;
            }
        }
    }

    /// <summary>Reads the raw SMU FMax ceiling. Use <see cref="CpuDescriptor.ToActualFMax"/> for user-facing values.</summary>
    public bool TryGetFMaxCeiling(out uint ceilingFMax)
    {
        ceilingFMax = 0;
        FmaxCommands? commands = _commands;
        if (commands is null)
        {
            return false;
        }

        if (TryReadFMax(commands, out ceilingFMax))
        {
            LastKnownFMaxCeiling = ceilingFMax;
            return true;
        }

        return false;
    }

    /// <summary>
    /// SMU FMax ceiling that was active before ZenVF's first unrestored write.
    /// Null when ZenVF has not changed FMax, or after a verified restore.
    /// </summary>
    public uint? RestoreTarget
    {
        get
        {
            lock (_writeSync)
            {
                return _restore.Target;
            }
        }
    }

    /// <summary>True while ZenVF has written an FMax that has not yet been verified as restored.</summary>
    public bool HasPendingRestore => RestoreTarget is not null;

    /// <summary>
    /// Writes the raw SMU FMax ceiling. Callers must convert actual boost values first.
    /// The first write of a dirty period reads the current ceiling and records
    /// it as <see cref="RestoreTarget"/>; the write is refused if that read fails.
    /// </summary>
    public SmuWriteResult SetFMaxCeiling(uint ceilingFMax)
    {
        lock (_writeSync)
        {
            FmaxCommands? commands = _commands;
            if (commands is null)
            {
                return new SmuWriteResult(false, 0, "SMU 未连接。");
            }

            if (ceilingFMax is < 400 or > 8000)
            {
                return new SmuWriteResult(false, 0, $"FMax 上限 {ceilingFMax} MHz 超出安全范围。");
            }

            if (!_restore.EnsureCaptured(() => TryReadFMax(commands, out uint before) ? before : null))
            {
                return new SmuWriteResult(false, 0, "写入前无法读取当前 FMax，无法记录恢复值，已拒绝写入。");
            }

            SmuWriteResult result = WriteFMaxCore(commands, ceilingFMax);
            _restore.RecordWrite(ceilingFMax, result.Success, result.ReadBackFMax);
            return result;
        }
    }

    /// <summary>
    /// Writes <see cref="RestoreTarget"/> back if ZenVF changed FMax. Returns
    /// success without touching the SMU when there is nothing to restore, so
    /// external FMax changes made by other tools are never overwritten.
    /// </summary>
    public SmuWriteResult RestoreFMax()
    {
        lock (_writeSync)
        {
            if (_restore.Target is not uint target)
            {
                return new SmuWriteResult(true, LastKnownFMaxCeiling ?? 0, "FMax 未被修改，无需恢复。");
            }

            SmuWriteResult result = SetFMaxCeiling(target);
            if (result.Success && _restore.HasPendingRestore)
            {
                return result with
                {
                    Success = false,
                    Message = $"已写入原始 FMax 上限 {target} MHz，但回读 {result.ReadBackFMax} MHz 不一致或回读失败。"
                };
            }

            return result;
        }
    }

    private SmuWriteResult WriteFMaxCore(FmaxCommands commands, uint ceilingFMax)
    {
        uint[] args = new uint[6];
        args[0] = ceilingFMax & 0xFFFFF;
        int hr = SendRsmu(commands.Set, ref args);
        if (hr != 0)
        {
            return new SmuWriteResult(false, 0, $"SMU 命令失败（HRESULT 0x{hr:X8}）。");
        }

        Thread.Sleep(120);
        bool readBack = TryReadFMax(commands, out uint current);
        LastKnownFMaxCeiling = readBack ? current : ceilingFMax;
        return new SmuWriteResult(
            true,
            readBack ? current : 0,
            readBack
                ? $"SMU FMax 上限已写入 {ceilingFMax} MHz，回读 {current} MHz。"
                : $"SMU FMax 上限已写入 {ceilingFMax} MHz，但回读失败。");
    }

    public PboAssessment AssessPbo(SensorSnapshot? snapshot = null)
    {
        FmaxCommands? commands = _commands;
        uint? currentCeiling = LastKnownFMaxCeiling;
        if (commands is not null && TryGetFMaxCeiling(out uint fmax))
        {
            currentCeiling = fmax;
        }

        int? current = currentCeiling is uint currentRaw ? _cpu.ToActualFMax(currentRaw) : null;
        int? stock = _cpu.StockFMaxMHz;
        int? stockCeiling = _cpu.StockFMaxCeilingMHz;
        float? scalar = PboScalar;
        FusedLimits? fused = Fused;
        List<string> evidence = new();
        bool? enabled = null;

        if (current is int c && stock is int s)
        {
            if (c > s)
            {
                evidence.Add($"当前最高频率 {c} MHz，高于默认 {s} MHz");
                enabled = true;
            }
            else
            {
                evidence.Add($"当前最高频率 {c} MHz，未超过默认 {s} MHz");
            }

            if (currentCeiling is uint ceiling)
            {
                evidence.Add(stockCeiling is int stockCeil && stockCeil != s
                    ? $"写入上限 {ceiling} MHz（默认上限 {stockCeil} MHz，偏移 {_cpu.FMaxCeilingOffsetMHz} MHz）"
                    : $"写入上限 {ceiling} MHz");
            }
        }
        else if (current is int c2)
        {
            evidence.Add($"当前最高频率 {c2} MHz（缺少该型号默认值，无法比较）");
            if (currentCeiling is uint ceiling2)
            {
                evidence.Add($"写入上限 {ceiling2} MHz");
            }
        }

        if (scalar is float sc)
        {
            // A scalar above 1.00 is already a deliberate PBO modification.
            // The old 1.005 threshold hid small but real changes such as 1.002.
            if (sc > 1.0001f)
            {
                evidence.Add($"PBO Scalar = {sc:0.00}，高于默认 1.00");
                enabled = true;
            }
            else
            {
                evidence.Add($"PBO Scalar = {sc:0.00}");
            }
        }

        if (snapshot is not null && fused is not null)
        {
            if (snapshot.PptLimit > 5 && fused.Ppt is int fp && snapshot.PptLimit > fp + 5)
            {
                evidence.Add($"PPT 限制 {snapshot.PptLimit:0} W 高于融合上限 {fp} W");
                enabled = true;
            }

            if (snapshot.TdcLimit > 1 && fused.Tdc is int ft && snapshot.TdcLimit > ft + 2)
            {
                evidence.Add($"TDC 限制 {snapshot.TdcLimit:0} A 高于融合上限 {ft} A");
                enabled = true;
            }

            if (snapshot.EdcLimit > 1 && fused.Edc is int fe && snapshot.EdcLimit > fe + 2)
            {
                evidence.Add($"EDC 限制 {snapshot.EdcLimit:0} A 高于融合上限 {fe} A");
                enabled = true;
            }
        }

        if (enabled is null && commands is not null)
        {
            enabled = false;
            evidence.Add("未发现 PBO 特征；PBO 也可能处于 Auto 且未修改任何限制");
        }

        string summary = enabled switch
        {
            true => "PBO 已开启",
            false => "PBO 未检测到",
            _ => "PBO 状态未知"
        };

        string detail = commands is null
            ? "SMU 不可用，无法读取 PBO 状态。"
            : enabled == true
                ? "检测到 PBO 相关设置已修改，直接显示为已开启。"
                : "未检测到 PBO 修改；PBO Auto 模式可能不会改变可见限制。";
        if (commands is not null && _cpu.FMaxCeilingOffsetMHz > 0)
        {
            detail += $" 这颗 CPU 的写入上限比最高频率高 {_cpu.FMaxCeilingOffsetMHz} MHz。";
        }

        return new PboAssessment
        {
            Enabled = enabled,
            Summary = summary,
            Detail = detail,
            Evidence = evidence,
            CurrentFMax = current,
            Scalar = scalar
        };
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _pawnIo.Dispose();
        }
    }

    private bool TryReadFMax(FmaxCommands commands, out uint value)
    {
        value = 0;
        uint[] args = new uint[6];
        int hr = SendRsmu(commands.Get, ref args);
        if (hr != 0)
        {
            return false;
        }

        value = args[0];
        if (value is >= 400 and <= 8000)
        {
            return true;
        }

        if (!ReferenceEquals(commands, FmaxCommands.Apu))
        {
            return false;
        }

        // Some APU firmware expects selector 3 in arg0 for GetBoostLimit.
        args = new uint[6];
        args[0] = 3;
        hr = SendRsmu(commands.Get, ref args);
        if (hr != 0)
        {
            return false;
        }

        value = args[0];
        return value is >= 400 and <= 8000;
    }

    private void ResolvePmTable()
    {
        try
        {
            using PciBusLock pciLock = new();
            if (!pciLock.Acquired)
            {
                return;
            }

            long[] result = _pawnIo.Execute("ioctl_resolve_pm_table", Array.Empty<long>(), 2);
            if (result.Length < 2)
            {
                return;
            }

            _pmTableVersion = (uint)result[0];
            switch (_pmTableVersion)
            {
                // Granite Ridge (Ryzen 9000 desktop) PM table, 0x994 bytes of
                // floats in volts.
                //
                // Verified on a 9800X3D (table 0x00620205, 2026-11-03) by
                // reading the table without refreshing it, i.e. the exact
                // snapshot HWiNFO had just requested, and comparing bit for
                // bit with HWiNFO's shared memory across idle / all-core /
                // single-core load:
                //   index 317 + N = HWiNFO "Core N VID"
                //   index 49      = HWiNFO "CPU VDDCR_VDD Voltage (SVI3 TFN)"
                //   index 54      = HWiNFO "CPU VDDCR_SOC Voltage (SVI3 TFN)"
                // Index 19 (previously used for VDDCR_VDD) is a different
                // quantity: ~19 mV higher under all-core load. It and index 18
                // remain last-resort fallbacks only.
                // 0x00621201 / 0x00621202 are assumed to share this layout.
                case 0x00620205:
                case 0x00621201:
                case 0x00621202:
                    _pmTableSize = 0x994;
                    _perCoreVoltageBase = 317;
                    _vddcrVddVoltageIndex = 49;
                    _vddcrVddFallbackIndex = 19;
                    _vddcrVddSecondFallbackIndex = 18;
                    break;
                default:
                    _pmTableSize = 0;
                    _perCoreVoltageBase = -1;
                    _vddcrVddVoltageIndex = -1;
                    _vddcrVddFallbackIndex = -1;
                    _vddcrVddSecondFallbackIndex = -1;
                    break;
            }
        }
        catch
        {
            _pmTableVersion = 0;
            _pmTableSize = 0;
            _perCoreVoltageBase = -1;
            _vddcrVddVoltageIndex = -1;
            _vddcrVddFallbackIndex = -1;
            _vddcrVddSecondFallbackIndex = -1;
        }
    }

    /// <summary>
    /// Reads per-core voltages and the VDDCR_VDD rail from the SMU PM table.
    /// On a 9800X3D these are bit-identical to HWiNFO's "Core N VID" and
    /// "CPU VDDCR_VDD Voltage (SVI3 TFN)" (see the layout notes in
    /// ResolvePmTable). LibreHardwareMonitor's own "Core #N VID" on Zen 4/5
    /// is not used when the PM table is available.
    /// </summary>
    public bool TryReadCoreVoltages(int coreCount, out double[] voltages)
    {
        return TryReadCoreVoltages(coreCount, out voltages, out _);
    }

    public bool TryReadCoreVoltages(int coreCount, out double[] voltages, out double packageVoltage)
    {
        voltages = Array.Empty<double>();
        packageVoltage = 0;
        int tableSize = _pmTableSize;
        int baseIndex = _perCoreVoltageBase;
        if (coreCount <= 0 || tableSize <= 0 || baseIndex < 0 || !_pawnIo.IsLoaded)
        {
            return false;
        }

        lock (_pmTableSync)
        {
            try
            {
                using PciBusLock pciLock = new();
                if (!pciLock.Acquired)
                {
                    return false;
                }

                _pawnIo.Execute("ioctl_update_pm_table", Array.Empty<long>(), 0);
                int longCount = (tableSize + 7) / 8;
                long[] raw = _pawnIo.Execute("ioctl_read_pm_table", Array.Empty<long>(), longCount);
                int bytesRead = raw.Length * sizeof(long);
                int requiredBytes = (baseIndex + coreCount) * sizeof(float);
                if (bytesRead < requiredBytes)
                {
                    return false;
                }

                float[] table = new float[tableSize / 4];
                // The driver may report the exact byte count instead of the
                // rounded-up long count, so the final few padding bytes can be
                // absent. All per-core entries are well before the padding.
                Buffer.BlockCopy(raw, 0, table, 0, Math.Min(tableSize, bytesRead));

                double package = ReadPmTableVoltage(table, _vddcrVddVoltageIndex);
                if (package <= 0)
                {
                    package = ReadPmTableVoltage(table, _vddcrVddFallbackIndex);
                }

                if (package <= 0)
                {
                    package = ReadPmTableVoltage(table, _vddcrVddSecondFallbackIndex);
                }

                if (package > 0)
                {
                    packageVoltage = package;
                }

                double[] values = new double[coreCount];
                bool any = false;
                for (int coreIndex = 0; coreIndex < coreCount; coreIndex++)
                {
                    int voltageIndex = baseIndex + coreIndex;
                    if ((uint)voltageIndex >= (uint)table.Length)
                    {
                        continue;
                    }

                    double value = SensorValueSanity.NormalizeVoltage(table[voltageIndex]);
                    if (SensorValueSanity.IsPlausibleVoltage(value))
                    {
                        values[coreIndex] = value;
                        any = true;
                    }
                }

                if (any)
                {
                    // No plausibility veto against the package rail here: idle
                    // cores legitimately sit near 0.75-0.8 V while VDDCR_VDD
                    // stays above 1.2 V, and the old "max core < 70% of the
                    // rail" check threw away correct, HWiNFO-identical data.
                    voltages = values;
                    return true;
                }
            }
            catch
            {
                // Fall back to LibreHardwareMonitor's per-core VID below.
            }
        }

        return false;
    }

    private static double ReadPmTableVoltage(float[] table, int index)
    {
        if ((uint)index >= (uint)table.Length)
        {
            return 0;
        }

        double value = SensorValueSanity.NormalizeVoltage(table[index]);
        return SensorValueSanity.IsPlausibleVoltage(value) ? value : 0;
    }

    private float? TryReadScalar(FmaxCommands commands)
    {
        uint[] args = new uint[6];
        int hr = SendRsmu(commands.GetScalar, ref args);
        if (hr != 0)
        {
            return null;
        }

        float value = BitConverter.Int32BitsToSingle((int)args[0]);
        return value is >= 0.1f and <= 10.0f ? value : null;
    }

    private FusedLimits? TryReadFused(FmaxCommands commands)
    {
        int? ppt = ReadFusedValue(commands.FusedPpt);
        int? tdc = ReadFusedValue(commands.FusedTdc);
        int? edc = ReadFusedValue(commands.FusedEdc);
        if (ppt is null && tdc is null && edc is null)
        {
            return null;
        }

        return new FusedLimits { Ppt = ppt, Tdc = tdc, Edc = edc };
    }

    private int? ReadFusedValue(uint command)
    {
        if (command == 0)
        {
            return null;
        }

        uint[] args = new uint[6];
        int hr = SendRsmu(command, ref args);
        if (hr != 0)
        {
            return null;
        }

        int value = (int)args[0];
        return value is > 0 and < 2000 ? value : null;
    }

    private int SendRsmu(uint command, ref uint[] args)
    {
        using PciBusLock pciLock = new();
        if (!pciLock.Acquired)
        {
            _lastRsmuHr = PciLockTimeoutHResult;
            return PciLockTimeoutHResult;
        }

        long[] input = new long[7];
        input[0] = command;
        for (int i = 0; i < 6; i++)
        {
            input[i + 1] = args[i];
        }

        long[] output = new long[6];
        int hr = _pawnIo.ExecuteHr("ioctl_send_smu_command", input, 7, output, 6, out uint returned);
        _lastRsmuHr = hr;
        if (hr == 0 && returned > 0)
        {
            for (int i = 0; i < Math.Min(6, (int)returned); i++)
            {
                args[i] = (uint)output[i];
            }
        }

        return hr;
    }

    private uint ReadUInt(string function)
    {
        long[] result = _pawnIo.Execute(function, Array.Empty<long>(), 1);
        return result.Length > 0 ? (uint)result[0] : 0;
    }

    private sealed record FmaxCommands(
        uint Get,
        uint Set,
        uint GetScalar,
        uint FusedPpt,
        uint FusedTdc,
        uint FusedEdc,
        string Name)
    {
        public static readonly FmaxCommands Desktop = new(
            DesktopGetFMax, DesktopSetFMax, DesktopGetPboScalar,
            DesktopFusedPpt, DesktopFusedTdc, DesktopFusedEdc,
            "RSMU · Zen 4 / Zen 5 桌面");

        public static readonly FmaxCommands Apu = new(
            ApuGetFMax, ApuSetFMax, ApuGetPboScalar,
            ApuFusedPpt, ApuFusedTdc, ApuFusedEdc,
            "RSMU · Zen 4 / Zen 5 APU");
    }
}

public readonly record struct SmuWriteResult(bool Success, uint ReadBackFMax, string Message);

internal sealed class PciBusLock : IDisposable
{
    private static readonly Mutex? PciMutex = CreateMutex();
    private readonly bool _acquired;

    public PciBusLock(int timeoutMs = 4000)
    {
        if (PciMutex is null)
        {
            _acquired = true;
            return;
        }

        try
        {
            _acquired = PciMutex.WaitOne(timeoutMs, false);
            if (!_acquired && Debugger.IsAttached)
            {
                Debug.WriteLine("PCI bus mutex timeout");
            }
        }
        catch (AbandonedMutexException)
        {
            _acquired = true;
        }
        catch (InvalidOperationException)
        {
            _acquired = false;
        }
    }

    public bool Acquired => _acquired;

    public void Dispose()
    {
        if (!_acquired || PciMutex is null)
        {
            return;
        }

        try
        {
            PciMutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not owned by this thread; ignore.
        }
    }

    private static Mutex? CreateMutex()
    {
        const string name = @"Global\Access_PCI";
        try
        {
            return Mutex.OpenExisting(name);
        }
        catch
        {
            try
            {
                return new Mutex(false, name);
            }
            catch
            {
                return null;
            }
        }
    }
}
