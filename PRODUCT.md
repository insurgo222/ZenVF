# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows

## Users

AMD Zen 4 / Zen 5 desktop owners who tune PBO and FMax by hand. They run Windows as administrator, already know V/F curves, CCDs, eCLK and SMU terminology, and use this tool during a tuning session on their own machine. They are comparing cores and steps, not browsing.

## Product Purpose

Sweep the CPU's maximum boost frequency in steps, load the selected cores with a built-in SSE2 workload, record measured effective frequency and voltage per core, and plot the resulting V/F curves. Success means the user can see where voltage rises, compare cores and CCDs, and leave with a curve, CSV or report without guessing whether the CPU was restored.

## Positioning

Reads and writes AMD RSMU directly through PawnIO with the bundled `RyzenSMU.bin`, without vendor overclocking software. Measures per-core V/F at every FMax step and keeps all-core and single-core sweeps side by side, with eCLK detection applied only where the frequency basis changes.

## Operating Context

A local, single-user Windows desktop application. The user elevates once, arms FMax takeover, picks a scan range, warm-up and sample time, then starts all-core or per-core testing and watches progress, telemetry and the runtime log. Runs can take tens of minutes; stopping, finishing or exiting must restore the original FMax. Exports are PNG, CSV and Markdown. Demo mode uses synthetic data and never touches hardware.

## Capabilities and Constraints

- All-core sweep across selected CCDs; per-core single-load sweep; per-core voltage view at one frequency step.
- Three chart tabs: highest-voltage core per CCD, per-core V/F, single-core V/F. Hover, wheel zoom, drag pan, PNG / CSV / Markdown export.
- Topology, 3D V-Cache, PBO state, FMax, PBO Scalar, PPT/TDC/EDC and eCLK detection are read from hardware; readings are labeled as measured or estimated.
- Temperature cutoff at 105 °C aborts the run and restores FMax.
- WPF on .NET 10, OxyPlot, LibreHardwareMonitor 0.9.6, PawnIO. All state stays on the machine.
- Chinese interface copy is the product's own language. Demo data is synthetic and must stay labeled.
- No invented benchmarks, customer counts, pricing or hardware claims.

## Brand Commitments

The name ZenVF and its Chinese operating vocabulary (“最高频率接管”, “全核测试”, “每核单独测试”, “每核电压”). Neutral toward AMD; the product is independent.

## Evidence on Hand

`README.md` documents every workflow and release change. `docs/*.png` are real captured states. Demo mode ships a 9950X3D dataset and an optional eCLK dataset. No external testimonials or benchmarks exist.

## Product Principles

1. The measurement leads. Chrome, labels and color never outrank the curve, the step and the core.
2. Restore first. Any state that writes FMax shows its armed state and its restore path at all times.
3. Compare without translation. All-core and single-core results use the same axes, units and core numbering.
4. Expert density over hand-holding. Keep the controls, telemetry and log in one working view; explain through placement, not paragraphs.
5. Local and honest. Every number is measured, derived, or labeled synthetic.

## Accessibility & Inclusion

Windows desktop conventions: keyboard focus, visible focus states, high-DPI scaling, and readable contrast in both light and dark system themes. No formal standard was specified.
