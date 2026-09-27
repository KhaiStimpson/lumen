# Lumen — Vertical Slice Plan (TDD §57)

Source of truth for scope: [tdd.md](tdd.md). Mockups: `docs/*.png` (a strong starting point; deviate where it improves the product).

## Decisions (2026-09-27)

| Topic | Decision |
|---|---|
| Name / runtime | `Lumen.*` projects, .NET 10, Avalonia 12.1 + AvaloniaEdit 12 |
| Scope | §57 vertical slice only — no agents, no JEV |
| Process model | Separate engine daemon from day one; gRPC over a per-user named pipe (Windows) / Unix socket (§47) |
| GitHub auth | `gh auth token` behind `IGitHubTokenSource` (env `LUMEN_GITHUB_TOKEN` overrides); Device Flow drops in later |
| Test target | [KhaiStimpson/andrew-crm#58](https://github.com/KhaiStimpson/andrew-crm/pull/58) — 60 files, ~19.7k additions, 14 generated files |
| Theme | Mockup layout and palette; all colours are tokens in `Theme/Tokens.axaml`; light + dark |
| MVVM | CommunityToolkit.Mvvm |
| Detection | Deterministic, pluggable pipeline shaped so JEV/agents slot in without rework (below) |

## Detection shape (built to be swapped later)

```text
Detectors (IChangeDetector)          → Candidate { ChangeUnits, Type, Evidence[], AttentionSignals, Facts }
   PeerPatternDetector (Roslyn)          e.g. "5 of 7 classes that take HttpClient throw ProviderOperationException"
   MechanicalClassifier                  generated/migration/designer/lock files → collapsed, never analysed
        ↓
Attention policy (IAttentionPolicy)  → Surface | Suppress | Investigate  (+ severity, priority, reason)
   RuleBasedAttentionPolicy  (now)       peers ≥ 3, support ≥ 75% (70% with ≥ 5 supporting), lift ≥ 1.5 (2.5 for dependencies)
   JevAttentionPolicy        (Phase 3)   same interface; AttentionSignals is the compact JEV state (§10.1)
        ↓
Explanation (IReviewPointExplainer)  → Title / Summary / Why this matters / Suggested comment / SurfaceSpec
   PeerDeviationExplainer    (now)       templates
   Agent-backed explainer    (Phase 4)   same interface
        ↓
ReviewPoint (stable id, evidence with provenance, counter-evidence, EvidenceState in words)
```

## Phases

- [x] **0. Scaffold** — solution, projects per §53 (trimmed), shared build props (warnings as errors)
- [x] **1. Contracts + daemon** — `review_engine.proto` (Ping, WatchPullRequest stream with replay, GetFileDiff, GetSourceFile, SetReviewPointState, PostReviewComment); Kestrel on a named pipe; app spawns/attaches; engine exits with its parent; transparent reconnect/restart
- [x] **2. GitHub + snapshot** — REST client (PR, viewer, paginated review comments, post comment); immutable `PullRequestSnapshot`; last PR cached in SQLite for offline fallback (§55)
- [x] **3. Repo checkout** — blobless bare clone, `refs/pull/N/head` fetch, merge-base, detached worktree per head SHA, hardened paths/args
- [x] **4. App shell + diff** — three panes, compact file tree, AvaloniaEdit unified diff with TextMate highlighting, custom gutter, generated files collapsed
- [x] **5. Roslyn** — changed classes from diff ranges; peers from base (merge-base) code — see deviation 1
- [x] **6. Pattern deviation** — detector + rule policy + template explainer, streamed `ReviewPointAdded`; golden harness against PR #58
- [x] **7. Review UX** — inline cards (full at the anchor, compact at other locations), shape-coded gutter markers, Examine mode with side-by-side comparison, precedent/evidence/comment tabs, source viewer, J/K/Enter/D/C/P/E/Esc/Ctrl+K, dismissals persisted
- [x] **8. Comment** — posts a single-line review comment at the anchor on the head side; only on explicit action; engine rejects lines outside the diff
- [x] **9. Motion** — `IMotionService` with the §26 presets, spring scrolling, card contract/reveal, card→panel shared-element transition, marker arrival halo, reduced-motion equivalents (OS setting or `--reduced-motion`)

## Results on PR #58

Warm cache: diff visible in ~1.6 s, review markers in ~3.2 s (§44 targets: < 2 s, < 5 s). Cold (first clone): ~7.6 s.

Three review points, 14 suppressed candidates, generated EF migrations collapsed:

1. **Medium** — 5 new enrichment providers catch exceptions broadly instead of throwing `ProviderOperationException`, unlike 5 of 7 `HttpClient`-based classes (exceptions: `AzureAiProvider`, `AzureOpenAiTranscriptionService`).
2. **Low** — 3 providers don't take `IConfiguration` (7 of 7 peers do).
3. **Low** — `EnrichmentScheduler` doesn't call `SaveChangesAsync` (13 of 16 `IBackgroundJobClient` users do).

## Deviations from the TDD (and why)

1. **Syntax-level Roslyn instead of `MSBuildWorkspace` (§7.1).** The detector needs constructor dependencies, base types,
   attributes, throw/catch shapes and field-receiver calls — all available from syntax, on any checkout, without a
   restore or build, in ~1 s for 190 files, and identically for base and head versions of a file. Repo-declared type
   names separate repository concepts from framework plumbing. A semantic model (and `MSBuildWorkspace`) becomes
   worthwhile for call graphs and data flow (correctness investigations), so it's deferred to that phase.
2. **App references `Lumen.Domain`** only for `PullRequestKey.TryParse`; everything else crosses the process boundary as contracts.
3. **Command palette** (§30) is deferred; `Ctrl+K` currently focuses "Go to file".
4. **OpenTelemetry** (§43) is deferred; the engine logs structured events via `ILogger` to `engine.log`.

## Deferred (explicitly out of slice)

JEV, agents and providers (Claude Code / Codex), review memory and Before Review mode, command palette, split diff,
OAuth Device Flow, OpenTelemetry export, minimap markers, incremental re-analysis across pushes (dismissals already
persist across pushes because review point ids are stable).
