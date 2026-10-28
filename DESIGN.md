---
name: ZenVF
description: Silicon floorplan interface for AMD Zen 4 / Zen 5 PBO V/F measurement.
colors:
  primary: "#5BC0BE"
  primary-light: "#0E6B6B"
  silicon: "#0B0E11"
  ceramic: "#E9EDEF"
  substrate: "#12171C"
  substrate-light: "#F7F8F8"
  text: "#E6EDF2"
  text-light: "#10161B"
  muted: "#9AA8B3"
  muted-light: "#55636E"
  faint: "#7C8A95"
  faint-light: "#6B7883"
  border: "#2A343D"
  border-light: "#C9D2D7"
  copper: "#C87533"
  copper-light: "#9A4E12"
  pass: "#7FBF9A"
  pass-light: "#2F7A55"
  warning: "#D8A84A"
  warning-light: "#8A6100"
  fail: "#E0715B"
  fail-light: "#A63A2B"
typography:
  display:
    fontFamily: "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"
    fontSize: "22px"
    fontWeight: 600
    lineHeight: 1.2
  title:
    fontFamily: "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"
    fontSize: "14.5px"
    fontWeight: 600
    lineHeight: 1.35
  body:
    fontFamily: "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"
    fontSize: "11.5px"
    fontWeight: 400
    lineHeight: 1.45
  label:
    fontFamily: "Cascadia Mono, Consolas"
    fontSize: "9.5px"
    fontWeight: 600
  measurement:
    fontFamily: "Cascadia Mono, Consolas"
    fontSize: "13px"
    fontWeight: 600
  plate:
    fontFamily: "Bahnschrift, Segoe UI Semibold, Microsoft YaHei UI"
    fontSize: "9.5px"
    fontWeight: 600
rounded:
  none: "0px"
  hairline: "1px"
  plate: "2px"
  switch: "12px"
spacing:
  xs: "4px"
  sm: "8px"
  md: "14px"
  lg: "18px"
  xl: "24px"
components:
  button-primary:
    backgroundColor: "{colors.primary}"
    textColor: "#071012"
    rounded: "{rounded.plate}"
    padding: "16px 10px"
  button-secondary:
    backgroundColor: "transparent"
    textColor: "{colors.text}"
    rounded: "{rounded.plate}"
    padding: "14px 9px"
  button-ghost:
    backgroundColor: "transparent"
    textColor: "{colors.muted}"
    rounded: "{rounded.plate}"
    padding: "9px 6px"
  button-danger:
    backgroundColor: "#2A1714"
    textColor: "#F0A698"
    rounded: "{rounded.plate}"
    padding: "14px 9px"
  field:
    backgroundColor: "#0C1115"
    textColor: "{colors.text}"
    rounded: "{rounded.plate}"
    height: "34px"
---

# Design System: ZenVF

## Overview

**Creative North Star: "The Silicon Floorplan"**

ZenVF is a measurement bench that shows the CPU as a physical object. Each physical core is a block on a CCD row, each voltage is a copper bar on that block, and each chart is a figure plate taken from the same die. The interface is a working instrument, not a dashboard: the curve, the step and the core lead; labels and chrome stay quiet.

The world is silicon and ceramic, not glass. It uses two grounds for the two system themes: near-black silicon with copper and oxide teal in dark, and ceramic package grey with ink and oxide teal in light. There are no cards nested inside cards, no rounded dashboard shells, and no decorative gradients. Depth comes from hairlines, substrate layers, and one offset shadow on the chart hover card.

The signature is the core floorplan strip under the chart. It is functional, not ornamental: every block carries a real core number and the measured voltage of the current step, the copper bar is its relative voltage, and the hottest core is outlined in copper. If the user leaves after one viewport, they should remember the die map, not a color scheme.

**Key Characteristics:**
- Silicon / ceramic grounds with copper voltage and oxide-teal measurement.
- Hairline-ruled sections; no nested cards, no rounded dashboard shells.
- Monospaced measurements with tabular figures; sans prose; Bahnschrift only for plate codes.
- One signature move: the per-core floorplan.
- Familiar Windows controls, standard tabs, standard window chrome.

## Colors

