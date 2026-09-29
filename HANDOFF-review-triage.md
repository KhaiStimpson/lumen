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
