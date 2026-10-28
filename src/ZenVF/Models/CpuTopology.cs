using System.Runtime.InteropServices;

namespace ZenVF.Models;

public sealed class CcdInfo
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required ulong ProcessorMask { get; init; }
    public required int PhysicalCoreCount { get; init; }
    public required IReadOnlyList<int> LogicalProcessors { get; init; }
    /// <summary>
    /// Logical processors belonging to each physical core, ordered by the
    /// core's lowest logical processor number. Single-core tests load every
    /// entry for one physical core so both SMT threads are busy.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<int>> CoreLogicalProcessors { get; init; } =
        Array.Empty<IReadOnlyList<int>>();
    public bool Is3DCache { get; init; }
}

public sealed record PhysicalCoreRef(
    int CcdIndex,
    int CoreIndexInCcd,
    int DisplayCoreId,
    int LogicalProcessor,
    IReadOnlyList<int> LogicalProcessors);

public sealed class CpuTopology
{
    public required IReadOnlyList<CcdInfo> Ccds { get; init; }
    public int CcdCount => Ccds.Count;

    /// <summary>
    /// Returns the physical cores on the selected CCDs in the same order as
    /// LibreHardwareMonitor's <c>Core #N</c> sensors. The display core id is
    /// zero-based; <see cref="PhysicalCoreRef.LogicalProcessor"/> is the first
    /// logical processor of the core and <see cref="PhysicalCoreRef.LogicalProcessors"/>
    /// contains all of its SMT threads.
    /// </summary>
    public IReadOnlyList<PhysicalCoreRef> GetPhysicalCores(IEnumerable<int>? ccdIndices = null)
    {
        HashSet<int>? selected = ccdIndices?.ToHashSet();
        List<PhysicalCoreRef> result = new();
        int displayCoreId = 0;

        foreach (CcdInfo ccd in Ccds.OrderBy(c => c.Index))
        {
            IReadOnlyList<IReadOnlyList<int>> coreCpus = ccd.CoreLogicalProcessors.Count > 0
                ? ccd.CoreLogicalProcessors
                : SplitLogicalProcessors(ccd.LogicalProcessors, ccd.PhysicalCoreCount);

            for (int coreIndex = 0; coreIndex < ccd.PhysicalCoreCount; coreIndex++)
            {
                IReadOnlyList<int> logicalProcessors = coreIndex < coreCpus.Count
                    ? coreCpus[coreIndex]
                    : Array.Empty<int>();
                int logicalProcessor = logicalProcessors.Count > 0
                    ? logicalProcessors[0]
                    : -1;
                if (selected is null || selected.Contains(ccd.Index))
                {
                    result.Add(new PhysicalCoreRef(
                        ccd.Index,
                        coreIndex,
                        displayCoreId,
                        logicalProcessor,
                        logicalProcessors));
                }

                displayCoreId++;
            }
        }

        return result;
    }

    private static List<IReadOnlyList<int>> SplitLogicalProcessors(
        IReadOnlyList<int> logicalProcessors,
        int physicalCoreCount)
    {
        List<int> logical = logicalProcessors.Distinct().OrderBy(i => i).ToList();
        int coreCount = Math.Max(1, physicalCoreCount);
        int threadsPerCore = Math.Max(1, logical.Count / coreCount);
        List<IReadOnlyList<int>> result = new();

        for (int core = 0; core < coreCount; core++)
        {
            List<int> cpus = logical
                .Skip(core * threadsPerCore)
                .Take(threadsPerCore)
                .ToList();
            result.Add(cpus);
        }

        return result;
    }

