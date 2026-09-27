# Lumen — Vertical Slice Plan (TDD §57)

Source of truth for scope: [tdd.md](tdd.md). Mockups: `docs/*.png` (a strong starting point; deviate where it improves the product).

## Decisions (2026-09-27)

| Topic | Decision |
|---|---|
| Name / runtime | `Lumen.*` projects, .NET 10, Avalonia 12.1 + AvaloniaEdit 12 |
| Scope | §57 vertical slice; then Phase 3 (JEV) and Phase 4 (agents), below |
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

---

# Phase 3 (JEV) and Phase 4 (investigation agents)

Research first, recorded in `docs/research/` — [openrouter-jev.md](research/openrouter-jev.md),
[agent-providers.md](research/agent-providers.md). Design rule for both: **nothing new may block or break the
deterministic slice.** JEV and agents only add signal on top of it, and every failure falls back to what exists today.

## Shape

```text
Detectors ─► candidates ─► IAttentionPolicy.DecideAllAsync(candidates, context)
                              JevAttentionPolicy
                                 ├─ RuleBasedAttentionPolicy  (always runs; the floor and the fallback, §38.2)
                                 └─ ISystemOneEvaluator        (OpenRouter; one request per change unit, §38.1)
                                      state  = source-free SystemOneState (from AttentionSignals, §10.1, §40)
                                      output = choice + probabilities per typed question (§10.2), persisted
                           ─► explainer (templates) ─► ReviewPoint streamed as today
                           ─► InvestigationCoordinator.Plan ─► InvestigationScheduler (priority, budgets, §35–36)
                                                         └─ role (Repository Pattern Investigator)
                                                              └─ IAgentProvider → AgentSession (ClaudeCodeProvider, §37)
                                                         ◄─ InvestigationResult (§13), grounded + persisted
                           ─► EvidenceAggregator merges it into the point (§14) ─► ReviewPointUpdated
```

## Steps

- [x] **P3.1 Research** — the TDD is accurate: OpenRouter's Decisions API (`POST /api/alpha/decisions`, alpha) serves
      `typesafe/jev-1.13` and returns probabilities natively (verified against the raw `openapi.yaml`). Claude Code is
      driven headless via `claude -p --output-format stream-json`; Anthropic's legal/compliance page explicitly allows
      invoking the user's own unmodified, signed-in binary
- [x] **P3.2 Settings and secrets** — `{dataDir}/settings.json` (privacy §40, JEV, agents); `WindowsCredentialStore`
      (§39A); `Lumen.Engine.exe connections [status|set-openrouter-key|remove-openrouter-key]`
- [x] **P3.3 JEV evaluator** — `ISystemOneEvaluator`, `attention/v1` questions (§10.2, all `noul`),
      `OpenRouterSystemOneEvaluator` (`data_collection: deny`, ZDR, no fallbacks, 8 s timeout, free key check);
      `SystemOneStateBuilder` is the single privacy chokepoint; every answer persisted (`AttentionEvaluations`)
- [x] **P3.4 JevAttentionPolicy** — one request per change unit; rules first; JEV may suppress (mechanical > 0.95, or
      judgement < 0.05 *and* deviation < 0.10), add priority and route to investigations, never surface what the rules
      rejected; no key / privacy / timeout / error / an evaluator ignoring cancellation → rule decisions (§38.2);
      pauses 15 min after a bad key or exhausted credits
- [x] **P4.1 Agent abstractions** — `IAgentProvider`, `AgentSession`, capabilities, `AgentBilling`;
      `SandboxedProcessRunner` (real `.exe` only, working dir under the data dir, allowlisted env — no API keys or tokens
      inherited — prompt on stdin, timeout, stdout cap, process-tree kill)
- [x] **P4.2 ClaudeCodeProvider** — `claude auth status --json` (free) for install/sign-in/plan;
      `claude -p --output-format stream-json --verbose --restricted --strict-mcp-config --no-session-persistence
      --permission-mode dontAsk --tools Read,Grep,Glob --json-schema …`; never `--bare`; refuses to start on metered
      billing and stops if the stream reports an API key source, unless metered usage is explicitly allowed