A restrained technical palette: two neutral grounds, one user-selectable accent, copper reserved for voltage, and three state inks.

### Primary
- **Oxide Teal** (#5BC0BE dark / #0E6B6B light): the default accent and the measurement ink. Used for primary actions, active tabs, focus, selected CCDs, and the first chart series. The user may replace it from the appearance window; copper and state colors do not move.
- **Accent Soft** (12% accent over the substrate): selected surface tint, never a full-card fill.

### Secondary
- **Copper** (#C87533 dark / #9A4E12 light): voltage and heat. Used for the voltage bar in the floorplan, the highest-voltage core outline, and the CCD1 series. It is not a decoration or a second brand accent.

### Tertiary
- **Pass Green** (#7FBF9A / #2F7A55): restore success, safe state, elevated badge.
- **Warning Amber** (#D8A84A / #8A6100): PBO on, driver warning, demo state.
- **Oxide Red** (#E0715B / #A63A2B): validation errors, stop action, failed restore.

### Neutral
- **Silicon** (#0B0E11): dark window ground.
- **Substrate** (#12171C): dark rail and plates.
- **Ceramic** (#E9EDEF): light window ground.
- **Ceramic Plate** (#F7F8F8): light rail and plates.
- **Text** (#E6EDF2 / #10161B): primary readings and headings.
- **Muted** (#9AA8B3 / #55636E): secondary labels and chart axes.
- **Faint** (#7C8A95 / #6B7883): metadata, plate codes, disabled hints.
- **Border** (#2A343D / #C9D2D7): hairlines and plate outlines.

### Named Rules
**The Copper Is Voltage Rule.** Copper appears only where a voltage is being encoded: floorplan bars, the hottest core, and the voltage series. It never becomes a generic hover or decorative accent.

**The Accent Does Work Rule.** The accent marks action, selection and focus. It is never used to tint a whole section or to draw attention to a label.

## Typography

**Display Font:** Segoe UI Variable Text (with Segoe UI, Microsoft YaHei UI)
**Body Font:** Segoe UI Variable Text (with Segoe UI, Microsoft YaHei UI)
**Measurement Font:** Cascadia Mono (with Consolas)
**Plate Code Font:** Bahnschrift (with Segoe UI Semibold)

**Character:** One UI family keeps the Chinese interface stable; Cascadia Mono is reserved for real measurements, not for the costume of "technical". Bahnschrift appears only on figure/plate codes and the chart axis titles.

### Hierarchy
- **Display** (600, 22 px, 1.2): window headings and the appearance title.
- **Headline** (600, 17–20 px): the processor name and primary section titles.
- **Title** (600, 14.5 px): section headings such as "最高频率接管" and "扫描范围".
- **Body** (400, 11.5–12.5 px): hints, log messages, descriptions.
- **Label** (600, 9.5 px, mono): field labels, plate metadata, status bar.
- **Measurement** (600, 9–19 px, mono): voltages, frequencies, percentages, core numbers, tick labels.
- **Plate Code** (600, 9.5 px): figure codes and axis titles.

### Named Rules
**The Measurement Rule.** If a value is measured or set in MHz, V, W, °C, or a core/CCD index, it is set in Cascadia Mono. Prose, buttons and headings never are.

**The No-Eyebrow Rule.** A heading carries itself; no small all-caps kicker sits above it.

## Layout

The main window is a fixed workbench. A 54 px die-label bar, a 360 px conditions rail, a flexible chart plate, and a 178 px measurement tape. The rail is ruled into processor, FMax takeover and sweep sections with a pinned run block at its foot. The chart plate is a figure: header, tabs, plot, then the core floorplan strip. Spacing is tight inside groups (4–8 px) and generous between sections (14–18 px, with a hairline rule). The window minimum is 1180 × 760; the desktop capture is 1500 × 940.

The appearance window and the per-core voltage window use the same grounds and hairlines. The former is 560 × 700 with three mode plates; the latter is 1120 × 720 with a frequency header, a plate chart and a footer readout.

## Elevation & Depth

Depth is tonal and linear. Plates sit on a darker or lighter ground with a 1 px border; the rail is a separate substrate layer; the floorplan uses an inset ground. There is one shadow: the chart hover card, with a 3 px offset and 16 px blur at 34% black. Buttons and tabs are flat at rest; state is carried by fill, border and focus ring, not by lifting.

### Shadow Vocabulary
- **Hover card** (`0 3px 16px rgba(0,0,0,0.34)`): only the chart hover readout.

### Named Rules
**The Flat-By-Default Rule.** Surfaces are flat. A shadow appears only on the floating chart hover card.

## Shapes

The form language is rectilinear. Plates, inputs, buttons and tags use a 2 px radius or 1 px on small tags; the window itself is square. The only circles are status dots, accent swatches and the switch track. Borders are 1 px hairlines; the floorplan blocks are 1 px outlined rectangles with a 3 px copper bar at the foot. There are no large rounded cards, no pill-shaped containers, and no decorative blobs.

## Components

### Buttons
- **Shape:** 2 px radius, flat fill, 1 px border only on secondary/danger.
- **Primary:** oxide teal fill with near-black text (dark) or white text (light); full width in the run block; 38 px height.
- **Hover / Focus:** accent-hover fill on primary; surface-2 fill on secondary/ghost; a 1 px accent focus ring offset 2 px on keyboard focus.
- **Secondary:** transparent with a 1 px border; used for the per-core test and PawnIO install.
- **Ghost:** transparent, muted text; used for chart actions, copy/clear and close.
- **Danger:** oxide-red soft fill and border; the stop button spans the two start rows.

### Tags / Chips
- **Style:** 1 px radius, chip fill, 1 px chip border; mono 8.5–9.5 px.
- **State:** selected CCDs use accent-soft fill and an accent border; the 3D V-Cache tag uses the same accent treatment.

### Cards / Containers
- **Corner Style:** 2 px.
- **Background:** substrate on silicon, ceramic plate on ceramic.
- **Shadow Strategy:** none; see Elevation.
- **Border:** 1 px border or divider.
- **Internal Padding:** 14–18 px for plates; 8–12 px for insets.

### Inputs / Fields
- **Style:** 1 px border, 2 px radius, input ground, mono 15 px for numeric values.
- **Focus:** accent border on keyboard focus; caret and selection use the accent.
- **Disabled:** 55% opacity; validation errors use oxide red text above the run block.

### Tabs
- **Style:** text tabs with a 2 px underline on the selected tab; no background chrome.
- **Default / Hover / Active:** muted → text → text with an accent underline.

### Core Floorplan (signature)
Each physical core is a 37 × 34 px block on its CCD row. The block shows `C0…Cn` and the measured voltage of the current step in Cascadia Mono. A 3 px copper bar at the foot is the row-relative voltage; the bar opacity rises with voltage. The highest-voltage core gets a copper border and copper-soft fill; a single-core target gets an accent border. The package, Tctl and average clock live to the right, separated by 1 px rules. With no data, every block reads `—`.

### Chart Plate
OxyPlot on a flat plate. Axis titles use Bahnschrift; tick labels use Cascadia Mono. The legend is inline at the top left (main) or outside above the plot (per-core). Grid lines are 1 px and low contrast; the plot border is a 1 px hairline. A hover readout is the only floating element, with a colored 7 px swatch, a title and a mono measurement line.

## Do's and Don'ts

### Do:
- **Do** keep copper for voltage and the accent for action/selection/focus.
- **Do** use Cascadia Mono for every measured value and core/CCD index.
- **Do** keep the floorplan in topology order and preserve core IDs across themes and runs.
- **Do** use hairlines and tonal layers instead of cards; keep 14–18 px between sections and 4–8 px inside a group.
- **Do** keep the window chrome and standard controls familiar; this is an Operate surface.
- **Do** show the restore state (FMax takeover, stop/exit restore) in the rail and the status line.

### Don't:
- **Don't** nest cards or rebuild the rail as a stack of rounded panels.
- **Don't** use gradient text, glass, glow, or colored left borders.
- **Don't** use Bahnschrift or large display type for UI labels, buttons or data.
- **Don't** put an eyebrow/kicker above a heading.
- **Don't** use the accent as a decorative tint across whole sections.
- **Don't** encode voltage with the user accent; copper is the voltage channel.
