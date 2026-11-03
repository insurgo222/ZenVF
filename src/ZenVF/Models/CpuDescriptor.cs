using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace ZenVF.Models;

/// <summary>
/// Static description of the host processor. CPUID is used for family/model,
/// the registry brand string is used for the marketing name.
/// </summary>
public sealed class CpuDescriptor
{
    private static readonly Dictionary<string, int> StockFMaxTable = new(StringComparer.OrdinalIgnoreCase)
    {
        // Zen 5 - desktop (Granite Ridge)
        ["9950X3D"] = 5700,
        ["9950X3D2"] = 5700,
        ["9950X"] = 5700,
        ["9900X3D"] = 5500,
        ["9900X"] = 5600,
        ["9700X"] = 5500,
        ["9600X"] = 5400,
        // AMD's fused FMax is 5250 MHz, but the highest step the boost
        // algorithm can actually use on this part is 5225 MHz.
        ["9800X3D"] = 5225,

        // Zen 5 - APU (Strix Point / Krackan Point)
        ["9955HX3D"] = 5400,
        ["9955HX"] = 5400,
        ["9850HX"] = 5400,
        ["9755HX"] = 5300,
        ["370"] = 5100,
        ["365"] = 5000,
        ["360"] = 5000,
        ["350"] = 5000,

        // Zen 4 - desktop (Raphael)
        ["7950X3D"] = 5700,
        ["7950X"] = 5700,
        ["7900X3D"] = 5600,
        ["7900X"] = 5600,
        ["7900"] = 5400,
        ["7800X3D"] = 5000,
        ["7700X"] = 5400,
        ["7700"] = 5300,
        ["7600X"] = 5300,
        ["7600"] = 5100,
        ["7500F"] = 5000,

        // Zen 4 - APU (Phoenix / Hawk Point)
        ["8700G"] = 5100,
        ["8600G"] = 5000,
        ["8500G"] = 5000,
        ["8300G"] = 4900,
        ["8700GE"] = 5100,
        ["8600GE"] = 5000,
        ["8500GE"] = 5000,
        ["7945HX3D"] = 5400,
        ["7945HX"] = 5400,
        ["7845HX"] = 5200,
        ["7745HX"] = 5100,
        ["7645HX"] = 5000,
        ["8945HS"] = 5200,
        ["8845HS"] = 5100,
        ["8645HS"] = 5000
    };

    /// <summary>
    /// Nominal base clock used as the default lower end of a sweep. The value
    /// is informational; the user can still edit the range.
    /// </summary>
    private static readonly Dictionary<string, int> BaseClockTable = new(StringComparer.OrdinalIgnoreCase)
    {
        // Zen 5 - desktop
        ["9950X3D"] = 4300,
        ["9950X3D2"] = 4300,
        ["9950X"] = 4300,
        ["9900X3D"] = 4400,
        ["9900X"] = 4400,
        ["9700X"] = 3800,
        ["9600X"] = 3900,
        ["9800X3D"] = 4700,

        // Zen 4 - desktop
        ["7950X3D"] = 4200,
        ["7950X"] = 4500,
        ["7900X3D"] = 4400,
        ["7900X"] = 4700,
        ["7900"] = 3700,
        ["7800X3D"] = 4200,
        ["7700X"] = 4500,
        ["7700"] = 3800,
        ["7600X"] = 4700,
        ["7600"] = 3800,
        ["7500F"] = 3700
    };