- [x] **P4.3 Investigations** — `RepositoryPatternInvestigator` (read-only), grounding (a finding is evidence only if it
      cites a real file and line in the repo), `InvestigationScheduler` (priority, cache first, per-PR cap, global
      concurrency, billing check), `GitAgentWorktreeFactory` for mutating roles / Deep budget, `Investigations` table
- [x] **P4.4 Surface** — `review_point_updated` and `investigation_status` events; "Analysing N areas…"; agent evidence
      in Examine → Evidence as "Agent investigation · claude-code · pattern-investigator/v1 · model"
- [ ] **P4.5 Live checks (opt-in, with the user's go-ahead)** — `LUMEN_LIVE_JEV=1` (needs a stored OpenRouter key; a
      fraction of a cent) and `LUMEN_LIVE_CLAUDE=1` (one investigation on the user's Claude subscription); then PR #58
      end to end with agents enabled

## How it behaves on PR #58

Without a key or consent nothing changes: the same three points, rule-based. With agents enabled and no JEV, the
no-JEV fallback routes pattern deviations *with exceptions* to the pattern investigator — R1 (providers not throwing
`ProviderOperationException`, 5 of 7 with 2 exceptions) is exactly that case. Not yet run live (P4.5).

## Deviations (Phase 3/4)

5. **Batched `ISystemOneEvaluator`.** §38 sketches `EvaluateAsync<TDecision>(state, question)`; the Decisions API
   answers a map of typed questions in one call, and §38.1 asks for batching, so the interface takes a question list.
   All `attention/v1` questions are `noul` (P(true)); `choice`/`score` are supported by the evaluator for later use.
6. **Decisions per candidate, batched per change unit.** `IAttentionPolicy` gained `DecideAllAsync` (default: loop) so
   JEV can see a change unit's candidates together; questions are keyed `c0_mechanical`, …
7. **Agent explanation arrives as an update, not through `IReviewPointExplainer`.** An agent run takes minutes; §44
   wants markers in < 5 s. The template explainer still produces the point immediately, and the investigation's
   evidence and "why the precedent exists" arrive later as `ReviewPointUpdated`. Results are cached per head SHA, so
   reopening a PR applies them without spending usage.
8. **Budgets are enforced by wall clock and output size, not model-call counts.** The installed Claude Code (2.1.257)
   does not list `--max-turns` in `--help` (the docs do); Tiny/Standard/Deep map to 90 s / 4 min / 10 min, 2/8/16 MB.
9. **Read-only investigators run in the review checkout**, confined by `--restricted` and read-only tools; isolated
   worktrees (§12) are for mutating roles and Deep budgets, which no shipped role uses yet.
10. **Only the Repository Pattern Investigator exists.** JEV can already request Correctness/Architecture
    investigations; the scheduler skips types with no role.
11. **Codex is not implemented.** `codex exec --json` exists locally (0.153.4), but OpenAI has not stated that third
    parties may drive a ChatGPT-signed-in Codex; see [agent-providers.md](research/agent-providers.md).
12. **Refuted findings demote, not hide.** A refuted pattern point becomes Low with conflicting evidence; the reviewer
    decides (§2.4). No point is ever surfaced by JEV or an agent alone.
13. **Connections UI is a CLI for now** (`Lumen.Engine.exe connections`), plus hand-edited `settings.json`; the §39
    connections screen is deferred.

## Deferred (explicitly out of slice)

Review memory and Before Review mode, Codex provider, Correctness/Test/History/Architecture/Security investigators,
generated tests, viewport-driven priority (§35), adaptive JEV thresholds, connections/settings UI, command palette,
split diff, OAuth Device Flow, OpenTelemetry export, minimap markers, incremental re-analysis across pushes
(dismissals and investigation results already persist because review point ids are stable).
