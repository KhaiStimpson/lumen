# Review triage — plan

Status: not started · Branch: `integration/review-triage` · Written: 2026-09-30

## Goal

On a large PR, Lumen tells the reviewer where to spend attention. Every hunk is placed in one of four tiers
(**Critical / Worth a look / Skim / Skip**). Mechanical changes (formatting, renames, moves, ripples, imports) are
*proven* mechanical and can be cleared in one keypress. Risky behaviour changes are ranked by signals that
predict bugs, and convention notes never outrank them. Judged on the golden set: every hunk the user labelled
critical lands in Critical or Worth a look, and **zero** real behaviour changes are labelled mechanical.

## Why (the finding this plan answers)

The only source of review points today is `PeerPatternDetector` (`src/Lumen.Roslyn/PeerPatternDetector.cs`). It
reports "N of M peers do X and this class doesn't", so it answers *what is unusual*, not *what is risky*. That is
how "3 providers don't take `IConfiguration`" was surfaced even though the class would never use it. Nothing
ranks the diff itself. `MechanicalClassifier` works per file (generated/lock/migration) and cannot see renames,
moves or formatting inside ordinary files.

## Ground rules

The loop prompt points here, which is why the loop prompt stays short. Everything binding lives in
this section.

- **Build:** `scripts/run-gated.sh dotnet build Lumen.slnx`
- **Tests:** `scripts/run-gated.sh dotnet test Lumen.slnx` — tests are written with every task (the repo is
  test-first; 343 tests at the start). Classifiers and scorers get unit tests over small diffs in
  `tests/Lumen.Analysis.Tests/Support/TestDiffs.cs` style. Live/opt-in tests (`LUMEN_LIVE_*`, `LUMEN_GOLDEN_*`) are
  never run without the user's go-ahead.
- **Model:** `sonnet` for loop iterations. Phases 2 and 5 (Roslyn equivalence, risk signals) may use `opus` if a
  task stalls — say so in the handoff.
- **Context backstop:** `250000` — the safety net, not the trigger. Phases end sessions; this
  catches a runaway task. A session that trips it means a phase was sized wrong.
- **Branching:** create `integration/review-triage` from `main` on the first task. Ticket branches off it, merged
  back `--no-ff`. Nothing reaches `main` until the whole effort is reviewed. Never push or post to GitHub without
  an explicit go-ahead.
- **UI changes:** Lumen is an Avalonia desktop app, so there is no mobile width. Capture desktop screenshots at
  1280×800 (plus one at 1920×1080 for the Review plan pane) in light and dark into
  `docs/screenshots/review-triage/` with `/flow:eyes` before the commit. A green build is not done for anything visible.
- **Safety invariant — mechanical must be proven.** A hunk is Skip/mechanical only if a classifier *proves* it
  (token equality, consistent identifier map, identical normalised body). Any doubt, parse error or partial
  match → not mechanical. A false "mechanical" is the worst bug this effort can ship; tests must include
  near-miss cases (a rename that also changes one literal, a move that also changes one line).
- **Nothing new may block or break the deterministic slice** (the existing rule from `docs/plan.md`). Triage runs
  without JEV, agents or network; AI only adds on top and every failure falls back to deterministic output.
- **Reasons in words, not scores.** Every tier assignment carries its reasons ("removed null guard", "12 callers",
  "no test changed"). A numeric score may exist internally for ordering; the UI never shows it as confidence
  (TDD §14, §51).
- **Out of scope:** `MSBuildWorkspace`/semantic model (fan-in is estimated from syntax and the repository type
  index); non-C# analysis beyond whitespace-only detection; JEV question changes; Codex; command palette;
  posting anything to GitHub.
- **Already decided, do not re-litigate:**
  - Four tiers, named Critical / Worth a look / Skim / Skip — they read as instructions to a reviewer.
  - Peer-pattern findings stay, but move to a collapsed **Consistency** group that never outranks a risk finding —
    they are occasionally right, and the user can switch them off in Settings.
  - AI stays opt-in (`privacy.allowCodeToAgents`, `agents.enabled`), as today — code leaving the machine is the
    user's call.
  - Triage is computed per head SHA and cached, like investigations, so reopening a PR is free.
  - Destructive migration operations are pulled out of the collapsed migration files as Critical; the rest of a
    migration stays Skip.
- One task per iteration. Stop and ask rather than guess. Do not skip ahead.

## Phase 1 — Triage model and measurement

Define what a tier is and build the yardstick first, so every later phase is measured instead of eyeballed.

