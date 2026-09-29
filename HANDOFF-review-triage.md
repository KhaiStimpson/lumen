# Handoff — review-triage

Branch: `integration/review-triage` (from main). Session: Phase 1.

## Done
- Phase 1 task 1: `src/Lumen.Domain/Triage.cs` (ChangeClass, TriageTier, HunkTriage, TriageGroup) + `tests/Lumen.Analysis.Tests/TriageDomainTests.cs`.
- Phase 1 task 2: `src/Lumen.Analysis/TriagePipeline.cs` — `IHunkClassifier.Classify(HunkContext)` returns `HunkVerdict?` (Class, Tier, Reasons, optional `GroupClaim(Key,Title)`); pipeline = first proof wins, throwing classifier = no claim, fallback NewCode/Skim (added file) or BehaviourChange/WorthALook; `MechanicalFileClassifier` wraps file-level `MechanicalClassifier` -> Generated/Skip. Hunk spans cover changed lines only (not context). `TriagePipeline.Default` = mechanical file classifier only; add new classifiers to it in Phase 2. Tests in `TriagePipelineTests.cs`.

## Practical notes
- A dev `Lumen.exe` from `src/Lumen.App/bin` is running (user's) and locks build output. Do NOT kill it. Build/test with
  `scripts/run-gated.sh dotnet build Lumen.slnx -p:UseArtifactsOutput=true -p:ArtifactsPath=C:/Dev/lumen-artifacts` (same for test).
- Analyzer CA1707 forbids underscores in test method names: use PascalCase.
- The Domain has no test project; domain tests live in `tests/Lumen.Analysis.Tests`.
- Baseline: all test projects green.

## Do not re-litigate
Everything in the plan's "Already decided" list. Open question pending at end of Phase 1: golden labels for PR #58 need user confirmation.

## Task 3 notes (golden labels)
- `src/Lumen.Analysis/TriageEvaluation.cs`: `TriageLabel(Path, StartLine, EndLine, Tier, Note, Side "head"|"old", Draft)`, `TriageLabelSet.Load/Parse/ToJson` (web camelCase, string enums), `TriageEvaluation.Evaluate(labels, TriageResult)` -> report (recall, mechanical false positives, unmatched labels, lines per tier/class, `Describe()`).
- `HunkTriage` gained `ChangedLines` (added+removed), filled by the pipeline.
- Recall counts a Critical label as recalled if ANY overlapping hunk is Critical/WorthALook. FP = label Critical/WorthALook overlapping a Skip hunk of a mechanical class.
- Hand-made fixture: `tests/fixtures/triage/acme-shop-1/{diff.patch,labels.json}`; copied to test output via csproj `None` item.
- Tool quirk: bash heredocs with lone `'` in content can fail; use the Write tool for source files. Backslashes in python-in-bash get eaten; use Edit for csproj paths.

## Task 4 — ticked on the user's instruction "don't wait, keep going"
The user chose to proceed without confirming labels. Labels stay `"draft": true`; the plan's open question stays open for them to correct later. Loop continues in the same session across phase boundaries (user instruction).

### Original question (still open)
Work is committed: draft labels `tests/fixtures/triage/KhaiStimpson-andrew-crm-58/labels.json` (32 labels, all `"draft": true`), `tests/Lumen.Analysis.Tests/Support/DiffsJsonl.cs` (replay parser), and `TriageGoldenEvaluationTests` (opt-in `LUMEN_TRIAGE_EVAL=1`; run with `dotnet test tests/Lumen.Analysis.Tests --filter TriageGoldenEvaluationTests --logger "console;verbosity=detailed"`).

Baseline (pipeline with only the file-level classifier): critical recall 2/3, 0 mechanical false positives, 3,887 changed lines = 659 WorthALook (BehaviourChange) / 3,228 Skim (NewCode) / 0 Critical / 0 Skip. The one miss is EnrichmentScheduler.cs (a NEW file, so it defaults to NewCode/Skim) — expected; Phase 5 risk signals must lift it.

**Question for the user (the plan requires an answer before ticking):** please confirm or correct the three drafted CRITICAL labels, and say if any WorthALook/Skim label should be Critical:
1. `Services/Ai/AiSourceAuthorization.cs` 74-83 — new ExternalKnowledge branch in the AI-source authorization gate.
2. `Services/CatchMeUpService.cs` 515-531 — untrusted external enrichment text enters the AI prompt context.
3. `Services/Enrichment/EnrichmentScheduler.cs` 46-50 — `IgnoreQueryFilters` across all tenants in the scheduled refresh.
Other candidates I rated WorthALook: opt-in consent toggle (Settings/Index.cshtml.cs 108-117), PeopleDataLabsProvider sending contact email to a paid third party (30-45), ImportService auto-enrich fan-out (105-116), DataProtection key handling (EnrichmentSettingsService 80-110).
After the answer: set `"draft": false` on confirmed labels (edit the tiers as corrected), tick task 4, then Phase 1 is done and `scripts/phase-boundary.sh` will report the boundary -> start Phase 2 on fresh context.

## Session continues past the Phase 1 boundary (user: "just continue through the full plan", unsupervised; PR at the end)

## Decisions made unsupervised (copy into the PR body)
1. Golden labels for #58 accepted as drafted (still `"draft": true`) — user to confirm later.
2. Proof method: every C# proof applies ONE hunk to the full base file (`HunkApplier.ApplyToBase`) and compares full-file Roslyn fingerprints (`CodeFingerprint`: tokens / normalised comments / directives+disabled text). Hunk fragments alone are unsafe (string/comment state). Needs `TriageSources` (base+head texts); without sources nothing is proven.
3. Non-C# files are never classified mechanical (YAML/Python/Markdown whitespace can be significant; CRLF matters to shell scripts).
4. Any parse error in either side → no proof. Directive (`#if`, `#region`, `#pragma`) changes are never mechanical.

## Phase 2 notes
- Roslyn triage code lives in `src/Lumen.Roslyn/Triage/`; `RoslynTriage.CreatePipeline()` is the full pipeline (add each new classifier to `RoslynTriage.Classifiers()`). `TriagePipeline.Default` (Analysis) stays file-rules-only.
- Tests: `tests/Lumen.Analysis.Tests/Triage/`; `Support/TriageHarness.cs` builds minimal LCS diffs from base/head texts (`TriageHarness.Single(base, head)`, `Run(new TestFile(path, base, head), ...)`).
- Gate script (scratchpad, not committed): builds+tests with artifacts output.
- Shell gotcha: never put `'\r'` through bash/sed/python -c — it becomes a literal CR. Use Write/Edit tools.
5. Imports-only covers plain `using Ns;` only. Alias, `using static`, `global using` changes rebind names → not mechanical.
6. Worked directly on `integration/review-triage` rather than per-task ticket branches (single unattended session, one commit per task keeps history reviewable).
7. Pure rename is Skip only for names that do not leak: locals, lambda/local-function params, params of private non-partial methods, private fields/methods/properties/events, and types (any accessibility — type names can reach logger categories or EF tables without a DbSet; judged acceptable). Public/protected/internal members, public-method params, record positional params and enum members are NOT pure renames (serialisation, model binding, named args) — they stay reviewable; Ripple may group their call sites.
8. Rename safety is judged PR-wide: declaration must be found in a changed file's base; the old name must be gone from this file's head (all changed heads for types); the new name must be absent from this file's base (all bases for types). Declared outside the PR → not a rename.
- Harness note: `TriageHarness.Run` asserts every hunk applies to its base, so "not mechanical" tests cannot pass vacuously.
9. Moves: exact moves are Skip only when the member stays in the same type/namespace and (across files) the destination imports the same namespaces, and it is not an order-sensitive field (initialiser or struct field). Otherwise Move/Skim with the caveat in words. Enum members never move (reordering renumbers). No moves in files containing `#if`. A moved member must own its lines (nothing else on first/last line, no trailing comment running past its end) — this closed a real false-mechanical found by the formatting near-miss test.
10. An edited move pairs one deleted + one added member with the same kind and name and ≥50% identical lines (LCS on trimmed lines); only differing lines are BehaviourChange ("edited while moving `X`"). Pipeline gained `HunkVerdict.Parts` so a hunk can be reported in parts.
- A new file's `using`/`namespace` header next to a moved type is reported as NewCode, not part of the move.
11. Ripple call sites are Skim, never Skip: without a semantic model a call `x.M(...)` cannot be proven to target the changed `M`. Grouped for one-key acknowledgement. The declaration hunk is BehaviourChange/WorthALook with the change in words ("changes the signature of `Quote`: added parameter `discount`"). Parameter ripples need the method/ctor name declared exactly once across the PR's base files; `this`/`params` signatures are skipped.
12. Destructive migrations: beyond the plan's list, `RenameTable` and `DeleteData` are also Critical; "narrowing AlterColumn" = smaller maxLength (or newly bounded), any `type`≠`oldType`, or nullable→required. Only `Up` is scanned. Migrations are now loaded into `TriageSources` despite being mechanical; an added migration is rebuilt from its hunk when sources are absent (fixture replay).
13. Phase 2 evaluation used the local andrew-crm clone for sources (`LUMEN_TRIAGE_REPO`, `git show` only, no fetch) — the recorded fixture has diffs but no file texts. Added an opt-in safety sweep (`LUMEN_TRIAGE_SWEEP=<clone>`) that prints every proven-mechanical hunk of recent commits; 152 read by hand, 0 false positives. Results in the plan.
- PHASE 2 DONE. Next: Phase 3 (engine: run pipeline after snapshot, cache per head SHA, contracts, TriageReady event, fixture re-record, acknowledge RPC).

## Phase 3 notes
- `TriageResult` moved to Lumen.Domain (with `ITriageStore`). SQLite schema v3 adds `TriageResults` (PK Repository, PullRequest, HeadSha, Version; JSON Result). Storage tests that pinned the schema version now expect 3.
- `src/Lumen.Engine/Sessions/TriageRunner.cs`: cache version = `triage/1` + hash of the repo's mechanical path rules (bump `ClassifierVersion` whenever a classifier's proof changes). Failures log and return null; the review continues. Session keeps `Triage`.
- `PullRequestSessionManager` runs triage after publishing the snapshot, before detectors (progress stage "triage").
14. Wire contract: proto enum zero values are the conservative ones (`CHANGE_CLASS_BEHAVIOUR_CHANGE`, `TRIAGE_TIER_WORTH_A_LOOK`) so an unset value is never mechanical/Skip. Per-file tier counts (`ChangedFileSummary.tier_lines`, changed lines per tier) are empty in the snapshot (which stays fast) and filled in the files `TriageReady` re-sends; the client takes `tier_lines` by path. Groups on the wire carry counts, not members (members = hunks with that `group_id`).
- `ChangeClasses.IsMechanical()` (Domain) is the one definition of mechanical classes (Ripple counts).
15. Summary wording: "proven mechanical" = mechanical class AND Skip; mechanical classes above Skip (ripple call sites, cross-type moves) read "mechanical but worth a skim" (proto `likely_mechanical_lines`). Zero parts are omitted; exact numbers with thousands separators (`TriageSummaryText` in Analysis).
16. The #58 fixture was NOT re-recorded live (needs GitHub + the user's go-ahead). Instead `FixtureTriageRecorder` (opt-in: `LUMEN_RECORD_TRIAGE=<fixture dir>`, `LUMEN_TRIAGE_REPO=<local clone>`) rebuilds the snapshot from local git with the engine's exact diff flags, runs `TriageRunner`, and splices `TriageReady` in after the snapshot; every other recorded event is untouched. Result: "19,743 lines changed: 15,864 proven mechanical, 3,228 new code, 651 changing existing behaviour" (the 14 generated/migration files; their only drops are in `Down`, correctly not Critical). `FixtureReviewSource`/`GrpcLikeReviewSource` needed no code change — they replay the stream; the app VM now keeps `Triage`.
17. Triage group ids are now stable: `TriagePipeline.IdFor(claimKey)` = "g" + 12 hex of SHA-256 of the claim key (cache version bumped to `triage/2`). Acknowledgements are `ReviewInteractions` rows on id `triage-group:<groupId>` with new `ReviewAction.Acknowledged/Unacknowledged` (latest wins, per PR across heads — like dismissals). RPC `SetTriageGroupAcknowledged`; event `TriageGroupAcknowledged`; `TriageReady.groups[].acknowledged` reflects stored state. App: `IReviewSource.SetTriageGroupAcknowledgedAsync`, `PullRequestViewModel.SetTriageGroupAcknowledgedAsync` + `TriageGroupChanged` event.
- PHASE 3 DONE. #58 has no rename/move/ripple groups; Phase 4 may want a second fixture with groups for screenshots (andrew-crm commits 85e0db9e, cc4e8ef9, d59a426f have renames/moves).

