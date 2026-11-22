# Lumen — Version 2 Plan

Previous plan and status: [plan.md](plan.md) (the TDD §57 vertical slice, plus Phase 3 JEV and Phase 4 agents).
Design source: [tdd.md](tdd.md). Mockups: `docs/*.png`.

## Why a v2

v1 built strong foundations: an engine daemon over gRPC, blobless clones and worktrees, a diff editor, guided
navigation, and the pattern-deviation pipeline with JEV and Claude Code investigations behind it. It did not build
the review itself. Today Lumen can only comment where it raised a review point (single line, posted immediately),
it fetches existing review threads but only counts them, it cannot submit a review, it shows no description, checks
or commits, and a pull request must be opened by pasting its URL. On andrew-crm#58 (60 files, ~19.7k additions) the
analysis produced three points, so on most pull requests there is little to guide.

v2 makes Lumen a complete replacement for GitHub's review UI and adds one headline insight feature, the **PR
walkthrough**, powered by a fast model.

## Status (2026-10-12): implemented

All eight milestones are built, through the engine (gRPC contract, additive only) and the app, and covered by tests.
The definition of done — a full review of andrew-crm#58 from the inbox to a submitted Approve, with the walkthrough
shown, without opening github.com — is `FullReviewTests` (headless, against the fixture).

| Milestone | Where it lives |
|---|---|
| M1 Inbox | `InboxService` (GraphQL search, unread from `PullRequestVisits`), `WatchInbox` stream, `InboxView` home screen, watched repositories in Settings → Inbox, command palette (`Ctrl+K`) |
| M2 PR context | `GetPullRequestDetails`; Conversation (Markdown, issue comments, reviews), Commits (commit-range scope, "since my last review"), Checks tabs; head-changed banner; `FollowAsync` polling |
| M3 Comment anywhere | Gutter `+`, drag/shift-click ranges, `DiffSide`/`start_line`, suggestions; pending review CRUD over GraphQL; "Post now" joins an existing pending review |
| M4 Threads in the diff | `reviewThreads` (outdated/resolved, pending-only labelled), thread cards inline, reply/resolve, `N`/`Shift+N` |
| M5 Submit a review | Review tray (`R`): pending list, summary, Comment / Approve / Request changes, `Ctrl+Enter` / `Ctrl+Shift+Enter` |
| M6 Diff upgrades | Split view (`S`, persisted), ignore whitespace (`W`, `git diff -w`), find in diff (`Ctrl+F`), full file (`X`) |
| M7 Fast model | `Lumen.Models`: `CloudflareWorkersAiProvider` (`@cf/zai-org/glm-5.3-flash`, OpenAI-compatible endpoint, optional AI Gateway), free credential check, daily cap, `connections set-cloudflare` |
| M8 Walkthrough | `WalkthroughGenerator` (per-file map, groups and risks reduce), grounding, cache per head/scope/settings, streamed `walkthrough_updated` events; Walkthrough tab, risk markers, reading order (`J`/`K` over groups) |

JEV is off by default (`jev.enabled`), registered only when enabled, and its switches moved under **Legacy** in
Connections.

### Deviations and notes

- **Split view cards** sit in the column they belong to, with a spacer row opposite, rather than spanning both
  columns.
- **Fixture data** for the v2 files (`details.json`, `commits.json`, `v2-events.jsonl`, `tests/fixtures/inbox`) is
  synthesized to match GitHub's shapes; re-record from a live PR with `LUMEN_LIVE_PR` + `LUMEN_RECORD_FIXTURE`.
- **No ETags**: the inbox uses GraphQL search, which has no conditional requests; it polls on an interval and tracks
  the rate limit instead.