    /// <summary>
    /// Some SKUs expose a fused FMax ceiling that is one 25 MHz step above
    /// the highest frequency Precision Boost can actually select. The 9800X3D
    /// is the verified case: fused FMax 5250, real max boost 5225. Do not
    /// apply this globally: the 7800X3D reports FMax 5050 with no extra step,
    /// and the 9950X3D exposes different per-CCD ceilings (5550 / 5750).
    /// </summary>
    private static readonly Dictionary<string, int> FMaxCeilingOffsetTable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["9800X3D"] = 25
    };

    public required string Name { get; init; }
    public required string Vendor { get; init; }
    public byte Family { get; init; }
    public byte Model { get; init; }
    public byte Stepping { get; init; }
    public bool IsAmd { get; init; }
    public bool IsZen4 { get; init; }
    public bool IsZen5 { get; init; }
    public bool IsApu { get; init; }
    public int PhysicalCores { get; init; }
    public int LogicalCores { get; init; }
    public int CcdCount { get; set; } = 1;
    public string ArchitectureLabel { get; init; } = "Unknown";
    public string Codename { get; init; } = "Unknown";
    public string? ModelToken { get; init; }
    /// <summary>Highest boost frequency the CPU can actually select, in MHz.</summary>
    public int? StockFMaxMHz { get; init; }
    /// <summary>Nominal base clock, used as the default sweep lower bound.</summary>
    public int? BaseClockMHz { get; init; }
    /// <summary>
    /// Difference between the SMU FMax ceiling and the highest actual boost
    /// step. 25 MHz for the 9800X3D, zero when not verified.
    /// </summary>
    public int FMaxCeilingOffsetMHz { get; init; }
    public int? StockFMaxCeilingMHz => StockFMaxMHz is int stock
        ? stock + FMaxCeilingOffsetMHz
        : null;

    /// <summary>
    /// True only for AMD family/model combinations whose RSMU FMax commands
    /// are known. Everything else may read sensors but must not touch the SMU.
    /// </summary>
    public bool IsSupported => IsAmd && (IsZen4 || IsZen5);

    /// <summary>
    /// Tjmax, where the CPU starts thermal throttling: 95 °C on Zen 4 / Zen 5,
    /// 89 °C on first-generation Zen 4 X3D parts (7800X3D / 7900X3D / 7950X3D).
    /// Used to recognise a temperature-limited step, not to abort.
    /// </summary>
    public double TjMaxC =>
        IsZen4 && Name.Contains("X3D", StringComparison.OrdinalIgnoreCase) ? 89.0 : 95.0;

    /// <summary>
    /// Tctl at which a sweep is aborted. Deliberately above Tjmax (user
    /// decision, 2026-11-03): the CPU regulates itself at Tjmax and non-X3D
    /// parts sit there under all-core load, so aborting at Tjmax would end
    /// normal runs. This is a backstop for a failed cooler.
    /// </summary>
    public double AbortTemperatureC => 105.0;

    public int ToActualFMax(uint ceilingFMax) =>
        Math.Max(0, (int)ceilingFMax - FMaxCeilingOffsetMHz);

    public uint ToCeilingFMax(int actualFMax) =>
        (uint)Math.Max(0, actualFMax + FMaxCeilingOffsetMHz);

    public static CpuDescriptor Detect()
    {
        CpuId.CpuIdResult id = CpuId.Read();
        // The brand string is never overridable: the model name selects the
        // stock FMax table and therefore the sweep's upper safety limit.
        string name = GetBrandString();
        if (string.IsNullOrWhiteSpace(name))
        {
            name = id.Brand.Trim();
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = "AMD Ryzen";
        }

        (bool zen4, bool zen5, bool apu, string codename) = MapArchitecture(id.Family, id.Model);
        string? token = ExtractModelToken(name);
        int? stock = token is not null && StockFMaxTable.TryGetValue(token, out int boost) ? boost : null;
        int? baseClock = token is not null && BaseClockTable.TryGetValue(token, out int baseMHz) ? baseMHz : null;
        int ceilingOffset = token is not null && FMaxCeilingOffsetTable.TryGetValue(token, out int offset) ? offset : 0;

        return new CpuDescriptor
        {
            Name = name,
            Vendor = id.Vendor,
            Family = id.Family,
            Model = id.Model,
            Stepping = id.Stepping,
            IsAmd = id.Vendor.Contains("AuthenticAMD", StringComparison.OrdinalIgnoreCase),
            IsZen4 = zen4,
            IsZen5 = zen5,
            IsApu = apu,
            PhysicalCores = Math.Max(1, GetPhysicalCoreCount()),
            LogicalCores = Environment.ProcessorCount,
            ArchitectureLabel = zen5 ? "Zen 5" : zen4 ? "Zen 4" : $"Family 0x{id.Family:X2}",
            Codename = codename,
            ModelToken = token,
            StockFMaxMHz = stock,
            BaseClockMHz = baseClock,
            FMaxCeilingOffsetMHz = ceilingOffset
        };
    }

    internal static (bool Zen4, bool Zen5, bool Apu, string Codename) MapArchitecture(byte family, byte model)
    {
        return (family, model) switch
        {
            (0x19, 0x61) => (true, false, false, "Raphael / Dragon Range"),
            (0x19, 0x74) => (true, false, true, "Phoenix"),
            (0x19, 0x75) => (true, false, true, "Hawk Point"),
            (0x19, 0x78) => (true, false, true, "Phoenix 2"),
            (0x1A, 0x44) => (false, true, false, "Granite Ridge"),
            (0x1A, 0x24) => (false, true, true, "Strix Point"),
            (0x1A, 0x60) => (false, true, true, "Krackan Point"),
            (0x1A, 0x08) => (false, true, false, "Shimada Peak"),
            // No wildcard for the rest of family 19h/1Ah: 19h also contains
            // Zen 3 (Vermeer, Cezanne, Rembrandt...), and RSMU command ids are
            // platform specific. An unknown model must never receive SMU writes.
            _ => (false, false, false, "Unknown")
        };
    }

    private static string GetBrandString()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static string? ExtractModelToken(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string upper = name.ToUpperInvariant();

        // Ryzen AI 300 series: "Ryzen AI 9 HX 370", "Ryzen AI 7 PRO 360".
        // The model number is three digits and the HX/PRO tier is a separate word.
        Match ai = Regex.Match(upper, @"\bAI\s+\d\s+(?:(?:HX|PRO)\s+)*(3\d{2})\b");
        if (ai.Success)
        {
            return ai.Groups[1].Value;
        }

        // Desktop / mobile four-digit models. HX3D must be listed so names
        // such as 9955HX3D / 7945HX3D are not cut off at "HX" by the trailing
        // word boundary; F covers parts such as 7500F.
        Match match = Regex.Match(upper, @"\b([789]\d{3}(?:X3D2|HX3D|X3D|HX|HS|GE|G|X|F|U)?)\b");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static int GetPhysicalCoreCount()
    {
        try
        {
            uint length = 0;
            NativeMethods.GetLogicalProcessorInformation(IntPtr.Zero, ref length);
            if (length == 0)
            {
                return Environment.ProcessorCount;
            }

            IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)length);
            try
            {
                if (!NativeMethods.GetLogicalProcessorInformation(buffer, ref length))
                {
                    return Environment.ProcessorCount;
                }

                int count = 0;
                int offset = 0;
                int recordSize = IntPtr.Size == 8 ? 32 : 28;
                while (offset + recordSize <= length)
                {
                    int relationship = System.Runtime.InteropServices.Marshal.ReadInt32(buffer, offset + IntPtr.Size);
                    if (relationship == 0) // RelationProcessorCore
                    {
                        count++;
                    }

                    offset += recordSize;
                }

                return count > 0 ? count : Environment.ProcessorCount;
            }
            finally
            {
                System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return Environment.ProcessorCount;
        }
    }
}