## Phase 4 notes
18. Review plan layout: picked **variant A (plan in the right pane)** unsupervised; record in `docs/design/review-plan.md`, mockup `docs/design/review-plan-variants.html` (published https://claude.ai/artifact/UDp6ymWhcbNFdwDWPBTnC8). HTML is reference for proportion, NOT markup to copy. User to confirm in PR.
- Build/test: no change. Screenshots: use /flow:eyes (or the app's DevCapture.cs) at 1280x800 + 1920x1080 light/dark into docs/screenshots/review-triage/.
19. Review points go to the folded Consistency section when they are peer-pattern notes (`Type == PatternDeviation`) — done here in Phase 4 because the pane needs it; Phase 6 task 1 adds the wire tier + the Settings switch. A point's tier = tier of the hunk its anchor sits in (else the file's most demanding hunk; else Worth a look). Hunks covered by no point/group fold into one "N (more) changes in M files" row per tier. Header counts "places to review" in Critical + Worth a look.
20. Second UI fixture `tests/fixtures/andrew-crm-refactor` = andrew-crm commit 85e0db9e run through the real engine offline (`LocalFixtureRecorder`: fake GitHub, `git archive` tree, `git show` sources; opt-in `LUMEN_RECORD_LOCAL`, `LUMEN_TRIAGE_REPO`, `LUMEN_LOCAL_COMMIT`, `LUMEN_LOCAL_PR`). It has rename/move groups; #58 has none. Labelled KhaiStimpson/andrew-crm#1001 (not a real PR number).
- Screenshots: `Lumen.exe --fixture <dir> --pr <ref> --theme Light|Dark --reduced-motion --capture <dir> --capture-script "size:1280x800,diff,expand:Skip"` (new DevCapture steps `size:WxH`, `expand:<SectionKey>`). Build output: C:/Dev/lumen-artifacts/bin/Lumen.App/debug/Lumen.exe.
- Known follow-up: a hunk renaming two names forms its own group separate from one-name groups (keys differ).
21. Open question "Skip tier: collapse or hide?" answered unsupervised: **neither moves nor hides** — Skip-only files stay in place in the tree, greyed (`TextBlock.name.mechanical` = Text.Tertiary), with "N lines mechanical" instead of +/− and as a tooltip; file-level generated files keep their existing folded list, now counted in lines. Critical files get `Icon.Correctness` in `Severity.High` (shape + colour). No fixture has Critical hunks yet, so the Critical mark is unit-tested but not in screenshots until Phase 5.
- Third fixture `tests/fixtures/andrew-crm-audit` (andrew-crm cc4e8ef9, labelled #1002): renamed-only files + migrations.
22. Diff folds: only hunks a proof put in **Skip** fold (mechanical-but-Skim hunks such as ripple call sites stay open). A fold is a `DiffFold` rendered through the review-card host (`fold:<path>#<old>:<new>` card id → `FoldBadgeView`): reason · N lines, ‹ n of m › through the group (tooltip = group title), Show/Hide. Expand state is per session (not persisted).
23. J/K walk plan stops: review points (not dismissed) and groups (acknowledged ones included), section order Critical → Worth a look → Skim → Skip → Consistency; "N more changes" rows are not stops. On #58 (points only) this equals the old order, so existing J/K tests hold. Header reads "Plan n of m". `A` toggles acknowledgement of the current group; does nothing on a review point.
- NOTE: this session overran its 250K context budget in Phase 4 (flow context backstop). Next session must start fresh.
24. Coverage = changed lines in Critical + Worth a look hunks where the file is ticked Viewed, the hunk's group is acknowledged, a review point in it was handled (not Visible), or the reviewer visited it (plan click or J/K). "You've covered N% of Critical and Worth a look" + meter in the plan; files header shows "N% covered" (Viewed count moves to its tooltip). Meter eases 320 ms (DoubleTransition on ScaleX), instant with reduced motion.
- PHASE 4 DONE. Next session: Phase 5 on fresh context (/flow:loop). The PR is open against main with Phases 1–4.
