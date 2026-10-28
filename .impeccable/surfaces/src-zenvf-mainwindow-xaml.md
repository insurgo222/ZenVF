---
version: 1
slug: "src-zenvf-mainwindow-xaml"
primary_target: "src/ZenVF/MainWindow.xaml"
related_targets: []
---

# ZenVF main window

Mode: Operate. Surface: the working V/F bench, its theme settings and the per-core voltage window.

## Direction contract

THESIS: A V/F bench rendered as a CPU die floorplan. Cores are functional blocks on silicon, the curve is the measurement taken across them, and the shell refuses the rounded-card dashboard.

OWN-WORLD: Silicon near-black ground, package-substrate panels, copper voltage, oxide-teal measurement, pass green and oxide red for state. Segoe UI Variable for UI, Cascadia Mono for every measured value, Bahnschrift for plate captions. Hairline rules, square corners, no nested cards. Signature: a floorplan telemetry strip where every physical core is a block carrying its measured voltage and the hottest core is outlined in copper.

STORY: The user sees the CPU as a physical object, locates each core spatially, reads the curve as evidence from those blocks, and can see that FMax is armed and will be restored.

FIRST VIEWPORT: A 56 px die-label title bar; a 360 px conditions rail with ruled sections for processor, FMax takeover and sweep; a chart plate filling the right with compact tabs, the plot, and the floorplan strip; the log below as a measurement tape. Start/stop remains pinned at the rail's foot.

FORM: code-led, desktop WPF. seed:8dd0c8075a569c7a

SIGNATURE: The floorplan strip. CCD rows of core blocks, each block's copper bar proportional to its voltage in the current step; the hottest core carries a copper outline and every block exposes core, voltage and clock on hover.

## Notes

- Theme follows Windows; both light and dark must read as the same die world.
- Keep every existing command, binding name and event handler so behavior is unchanged.
- Remove eyebrow labels and English duplicate captions; the layout explains.
