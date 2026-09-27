# Handoff — Phase 3 (JEV) / Phase 4 (agents)

Branch `phase3-4-jev-agents`, worktree `C:\Dev\repos\lumen\.claude\worktrees\phase3-4` (branched from main @ 5b8ebfb;
no git remote). The plan and checklist live in [plan.md](plan.md) under "Phase 3 (JEV) and Phase 4".

## Done

- Research reports: [research/openrouter-jev.md](research/openrouter-jev.md), [research/agent-providers.md](research/agent-providers.md)
  (the researchers hit a usage limit right after writing; check each report's "could not determine" section).
- Commit 19a348b: Domain `InvestigationResult`/`AgentInvestigationEvidence`/`PrivacySettings`/`ISecretStore`,
  `AttentionEvaluationRecord` + `IAttentionEvaluationStore`/`IInvestigationStore`; SQLite migration 2; `WindowsCredentialStore`;
  `IAttentionPolicy.DecideAllAsync` (default loops `DecideAsync`) + `AttentionDecision.Investigations`; `PipelineResult.Decisions`.
- Uncommitted: `src/Lumen.Agents` (csproj, `Execution/SandboxedCommand.cs`, `Execution/CommandPolicy.cs`) — not yet in `Lumen.slnx`.

## Next

1. Finish `Lumen.Agents`: `SandboxedProcessRunner` (resolve `.exe` on PATH only, scrubbed env, stdin prompt, timeout,
   output cap, process-tree kill), `IAgentProvider`/`AgentSession` (§37.1), `ClaudeCodeProvider`
   (`claude auth status --json`; `claude -p --output-format stream-json --verbose`; never `--bare`; abort if the init
   event shows an API key source). Add project + `tests/Lumen.Agents.Tests` to `Lumen.slnx`.
2. `Lumen.Jev`: `ISystemOneEvaluator`, versioned questions, OpenRouter evaluator (strict JSON schema, no-training/ZDR
   routing), `JevAttentionPolicy` wrapping `RuleBasedAttentionPolicy` (fallback on any failure; never surfaces what rules reject).
3. Engine: `settings.json` (privacy/JEV/agents; agents off by default), `connections` CLI (key from stdin → Credential
   Manager), investigation planner/scheduler, `ReviewPointUpdated` + `InvestigationStatus` proto events.
4. App: "Analysing N areas…" status; agent evidence label in Examine → Evidence.

## Needs the user

- An OpenRouter API key (and OK to spend a few cents) for the live JEV test.
- A go-ahead before any live Claude Code investigation (uses their Pro subscription quota; `claude auth status` shows signed in).
- Never post to GitHub without an explicit go-ahead.

## Checks

`dotnet build Lumen.slnx` and `dotnet test Lumen.slnx` were green at the last commit (storage tests updated for schema v2).
