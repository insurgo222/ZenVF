using System.Text.RegularExpressions;
using LibreHardwareMonitor.Hardware;
using ZenVF.Interop;
using ZenVF.Models;

namespace ZenVF.Services;

public interface ISensorService : IDisposable
{
    bool IsAvailable { get; }
    string? Error { get; }
    void Start();
    SensorSnapshot Sample();
}

public sealed class LhmSensorService : ISensorService
{
    private static readonly Regex CoreEffectiveClockRegex = new(@"^Core #(\d+) \(Effective\)$", RegexOptions.Compiled);
    private static readonly Regex CoreClockRegex = new(@"^Core #(\d+)$", RegexOptions.Compiled);
    private static readonly Regex CoreVidRegex = new(@"^Core #(\d+) VID$", RegexOptions.Compiled);
    private static readonly Regex CoreTempRegex = new(@"^Core #(\d+)$", RegexOptions.Compiled);

    private readonly CpuTopology _topology;
    private readonly SmuController? _smu;
    private readonly bool _correctSvi3Vid;
    private readonly object _sync = new();
    private Computer? _computer;
    private IHardware? _cpu;
    private bool _started;

    public LhmSensorService(CpuTopology topology, SmuController? smu = null, bool correctSvi3Vid = false)
    {
        _topology = topology;
        _smu = smu;
        _correctSvi3Vid = correctSvi3Vid;
    }

    public bool IsAvailable { get; private set; }
    public string? Error { get; private set; }

