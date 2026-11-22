# Lumen application review — 3 October 2026

## Assessment

Lumen has a useful foundation: a separate local engine, replayable review events, a readable diff, deterministic analysis, explicit comment posting, and optional cloud investigations. The largest product gap is that **the review tiers mostly describe the shape of a change rather than its actual risk**. New files generally land in Skim; ordinary modifications land in Worth a look. The planned risk signals and correctness investigators are still missing.

This review corrected reproducible defects in state handling, navigation, evidence, settings, authentication, offline recovery, privacy/billing wiring, process output limits, distribution, and mechanical classification. Remaining feature work and improvement recommendations below were assessed, not implemented.

## Scope and method

Reviewed the current working tree across all eleven source projects, the eight test projects, README, technical design, vertical-slice plan, triage plan, and existing fixture workflows. The review followed both implementation/standards and design/specification axes, with independent backend and UI/analysis passes. Because the request covered the application, the review was not limited to a recent Git diff.

Verification used regression reproductions followed by fixes, real local Git processes, named-pipe engine tests with fake GitHub/provider boundaries, headless desktop interaction, light/dark screenshots, a Release build, and a publish/startup smoke check. Existing unrelated `.flow`, handoff, and Proxmox database changes were preserved. No commits, pushes, GitHub comments, or deployments were made. No live GitHub, JEV, or Claude investigations were invoked.

## 1. Bugs fixed

| # | Defect and consequence | Correction and regression coverage |
|---|---|---|
| 1 | Reviewer decisions were persisted and streamed but not applied to the session's current point. A later investigation emitted stale Visible state and scheduling could still see dismissed points as open. | Update the latest point and publish under one session lock; apply evidence against that latest state. Named-pipe tests pause an investigation, save Dismissed/Examined/Commented, and assert the result preserves it. |
| 2 | Resolved state was persisted as Restored, becoming Visible after re-analysis. | Add a distinct persisted Resolved action and round-trip it through state mapping. Regression re-analyses a resolved point. |
| 3 | An invalid/outdated review-point ID was validated after posting its comment. The operation could report failure despite creating a comment, inviting duplicates on retry. | Validate the ID before calling GitHub. Regression asserts no post occurs for an unknown point. |
| 4 | Selecting a file whose diff failed to load left the previous file's code visible under the new selection. | Clear the previous document and markers when changing paths; report the failed load. Regression checks that another file's code is never shown. |
| 5 | Refresh retained old selection, triage, folds, progress, and source state. In-flight requests could refill the refreshed caches with old results. | Cancel/invalidate requests from the prior analysis and reset derived state. Regression refreshes into a replacement snapshot with no review points. |
| 6 | Selecting a triage group or More changes highlighted a target without scrolling to it. From Examine it could leave an empty panel; deletion targets lacked old-side navigation. | Publish plan navigation to the editor, scroll to the correct side/fold, and return to Diff. View-model and actual-window tests verify navigation. |
| 7 | Failed GitHub Viewed updates rolled back the checkbox but retained the optimistic coverage percentage. | Recalculate coverage after rollback. Existing viewed-state tests cover the failure path. |
| 8 | Changed peers were evaluated using merge-base facts but their cited excerpts and opened source came from head. Evidence could show code that did not establish the claimed precedent. | Preserve merge-base source for precedent snippets; explicitly request base source in the preview, including renamed paths. Detector, UI request, and engine revision tests cover this. |
| 9 | Triage cache identity omitted the merge base. Retargeting a PR with the same head could reuse hunks from a different diff. | Include merge-base identity in the cache version. Regression uses the same head with different merge bases. |
| 10 | Hand-edited global/repository settings containing null sections, list entries, blank models, or invalid limits could crash settings or analysis. Null recent-PR lists could prevent the app opening. | Normalize values on load, preserve valid preferences, clamp limits, and fall back safely. Global, repository, and app-settings regressions cover malformed inputs. |
| 11 | The engine cached a rejected GitHub token indefinitely; following the login hint and retrying still used the old credential. | Invalidate the cache on HTTP 401. The next user operation reads fresh credentials; writes are never automatically replayed. Regression asserts exactly one rejected POST and fresh auth on the next GET. |
| 12 | Offline fallback covered network exceptions but missed GitHub 5xx responses and timeouts. Cached PRs were unavailable during those transient failures. | Expand fallback to transient server failures and non-user-cancelled timeouts. Authentication errors still remain errors. Regressions cover both cached recovery and revoked authentication. |
| 13 | Allow metered usage passed the scheduler check but did not reach the actual agent request, so the provider still refused it. | Carry the explicit opt-in through the investigation brief to the session request. Regression inspects a fake metered provider's request. |
| 14 | Turning off investigations/cloud permission did not prevent already-queued investigations from starting. | Recheck current permission and billing policy after obtaining a scheduler slot. Regression disables permission while another job occupies that slot. Already-running work may finish; immediate cancellation is an improvement below. |
| 15 | The output cap was checked after reading a complete line. Newline-free output could allocate far beyond the configured limit. | Bound raw-byte reads before decoding/buffering. Real Git blob tests cover oversized newline-free output, exact byte limits, UTF-8 split across buffers, CR/LF, and final unterminated lines. |
| 16 | Publishing the app omitted its separate engine, so the published distribution could not review PRs. | Include engine files in publish output. Verified executable/runtime configuration presence and local engine startup. |
| 17 | Quoted Git binary and rename paths were not consistently unescaped, breaking path lookup/display for escaped names. | Decode complete quoted paths before stripping prefixes, including rename metadata. Parser regressions cover binary and rename paths. |
| 18 | Moving initialized automatic properties/event fields, or storage members in structs, could be classified Skip even when initialization/layout order changed. | Keep order-sensitive member moves in Skim, following the existing field policy. Near-miss tests cover properties, events, and struct/record-struct storage. |
| 19 | Import additions/removals were classified Skip even though unchanged body tokens can bind to different extension methods/types. | Keep import changes in Skim with a binding caveat; only reordering remains Skip. Regression demonstrates changed extension-method resolution. |
| 20 | Renames inside `nameof`/`typeof` were classified mechanical although observed names/type identity changed. | Conservatively decline mechanical rename proof when a mapped name is observed this way on either side. Regressions cover local names and reflected types. |
| 21 | Deleted peer classes disappeared from the supposed pre-PR baseline, reducing support and suppressing valid precedent findings. | Include deleted classes' merge-base facts and snippets while excluding them from changed-type findings. Regression deletes conforming peers and verifies the original baseline still supports the finding. |

