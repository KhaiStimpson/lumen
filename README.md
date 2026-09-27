# Lumen

A desktop pull-request review tool that keeps the code central and points the reviewer to the few places that
deserve judgement — starting with **pattern deviations**: code that differs from how this repository normally
solves the same problem, shown next to the precedent it deviates from.

Design: [docs/tdd.md](docs/tdd.md) · Current plan and status: [docs/plan.md](docs/plan.md)

## Run it

Prerequisites: .NET 10 SDK, Git, and the GitHub CLI signed in (`gh auth login`).

```bash
dotnet build Lumen.slnx
```

```bash
src/Lumen.App/bin/Debug/net10.0/Lumen.exe --pr https://github.com/KhaiStimpson/andrew-crm/pull/58
```

Or start `Lumen.exe` with no arguments and paste a pull request URL (or `owner/repo#123`).

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

### JEV and investigation agents (optional)

Both are off until you connect them, and a review never waits on either: if they're missing, slow or failing, the
deterministic review is unchanged.

- **JEV** (attention routing via OpenRouter's Decisions API, `typesafe/jev-1.13`) receives counts and categories only —
  no code, paths or names. It's metered by OpenRouter. Store a key in Windows Credential Manager (read from the
  console, never from the command line):

  ```bash
  src/Lumen.Engine/bin/Debug/net10.0/Lumen.Engine.exe connections set-openrouter-key
  ```

- **Investigation agents** run your own signed-in Claude Code CLI (`claude`) read-only on the checkout, on your Claude
  subscription. They send repository code to Anthropic, so they need two explicit settings in
  `%LOCALAPPDATA%\Lumen\settings.json`:

  ```json
  { "privacy": { "allowCodeToAgents": true }, "agents": { "enabled": true } }
  ```

  Lumen never switches to metered API billing unless you also set `"agents": { "allowMeteredUsage": true }`.

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
| `C` | Comment (`Ctrl+Enter` posts) |
| `P` / `E` | Show precedent / evidence |
| `Ctrl+K`, `F` | Go to file |

Posting a comment always requires an explicit click or `Ctrl+Enter`; nothing is sent to GitHub otherwise.

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
| `Lumen.GitHub` | REST client and `gh` token source |
| `Lumen.Repository` | git CLI: blobless clone, PR fetch, merge-base, worktrees, diff |
| `Lumen.Jev` | JEV: typed attention questions, source-free state, OpenRouter Decisions API, `JevAttentionPolicy` |
| `Lumen.Agents` | Agent providers (Claude Code), sandboxed process runner, investigators, scheduler |
| `Lumen.Storage` | SQLite: review interactions, JEV answers, investigation results, cache; Windows Credential Manager |

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
| `LUMEN_LIVE_CLAUDE=1` | One real pattern investigation through your Claude Code sign-in (uses subscription quota; refuses metered billing) |

`LUMEN_LIVE_PR` runs the real engine, so it will also call JEV if a key is stored and agents if they're enabled.
