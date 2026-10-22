# Lumen

A desktop pull-request review tool that keeps the code central and points the reviewer to the few places that
deserve judgement — starting with **pattern deviations**: code that differs from how this repository normally
solves the same problem, shown next to the precedent it deviates from.

Design: [docs/tdd.md](docs/tdd.md) · v1 plan: [docs/plan.md](docs/plan.md) · Current plan and status:
[docs/plan-v2.md](docs/plan-v2.md) · The Map: [docs/map.md](docs/map.md)

Version 2 makes Lumen a place to do the whole review: an **inbox** of the pull requests waiting on you, the PR's
conversation, commits and checks, comments on any line (single or multi-line, with suggestions) gathered into a
**pending review** you submit as Comment / Approve / Request changes, existing threads inline in the diff, split view,
whitespace toggle, find in diff — and an optional AI **walkthrough** (intent, reading order, risky areas) from a fast
model.

The **Map** tab (`M`) shows the pull request as the system it builds: its components placed by how they use each other
(**Blueprint**, linked by Roslyn from the code), or sized by lines added (**Terrain**), with lenses for risky areas, what
changed since your last review, and which code the pull request's tests exercise. See [docs/map.md](docs/map.md).

## Run it

Prerequisites: .NET 10 SDK, Git, and the GitHub CLI signed in (`gh auth login`).

```bash
dotnet build Lumen.slnx
```

```bash
src/Lumen.App/bin/Debug/net10.0/Lumen.exe --pr https://github.com/KhaiStimpson/andrew-crm/pull/58
```

Or start `Lumen.exe` with no arguments: the home screen is the **inbox** (review requested, your own, and
watched repositories, with unread and new-since-your-last-visit marks), refreshed in the background. Paste a pull request URL (or `owner/repo#123`) into the go-to box
(`Ctrl+K`) to open anything else. Repositories to watch beyond your own requests are under **Settings → Inbox**.

The app starts the review engine (`engine/Lumen.Engine.exe` next to it) as a separate process and talks to it
over a per-user named pipe with gRPC. The engine keeps a blobless clone and a worktree per PR head under
`%LOCALAPPDATA%\Lumen` and exits when the app does. Engine logs: `%LOCALAPPDATA%\Lumen\logs\engine.log`.

### Useful flags

| Flag | Effect |
|---|---|
| `--pr <url or owner/repo#n>` | Open a pull request on start |
| `--fixture tests/fixtures/andrew-crm-58` | Replay a recorded session instead of GitHub (no network) |
| `--theme Light\|Dark` | Force a theme (default follows the OS; toggle in the title bar) |
| `--reduced-motion` | Reduced-motion equivalents for every transition (also follows the OS setting) |
| `--capture <dir> --capture-script diff,next,examine,evidence,comment,dark` | Render states to PNGs and exit (design review without a human) |

Capture steps for the v2 screens: `inbox`, `open`, `conversation`, `commits`, `checks`, `files`, `since`, `all`,
`thread`, `draft`, `tray`, `split`, `find`, `palette`, `risk`, `group`, `order`. For the Map: `map`, `blueprint`,
`terrain`, `lens:attention`, `lens:since`, `lens:tests`, `mapselect:<file name>`, `mapopen`, and `morph:terrain` /
`morph:blueprint` (switch the layout and save frames while it animates).

### Fast model and walkthrough (optional)

The walkthrough uses **GLM 5.3 Flash on Cloudflare Workers AI** (`@cf/zai-org/glm-5.3-flash`, optionally through AI
Gateway). It sends the PR's diff to Cloudflare, so it stays off until you connect it **and** turn on **Walk me through each
pull request** (`privacy.allowCodeToFastModel`) in Connections. Add the account id and API token from Connections, or
from a console (the token is read from input):

```bash
src/Lumen.Engine/bin/Debug/net10.0/Lumen.Engine.exe connections set-cloudflare
```