The triage classifier cache version is now `triage/3`, invalidating cached decisions made under the older rules. The pipe contract version is now `v2`, preventing attachment to an older engine that would silently ignore base-revision source requests.

Also corrected an order-dependent screenshot-test failure on a clean Release output directory and replaced obsolete Avalonia TextBox Watermark properties with PlaceholderText.

## 2. Missing features, in recommended order

Priorities: **P1** materially improves trustworthy review; **P2** completes common workflows; **P3** adds convenience or later differentiation.

### P1 — Risk ranking and actionable correctness findings

**Evidence:** `docs/review-triage-plan.md` Phases 5–7 remain open; `TriagePipeline.Fallback` assigns new files Skim and ordinary changes Worth a look without examining impact. Destructive migration detection exists, but broad risk detection does not.

Implement removed safeguards first: null/input validation, throws, locks, transactions, awaits, resource disposal, and persistence calls. Add auth, money, concurrency, persistence, and public-contract tags; estimated fan-in; changed behavior without corresponding tests; complexity delta; and cached bug-fix hotspots. Produce a reasoned tier and a risk card anchored in the diff.

**Acceptance:** A new authentication/payment service is not automatically low attention; removed guards become reviewable findings; moving a guard is distinguished from removing it; human-labelled critical changes achieve the stated recall target on multiple PRs. Keep thresholds and proof caveats visible in words.

### P1 — Reduce irrelevant dependency/convention notes

**Evidence:** The peer detector compares missing traits. Its missing-dependency suppression prevents secondary call warnings, but does not establish whether a dependency recommendation is relevant to the changed class's work. The documented IConfiguration example remains an important false-positive case.

Implement the planned missing-dependency relevance gate before emitting “doesn't take X.” Require evidence that this class performs the work for which its peers use X. Add a Consistency visibility switch, dismissal reasons, and an explicit per-repository mute with an undo route.

**Acceptance:** A class that never reads configuration is not told to inject IConfiguration merely because peers do. A useful missing QueueClaimService warning retains its supporting evidence.

### P1 — Existing conversations and complete GitHub review submission

**Evidence:** Snapshots load review comments, but the application does not provide a complete conversation/review workflow. The contract supports individual comments, not overall Approve/Request changes/Comment review submission or reply/resolution flows.