- [x] Domain types in `Lumen.Domain`: `ChangeClass` (Formatting, CommentsOnly, ImportsOnly, Rename, Move, Ripple,
      Generated, NewCode, BehaviourChange), `TriageTier` (Critical, WorthALook, Skim, Skip), `HunkTriage` (file,
      head/old line span, class, tier, reasons, optional group id) and `TriageGroup` (id, class, title such as
      "Renamed `Foo`→`Bar`", members). JSON round-trip tests.
- [x] `IHunkClassifier` seam in `Lumen.Analysis` and a `TriagePipeline` that runs classifiers in order, lets the
      first proof win, and defaults unclaimed hunks to NewCode (added file/type) or BehaviourChange. Existing
      file-level `MechanicalClassifier` results become Generated/Skip through it. Unit tests.
- [x] Golden label format `tests/fixtures/triage/<owner>-<repo>-<pr>/labels.json` (path, line span, expected tier,
      note) and a `TriageEvaluation` that reports critical recall, mechanical false positives, and lines per tier.
      Unit-tested on a hand-made fixture.
- [x] Draft labels for andrew-crm#58 from `tests/fixtures/andrew-crm-58/diffs.jsonl`, marked `"draft": true`, and
      an opt-in test (`LUMEN_TRIAGE_EVAL=1`) that prints the evaluation. **Stop and ask the user to confirm or
      correct the critical labels** before ticking.

## Phase 2 — Prove the mechanical changes

Layer 1: take the noise out with Roslyn proofs, each classifier independent and conservative.

- [x] Formatting-only: the member's token stream is identical ignoring trivia (whitespace, line breaks). Near-miss
      tests (one changed literal, reordered tokens).
- [x] Comments/docs-only and imports-only (`using` directives added/removed/reordered, nothing else changed).
- [ ] Pure rename: old and new syntax trees are equal under a single consistent identifier map; reports the map
      and becomes a `TriageGroup` across files. Near-miss tests (rename plus a changed literal; inconsistent map).
- [ ] Moved code: normalised member bodies removed in one place and added in another within the PR (same file or
      across files), grouped as "Moved `X` from A to B". An edited move is BehaviourChange on the edited lines only.
- [ ] Ripple: a signature change (added/removed/renamed parameter, renamed member) plus call-site edits that only
      apply that change, collapsed into one group; the signature change itself stays reviewable.
- [ ] Destructive migrations: parse `Migrations/*.cs` `Up` bodies for `DropColumn`, `DropTable`, `RenameColumn`,
      narrowing `AlterColumn`, `Sql(...)`; those lines become Critical ("drops column `Invoices.Total`"); the rest
      stays Skip.
- [ ] Run the evaluation on PR #58 (fixture replay, no network); record lines-per-class and false positives in the
      "Results" section below. Any mechanical false positive is fixed before ticking.

## Phase 3 — Triage through the engine

Compute triage in the engine, cache it, and stream it to the app.

- [ ] Engine runs `TriagePipeline` after the snapshot and before detectors; results cached per head SHA in SQLite
      (`SqliteReviewStore`, new table) with a store test.
- [ ] Contracts: `HunkTriage`, `TriageGroup`, `TriageSummary` messages and a `TriageReady` event on
      `WatchPullRequest`; `ChangedFileSummary` gains per-file tier counts. Mapping tests both directions.
- [ ] `TriageSummary` in words: "19,700 lines changed: 14,100 proven mechanical, 3,200 new code, 2,400 changing
      existing behaviour", with per-tier line counts.
- [ ] Re-record `tests/fixtures/andrew-crm-58/events.jsonl` so `--fixture` replay includes triage; `FixtureReviewSource`
      and `GrpcLikeReviewSource` updated; App tests green.
- [ ] Acknowledging a group persists like dismissals (`SetTriageGroupAcknowledged` RPC, stored per PR); engine test.

## Phase 4 — The Review plan in the app

Replace the flat review-point list with a tiered plan the reviewer works through.

- [ ] `/flow:design` pass for the Review plan pane (three variants against the mockups and `Theme/Tokens.axaml`);
      record the decision in `docs/design/review-plan.md`. **Stop for the user's pick** before ticking.
- [ ] Review plan pane: summary line, then Critical / Worth a look / Skim / Skip sections with groups and review
      points in their tier; Consistency collapsed at the bottom. View-model tests; screenshots.
- [ ] File tree heat: mechanical-only files greyed with a "N lines mechanical" count, files with Critical hunks
      marked. Screenshots.
- [ ] Diff: mechanical hunks collapsed behind a reason badge ("pure rename `Foo`→`Bar`", "formatting only"),
      expandable; group members link to each other. Screenshots.