    public static CpuTopology Detect(CpuDescriptor cpu)
    {
        List<ulong> coreMasks = new();
        List<(ulong Mask, int Level, int Type, int Size)> caches = new();

        try
        {
            uint length = 0;
            NativeMethods.GetLogicalProcessorInformation(IntPtr.Zero, ref length);
            if (length > 0)
            {
                IntPtr buffer = Marshal.AllocHGlobal((int)length);
                try
                {
                    if (NativeMethods.GetLogicalProcessorInformation(buffer, ref length))
                    {
                        int recordSize = IntPtr.Size == 8 ? 32 : 24;
                        int offset = 0;
                        while (offset + recordSize <= length)
                        {
                            ulong mask = (ulong)Marshal.ReadIntPtr(buffer, offset).ToInt64();
                            int relationship = Marshal.ReadInt32(buffer, offset + IntPtr.Size);
                            if (relationship == 0 && mask != 0) // RelationProcessorCore
                            {
                                coreMasks.Add(mask);
                            }
                            else if (relationship == 2 && mask != 0) // RelationCache
                            {
                                // CACHE_DESCRIPTOR starts right after the
                                // ULONG_PTR mask and the relationship enum.
                                int cacheOffset = offset + IntPtr.Size + sizeof(int);
                                int level = Marshal.ReadByte(buffer, cacheOffset);
                                int size = Marshal.ReadInt32(buffer, cacheOffset + 4);
                                int type = Marshal.ReadInt32(buffer, cacheOffset + 8);
                                caches.Add((mask, level, type, size));
                            }

                            offset += recordSize;
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
        }
        catch
        {
            // Fall through to a topology derived from the core count.
        }

        List<(ulong Mask, int Size)> l3Groups = caches
            .Where(c => c.Level == 3 && c.Type == 0)
            .GroupBy(c => c.Mask)
            .Select(g => (g.Key, g.Max(x => x.Size)))
            .OrderBy(x => LowestBit(x.Key))
            .ToList();

        if (coreMasks.Count == 0)
        {
            coreMasks.Add((1UL << Math.Min(Environment.ProcessorCount, 64)) - 1);
        }

        coreMasks.Sort((a, b) => LowestBit(a).CompareTo(LowestBit(b)));

        int expectedCcds = Math.Max(1, (coreMasks.Count + 7) / 8);
        if (l3Groups.Count is 0 or > 8 || l3Groups.Count > coreMasks.Count)
        {
            l3Groups = BuildFallbackGroups(coreMasks, expectedCcds);
        }

        List<CcdInfo> ccds = new();
        for (int i = 0; i < l3Groups.Count; i++)
        {
            ulong mask = l3Groups[i].Mask;
            List<int> logical = Bits(mask);
            List<ulong> ccdCoreMasks = coreMasks
                .Where(core => (core & mask) != 0)
                .OrderBy(LowestBit)
                .ToList();
            List<IReadOnlyList<int>> coreCpus = ccdCoreMasks
                .Select(core => (IReadOnlyList<int>)Bits(core & mask))
                .Where(cpus => cpus.Count > 0)
                .ToList();

            int physical = coreCpus.Count;
            if (physical == 0)
            {
                physical = Math.Max(1, coreMasks.Count / l3Groups.Count);
                coreCpus = SplitLogicalProcessors(logical, physical);
            }

            // The largest L3 slice on a dual-CCD X3D part is the 3D V-Cache CCD.
            // Some X3D parts (e.g. 9950X3D) carry the same large slice on both CCDs.
            // Single-CCD X3D parts (9800X3D, 7800X3D) also need the badge.
            int maxSize = l3Groups.Max(x => x.Size);
            bool uniqueMax = l3Groups.Count(x => x.Size == maxSize) == 1;
            bool bothMax = l3Groups.Count > 1 && l3Groups.All(x => x.Size == maxSize) &&
                           cpu.Name.Contains("X3D", StringComparison.OrdinalIgnoreCase);
            bool singleCcd3D = l3Groups.Count == 1 &&
                               cpu.Name.Contains("X3D", StringComparison.OrdinalIgnoreCase);
            bool is3D = singleCcd3D || (l3Groups.Count > 1 && l3Groups[i].Size >= maxSize && (uniqueMax || bothMax));

            ccds.Add(new CcdInfo
            {
                Index = i,
                Name = $"CCD{i}",
                ProcessorMask = mask,
                PhysicalCoreCount = physical,
                LogicalProcessors = logical,
                CoreLogicalProcessors = coreCpus,
                Is3DCache = is3D
            });
        }

        if (ccds.Count == 0)
        {
            int cores = coreMasks.Count;
            ulong allCoreMask = coreMasks.Aggregate(0UL, (a, b) => a | b);
            List<int> logical = Bits(allCoreMask);
            ccds.Add(new CcdInfo
            {
                Index = 0,
                Name = "CCD0",
                ProcessorMask = allCoreMask,
                PhysicalCoreCount = cores,
                LogicalProcessors = logical,
                CoreLogicalProcessors = SplitLogicalProcessors(logical, cores)
            });
        }

        return new CpuTopology { Ccds = ccds };
    }

    private static List<(ulong Mask, int Size)> BuildFallbackGroups(List<ulong> coreMasks, int ccdCount)
    {
        List<ulong> ordered = coreMasks.OrderBy(LowestBit).ToList();
        List<(ulong, int)> groups = new();
        int index = 0;
        for (int ccd = 0; ccd < ccdCount; ccd++)
        {
            int remainingCcds = ccdCount - ccd;
            int remainingCores = ordered.Count - index;
            int take = Math.Max(1, (int)Math.Ceiling(remainingCores / (double)remainingCcds));
            ulong mask = 0;
            for (int i = 0; i < take && index < ordered.Count; i++, index++)
            {
                mask |= ordered[index];
            }

            if (mask != 0)
            {
                groups.Add((mask, 0));
            }
        }

        return groups;
    }

    private static int LowestBit(ulong value)
    {
        if (value == 0)
        {
            return 64;
        }

        int bit = 0;
        while ((value & 1) == 0)
        {
            value >>= 1;
            bit++;
        }

        return bit;
    }

    private static List<int> Bits(ulong mask)
    {
        List<int> result = new();
        for (int i = 0; i < 64; i++)
        {
            if ((mask & (1UL << i)) != 0)
            {
                result.Add(i);
            }
        }

        return result;
    }
}