Render existing conversations with author, revision/outdated state, side, and resolution. Add editable local drafts and explicit batch submission, replies, and thread resolution. Prevent duplicate advice when an existing thread already raises the issue.

**Acceptance:** A reviewer can understand previous discussion and finish a review in Lumen, with a final explicit submission action and clear recovery if submission fails.

### P2 — Navigate every change in a tier

**Evidence:** More changes currently targets its first hunk; J/K stops cover review points and groups, not every ungrouped hunk.

Expand aggregate rows into a navigable list or provide Next/Previous change within a tier. Include deleted-side targets and preserve selection when filters change.

**Acceptance:** “48 changes in 22 files” lets the reviewer reach all 48 changes; coverage follows actual visitation/acknowledgement rather than opening an aggregate once.

### P2 — Implement or expose unavailable investigation roles

**Evidence:** Only RepositoryPatternInvestigator is registered, while attention routing can request other investigation types. Unsupported jobs are filtered out.

Implement Correctness first, using bounded questions tied to deterministic risk reasons. Show which roles are available and why an investigation was skipped. Tests, history, architecture, and security roles can follow. The planned grounded, cached PR tour is useful after risk ordering exists.

**Acceptance:** Requested work never disappears without explanation; each result cites real revision/path/line evidence and failures preserve deterministic output.

### P2 — Explicit binary, rename-only, and metadata views

**Evidence:** The diff document primarily renders text hunks. Binary, pure rename, and mode-only changes can leave little or no explanatory editor content.

Add an explicit change summary for these cases, optional image preview where useful, and old/new paths. Add in-diff search, word highlighting, and optional split diff.

**Acceptance:** A blank text diff cannot be mistaken for an unchanged file, and non-text changes remain understandable and reviewable.

### P2 — Broader analysis coverage and revision-aware refresh

**Evidence:** Syntax peer extraction focuses on top-level non-static C# classes. Records, structs, nested/static types and other languages have substantially less coverage. Refresh remains a full analysis.

Publish the supported-analysis scope in the product, then extend records/structs and the languages important to users. Add changed-head notifications and incremental invalidation by affected symbols/evidence. Retain decisions only when their underlying claim remains applicable.

**Acceptance:** Unsupported code never implies “checked and safe”; pushed commits visibly invalidate stale evidence and revisit affected decisions.

### P3 — Personal review memory and Before Review mode

**Evidence:** The technical design's Review Memory phase specifies historical review ingestion and personal patterns. Current storage retains actions and cached evidence, not that learning layer.

Start with explicit dismissal reasons and repository mutes; later ingest historical review feedback and provide an author-side pre-review. Keep inferred conventions separate from user-approved rules.

### P3 — Finish desktop workflow conveniences

Add a real command palette, selectable/exportable summaries, multi-PR switching, richer keyboard help, and supported-platform credential stores if macOS/Linux become delivery targets. The current Windows credential workflow is functional and verified.

## 3. Improvements beyond feature completeness

