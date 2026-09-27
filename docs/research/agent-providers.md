# Agent Providers: Claude Code & Codex as headless investigators for Lumen

Accessed 2026-09-27. Installed locally: Claude Code **2.1.257**, Codex CLI **0.153.4** (Windows, via
`claude.exe` at `C:\Users\khail\.local\bin\claude.exe` and `codex`/`codex.cmd` at
`C:\Users\khail\AppData\Roaming\npm\`). Auth checked read-only: `claude auth status --json` showed
this machine's Claude Code is logged in via `claude.ai`, plan `pro`; `codex login status` showed
"Logged in using ChatGPT". No prompts were run, no sign-in/out was performed.

## Verdict

Both CLIs can be driven headlessly from a .NET child process with machine-readable output, and
both can use the user's own subscription sign-in — this is officially supported for Claude Code and
explicitly compliant **only** if Lumen spawns the unmodified official binary and never intermediates
credentials (`code.claude.com/docs/en/legal-and-compliance`, accessed 2026-09-27). For Codex, the
analogous policy is **not written down anywhere as clearly** — third-party programmatic use of
"Sign in with ChatGPT" is unresolved in OpenAI's own GitHub discussions as of August 2026. Treat
Codex-via-ChatGPT-login as **lower-confidence** than Claude-via-subscription until OpenAI publishes
an explicit policy; API-key mode is the documented-safe fallback for Codex automation.

There is no .NET Agent SDK for either vendor. The correct integration is: spawn the official CLI
binary as a subprocess, in `--print`/`exec` mode, with JSON/stream-JSON output, exactly as §37 of
`docs/tdd.md` already specifies.

## What changes because of this

1. **Anthropic's compliance page is unambiguous and directly supports the TDD §37 design.**
   `code.claude.com/docs/en/legal-and-compliance` states: *"Anthropic does not permit third-party
   developers to offer Claude.ai login into their own applications, or to route requests through
   Free, Pro, or Max plan credentials on behalf of their users... developers may not collect,
   store, or intermediate Claude.ai credentials or session tokens — sign-in to a Claude account must
   complete through Anthropic's own flow."* But it also carves out exactly what Lumen wants: *"Nor
   does it prevent an end user from signing in to the unmodified Claude Code binary with their own
   Claude subscription, including where a platform hosts Claude Code."* Conditions for the
   "customers may offer Claude Code in their products" carve-out: **the binary must not be
   modified**, and **Lumen may not pay for, resell, or intermediate usage** — each end user
   authenticates directly with Anthropic. This is a hard constraint on implementation: Lumen must
   shell out to the real `claude.exe`/`claude` binary as installed by the user or Anthropic's own
   installer, not a forked/vendored copy, and must never proxy or cache the user's OAuth token.

2. **Codex has no equivalent published policy** — this is a finding, not an assumption I should
   have made. OpenAI's own maintainer, when asked directly in
   [openai/codex#8338](https://github.com/openai/codex/discussions/8338), pointed only to the
   generic ToS and Apache-2.0 license and did not confirm whether third-party apps invoking `codex`
   with ChatGPT OAuth is permitted; follow-up questions (Feb/May/Jul/Aug 2026) went unanswered.
   `developers.openai.com/codex/auth` (redirects to `learn.chatgpt.com/docs/auth`) recommends API-key
   auth "for programmatic Codex CLI workflows, such as CI/CD jobs" and warns not to "expose Codex
   execution in untrusted or public environments," without a clear third-party carve-out analogous
   to Anthropic's. **Recommendation below reflects this asymmetry**: default Codex to API-key mode
   in Lumen, and treat ChatGPT-login mode as opt-in/experimental with a visible warning.

3. **Claude Code's billing precedence order is fully documented and testable, so Lumen can
   guarantee "never silently switch to metered billing."** Documented precedence
   (`code.claude.com/docs/en/authentication`, "Authentication precedence" section), highest first:
   cloud provider env vars (Bedrock/Vertex/Foundry) → `ANTHROPIC_AUTH_TOKEN` → `ANTHROPIC_API_KEY` →
   `apiKeyHelper` → `CLAUDE_CODE_OAUTH_TOKEN` → Anthropic profile/WIF → subscription OAuth from
   `/login` (the default for Pro/Max/Team/Enterprise). Quote: *"If you have an active Claude
   subscription but also have `ANTHROPIC_API_KEY` set in your environment, Claude Code uses the API
   key once you approve it... In non-interactive mode (`-p`), the key is always used when present."*
   That last clause is the load-bearing one: in `-p` mode there is **no interactive approval
   prompt** — if `ANTHROPIC_API_KEY` (or `ANTHROPIC_AUTH_TOKEN`) is present in the child process
   env, billing silently switches to metered API, with no confirmation step. Lumen must strip these
   (and `CLAUDE_CODE_USE_BEDROCK`/`_VERTEX`/`_FOUNDRY`, `CLAUDE_CODE_OAUTH_TOKEN`, `ANTHROPIC_PROFILE`)
   from the child environment unless the user has explicitly chosen Direct API mode.

4. **`--bare` mode — the mode the docs recommend for all scripted/SDK calls — never uses the
   subscription at all.** Quote: *"`--bare` is the recommended mode for scripted and SDK calls, and
   will become the default for `-p` in a future release."* But also: *"In bare mode, Claude Code
   never reads OAuth credentials or the system keychain... Set `ANTHROPIC_API_KEY` before running
   it, because bare mode doesn't use your subscription login."* **This directly conflicts with
   Lumen's subscription-first goal.** Lumen must NOT pass `--bare` when it wants subscription
   billing, even though `--bare` is faster to start and is Anthropic's stated future default for
   `-p`. This is worth flagging now because a future Claude Code version may default `-p` to bare
   mode, silently breaking subscription auth for Lumen unless Lumen passes an explicit
   opt-out/equivalent flag at that time.

5. **Agent SDK credits (a separate metered pool for `claude -p`/SDK/third-party usage) exist in
   documentation but are currently paused, not live.** `support.claude.com` article
   15036540 states the June 15, 2026 rollout (separate monthly credit — e.g. $20 Pro / $100 Max 5x /
   $200 Max 20x — covering "Claude Agent SDK usage... the `claude -p` command... and third-party
   apps built on the Agent SDK") was paused: *"Update June 15: We're pausing the changes to Claude
   Agent SDK usage described below. For now, nothing has changed: Claude Agent SDK, `claude -p`, and
   third-party app usage still draw from your subscription's usage limits."* So **today**, Lumen's
   `claude -p` calls draw from the user's normal Pro/Max session/weekly usage window, same as
   interactive use — but Anthropic could resume the separate-credit rollout at any time, which would
   change UX expectations (a capped, non-rolling-over sub-budget). Not something Lumen can control,
   but should surface via `UsageStatus` capability so users aren't surprised.

6. **The installed CLI version (2.1.257) and the published docs disagree on at least two flags.**
   `claude --help` locally does **not** list `--max-turns` at all (grepped the full help text — no
   match), although `code.claude.com/docs/en/cli-reference` documents it ("Limit the number of
   agentic turns (print mode only)... No limit by default"). Similarly, local `--permission-mode`
   choices are `acceptEdits, auto, bypassPermissions, manual, dontAsk, plan` — no `default` value —
   while the doc page lists `default, acceptEdits, plan, auto, dontAsk, bypassPermissions, manual`
   (manual as alias for default). **UNVERIFIED**: whether `--max-turns` is silently accepted despite
   being absent from `--help` (I did not execute it, to avoid spending usage) — settle this by
   running `claude -p --max-turns 1 "healthcheck prompt"` once, accepting the token cost, or by
   diffing `claude --help` across versions. Likewise for `codex exec --help`: it does **not** list
   `-a/--ask-for-approval` (only present on the top-level `codex` command), contradicting the
   `learn.chatgpt.com/docs/developer-commands` page which documents `--ask-for-approval` as an
   `exec` flag. Treat both as version-drift until confirmed on the exact pinned version Lumen ships
   against.

## A. Claude Code headless

### A.1 Flags confirmed on installed v2.1.257 (`claude --help`, `claude -p --help`)

Present: `--add-dir`, `--agent`, `--agents`, `--allow-dangerously-skip-permissions`,
`--allowedTools`/`--allowed-tools`, `--append-system-prompt`, `--autocompact`, `--bare`, `--betas`,
`--dangerously-skip-permissions`, `-d/--debug`, `--debug-file`, `--disallowedTools`, `--fallback-model`,
`--file`, `--fork-session`, `--forward-subagent-text`, `--include-hook-events`,
`--include-partial-messages`, `--input-format` (`text`|`stream-json`), `--json-schema`,
`--mcp-config`, `--model`, `--no-session-persistence`, `--output-format` (`text`|`json`|`stream-json`),
`--permission-mode` (`acceptEdits, auto, bypassPermissions, manual, dontAsk, plan`), `--plugin-dir`,
`--plugin-url`, `-p/--print`, `-r/--resume`, `--session-id`, `--setting-sources`, `--settings`,
`--strict-mcp-config`, `--system-prompt`, `--system-prompt-snapshot`, `--tools`, `--verbose`.

Documented on `code.claude.com/docs/en/cli-reference` (accessed 2026-09-27) but **not observed** in
local `--help`: `--max-turns`, `--max-budget-usd`, `--permission-prompt-tool`,
`--permission-prompts`, `--append-system-prompt-file`, `--system-prompt-file`,
`--exclude-dynamic-system-prompt-sections` (this one *does* appear locally too). These may be
present-but-undocumented-in-help (Commander.js sometimes hides less-common flags from the summary)
rather than absent — **UNVERIFIED**, settle by running `claude -p --max-turns 1 "..."` once.

### A.2 stream-json event shapes

From `code.claude.com/docs/en/headless` (accessed 2026-09-27), confirmed by doc text (not raw
type-file — the TypeScript SDK reference page's exact `SDKSystemMessage`/`SDKResultMessage`
interface text could not be extracted verbatim via automated fetch; see "could not determine"):

- **`system` / subtype `init`** — first event unless preceded by `plugin_install` or hook-lifecycle
  events. Documented fields: `plugins` (array of `{name, path}`), `plugin_errors`, `mcp_servers`
  (array of `{name, status}`), `mcp_server_errors`, `capabilities` (array of protocol-behavior
  strings, e.g. `interrupt_receipt_v1`, present v2.1.205+). The page also references model, tools
  and session metadata generically ("reports session metadata including the model, tools, MCP
  servers, and loaded plugins") without printing the full JSON key list in the fetched excerpt.
  Example shape (reconstructed from documented fields, not a literal doc quote):
  ```json
  {"type":"system","subtype":"init","session_id":"...","model":"claude-sonnet-4-6",
   "tools":["Read","Grep","Glob","Bash"],"mcp_servers":[],"plugins":[],
   "capabilities":["interrupt_receipt_v1"]}
  ```
  `apiKeySource` as a named field of `system/init` was **not found verbatim** in fetched docs —
  UNVERIFIED (see below).
- **`assistant`/`user` messages** — carry a `parent_tool_use_id` field; `null` for the main
  conversation, set to the spawning tool-call ID for subagent messages (requires
  `--forward-subagent-text` or `CLAUDE_CODE_FORWARD_SUBAGENT_TEXT` to see subagent *text/thinking*
  blocks; `tool_use`/`tool_result` blocks appear by default).
- **`result`** — final line of the stream. Quote: *"The last line of the stream is a `result`
  message with the final response text, cost, and session metadata."* With `--output-format json`,
  confirmed fields: `result` (text), `session_id`, `total_cost_usd`, per-model cost breakdown,
  and with `--json-schema`, a `structured_output` field: *"The response includes metadata about the
  request (session ID, usage, etc.) with the structured output in the `structured_output` field."*
  Subtypes `success` / `error_max_turns` / `error_during_execution` are referenced by the task brief
  and are consistent with Claude Agent SDK community usage, but the **exact enumerated subtype list
  and `is_error`/`num_turns`/`duration_ms` field names could not be confirmed verbatim** from a
  fetchable primary-source page in this session — the TypeScript reference page's type-definition
  section did not come through in automated fetches (see "could not determine").
- **`system/api_retry`** — fully documented with fields `type`, `subtype`, `attempt`, `max_retries`,
  `retry_delay_ms`, `error_status`, `error` (enum incl. `authentication_failed`, `rate_limit`,
  `billing_error`, etc.), `uuid`, `session_id`.
- **Permission denials** — with `--permission-prompts none`, denials appear as `permission_denied`
  system messages, and the final `result` message lists them in a `permission_denials` field.

### A.3 Auth behaviour and "never silently switch to metered billing"

- Default: a Pro/Max/Team/Enterprise user's stored OAuth login (`/login` credential) is used; this
  is explicitly last/lowest in the precedence list, meaning **any** of the other credential sources
  present in the environment overrides it silently in `-p` mode (see finding 3 above).
- Env vars that force metered API billing: `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`,
  `CLAUDE_CODE_USE_BEDROCK`, `CLAUDE_CODE_USE_VERTEX`, `CLAUDE_CODE_USE_FOUNDRY`,
  `CLAUDE_CODE_OAUTH_TOKEN` (long-lived token, still billed to subscription but bypasses interactive
  `/login`), `ANTHROPIC_PROFILE` / WIF federation vars, and any `apiKeyHelper` setting.
- Detect installed + signed in **without spending usage**: `claude auth status --json` — verified
  locally, returns immediately with no model call:
  ```json
  {"loggedIn": true, "authMethod": "claude.ai", "apiProvider": "firstParty",
   "email": "...", "subscriptionType": "pro"}
  ```
  This is the officially exposed, documented subcommand (`claude auth --help` lists `status`) and is
  the recommended detection surface — no credential files need to be read. `claude doctor` is also
  documented as a no-network-call health check ("Check the health of your Claude Code
  installation... without a trust prompt") but does not report auth state as cleanly as `auth
  status --json`.
- **Recommended guarantee mechanism for Lumen** (synthesizing the above; this part is opinion/design,
  not a doc quote):
  1. Before spawning, run `claude auth status --json` once per session-start to confirm
     `loggedIn:true` and capture `subscriptionType`.
  2. Build the child process environment from a **clean allowlist**, not by inheriting the parent
     env: explicitly `Environment.SetEnvironmentVariable` only what's needed, and explicitly leave
     out/clear `ANTHROPIC_API_KEY`, `ANTHROPIC_AUTH_TOKEN`, `CLAUDE_CODE_USE_BEDROCK`,
     `CLAUDE_CODE_USE_VERTEX`, `CLAUDE_CODE_USE_FOUNDRY`, `CLAUDE_CODE_OAUTH_TOKEN`,
     `ANTHROPIC_PROFILE`, `ANTHROPIC_FEDERATION_RULE_ID`, `ANTHROPIC_ORGANIZATION_ID` — unless the
     user's chosen provider mode is explicitly "Direct API".
  3. Never pass `--bare` in subscription mode (it forces API-key/keychain-free auth).
  4. If Lumen ever needs to inspect which credential source was actually used for a completed run,
     there is no `apiKeySource` field confirmed in `result`/`init` from this session's fetches;
     fall back to checking `/status`-equivalent behavior isn't available headlessly, so the
     env-allowlist approach above is the **only reliable enforcement point** Lumen fully controls —
     treat it as a precondition, not something to verify after the fact from stream-json.

### A.4 Policy — quoted, primary source

`code.claude.com/docs/en/legal-and-compliance` (accessed 2026-09-27), section "Authentication and
credential use":

> "OAuth authentication is intended exclusively for purchasers of Claude Free, Pro, Max, Team, and
> Enterprise subscription plans and is designed to support ordinary use of Claude Code and other
> native Anthropic applications... Anthropic does not permit third-party developers to offer
> Claude.ai login into their own applications, or to route requests through Free, Pro, or Max plan
> credentials on behalf of their users. Moreover, developers may not collect, store, or intermediate
> Claude.ai credentials or session tokens — sign-in to a Claude account must complete through
> Anthropic's own flow."
>
> "Nor does it prevent an end user from signing in to the unmodified Claude Code binary with their
> own Claude subscription, including where a platform hosts Claude Code as described under *Can
> customers offer Claude Code in their products?*"

And from "Can customers offer Claude Code in their products?":

> "The Claude Code binary must not be modified. Claude Code must be installed and run as published
> by Anthropic, and customers may not remove, disable, or restrict any authentication method built
> into it... Customers may not pay for, resell, or intermediate Claude usage on their end users'
> behalf. Each end user must authenticate with their own Anthropic API key, Claude subscription plan
> credentials, or 3P inference provider credential."

**Clearly allowed**: Lumen spawning the official, unmodified `claude` binary that the user
themselves installed and signed into, letting Anthropic's own `/login` OAuth flow run unmodified,
with the user's own subscription billed directly to Anthropic. This is exactly TDD §37.2's design.

**Clearly disallowed**: Lumen offering its own "Sign in with Claude" inside the app, caching or
proxying the OAuth token, or billing Lumen's own account for users' usage.

**Unclear / contact-sales territory**: nothing found suggests Lumen's exact design is unclear — the
carve-out language directly matches it. The remaining ambiguity is operational, not legal: exactly
how strictly "must not be modified" is interpreted if Lumen wraps `claude` with a supervisor process
that sets env vars and flags (this is normal subprocess invocation, not modification of the binary,
and matches the doc's own examples of `-p`/CLI usage) — treat as low risk, not verified with Anthropic
directly. **Compliant path**: invoke the real installed `claude`/`claude.exe`, never fork/patch it,
never store its OAuth token, and let auth failures surface to the user via `/login`.

### A.5 Claude Agent SDK — languages, .NET, CLI-wrapping

From `code.claude.com/docs/en/agent-sdk/overview` (accessed 2026-09-27): Python and TypeScript only.
Quote: *"An agent is an application that completes a task... The Agent SDK gives you the same
tools, agent loop, and context management that power Claude Code, programmable in Python and
TypeScript."* And: *"A library that runs the Claude Code binary"* — i.e. the SDK is a wrapper around
the same `claude` CLI binary, not a separate inference client. No .NET/C# SDK exists. Explicit
guidance for other languages: *"To drive the same agent loop from a language other than Python or
TypeScript, [run the CLI as a subprocess] with the `-p` flag and `--output-format json`."*

Additional policy quote directly on this page (separate from the legal-and-compliance page above,
same substance): *"Unless previously approved, Anthropic does not allow third party developers to
offer claude.ai login or rate limits for their products, including agents built on the Claude Agent
SDK. Use the API key authentication methods described in the Quickstart instead."* — this is the SDK
quickstart's framing (aimed at typical SaaS-style third-party products); it is reconciled with the
CLI carve-out above by the distinction between (a) a product that offers its **own** login/quota
using Anthropic's backend, which is disallowed, and (b) a product that shells out to the user's own
already-authenticated Claude Code binary, which is allowed per legal-and-compliance's explicit
carve-out.

**Recommendation**: a .NET app should spawn `claude -p` directly as a subprocess (no SDK available in
.NET; the TS/Python SDKs offer nicer ergonomics like typed messages and callback-based tool
permission but are themselves CLI wrappers, so a direct subprocess call loses only convenience, not
capability).

### A.6 Windows specifics

- Executable resolved locally at `C:\Users\khail\.local\bin\claude.exe` (native installer path,
  not the npm shim). An npm-installed alternative typically resolves to a `.cmd` shim under
  `%APPDATA%\npm\claude.cmd`; when spawning from .NET, resolve via `where claude` or check both
  known install locations rather than hard-coding one.
- Prefer passing the prompt as the last positional CLI argument for short prompts, or via stdin
  (`cat file | claude -p`) for larger inputs — the docs note *"Piped stdin is capped at 10MB."*
  `System.Diagnostics.Process` on Windows should set `RedirectStandardInput = true` and write the
  prompt to `StandardInput`, then close it, rather than relying on argv quoting.
- Quoting pitfalls: Windows argv quoting for `ProcessStartInfo.ArgumentList` (recommended — avoids
  manual quoting entirely) rather than building a single `Arguments` string. Tool-scope strings like
  `Bash(git diff *)` contain parentheses and spaces meaningful to the shell if invoked via `cmd /c`;
  using `ArgumentList` (argv-array) sidesteps shell reinterpretation entirely and is the safer path
  in .NET.
- Working directory: set `ProcessStartInfo.WorkingDirectory` explicitly to the worktree path (per
  TDD §41's "explicit working directory" requirement) rather than relying on `--add-dir` or `-C`
  (Claude Code has no `-C`; use the process's own working directory plus `--add-dir` for any *extra*
  directories beyond the cwd).

## B. Codex

### B.1 `codex exec` (confirmed locally, `codex-cli 0.153.4`, `codex exec --help`)

Flags present: `-c/--config`, `--enable`/`--disable` (feature flags), `--strict-config`,
`-i/--image`, `-m/--model`, `--oss`, `--local-provider`, `-p/--profile`, `-s/--sandbox` (values
`read-only`, `workspace-write`, `danger-full-access`), `--approve-for-me`,
`--dangerously-bypass-approvals-and-sandbox`, `--dangerously-bypass-hook-trust`, `-C/--cd`,
`--add-dir`, `--thread-source`, `--skip-git-repo-check`, `--ephemeral`, `--ignore-user-config`,
`--ignore-rules`, `--output-schema <FILE>`, `--color`, `--json` (JSONL to stdout),
`-o/--output-last-message <FILE>`.

Per `learn.chatgpt.com/docs/developer-commands?surface=cli` (redirected from
`developers.openai.com/codex/cli/reference`, accessed 2026-09-27): `--json` event types are
`thread.started`, `turn.started`, `item.*`, `turn.completed` (carries usage), and `error`.
`--sandbox workspace-write` is described as preferred "for unattended local work that can stay
inside the workspace." `--ask-for-approval` (`on-request`|`never`) is documented for `exec` on that
page but was **not observed** in local `codex exec --help` (only visible on the top-level `codex`
command in this installed version) — version-drift, flagged above.

`codex app-server`: JSON-RPC-ish daemon over stdio/websocket. Per the fetched summary: *"primarily
for development and debugging"* and *"Experimental... may change without notice."* **Not**
recommended as the integration surface for a stable product today; `codex exec --json` is the
documented, stabler surface for one-shot programmatic runs.

### B.2 Auth

- `codex login` — browser OAuth via ChatGPT account (confirmed locally: `codex login status` →
  "Logged in using ChatGPT", no `--json` output option on that subcommand — confirmed by direct
  test, `codex login status --json` errors `unexpected argument '--json' found`). Detection without
  spending: `codex login status` is a local, offline status check (text output only in this
  version).
- API-key mode: `codex login --with-api-key` (reads key from stdin, e.g.
  `printenv OPENAI_API_KEY | codex login --with-api-key`) or `--with-access-token`. Once logged in
  with an API key, "some features that rely on ChatGPT workspace access or cloud services are
  limited or unavailable" (per `learn.chatgpt.com/docs/auth`).
- No `OPENAI_API_KEY`/`CODEX_API_KEY` env-var precedence table as detailed as Anthropic's was found
  in the fetched pages — Codex's auth model is login-state-based (`codex login` writes credentials
  to `$CODEX_HOME`) rather than the multi-source env-var precedence Claude Code documents.
  **UNVERIFIED**: exact behavior if `OPENAI_API_KEY` is set in the environment *while* also logged in
  via `codex login` (ChatGPT) — settle by inspecting `codex doctor` output or Codex's own docs page
  on environment variables, not reached in this session.

### B.3 Policy

See finding 2 above: no Anthropic-style explicit carve-out found. `learn.chatgpt.com/docs/auth`
recommends API-key auth for "programmatic Codex CLI workflows, such as CI/CD jobs" and cautions
against exposing "Codex execution in untrusted or public environments" — consistent with, but not
as specific as, Anthropic's third-party language. The Codex CLI itself is Apache-2.0 licensed
(confirmed via `gh api repos/openai/codex` — `license.name: "Apache License 2.0"`), so there is no
"binary must not be modified" constraint as with Claude Code, but that also means there is no
equivalent explicit permission to shell out to the user's ChatGPT-authenticated `codex` the way
Anthropic explicitly permits for `claude`.

## C. Recommendation for Lumen's `IAgentProvider`/`AgentSession` (Claude Code first)

**Command line for a read-only investigation** (e.g. Correctness/Architecture/History Investigator
from TDD §11):

```
claude -p --output-format stream-json --verbose --include-partial-messages \
  --permission-mode dontAsk \
  --allowedTools "Read,Grep,Glob,Bash(git log *),Bash(git diff *),Bash(git show *),Bash(git blame *)" \
  --disallowedTools "Edit,Write,WebFetch,*" \
  --add-dir <worktree-path> \
  --no-session-persistence \
  --json-schema '<InvestigationResult-shaped schema>' \
  "<investigator prompt>"
```

- `--permission-mode dontAsk` denies anything not explicitly allow-listed instead of prompting (no
  human is present to answer a prompt in a headless investigator).
- `--allowedTools` restricted to read/search tools plus a narrow, read-only `Bash(git ...)`
  allowlist — matches TDD §41's "command allowlist / policy" requirement; do **not** grant bare
  `Bash` (arbitrary command execution) for a read-only investigator role.
- `--disallowedTools "Edit,Write,WebFetch,*"` — belt-and-braces: explicit deny of mutation and
  network-fetch tools (the trailing bare `Edit`/`Write` names remove the tool from Claude's context
  entirely, per the doc's semantics, rather than merely denying calls).
- `--add-dir` scopes file access to the isolated worktree from TDD §12; do not run from/grant the
  user's home directory.
- `--no-session-persistence` avoids leaving transcripts on disk for an ephemeral investigation
  (`-p`-only flag).
- Do **not** pass `--bare` (breaks subscription auth) and do **not** pass `--dangerously-skip-permissions`.

**Typed final answer**: use `--json-schema` shaped to (a subset of) `InvestigationResult` from TDD
§13 — Claude Code validates the schema and returns `structured_output` in the final `json`/
`stream-json` result message. If `--json-schema` proves too restrictive for a given investigator
prompt (e.g. it needs free-form `Evidence` lists), fall back to: instruct the prompt to emit a single
fenced JSON block as its final answer, use `--output-format json`, and parse the `result` string with
a permissive JSON extractor (regex for the last ```json fence, then `System.Text.Json` parse) —
treat parse failure as `InvestigationOutcome.Inconclusive`, not a crash.

