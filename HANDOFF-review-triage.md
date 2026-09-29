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