| Priority | Recommendation | Why / completion criterion |
|---|---|---|
| P1 | Make proof scope explicit and expand semantic near-miss tests. | Syntax-only renames/moves can still involve serialization, reflection by string, external callers, attributes, CallerArgumentExpression, or binding outside changed files. Test these cases and retain reviewability when safety cannot be established. An import-only label is a shape description, not proof of unchanged behavior. |
| P1 | Expand and human-confirm the golden evaluation set. | The existing primary fixture is mostly new C# code and draft labels are not a sufficient accuracy claim. Include auth, persistence, public API, refactoring, generated-code, and multi-language PRs. Track critical recall and mechanical false positives by category. |
| P1 | Expose stale/offline/revision state. | Cached recovery is useful, but users need fetched time, head/base identity, and a visible offline banner. Disable or explain actions that require live GitHub. Show whether evidence comes from merge-base, head, or cached provider output. |
| P2 | Add cancellation and clearer investigation lifecycle. | Privacy changes now stop queued work; an already-running session may finish. Provide Stop active investigations, active/queued counts, quota/billing mode, and skipped/unavailable reasons. |
| P2 | Bound disk and in-memory retention. | Repository/worktree caches, session dictionaries, event logs, and subscriber queues need limits. Add usage display, clear-cache controls, session eviction, and safe cleanup that preserves active work. |
| P2 | Save preferences atomically and surface recovery. | Direct writes can leave truncated settings on interruption, despite safe-load fallback. Write temporary files and replace atomically; retain a recoverable backup and show a local settings recovery message. |
| P2 | Harden engine startup and logging failure paths. | Serialize stdout/stderr writes to the shared log, keep pipe streams drained when logging fails, and make process/channel ownership explicit on startup exceptions/cancellation. Add reconnect, engine-crash, and multi-client refresh tests. |
| P2 | Preserve comment drafts and improve ambiguous-post recovery. | A server may accept a comment before a response or local persistence fails. Store drafts before sending and reconcile successful posts before suggesting retry. Current early validation eliminates one duplicate-post cause, not every network ambiguity. |
| P2 | Add local diagnostic visibility with redaction. | Classifier failures safely fall back but are otherwise easy to miss. Log classifier identity/count/duration and provide a redacted diagnostic export without code or credentials by default. |
| P2 | Improve module boundaries around review state. | PullRequestViewModel combines streaming, navigation, caches, coverage, comments, and settings consequences. Separate state/event reduction from navigation and persistence so concurrency behavior can be tested without a window. Avoid a broad refactor in the bug-fix patch. |
| P3 | Adapt the header to available desktop space. | Inspected 1280×800 captures show file-name/addition-count pressure in the center header. Wrap or move secondary metrics, retain the current path, and verify minimum useful pane sizes, font scaling, focus order, and accessible names on custom gutter/fold controls. |
| P3 | Keep project documentation synchronized with shipped behavior. | The vertical-slice plan still describes Connections/settings as deferred although they exist; the triage document's opening status says not started despite completed phases. Publish a concise current capability matrix and retain historical decisions separately. |

## Recommended next delivery sequence

1. **Trustworthy risk review:** human-labelled golden PRs, safeguard/sensitive-area signals, risk tiering, relevant dependency notes, and precise proof language.
2. **Complete the review workflow:** existing threads, persistent drafts, explicit overall review submission, and navigation through every loose hunk.
3. **Operational reliability:** freshness indicators, active cancellation, retention controls, atomic saves, and diagnostics.
4. **Broader intelligence:** correctness investigations, incremental refresh, additional syntax/languages, review memory, and PR tours.

## Verification

Release build: **passed, zero warnings and zero errors**. Final test outcomes across the eight projects: **543 passed** (Analysis 280, App 79, Engine 60, Agents 39, GitHub 16, Jev 30, Repository 24, Storage 15). The final Viewed/coverage regression was also run separately after strengthening its assertion.

The sandbox cannot access the user's Avalonia licensing folder or Windows credential persistence. Builds/tests used `-p:UsedAvaloniaProducts=` to avoid the build-service licensing probe; the uniquely named throwaway Credential Manager test was separately verified under the normal Windows account and passed. This was an environment workaround, not a product change.

One Engine source-revision test returned FailedPrecondition once during verification, then passed in isolation, in the full suite, and in **eight consecutive full Engine-suite repeats**. Its underlying transient cause was not established; the test now checks analysis failure explicitly before requesting source. Keep this case in concurrency/soak coverage rather than treating the reruns as proof that no transient failure exists.

Reproduction commands:

```powershell
dotnet build Lumen.slnx --no-restore -c Release -p:UsedAvaloniaProducts=
dotnet test Lumen.slnx --no-build --no-restore -c Release -p:UsedAvaloniaProducts= --filter 'FullyQualifiedName!~WindowsCredentialStoreTests'
# Run the following under the normal Windows account, outside the restricted sandbox:
dotnet test tests/Lumen.Storage.Tests/Lumen.Storage.Tests.csproj --no-build --no-restore -c Release --filter FullyQualifiedName~WindowsCredentialStoreTests
dotnet publish src/Lumen.App/Lumen.App.csproj --no-build --no-restore -c Release -p:UsedAvaloniaProducts= -o artifacts/review/publish
git diff --check
```

Published engine executable/runtime configuration are included and local startup was verified. Cloud/provider suites are opt-in and were not run; successful offline tests do not establish live GitHub permissions, current provider behavior, or metered billing execution. Publish validation covers the ordinary framework-dependent Windows output, not an installer or every self-contained/RID configuration.

Visual evidence: [Light desktop navigation](screenshots/application-review/plan-navigation-1280x800-light.png) and [Dark desktop navigation](screenshots/application-review/plan-navigation-1280x800-dark.png).