- **Walkthrough latency targets** (first content < 10 s, full walkthrough < 30 s on #58) are not measured live;
  the opt-in `LUMEN_LIVE_GLM=1` and `LUMEN_LIVE_PENDING_PR` tests exist but were not run without the user's go-ahead.
- **Enabling JEV** takes effect after an engine restart (the policy is registered at startup).
- The engine also reads Cloudflare credentials from `LUMEN_CLOUDFLARE_ACCOUNT_ID` / `LUMEN_CLOUDFLARE_API_TOKEN`, for
  Linux and CI where there is no credential manager.

### After v2: the Map (2026-10-22)

A **Map** tab shows the pull request as the system it builds: the walkthrough names components, Roslyn links them from
the code, and the app draws them as a Blueprint (by how they use each other) or a Terrain (by lines added), with
Attention / Since my review / Tests lenses. Design, pipeline and limits: [map.md](map.md). Covered by `MapTests`,
`CodeLinkFinderTests` and the component cases in `WalkthroughGeneratorTests`.

## Decisions (2026-10-12)

| Topic | Decision |
|---|---|
| Role | Replace GitHub's review UI: read, comment, reply, resolve, approve, all in Lumen |
| Pull requests | AI-generated PRs, teammates' PRs, and the user's own PRs before requesting review |
| Release shape | Review basics **plus** the walkthrough in v2; deeper AI in v3 |
| Comment delivery | Both: add to a pending review by default, with a per-comment "Post now" |
| Inbox | Review requested from me, my open PRs, and PRs in repositories I choose to watch |
| Platform | Windows, one github.com account (unchanged) |
| Languages | C#/.NET, TypeScript/JavaScript, SQL/infra. The walkthrough is language-neutral (it reads the diff) |
| Models | **GLM 5.3 Flash on Cloudflare** for fast passes (walkthrough, summaries, triage); **Claude Code** for deep investigations |
| Privacy | Code goes to a provider only after an explicit per-provider opt-in (as Claude Code today) |
| JEV | Kept in the code but switched off and not developed further; P4.5 live checks are dropped. GLM takes over triage in v3 |
| Before Review mode | Not a separate mode: the walkthrough on your own PR is the before-review check |

## Principles carried over

- The code stays the primary surface (TDD §2.1). New panels support the diff, they don't replace it.
- **Nothing is sent to GitHub without an explicit action** (a click or `Ctrl+Enter`), and nothing leaves the machine
  for a model provider without the opt-in.
- A review never waits on AI. If GLM is missing, slow or failing, the review client works exactly the same.
- Keyboard first: every new action gets a key, and `Ctrl+K` becomes a real command palette.
- Every milestone ships with view-model tests, engine tests against fakes, and a fixture so `--capture` can render it.

## Milestones

Milestones 1–6 are the review client. Milestones 7–8 (GLM and the walkthrough) only need the diff the engine already
has, so they can run in parallel with 2–6.

### M1. Inbox

- A start screen replacing the paste-a-URL view (pasting a URL or `owner/repo#n` still works from it and from `Ctrl+K`).
- Three sections: **Review requested** (from me or a team I'm in), **My open PRs**, **Watched repositories**.
- Each row: repository, number, title, author, age, size (+/−, files), draft state, check status, review state
  (approved / changes requested / awaiting), and an unread marker when there's activity since I last opened it.
- Watched repositories are a list in Settings (`settings.json` → `inbox.watchedRepositories`).
- Engine: GraphQL search queries (`is:open is:pr review-requested:@me`, `author:@me`, `repo:x/y`) in one round trip;
  polled every 2 minutes while the inbox is visible and on focus, with ETag/`If-None-Match` where REST is used.
- "Last opened" per PR and head SHA is stored in SQLite (used again by M2's "changes since I last reviewed").
- Contracts: `ListInbox` (unary) and `WatchInbox` (stream of updates).

### M2. Pull request context

- A tab strip above the diff: **Files** (today's view), **Conversation**, **Commits**, **Checks**.
- **Conversation**: the description (rendered Markdown), timeline of issue comments, reviews and their verdicts, with
  a composer for a top-level comment. Review comment threads link into the Files tab at their location.
- **Commits**: list with message, author and time; choosing one or a range limits the Files diff to it.
- **Since last review**: a toggle that limits the diff to changes after the head SHA at my last submitted review (or
  last open, if I never reviewed), computed in the engine with `git diff <old-head>..<new-head>` on the existing
  checkout. Force-pushes fall back to comparing file contents and say so.
- **Checks**: check runs and statuses for the head commit with state, duration and a link out; no log viewer in v2.
- The header shows branch names, mergeability, reviewers and labels.
- Contracts: `GetPullRequestDetails`, `GetCommitDiff` / a `range` on `GetFileDiff`, `PostIssueComment`; new events
  `TimelineUpdated`, `ChecksUpdated`, `HeadChanged`.

### M3. Comment anywhere

- Hover any diff line for a `+` in the gutter; click to comment on that line, or drag/shift-click to select a range.
  Works on both sides (deleted lines comment on the base side).
- Composer: Markdown, preview, `Ctrl+Enter` = **Add to review**, `Ctrl+Shift+Enter` = **Post now**.
- **Suggest change** inserts a ```` ```suggestion ```` block prefilled with the selected lines.
- Review-point comments use the same composer (their suggested comment is the starting draft), so they gain ranges,
  suggestions and add-to-review too.
- Lines outside the diff are rejected by the engine, as today; in Full file mode the `+` only appears on diff lines.
- GitHub: REST `POST /pulls/{n}/comments` with `start_line`/`line`/`side` for Post now; pending review comments are
  M5's GraphQL `addPullRequestReviewThread`.

### M4. Threads in the diff

- Existing review threads render inline under their line, like review cards: author, avatar initial, time, Markdown body,
  replies, and suggestion blocks shown as a mini diff.
- Reply, resolve / unresolve, and collapse. Resolved threads start collapsed.
- **Outdated** threads (their line no longer exists at the head) are labelled and shown at their original position
  when that commit is in view, otherwise listed at the top of the file.
- File tree shows a thread count per file; `N` / `Shift+N` move between unresolved threads.
- Engine: replace the REST `GetReviewCommentsAsync` (flat, counted) with GraphQL `reviewThreads` (ids, `isResolved`,
  `isOutdated`, `line`/`startLine`/`originalLine`, comments); `resolveReviewThread` / `unresolveReviewThread`;
  `addPullRequestReviewThreadReply`. `ReviewThread` in `Lumen.Domain` grows to a thread with comments.
- Contracts: `ThreadsUpdated` event, `ReplyToThread`, `SetThreadResolved`.

### M5. Submit a review

- A **review tray** (bottom of the right pane, `R` to open) lists my pending comments with jump-to and delete/edit.
- Summary box plus **Comment**, **Approve** and **Request changes**. Approve and Request changes are unavailable on my own PRs, as on GitHub.
- The pending review lives on GitHub (GraphQL `addPullRequestReview` without an event, then
  `addPullRequestReviewThread` per comment, `submitPullRequestReview` to finish), so a draft survives restarts and
  matches what github.com shows. A pending review started on github.com is picked up and continued.
- "Post now" comments bypass the tray. GitHub rejects single comments while a pending review exists, so in that case
  the comment is added to the pending review and the UI says so.
- After submitting, the PR is marked reviewed at the current head (feeds "since last review").
- Contracts: `GetPendingReview`, `AddPendingComment`, `UpdatePendingComment`, `DeletePendingComment`, `SubmitReview`.

### M6. Diff upgrades

- **Split view** (side-by-side) alongside unified, persisted per user; review cards and threads span both columns.
- **Find in file** (`Ctrl+F`) with match highlighting; works in diff and Full file modes.
- **Command palette** (`Ctrl+K`, TDD §30): go to file, go to PR, actions (approve, toggle split, since last review,
  open on GitHub) with their shortcuts shown. "Go to file" becomes one of its modes.
- Whitespace-insensitive toggle (`git diff -w`).

### M7. Fast model provider: GLM 5.3 Flash on Cloudflare

- `IModelProvider` in a new `Lumen.Models` project: a small contract for single-shot, structured (JSON schema)
  completions with streaming, timeout and cancellation. Distinct from `IAgentProvider`, which runs tools in a checkout.
- `CloudflareWorkersAiProvider`: Cloudflare account id + API token stored in Windows Credential Manager (as the
  OpenRouter key is); the model id is a setting (default to GLM 5.3 Flash; confirm the exact Workers AI model name
  and whether it's served directly or through AI Gateway before building).
- Privacy: a new `privacy.allowCodeToFastModel` switch, off by default, set from **Connections**, next to Claude Code's.
  Without it the walkthrough isn't offered (it needs the diff).
- Limits in **Cloud AI limits**: timeout, max diff size sent, and a daily request cap.
- Connections shows status and a free check (a minimal request, or the token verify endpoint).
- JEV: hide its controls behind "Legacy" in Connections and leave `JevAttentionPolicy` unregistered by default; the
  rule-based policy remains the floor.

### M8. PR walkthrough

The "Review focus" panel from the mockups, at the top of the right pane, and the first thing shown on open.

- **Intent**: two or three sentences on what the PR is trying to do, from the title, description, commit messages and
  diff, plus a note where the description and the diff disagree.
- **Change groups**: files grouped into logical changes ("New enrichment providers", "EF migration", "DI wiring"),
  each with a one-line summary. Mechanical files go in their own collapsed group.
- **Reading order**: groups ordered so dependencies come first. `J` / `K` walk the groups when no review points are
  open; the file tree can be switched to group order.
- **Risky areas**: up to five locations with a short reason (concurrency, error handling, security-sensitive,
  behaviour change, missing tests). Each anchors in the diff as a marker of its own shape, but is labelled as a
  model's reading, not evidence (TDD §2.3), and never posts anything.
- **Own PRs** (`author == viewer`): the same walkthrough, framed as "before you request review", with the risky areas
  as a checklist.
- Pipeline: a map-reduce over the diff so it scales to PRs like #58. Per-file summaries in parallel (bounded), then
  one pass that groups, orders and picks risks. Mechanical files send only their path and size. Output is a JSON
  schema validated in the engine; anything citing a path or line not in the diff is dropped.
- Streaming: groups appear as they're ready; a new `WalkthroughUpdated` event.
- Cache by head SHA (and settings hash) in SQLite, so reopening a PR costs nothing; "since last review" gets a short
  "what changed since you last looked" walkthrough of the delta.
- Targets: first content in < 10 s, full walkthrough in < 30 s on #58.

## Order of work

```text
M1 Inbox ─► M2 Context ─► M3 Comment anywhere ─► M4 Threads ─► M5 Submit ─► M6 Diff upgrades
                                       M7 GLM provider ─► M8 Walkthrough   (parallel from M1)
```

M3–M5 share the composer and GitHub review plumbing; M4 before M5 because the tray reuses the thread rendering.
Done for v2 means: a full review of andrew-crm#58 from the inbox to a submitted Approve without opening github.com,
with the walkthrough shown.

## Engine and contract changes (summary)

- `Lumen.GitHub`: move review data to GraphQL (threads, pending reviews, timeline, search); keep REST where it's
  simpler (comments posted now, check runs). Add rate-limit awareness (`X-RateLimit-Remaining`, GraphQL cost).
- `Lumen.Domain`: `InboxItem`, `PullRequestDetails` (description, timeline, commits, checks), `ReviewThread` with
  comments and resolution, `PendingReview`, `Walkthrough`.
- `Lumen.Storage`: tables for last-opened / last-reviewed heads, walkthrough cache. Schema migrations as today.
- `review_engine.proto`: the RPCs and events listed per milestone. Additive only, so fixtures keep replaying.
- Fixtures: re-record `andrew-crm-58` with threads, timeline and a walkthrough; add a fixture for an inbox.

## Testing

- GitHub client tests against recorded GraphQL/REST payloads (`FakeHttpMessageHandler`), including pending review
  conflicts, outdated threads and force-pushes.
- App tests per milestone (composer keys, tray submit flow, thread resolve, split view anchors) and `--capture`
  scripts for the new states in light and dark.
- Walkthrough: a fake `IModelProvider` for unit tests; grounding tests (invented paths/lines are dropped); an opt-in
  `LUMEN_LIVE_GLM=1` test with a synthetic diff.
- Opt-in live test that creates and deletes a pending review on a scratch PR (never submits) — only run with the
  user's go-ahead.

## v3 (next, not in v2)

- Claude Code investigations for correctness and risk (the Correctness, Test and Security investigators from TDD §11),
  started from walkthrough risky areas.
- GLM as the attention router in place of JEV.
- Convention detection for TypeScript/JavaScript and SQL/infra alongside the C# Roslyn detector.
- Tests and verification: missing-test detection, running the PR's tests, generated tests that confirm or rule out a
  suspected bug.
- Review memory (TDD §15) from my past comments and feedback on my PRs.

## Open questions

Resolved in the build: the model id is `@cf/zai-org/glm-5.3-flash`; it is called directly by default, with an optional
AI Gateway id in Cloud AI limits. The remaining questions stand:

- GLM 5.3 Flash's real context window (Lumen caps each request at the max diff size) and the AI Gateway caching,
  logs and retention settings to recommend.
- Whether Cloudflare's data handling for Workers AI meets the bar for sending code, and how to word the opt-in.
- Inbox polling vs. a GitHub notifications-based refresh (cheaper on rate limits; needs the `notifications` scope).
