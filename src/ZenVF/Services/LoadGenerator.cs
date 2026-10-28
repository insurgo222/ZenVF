using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using ZenVF.Models;

namespace ZenVF.Services;

/// <summary>
/// Starts one pinned 128-bit SSE2 stress thread per logical processor on the
/// selected CCDs. The workload deliberately stays on 128-bit SSE2 instead of
/// 256-bit AVX/AVX2: this is the all-core load regime used by Prime95's SSE
/// torture test, where every core is busy without AVX downclocking/power.
/// Single-core tests load both SMT threads of one physical core, so the core
/// is fully busy and per-core effective-clock sensors report the real clock.
/// </summary>
public sealed class LoadGenerator : IDisposable
{
    private readonly List<Thread> _threads = new();
    private readonly ConcurrentBag<int> _observedCpus = new();
    private volatile bool _running;
    private int _activeThreads;
    private string? _lastError;

    public string? LastError => _lastError;
    public int ActiveThreads => Volatile.Read(ref _activeThreads);

    /// <summary>
    /// Logical processors on which the current load threads actually ran.
    /// Used for diagnostics; affinity is set before the workload starts.
    /// </summary>
    public IReadOnlyList<int> ObservedProcessors => _observedCpus
        .Distinct()
        .OrderBy(cpu => cpu)
        .ToList();

    public IReadOnlyList<string> Start(CpuTopology topology, IEnumerable<int> ccdIndices)
    {
        Stop();
        List<string> warnings = new();
        _running = true;
        _lastError = null;

        HashSet<int> requested = ccdIndices.Distinct().OrderBy(i => i).ToHashSet();
        foreach (int ccdIndex in requested)
        {
            if (topology.Ccds.All(c => c.Index != ccdIndex))
            {
                warnings.Add($"CCD{ccdIndex} 不存在，已跳过。");
            }
        }

        HashSet<int> assignedCpus = new();
        foreach (CcdInfo ccd in topology.Ccds.Where(c => requested.Contains(c.Index)).OrderBy(c => c.Index))
        {
            List<int> cpus = ccd.LogicalProcessors.Distinct().OrderBy(i => i).ToList();
            if (cpus.Count == 0)
            {
                warnings.Add($"CCD{ccd.Index} 没有可用的逻辑处理器，无法启动 SSE2 负载。");
                continue;
            }

            foreach (int cpu in cpus)
            {
                if (!assignedCpus.Add(cpu))
                {
                    continue;
                }

                Thread thread = new(() => LoadLoop(cpu))
                {
                    IsBackground = true,
                    Name = $"ZenVF-SSE2-CCD{ccd.Index}-CPU{cpu}",
                    Priority = ThreadPriority.Normal
                };
                _threads.Add(thread);
                thread.Start();
            }
        }

        if (_threads.Count == 0)
        {
            _running = false;
            warnings.Add("没有启动任何 SSE2 负载线程。");
        }

        return warnings;
    }

    /// <summary>
    /// Starts one SSE2 thread per logical processor of the requested physical
    /// core (both SMT siblings on a normal desktop Ryzen). Loading the whole
    /// physical core is required for LHM's per-core effective clock, which is
    /// the average of the core's SMT threads; loading only one sibling makes
    /// that sensor report roughly half the actual core clock.
    /// </summary>
    public IReadOnlyList<string> StartSingleCore(PhysicalCoreRef core)
    {
        Stop();
        List<string> warnings = new();
        _running = true;
        _lastError = null;

        List<int> cpus = (core.LogicalProcessors ?? Array.Empty<int>())
            .Where(cpu => cpu >= 0)
            .Distinct()
            .OrderBy(cpu => cpu)
            .ToList();
        if (cpus.Count == 0 && core.LogicalProcessor >= 0)
        {
            cpus.Add(core.LogicalProcessor);
        }

        if (cpus.Count == 0)
        {
            _running = false;
            warnings.Add($"Core #{core.DisplayCoreId} 没有可用的逻辑处理器，无法启动单核负载。");
            return warnings;
        }

        foreach (int cpu in cpus)
        {
            if (cpu >= 64)
            {
                warnings.Add($"Core #{core.DisplayCoreId} 的逻辑处理器编号 {cpu} 超出单处理器组绑定范围，负载可能无法固定到该核心。");
            }

            int targetCpu = cpu;
            Thread thread = new(() => LoadLoop(targetCpu))
            {
                IsBackground = true,
                Name = $"ZenVF-SSE2-Single-CCD{core.CcdIndex}-Core{core.DisplayCoreId}-CPU{targetCpu}",
                Priority = ThreadPriority.Normal
            };
            _threads.Add(thread);
            thread.Start();
        }

        return warnings;
    }

    public void Stop()
    {
        _running = false;
        foreach (Thread thread in _threads)
        {
            try
            {
                thread.Join(2000);
            }
            catch
            {
                // Ignore join failures during shutdown.
            }
        }

        _threads.Clear();
        _observedCpus.Clear();
        Volatile.Write(ref _activeThreads, 0);
    }

    public void Dispose() => Stop();