The walkthrough streams in after the diff and never blocks it; anything that cites a path or line outside the diff is
dropped, results are cached per head, and a daily request cap lives under Cloud AI limits. On Linux/CI the engine
also reads `LUMEN_CLOUDFLARE_ACCOUNT_ID` / `LUMEN_CLOUDFLARE_API_TOKEN`.

### JEV and investigation agents (optional)

JEV is legacy in v2: it's off by default and its switches live under **Legacy** in Connections (enabling it takes
effect after an engine restart). Both are off until you connect them, and a review never waits on either: if they're missing, slow or failing, the
deterministic review is unchanged.

- **JEV** (attention routing via OpenRouter's Decisions API, `typesafe/jev-1.13`) receives counts and categories only —
  no code, paths or names. It's metered by OpenRouter. Add your key from **Connections** (the plug button in the
  title bar), which stores it in Windows Credential Manager and checks it for free. Or from a console (the key is read
  from input, never from the command line):

  ```bash
  src/Lumen.Engine/bin/Debug/net10.0/Lumen.Engine.exe connections set-openrouter-key
  ```

- **Investigation agents** run your own signed-in Claude Code CLI (`claude`) read-only on the checkout, on your Claude
  subscription. They send repository code to Anthropic, so they stay off until you turn on **Run investigations** in
  Connections (which sets `privacy.allowCodeToAgents` and `agents.enabled` together).

Connections also holds the **Cloud AI** master switch and the JEV switches. They save to
`%LOCALAPPDATA%\Lumen\settings.json` and apply without a restart.

### Settings

Connections is one section of the **Settings** overlay (plug button for Connections, sliders button for Review):

- **Review**: how picky convention detection is. Quiet, Balanced (the default) or Thorough, with the numbers behind
  them under Advanced. Edit it for all repositories, or override it for the open pull request's repository.
- **Ignored & files**: names never treated as a convention, paths treated as mechanical, and paths never flagged. A
  repository's lists add to the ones for all repositories.
- **Inbox**: repositories to watch, whose open pull requests join the inbox.
- **Cloud AI limits**: the fast model id, AI Gateway, timeout, diff size limit and daily request cap; the JEV model, timeout and zero-data-retention routing; investigations per pull request and at
  once; and metered usage (Lumen never switches to metered API billing without it).
- **Appearance**: theme and motion.

Global settings live in `settings.json`, a repository's overrides in `%LOCALAPPDATA%\Lumen\review\{owner}\{repo}.json`.
Review changes apply to the next analysis: a **Re-analyse** button appears instead of the open pull request re-running
on its own.

Check what's connected (costs nothing):

```bash
src/Lumen.Engine/bin/Debug/net10.0/Lumen.Engine.exe connections
```

### Keyboard

| Key | |
|---|---|
| `J` / `K` | Next / previous review point |
| `Enter` | Examine the current point |
| `Esc` | Back to the diff (or cancel a comment) |
| `D` | Dismiss |
| `C` | Comment on the current point (`Ctrl+Enter` adds to the review, `Ctrl+Shift+Enter` posts now) |
| Gutter `+`, drag or shift-click | Comment on a line or a range of lines ("Suggest change" pre-fills a suggestion) |
| `N` / `Shift+N` | Next / previous review thread |
| `]` / `[` | Next / previous change in the file, then on into the next / previous file |
| `R` | Review tray: pending comments, summary, Comment / Approve / Request changes |
| `S` | Split / unified diff |
| `W` | Ignore whitespace |
| `X` | Full file / diff only |
| `Ctrl+F` | Find in diff (`Enter` / `Shift+Enter` next / previous) |
| `P` / `E` | Show precedent / evidence |
| `Ctrl+K`, `F` | Command palette: files, pull requests, actions |
| `M` | The Map, or back to the files (`Esc` too) |
| `B` / `T` | On the Map: Blueprint / Terrain |
| `L` | On the Map: next lens (Attention, Since my review, Tests) |
| `Enter` | On the Map: open the selected file in the diff |