**Timeouts / cancellation**: wrap the spawned process in `Process` + `CancellationToken`; on
cancellation or timeout, do **not** send `SIGKILL`/`Process.Kill()` alone — Claude Code's own docs
describe graceful shutdown semantics for `SIGTERM` (finishes the in-flight tool call, runs
`SessionEnd` hooks, terminates any running Bash command's process tree) vs `SIGINT` (ends the turn
cleanly). .NET doesn't have direct POSIX-signal parity on Windows for a Win32 process, so in
practice: call `Process.CloseMainWindow()`/attempt graceful stdin-close first (closing stdin is
documented to make Claude Code end the turn on SIGTERM-triggered exit), then `Process.Kill(entireProcessTree: true)` after a grace
period (e.g. 5s) if it hasn't exited — this also guards against orphaned child `Bash` processes,
which is a known Windows `Process.Kill()` gotcha (must pass `entireProcessTree: true`, available
since .NET 5+).

**Output size limits**: stream-json can be arbitrarily large; read `StandardOutput` as an async
stream line-by-line (don't buffer whole output), enforce a byte cap (e.g. 20 MB) and kill the
process if exceeded, per TDD §41's "output limits."

**Detecting subscription vs metered billing**: run `claude auth status --json` at provider
initialization (cheap, no model call) to populate `AgentAuthenticationState`/`UsageStatus`
capability; enforce billing-mode guarantee via the environment allowlist in A.3 above rather than by
inspecting the `init`/`result` stream events (no confirmed `apiKeySource` field to key off of from
this session's research — see "could not determine").

## What I could not determine

- **Exact `SDKSystemMessage`/`SDKResultMessage` TypeScript interfaces**, including whether
  `apiKeySource` is a literal field name on `system/init`, and the literal enumerated
  `result.subtype` values / exact field names (`is_error`, `num_turns`, `duration_ms`). Automated
  fetches of `code.claude.com/docs/en/agent-sdk/typescript` repeatedly returned the page's
  installation/`Options`-type section rather than the message-type reference, and the TS SDK's
  source (`github.com/anthropics/claude-agent-sdk-typescript`) does not expose a top-level
  `src/types.ts` via the GitHub contents API in this session. **Settle by**: `npm view
  @anthropic-ai/claude-agent-sdk` then reading the published `.d.ts` from the installed package, or
  running one real `claude -p --output-format json "hello"` call (accepting the small token cost)
  and inspecting the actual JSON.
- **Whether `--max-turns`/`--max-budget-usd`/`--permission-prompt-tool`/`--permission-prompts` are
  truly absent from installed v2.1.257 or just hidden from `--help`.** Settle by executing one
  `claude -p --max-turns 1 "ping"` (small cost) or diffing against the changelog for the exact
  version pin Lumen ships against.
- **Whether `codex exec --ask-for-approval` works despite not appearing in local `--help`.** Settle
  the same way, or check `codex exec --help` against the specific Codex version Lumen pins.
- **OpenAI's explicit position on third-party apps invoking `codex` with ChatGPT sign-in.** No
  primary source found stating this is permitted or forbidden; the GitHub maintainer explicitly
  declined to clarify. Settle by contacting OpenAI developer support/legal before shipping a
  ChatGPT-login Codex integration as a default (non-opt-in) path.
- **Interaction between `OPENAI_API_KEY` env var and an active `codex login` (ChatGPT) session** —
  not found in the pages reached this session.

## Opinion

Ship the Claude Code integration first, exactly as TDD §37.2 describes, using the environment
allowlist + `claude auth status --json` precondition check as the enforcement mechanism for "never
silently switch to metered billing" — this is fully supported by Anthropic's own documented
precedence rules and is cheap to implement (no extra infrastructure, just careful `ProcessStartInfo`
construction). For Codex, given the unresolved third-party policy question, default new Lumen
installs to **API-key mode for Codex** and gate ChatGPT-login mode behind an explicit, clearly-labeled
opt-in ("uses your ChatGPT sign-in — third-party use of this is not clearly documented by OpenAI"),
revisiting once OpenAI publishes clearer guidance or Lumen gets direct confirmation. Cost-wise, both
paths are effectively free to integrate (subprocess spawning, no SDK license fees); the real cost is
support burden if Anthropic's Agent SDK credits rollout resumes and changes the shared-vs-separate
usage pool without much notice — worth a changelog watch on `support.claude.com` article 15036540
rather than one-time verification.

## Sources

- https://code.claude.com/docs/en/headless (accessed 2026-09-27)
- https://code.claude.com/docs/en/cli-reference (accessed 2026-09-27)
- https://code.claude.com/docs/en/authentication (accessed 2026-09-27)
- https://code.claude.com/docs/en/costs (accessed 2026-09-27)
- https://code.claude.com/docs/en/agent-sdk/overview (accessed 2026-09-27)
- https://code.claude.com/docs/en/legal-and-compliance (accessed 2026-09-27)
- https://support.claude.com/en/articles/15036540-use-the-claude-agent-sdk-with-your-claude-plan (accessed 2026-09-27)
- https://www.anthropic.com/legal/aup (accessed 2026-09-27)
- https://www.anthropic.com/legal/consumer-terms (accessed 2026-09-27, note: fetched via automated summarizer, not raw text — treat quotes there as paraphrase-level, cross-check against `legal-and-compliance` page which was fetched more directly)
- https://developers.openai.com/codex/auth → https://learn.chatgpt.com/docs/auth (accessed 2026-09-27)
- https://developers.openai.com/codex/cli/reference → https://learn.chatgpt.com/docs/developer-commands?surface=cli (accessed 2026-09-27)
- https://github.com/openai/codex/discussions/8338 (accessed 2026-09-27)
- https://github.com/openai/codex (LICENSE via GitHub API, Apache-2.0, accessed 2026-09-27)
- https://venturebeat.com/technology/anthropic-reinstates-openclaw-and-third-party-agent-usage-on-claude-subscriptions-with-a-catch (secondary source, used only to locate the primary support.claude.com article; accessed 2026-09-27)
- Local verification: `claude --version` → 2.1.257; `claude --help`; `claude -p --help`; `claude auth --help`; `claude auth status --json`; `claude doctor --help`; `codex --version` → codex-cli 0.153.4; `codex --help`; `codex exec --help`; `codex login --help`; `codex login status`; `codex login status --json` (errors — flag not supported); `codex app-server --help`; `codex mcp-server --help` — all run 2026-09-27, no prompts sent, no sign-in/out performed.
- `docs/tdd.md` §11–13, §37, §41 (this repo, read 2026-09-27) — design constraints this report validates against.
