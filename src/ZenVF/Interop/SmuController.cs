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
    private FmaxCommands? _commands;
    private bool _initialized;
    private bool _disposed;
    private uint _pmTableVersion;
    private int _pmTableSize;
    private int _perCoreVoltageBase = -1;
    private int _vddcrVddVoltageIndex = -1;
    private int _vddcrVddPeakVoltageIndex = -1;
    private int _svi3VddVoltageIndex = -1;

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
                ResolvePmTable();

                List<FmaxCommands> candidates = _cpu.IsApu
                    ? new[] { FmaxCommands.Apu, FmaxCommands.Desktop }.ToList()
                    : new[] { FmaxCommands.Desktop, FmaxCommands.Apu }.ToList();

                foreach (FmaxCommands candidate in candidates)
                {
                    if (TryReadFMax(candidate, out uint value) && value is >= 400 and <= 8000)
                    {
                        _commands = candidate;
                        LastKnownFMaxCeiling = value;
                        break;
                    }
                }

                if (_commands is null)
                {
                    InitializationError = "RSMU 未返回有效的 FMax。BIOS 可能锁定了 PBO，或该 CPU 不受支持。";
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

    /// <summary>Writes the raw SMU FMax ceiling. Callers must convert actual boost values first.</summary>
    public SmuWriteResult SetFMaxCeiling(uint ceilingFMax)
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
            current,
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

        // Older APUs expect selector 3 in arg0 for the GetBoostLimit command.
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
                // Granite Ridge (Ryzen 9000 desktop) PM table. The per-core
                // voltage block starts at index 317; the values are volts.
                // 0x00620205 is the table version reported by current AGESA
                // on the 9800X3D; it has the same 0x994 layout.
                //
                // Index 19 is the SVI3 VDDCR_VDD telemetry average, the same
                // package-rail value HWiNFO exposes as "CPU VDDCR_VDD Voltage".
                // Index 18 (peak) and index 49 (Vcore P1) are fallbacks because
                // table revisions have moved this block before.
                case 0x00620205:
                case 0x00621201:
                case 0x00621202:
                    _pmTableSize = 0x994;
                    _perCoreVoltageBase = 317;
                    _vddcrVddVoltageIndex = 19;
                    _vddcrVddPeakVoltageIndex = 18;
                    _svi3VddVoltageIndex = 49;
                    break;
                default:
                    _pmTableSize = 0;
                    _perCoreVoltageBase = -1;
                    _vddcrVddVoltageIndex = -1;
                    _vddcrVddPeakVoltageIndex = -1;
                    _svi3VddVoltageIndex = -1;
                    break;
            }
        }
        catch
        {
            _pmTableVersion = 0;
            _pmTableSize = 0;
            _perCoreVoltageBase = -1;
            _vddcrVddVoltageIndex = -1;
            _vddcrVddPeakVoltageIndex = -1;
            _svi3VddVoltageIndex = -1;
        }
    }

    /// <summary>
    /// Reads per-core VDDCR voltage from the SMU PM table. LibreHardwareMonitor
    /// 0.9.6 exposes "Core #N VID" on Zen 4/5 from MSR 0xC0010293, which is
    /// known to be wrong on these CPUs (it changes inversely with load). The PM
    /// table is the same source HWiNFO uses and gives usable per-core values.
    /// The package output is the SVI3 VDDCR_VDD telemetry average, used by the
    /// per-core isolated test instead of the unreliable Core VID.
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
                    package = ReadPmTableVoltage(table, _vddcrVddPeakVoltageIndex);
                }

                if (package <= 0)
                {
                    package = ReadPmTableVoltage(table, _svi3VddVoltageIndex);
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
                    double maxCoreVoltage = values.Where(SensorValueSanity.IsPlausibleVoltage)
                        .DefaultIfEmpty(0)
                        .Max();
                    if (package > 0 && maxCoreVoltage > 0 && maxCoreVoltage < package * 0.70)
                    {
                        // The per-core block at 317 is not the real core
                        // voltage on this table revision. Reject it so the
                        // caller can use the corrected SVI3 per-core fallback.
                        return false;
                    }

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
            return unchecked((int)0x80070005);
        }

        long[] input = new long[7];
        input[0] = command;
        for (int i = 0; i < 6; i++)
        {
            input[i + 1] = args[i];
        }

        long[] output = new long[6];
        int hr = _pawnIo.ExecuteHr("ioctl_send_smu_command", input, 7, output, 6, out uint returned);
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
