# Settings overlay

Approved 2026-09-28: variant A from the design round
(<https://claude.ai/artifact/RRwWLDqmPYPanwbi6S9k6E>). The HTML there is reference for proportion and behaviour,
**not markup to copy**. Everything below is built from the existing Avalonia theme.

## What it is

One modal Settings card replaces the Connections card. It keeps the Connections card's behaviour: scrim, click
outside or Escape to close, and review shortcuts stay with the view underneath.

- Title bar: the plug button opens Settings on **Connections**; a new sliders button (`Icon.Sliders`, distinct from
  the theme button's sun) opens it on **Review**.
- Sections, listed down the left: **Connections**, **Review**, **Ignored & files**, **Cloud AI limits**, then under
  an "App" label, **Appearance**.
- Changes save as they are made, as the Connections switches already do. Nothing needs a restart.

## Hierarchy and emphasis

1. The section's content. Its title is `h3` with a `caption` line under it.
2. The one primary action: **Re-analyse #N**. It sits in a footer bar on an `Accent.Soft` ground, and appears only
   while a pull request is open and a review setting changed since that PR was analysed. Pressing it closes Settings,
   so the fresh analysis is what the reviewer sees next. The same prompt stays above the diff as a banner after the
   overlay closes (variant B's banner, as recommended).
3. Everything else is quiet: `ghost` nav rows, `subtle` controls, `caption tertiary` explanations.

Colour stays reserved for meaning. The accent marks the selected preset, the selected scope, override dots and the
primary action. Nothing else.

## Spacing rhythm

The same 4-based scale as the Connections view: 2 between a label and its caption, 8–12 inside a group, 18–20
between groups. Card: 760 wide (was 520), header padding 20,16. Nav column 184 wide on `Surface.Sunken`, with a
`Border.Subtle` rule. Content padding 24,20.

## Scope

Review and Ignored & files show a two-option switch in the section header: **All repositories** or the open
repository's name. It is shown only when a pull request is open; otherwise the sections edit the global defaults.

- Sensitivity is one layer or the other. A repository either inherits it or overrides it as a whole. While
  overriding, a dot, "Overrides All repositories (Balanced)" and a **Reset** link sit above the presets.
- Lists (ignored names, mechanical paths, skipped paths) add up: a repository's entries are applied on top of the
  global ones. Built-in entries are listed read-only.
- Global values live in `settings.json` under `review`. Repository overrides live in
  `{dataDir}/review/{owner}/{repo}.json`. Both stay local and are never committed to the repository.

## Components reused

| Need | Existing piece |
| --- | --- |
| Card, scrim, close button | `Border.card`, the ConnectionsOverlay pattern in `src/Lumen.App/Views/MainWindow.axaml` |
| Section list | `Button.row` / `Button.row.current` in `src/Lumen.App/Theme/Styles.axaml` |
| Switches | `ToggleSwitch.setting` |
| Labels | `TextBlock.h2/h3/body/caption/section/tertiary` |
| Primary and quiet actions | `Button.primary`, `Button.subtle`, `Button.link`, `Button.ghost` |
| Chips for list entries | `Border.pill` |
| Connections content | `src/Lumen.App/Views/ConnectionsView.axaml`, unchanged, hosted as a section |

## Components that are new

- **Segmented control** (`RadioButton.segment` inside `Border.segmented`). It picks scope, theme and motion. Tabs
  (`RadioButton.tab`) read as navigation, not as a value, and nothing else picks one of two or three values.
- **Preset card** (`RadioButton.preset`). The Quiet / Balanced / Thorough choice. Each option needs a sentence
  explaining it, which a segmented control can't hold.
- **Stepper** (`NumericUpDown.stepper`). Styled to the control radius and `Surface.Sunken`, with a monospace,
  tabular value. It is used for the Advanced numbers and the Cloud AI limits.

## Choices rejected

- **Tabs in today's 520 card (variant B).** Five sections plus Advanced numbers don't fit in 520, and tabs don't
  scale past five.
- **A side sheet with a global-vs-repo table (variant C).** It shows inheritance most clearly, but it doubles every
  row. It is a better fit for a later read-only "what differs" summary.
- **Automatic re-analysis on change.** A re-run can start JEV calls and investigations, and a few quick edits
  shouldn't each trigger one.
- **Editing the JEV thresholds.** They calibrate the model, not the user's taste.
- **Exposing the detector's candidate pre-filter.** It is derived as agreement − 15 points (minimum 50%), so it can
  never sit above the decision threshold and silently hide everything.
- **Committed `.lumen/` repository files.** Deferred. Settings stay local for now.