    private void LoadLoop(int cpu)
    {
        try
        {
            Thread.BeginThreadAffinity();
            bool affinitySet = false;
            if (cpu is >= 0 and < 64)
            {
                UIntPtr mask = (UIntPtr)(1UL << cpu);
                UIntPtr previous = NativeMethods.SetThreadAffinityMask(NativeMethods.GetCurrentThread(), mask);
                affinitySet = previous != UIntPtr.Zero;
                if (!affinitySet)
                {
                    NativeMethods.GroupAffinity group = new()
                    {
                        Mask = mask,
                        Group = 0
                    };
                    affinitySet = NativeMethods.SetThreadGroupAffinity(
                        NativeMethods.GetCurrentThread(),
                        ref group,
                        out _);
                }
            }

            if (!affinitySet)
            {
                _lastError = $"无法把负载线程绑定到 CPU{cpu}。";
            }

            // Let a pending affinity migration take effect before recording
            // the logical processor for diagnostics.
            Thread.Sleep(1);
            _observedCpus.Add(NativeMethods.GetCurrentProcessorNumber());

            Interlocked.Increment(ref _activeThreads);
            if (Sse2.IsSupported)
            {
                RunSse2Loop();
            }
            else
            {
                RunScalarLoop();
            }
        }
        catch (Exception ex)
        {
            _lastError = ex.Message;
            Debug.WriteLine($"SSE2 load thread {cpu} failed: {ex}");
        }
        finally
        {
            Interlocked.Decrement(ref _activeThreads);
            Thread.EndThreadAffinity();
        }
    }

    private void RunSse2Loop()
    {
        // Twelve independent chains hide multiply latency and keep both 128-bit
        // FP pipes fed. The recurrence v = v * factor + bias settles near 1.0,
        // so the old per-iteration Max/clamp is unnecessary. That removes the
        // clamp ops from the hot loop while keeping the same SSE2-only load
        // regime; two sqrt chains add latency without turning the loop into a
        // single sqrt-throughput test.
        Vector128<float> factor = Vector128.Create(0.99999988f);
        Vector128<float> bias = Vector128.Create(0.00000012f);

        Vector128<float> v0 = Vector128.Create(1.01f, 1.02f, 1.03f, 1.04f);
        Vector128<float> v1 = Vector128.Create(1.05f, 1.06f, 1.07f, 1.08f);
        Vector128<float> v2 = Vector128.Create(1.09f, 1.10f, 1.11f, 1.12f);
        Vector128<float> v3 = Vector128.Create(1.13f, 1.14f, 1.15f, 1.16f);
        Vector128<float> v4 = Vector128.Create(1.17f, 1.18f, 1.19f, 1.20f);
        Vector128<float> v5 = Vector128.Create(1.21f, 1.22f, 1.23f, 1.24f);
        Vector128<float> v6 = Vector128.Create(1.25f, 1.26f, 1.27f, 1.28f);
        Vector128<float> v7 = Vector128.Create(1.29f, 1.30f, 1.31f, 1.32f);
        Vector128<float> v8 = Vector128.Create(1.33f, 1.34f, 1.35f, 1.36f);
        Vector128<float> v9 = Vector128.Create(1.37f, 1.38f, 1.39f, 1.40f);
        Vector128<float> v10 = Vector128.Create(1.41f, 1.42f, 1.43f, 1.44f);
        Vector128<float> v11 = Vector128.Create(1.45f, 1.46f, 1.47f, 1.48f);

        while (_running)
        {
            for (int i = 0; i < 512; i++)
            {
                v0 = Sse.Add(Sse.Multiply(v0, factor), bias);
                v1 = Sse.Add(Sse.Multiply(v1, factor), bias);
                v2 = Sse.Add(Sse.Multiply(v2, factor), bias);
                v3 = Sse.Add(Sse.Multiply(v3, factor), bias);
                v4 = Sse.Sqrt(Sse.Add(Sse.Multiply(v4, factor), bias));
                v5 = Sse.Add(Sse.Multiply(v5, factor), bias);
                v6 = Sse.Add(Sse.Multiply(v6, factor), bias);
                v7 = Sse.Add(Sse.Multiply(v7, factor), bias);
                v8 = Sse.Add(Sse.Multiply(v8, factor), bias);
                v9 = Sse.Add(Sse.Multiply(v9, factor), bias);
                v10 = Sse.Add(Sse.Multiply(v10, factor), bias);
                v11 = Sse.Sqrt(Sse.Add(Sse.Multiply(v11, factor), bias));
            }

            // Cross-couple the chains occasionally so the JIT cannot remove
            // any accumulator as dead code.
            v0 = Sse.Add(v0, Sse.Multiply(v1, bias));
            v2 = Sse.Add(v2, Sse.Multiply(v3, bias));
            v4 = Sse.Add(v4, Sse.Multiply(v5, bias));
            v6 = Sse.Add(v6, Sse.Multiply(v7, bias));
            v8 = Sse.Add(v8, Sse.Multiply(v9, bias));
            v10 = Sse.Add(v10, Sse.Multiply(v11, bias));
        }

        Vector128<float> sink = Sse.Add(Sse.Add(v0, v1), Sse.Add(v2, v3));
        sink = Sse.Add(sink, Sse.Add(Sse.Add(v4, v5), Sse.Add(v6, v7)));
        sink = Sse.Add(sink, Sse.Add(Sse.Add(v8, v9), Sse.Add(v10, v11)));
        GC.KeepAlive(sink);
    }

    private void RunScalarLoop()
    {
        double a = 1.01;
        double b = 1.02;
        double c = 1.03;
        double d = 1.04;
        while (_running)
        {
            for (int i = 0; i < 4096; i++)
            {
                a = (a * 0.9999999) + 0.0000001;
                b = (b * 1.0000001) + 0.0000001;
                c = Math.Sqrt((c * 0.9999999) + 0.0000001);
                d = Math.Sqrt((d * 1.0000001) + 0.0000001);
            }
        }

        GC.KeepAlive(a + b + c + d);
    }
}
