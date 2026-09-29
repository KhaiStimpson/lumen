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