    public void Start()
    {
        lock (_sync)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            try
            {
                _computer = new Computer
                {
                    IsCpuEnabled = true,
                    IsMotherboardEnabled = false,
                    IsControllerEnabled = false,
                    IsGpuEnabled = false,
                    IsMemoryEnabled = false,
                    IsStorageEnabled = false,
                    IsNetworkEnabled = false
                };
                _computer.Open();
                _cpu = _computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
                if (_cpu is null)
                {
                    Error = "LibreHardwareMonitor 未找到 AMD CPU 传感器。";
                    IsAvailable = false;
                    return;
                }

                _cpu.Update();
                IsAvailable = _cpu.Sensors.Any(s => s.Value.HasValue);
                if (!IsAvailable)
                {
                    Error = "CPU 传感器尚未就绪（可能需要管理员权限或 PawnIO 驱动）。";
                }
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                IsAvailable = false;
            }
        }
    }

    public SensorSnapshot Sample()
    {
        lock (_sync)
        {
            if (!IsAvailable || _cpu is null)
            {
                return new SensorSnapshot();
            }

            try
            {
                _cpu.Update();
                List<ISensor> sensors = _cpu.Sensors.ToList();
                Dictionary<int, double> clocks = new();
                Dictionary<int, double> coreClocks = new();
                Dictionary<int, double> coreClockMaxes = new();
                Dictionary<int, double> volts = new();
                Dictionary<int, double> temps = new();

                foreach (ISensor sensor in sensors)
                {
                    if (sensor.Value is not float value)
                    {
                        continue;
                    }

                    Match match = CoreEffectiveClockRegex.Match(sensor.Name);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int clockId))
                    {
                        clocks[clockId] = value;
                        continue;
                    }

                    match = CoreClockRegex.Match(sensor.Name);
                    if (match.Success
                        && sensor.SensorType == SensorType.Clock
                        && int.TryParse(match.Groups[1].Value, out int coreClockId))
                    {
                        coreClocks[coreClockId] = value;
                        double maxValue = sensor.Max is float max
                            && SensorValueSanity.IsPlausibleClock(max)
                            ? max
                            : value;
                        coreClockMaxes[coreClockId] = Math.Max(value, maxValue);
                        continue;
                    }

                    match = CoreVidRegex.Match(sensor.Name);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int vidId))
                    {
                        double normalizedVoltage = SensorValueSanity.NormalizeVoltage(value);
                        if (_correctSvi3Vid && normalizedVoltage > 0)
                        {
                            // LHM 0.9.6 still applies the SVI2 1.55 - VID
                            // formula on Zen 4/5. These CPUs expose the raw
                            // SVI3 code, so undo LHM's subtraction (the result
                            // is VID * 0.00625, the same voltage HWiNFO shows).
                            normalizedVoltage = 1.55 - normalizedVoltage;
                        }

                        if (SensorValueSanity.IsPlausibleVoltage(normalizedVoltage))
                        {
                            volts[vidId] = normalizedVoltage;
                            continue;
                        }
                    }

                    match = CoreTempRegex.Match(sensor.Name);
                    if (match.Success && int.TryParse(match.Groups[1].Value, out int tempId) && sensor.SensorType == SensorType.Temperature)
                    {
                        temps[tempId] = value;
                    }
                }

                List<SensorCoreSample> cores = clocks.Keys
                    .Concat(coreClocks.Keys)
                    .Distinct()
                    .OrderBy(coreId => coreId)
                    .Select(coreId => new SensorCoreSample
                    {
                        CoreId = coreId,
                        CoreClock = coreClocks.TryGetValue(coreId, out double c) ? c : 0,
                        CoreClockMax = coreClockMaxes.TryGetValue(coreId, out double cm) ? cm : 0,
                        EffectiveClock = clocks.TryGetValue(coreId, out double e) ? e : 0,
                        Vid = volts.TryGetValue(coreId, out double v) ? v : 0,
                        Temperature = temps.TryGetValue(coreId, out double t) ? t : 0
                    })
                    .ToList();

                bool pmTableApplied = false;
                double vddcrVdd = 0;
                if (_smu is not null && cores.Count > 0)
                {
                    bool pmCoreVoltages = _smu.TryReadCoreVoltages(
                        cores.Count,
                        out double[] pmVoltages,
                        out double packageVoltage);
                    if (SensorValueSanity.IsPlausibleVoltage(packageVoltage))
                    {
                        vddcrVdd = packageVoltage;
                    }

                    if (pmCoreVoltages)
                    {
                        double[] valid = pmVoltages
                            .Where(SensorValueSanity.IsPlausibleVoltage)
                            .ToArray();
                        double pmFallback = valid.Length > 0 ? valid.Average() : 0;
                        double lhmMaxVoltage = cores
                            .Select(c => c.Vid)
                            .DefaultIfEmpty(0)
                            .Max();
                        bool pmLooksLikeBadLhmValues = _correctSvi3Vid
                            && lhmMaxVoltage >= 0.80
                            && pmFallback < lhmMaxVoltage - 0.20;
                        if (pmFallback > 0 && !pmLooksLikeBadLhmValues)
                        {
                            cores = cores.Select(c =>
                            {
                                int index = c.CoreId - 1;
                                double voltage = index >= 0 && index < pmVoltages.Length
                                    ? pmVoltages[index]
                                    : 0;
                                if (!SensorValueSanity.IsPlausibleVoltage(voltage))
                                {
                                    // A missing core entry is better represented by
                                    // the package average than by the known-bad LHM
                                    // Zen 4/5 per-core VID.
                                    voltage = pmFallback;
                                }

                                return new SensorCoreSample
                                {
                                    CoreId = c.CoreId,
                                    CoreClock = c.CoreClock,
                                    CoreClockMax = c.CoreClockMax,
                                    EffectiveClock = c.EffectiveClock,
                                    Vid = voltage,
                                    Temperature = c.Temperature
                                };
                            }).ToList();
                            pmTableApplied = true;
                        }
                    }
                }

                bool pmTableExpected = _smu?.HasPerCoreVoltageTable == true;
                double fallbackVoltage = SensorValueSanity.NormalizeVoltage(
                    GetSensorValue(sensors, "Core (SVI2 TFN)")
                    ?? GetSensorValue(sensors, "Core VIDs")
                    ?? 0);
                if (!SensorValueSanity.IsPlausibleVoltage(fallbackVoltage))
                {
                    fallbackVoltage = 0;
                }

                if (fallbackVoltage > 0)
                {
                    cores = cores.Select(c => c.Vid <= 0 ? new SensorCoreSample
                    {
                        CoreId = c.CoreId,
                        CoreClock = c.CoreClock,
                        CoreClockMax = c.CoreClockMax,
                        EffectiveClock = c.EffectiveClock,
                        Vid = fallbackVoltage,
                        Temperature = c.Temperature
                    } : c).ToList();
                }

                bool hasPerCoreVoltage = cores.Count > 0
                    && cores.All(c => SensorValueSanity.IsPlausibleVoltage(c.Vid));

                // LibreHardwareMonitor exposes "Core #N" sensors in ascending
                // physical-core order. Map that order onto the detected CCDs so
                // the per-core voltage window can color and group cores.
                Dictionary<int, int> ccdByCore = new();
                int coreCursor = 0;
                foreach (CcdInfo ccd in _topology.Ccds)
                {
                    for (int i = 0; i < ccd.PhysicalCoreCount && coreCursor < cores.Count; i++, coreCursor++)
                    {
                        ccdByCore[cores[coreCursor].CoreId] = ccd.Index;
                    }
                }

                cores = cores.Select(c => new SensorCoreSample
                {
                    CoreId = c.CoreId,
                    CcdIndex = ccdByCore.TryGetValue(c.CoreId, out int ccdIndex) ? ccdIndex : 0,
                    CoreClock = c.CoreClock,
                    CoreClockMax = c.CoreClockMax,
                    EffectiveClock = c.EffectiveClock,
                    Vid = c.Vid,
                    Temperature = c.Temperature
                }).ToList();

                List<CcdSensorSample> ccdSamples = new();
                foreach (CcdInfo ccd in _topology.Ccds)
                {
                    List<SensorCoreSample> group = cores.Where(c => c.CcdIndex == ccd.Index).ToList();
                    if (group.Count == 0)
                    {
                        continue;
                    }

                    SensorCoreSample loaded = group.Where(c => c.EffectiveClock > 0)
                        .OrderByDescending(c => c.EffectiveClock)
                        .FirstOrDefault() ?? group[0];
                    double averageVoltage = group.Where(c => c.Vid > 0.15).Select(c => c.Vid).DefaultIfEmpty(0).Average();
                    double ccdTemp = GetSensorValue(sensors, $"CCD{ccd.Index + 1} (Tdie)") ?? loaded.Temperature;
                    ccdSamples.Add(new CcdSensorSample
                    {
                        CcdIndex = ccd.Index,
                        CoreId = loaded.CoreId,
                        ClockMHz = loaded.EffectiveClock > 0 ? loaded.EffectiveClock : loaded.CoreClock,
                        Voltage = loaded.Vid > 0.15 ? loaded.Vid : averageVoltage,
                        Temperature = ccdTemp
                    });
                }

                return new SensorSnapshot
                {
                    Timestamp = DateTime.Now,
                    Cores = cores,
                    Ccds = ccdSamples,
                    Tctl = GetSensorValue(sensors, "Core (Tctl/Tdie)")
                        ?? GetSensorValue(sensors, "Core (Tdie)")
                        ?? GetSensorValue(sensors, "Core (Tctl)")
                        ?? 0,
                    PackagePower = GetSensorValue(sensors, "CPU PPT")
                        ?? GetSensorValue(sensors, "Package", SensorType.Power)
                        ?? 0,
                    PptLimit = GetSensorValue(sensors, "CPU PPT Limit") ?? 0,
                    TdcLimit = GetSensorValue(sensors, "TDC Limit") ?? 0,
                    EdcLimit = GetSensorValue(sensors, "EDC Limit") ?? 0,
                    AverageEffectiveClock = GetSensorValue(sensors, "Cores (Average Effective)") ?? 0,
                    BusSpeedMHz = GetSensorValue(sensors, "Bus Speed") ?? 0,
                    HasPerCoreVoltage = hasPerCoreVoltage,
                    VoltageSource = pmTableApplied
                        ? "SMU PM table"
                        : _correctSvi3Vid
                            ? "LHM Core VID（SVI3 修正）"
                            : pmTableExpected
                                ? "LHM Core VID（PM table 读取失败）"
                                : "LHM Core VID",
                    VddcrVdd = vddcrVdd,
                    VddcrVddSource = vddcrVdd > 0
                        ? "CPU VDDCR_VDD（SMU PM table SVI3 遥测）"
                        : pmTableExpected
                            ? "CPU VDDCR_VDD（PM table 读取失败）"
                            : "CPU VDDCR_VDD 不可用"
                };
            }
            catch (Exception ex)
            {
                Error = ex.Message;
                return new SensorSnapshot();
            }
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            try
            {
                _computer?.Close();
            }
            catch
            {
                // Ignore shutdown races with the driver.
            }

            _computer = null;
            _cpu = null;
            IsAvailable = false;
        }
    }

    private static double? GetSensorValue(IEnumerable<ISensor> sensors, string name, SensorType? type = null)
    {
        ISensor? sensor = sensors.FirstOrDefault(s =>
            string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase) &&
            (type is null || s.SensorType == type));
        return sensor?.Value is float value ? value : null;
    }
}
