# Review plan pane

Chosen 2026-09-30: **variant A, the plan in the right pane**, from the design round
(<https://claude.ai/artifact/UDp6ymWhcbNFdwDWPBTnC8>, source `docs/design/review-plan-variants.html`). The user was
away and asked for a best-judgement pick. This choice is listed in the PR for them to confirm. The HTML is reference
for proportion and behaviour, **not markup to copy**. Everything below is built from the existing Avalonia theme.

## What it is

The right pane's flat **Review focus** list becomes the **Review plan**. The file tree (left) and the diff (centre)
keep their places, so the diff stays full width at 1280.

Top to bottom:

1. `TextBlock.section` "REVIEW PLAN".
2. **Summary line**, `body`: `TriageSummary.text` (e.g. "19,743 lines changed: 15,864 proven mechanical, 3,228 new
   code, 651 changing existing behaviour"). Under it, a 6-high **tier bar** of changed lines per tier. The widths come
   from `TriageSummary.tier_lines`, not from any score.
3. **Progress line**, `caption`: "You've covered N% of Critical and Worth a look", plus a 4-high meter
   (`Marker.Verified` on `Border.Subtle`). Phase 4's last task fills it. Until then the line reads "N items to review".
4. **Tier sections** Critical, Worth a look, Skim. Each is a header row with a tier pill and a line count on the
   right (`caption tertiary`, tabular). Below it sit the rows in that tier:
   - **Review points** keep today's row exactly: kind circle, title, location, severity pill.
   - **Groups** use the same `Button.row` grid. The leading 22×22 circle is `Surface.Sunken` with a `Border.Subtle`
     edge and `Icon.Generated`, the title is the group title, and the caption is "12 hunks · 40 lines · reason".
     The trailing slot holds a `Border.kbd` "A", which becomes a tick once the group is acknowledged. An acknowledged
     row fades (`Converters.DimIf`) but stays in place and reachable.
   - A **hunk not covered by a point or a group** is not listed one by one. Each tier ends with a quiet row: "7 more
     changes in 5 files", which opens the first of them.
5. **Skip**, folded: one `Button.row` with `Icon.ChevronRight`, "Skip · 15,864 lines in 14 files". The same
   expander pattern as today's "generated files" row in the left pane.
6. **Consistency**, folded: peer-pattern review points that Phase 6 moves out of risk tiers. It sits last and is
   never above a risk tier.
7. Precedent and Patterns keep their existing sections, below the plan. Keyboard hints stay at the bottom and add
   `A` "acknowledge".

## Hierarchy and emphasis

1. The current item (`Button.row.current`) and the Critical section.
2. The summary line. It is the only sentence-length text above the fold.
3. Everything else is quiet: tier headers are `h3`-weight 12.5, counts are `caption tertiary`, folded rows are
   `caption` secondary.

Colour stays reserved for meaning. The tier pills reuse the severity pills: Critical = `Border.pill.High`, Worth a
look = `Border.pill.Medium`, Skim = `Border.pill.Low`, and Skip = the plain `Border.pill`. No row is tinted by tier.

## Spacing rhythm

The pane's existing 4-based scale: 18 padding, 22 between the plan and the Precedent section, 14 between the
summary block and the first tier, 6 above each tier header, and rows keep `Margin="-8,0" Padding="10,9"`. At 1920 the
pane may be widened by the user (the pane widths already persist). Nothing in the plan assumes a width, and titles
wrap to at most two lines (`MaxLines="2"`).

## Components reused

| Need | Existing piece |
| --- | --- |
| Pane, sections | `Border.pane`, `TextBlock.section/h3/body/caption/tertiary` in `src/Lumen.App/Theme/Styles.axaml` |
| Rows, current row | `Button.row`, `Button.row.current` |
| Tier pills | `Border.pill.High/.Medium/.Low`, `Border.pill` |
| Key hints | `Border.kbd` |
| Fold/expand row | the generated-files expander in `src/Lumen.App/Views/PullRequestView.axaml` |
| Dimming acknowledged rows | `Converters.DimIf` in `src/Lumen.App/Views/Converters.cs` |
| Icons | `Icon.Generated`, `Icon.ChevronRight`, `Icon.ChevronDown` in `src/Lumen.App/Theme/Tokens.axaml` |

## Components that are new

- **Tier bar**: four `Border`s in a `Grid` with star widths from line counts. Nothing existing shows proportion, and
  the convention strength bar shows only one value.
- **Group row**: a `DataTemplate` for a new `TriageGroupViewModel`. It uses the same `Button.row` grid as a review
  point, so the list scans as one column.

## Choices rejected

- **B, a Plan tab in the left pane.** At 284 it is narrower than the right pane, and it would hide the file tree
  that the Phase 4 heat marks live in.
- **C, a plan board before the diff.** It gives the best overview, but it is a mode switch. J/K would have to leave
  the board, and the board and the diff could never be seen together.
- **Scores or confidence per item.** Tiers carry reasons in words (TDD §14, §51).
- **Hiding Skip entirely.** Proven-mechanical code stays one click away. Whether Skip files should also collapse in
  the file tree is the plan's open question, answered in the file-tree task.
- **Tinting rows by tier.** The pills already say the tier, and colour stays for severity and the one accent.