The diff's scrollbar is a map of the file: removed lines on the left, added lines on the right, review points, threads,
drafts and find matches on top. Click a mark to jump to it; hover it to see its lines. A file that was only moved or
renamed shows the path change ("Moved without changes") instead of a diff; a rename with edits shows just the edits.

With a walkthrough's reading order on, `J` / `K` step through its groups. Posting anything always requires an explicit
click or key; nothing is sent to GitHub otherwise, and a review stays pending until you submit it.

## How a review point is found

```text
git diff (merge-base..head) ─► changed files ─► mechanical? (migrations, *.Designer.cs, lock files) ─► collapsed
                                   │
                                   ▼
Roslyn syntax ─► type facts per class: constructor dependencies, base types, attributes, throws,
                 swallowing catches, calls through fields
                                   │
Peers = classes sharing a base type, constructor dependency, or name role — taken from the code *before* this PR
                                   │
A trait most peers share, that is distinctive to the role (lift), and that the changed class lacks
                                   ▼
Attention policy (rules; JEV on top when connected) ─► Explainer (templates) ─► ReviewPoint + evidence
                                   │
Investigations (optional, in the background) ─► agent evidence with provenance ─► the review point is updated
```

Every claim carries provenance (`peer-pattern/v1`), counter-evidence (peers that don't follow the convention),
and an evidence state in words ("Strong repository precedent", "Precedent, with exceptions") — never a
confidence percentage.

## Projects

| Project | Role |
|---|---|
| `Lumen.App` | Avalonia 12 desktop client: diff editor (AvaloniaEdit), review cards, examine mode, motion |
| `Lumen.Engine` | Local daemon: sessions, event log with replay, gRPC service on a named pipe |
| `Lumen.Contracts` | `review_engine.proto` and the pipe/socket channel factory |
| `Lumen.Domain` | Pull request, diff, review point and evidence model; unified diff parser; ports |
| `Lumen.Analysis` | Pipeline seams: detectors → attention policy → explainer; mechanical classifier |
| `Lumen.Roslyn` | Type facts, repository index, peer-pattern detector, template explainer |
| `Lumen.GitHub` | REST and GraphQL client (inbox, details, threads, pending reviews) and `gh` token source |
| `Lumen.Repository` | git CLI: blobless clone, PR fetch, merge-base, worktrees, diff |
| `Lumen.Jev` | JEV: typed attention questions, source-free state, OpenRouter Decisions API, `JevAttentionPolicy` |
| `Lumen.Models` | Fast model providers (Cloudflare Workers AI) and the walkthrough generator with grounding |
| `Lumen.Agents` | Agent providers (Claude Code), sandboxed process runner, investigators, scheduler |
| `Lumen.Storage` | SQLite: review interactions, visits, walkthroughs, JEV answers, investigation results, cache; Windows Credential Manager |

## Tests

```bash
dotnet test Lumen.slnx
```

Opt-in suites that touch the network or a local clone:

| Variable | Test |
|---|---|
| `LUMEN_LIVE_PR=owner/repo#n` | Full engine against real GitHub (read-only) |
| `LUMEN_LIVE_PR` + `LUMEN_RECORD_FIXTURE=<dir>` | Re-record a UI fixture |
| `LUMEN_GOLDEN_CHECKOUT=<clone at PR head>`, `LUMEN_GOLDEN_BASE=origin/dev` | Print the pipeline's review points, suppressions and conventions |
| `LUMEN_LIVE_JEV=1` | One real Decisions API call with a synthetic state (needs a stored OpenRouter key; metered, a fraction of a cent) |
| `LUMEN_LIVE_GLM=1` | One real walkthrough of a synthetic diff through Cloudflare Workers AI (needs Cloudflare credentials) |
| `LUMEN_LIVE_PENDING_PR=owner/repo#n` | Creates and deletes a pending review on a scratch PR (never submits) — run only on a PR you own |
| `LUMEN_LIVE_CLAUDE=1` | One real pattern investigation through your Claude Code sign-in (uses subscription quota; refuses metered billing) |

`LUMEN_LIVE_PR` runs the real engine, so it will also call JEV if a key is stored and agents if they're enabled.
