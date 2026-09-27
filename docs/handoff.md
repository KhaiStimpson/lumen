# Handoff — Phase 3 (JEV) / Phase 4 (agents)

Branch `phase3-4-jev-agents`, worktree `C:\Dev\repos\lumen\.claude\worktrees\phase3-4` (branched from main @ 5b8ebfb;
no git remote; fast-forwarded into main). Status, design and deviations: [plan.md](plan.md) → "Phase 3 (JEV) and Phase 4".

## State

P3.1–P4.4 are done and committed, plus an in-app **Connections** panel (plug button in the title bar; TDD §39) that
stores the OpenRouter key through the engine and shows JEV/Claude status. `dotnet build Lumen.slnx` has 0 errors (the
warnings are the pre-existing Avalonia `Watermark` ones) and `dotnet test Lumen.slnx` passes (298 tests). Only **P4.5, the live checks**, remains, and it needs you.

## Needs the user (nothing here has been done without asking)

1. **JEV live check**: store an OpenRouter key (Connections panel, or `Lumen.Engine.exe connections set-openrouter-key`), then run
   `LUMEN_LIVE_JEV=1 dotnet test tests/Lumen.Jev.Tests --filter LiveJevTests`. Metered; roughly $0.00002 per call.
   If it returns 503, the TypeSafe endpoint may not be ZDR-eligible — set `"jev": { "requireZeroDataRetention": false }`
   (the state is source-free either way) and say so in plan.md.
2. **Claude live check**: `LUMEN_LIVE_CLAUDE=1 dotnet test tests/Lumen.Agents.Tests --filter LiveClaudeCodeTests`.
   Uses Claude Pro quota (currently signed in, per `claude auth status`). This also confirms the unverified bits: the
   `structured_output` field on the `result` event and whether `--json-schema` + `--restricted` behave as expected.
3. **PR #58 end to end** with `settings.json` `{ "privacy": { "allowCodeToAgents": true }, "agents": { "enabled": true } }`,
   then record results in plan.md "How it behaves on PR #58".

Never post to GitHub without an explicit go-ahead.

## Loose ends worth knowing

- The researchers stopped at a usage limit right after writing their reports; the OpenRouter claims were re-verified
  against the raw `openapi.yaml`. `typesafe/jev-latest`/`jev-router` are unverified (not used).
- `LUMEN_LIVE_PR` builds the real engine, so it will call JEV/agents if they're configured.