- [ ] Keyboard: `A` acknowledges the focused group (all members), `J/K` walk the plan in tier order, acknowledged
      groups fade but stay reachable. Tests for navigation order.
- [ ] Progress line "You've covered N% of Critical and Worth a look" replacing file counts; motion per §25 with
      reduced-motion equivalent. Screenshots light and dark.

## Phase 5 — Rank what's left by risk

Layer 2: deterministic risk signals on BehaviourChange/NewCode hunks, with reasons in words.

- [ ] `RiskSignals` per hunk: modifies existing member vs new code; estimated fan-in (references to the changed
      member name across the repository type index / syntax scan, capped). Unit tests.
- [ ] Removed safeguards: a removed `if` guard with throw/return, `throw`, `ArgumentNullException.ThrowIfNull`-style
      validation, `await`, `using`, `lock`, transaction scope or `SaveChanges`. Near-miss tests (guard moved, not removed).
- [ ] Sensitive-area tags as data (settings-extensible, shown in Settings → Review): auth, money, persistence and
      transactions, concurrency (`lock`, `Task.Run`, `async void`, static mutable fields), catch widened or
      emptied, SQL built from strings, public contracts (DTO/record shapes, routes, `.proto`, JSON names), config defaults.
- [ ] Test coupling and complexity: behaviour changed with no test file touched for that type or folder;
      cyclomatic complexity delta per member.
- [ ] Git hotspots: bug-fix commit count per file from the checkout's history (`fix`/`bug`/`hotfix` in subjects,
      last 12 months), cached per repository.
- [ ] `RiskTierer` combines signals into Critical / Worth a look / Skim with the reasons list; tune thresholds on
      the golden set and record recall in "Results". Critical recall target: every user-labelled critical hunk in
      Critical or Worth a look.

## Phase 6 — Findings worth reading

Layer 4 plus risk cards: demote convention noise, teach from dismissals, and give risky hunks real review points.

- [ ] Review points carry a tier; peer-pattern points go to Consistency and rank below every risk point; Settings
      → Review gets a "Show consistency notes" switch (default on, collapsed).
- [ ] Missing-dependency gate: a "doesn't take `X`" finding is only reported if the class does the work peers use
      `X` for (calls the same members/purposes); otherwise suppressed with a reason. Regression test from the
      `IConfiguration` case on PR #58.
- [ ] Dismissal learning: dismissing with "Doesn't apply to this repository" mutes that convention in
      `review/{owner}/{repo}.json`; it appears under Settings → Ignored and can be restored. Tests.
- [ ] `RiskHunkDetector` + template explainer: Critical hunks with a removed safeguard or sensitive tag become
      review points (`CorrectnessRisk`, `SecurityRisk`, `BehaviourChange`) with "why it matters" and a suggested
      comment, anchored inside the diff.
- [ ] Re-run the golden evaluation and a before/after comparison on PR #58; record in "Results".

## Phase 7 — Targeted AI (opt-in)

Layer 3: spend agent time only on the few spots that matter, and give the reviewer a reading order.

- [ ] Correctness Investigator role (read-only, grounded like the pattern investigator) with question templates
      per risk reason, e.g. removed guard → "Can null reach `X` after this change? Cite the caller."
- [ ] Scheduler routes the top N Critical hunks (Settings → Cloud AI limits, default 8) to it; results merge via
      `EvidenceAggregator`; refuted stays visible, demoted.
- [ ] PR tour: one agent call returning an ordered reading plan over triage groups and critical hunks ("start with
      X, Y consumes it, the rest is plumbing"), schema-validated, every step grounded to a real path, cached per
      head SHA; deterministic fallback orders by tier and dependency.
- [ ] App: tour shown at the top of the Review plan, investigation evidence on risk cards (existing Examine →
      Evidence surface). Screenshots.
- [ ] Live check `LUMEN_LIVE_CLAUDE=1` on PR #58 — **needs the user's go-ahead** (uses their Claude quota); record
      results.

## Results

(Filled in by Phases 2, 5 and 6.)

## Open questions

- [ ] **Golden labels (blocking the end of Phase 1):** the user confirms/corrects the critical hunks on PR #58.
- [ ] **More golden PRs (non-blocking):** which recent PRs gave the useless "constructor not injected" reviews?
      Adding them to `tests/fixtures/triage/` makes the risk tuning in Phase 5 much more trustworthy.
- [ ] **Review plan layout (blocking the end of Phase 4, task 1):** the user picks from the design variants.
- [ ] **Skip tier (non-blocking):** collapse Skip files in the tree (default) or hide them entirely?
