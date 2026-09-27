# Technical Design Document — Agentic Pull Request Review Desktop App

**Working title:** CodeReview  
**Status:** Draft  
**Target:** Desktop application for GitHub pull request review  
**Primary platform:** Windows first, cross-platform capable  
**UI framework:** Avalonia UI recommended, Uno Platform viable alternative  
**Backend/runtime:** .NET 8/9  
**Core idea:** A minimal, visually refined code review environment that keeps code central while using deterministic analysis, JEV triage, repository precedent, review memory, and specialist agents to direct human attention toward the parts of a pull request that deserve real judgment.

---

## 1. Executive Summary

AI-assisted development is increasing the volume and size of pull requests. The bottleneck is shifting from code generation to human review.

The purpose of this application is not to replace reviewers or produce large volumes of AI comments. It is to make high-quality human review faster by:

1. Keeping the code diff as the primary review surface.
2. Identifying a small number of high-value **review points**.
3. Showing repository precedent beside questionable code.
4. Detecting pattern deviations, architectural drift, missing tests, suspicious abstractions, correctness risks, and behavioural changes.
5. Learning from:
   - the user's past review comments,
   - comments others leave on the user's PRs,
   - repository conventions,
   - historical PR discussions.
6. Using JEV as a fast attention-routing model.
7. Using Claude/Codex-style agents only for deeper investigations.
8. Using static analysis and executable evidence before generative reasoning where possible.
9. Presenting all results through a minimal, animated, high-quality desktop interface.

The product should feel like an expert reviewer working beside the user, not like an AI chatbot attached to GitHub.

---

# 2. Product Principles

## 2.1 Code remains the primary surface

The user should still review actual code.

The app must not hide the PR behind summaries, scores, or agent dashboards.

The default workflow is:

```text
Open PR
  ↓
Review code
  ↓
System marks important locations
  ↓
Reviewer inspects / compares / investigates
  ↓
Comment, dismiss, or continue
```

---

## 2.2 One review point at a time

The UI should avoid information density.

At any moment, the application should foreground:

- one code region,
- one review question,
- one evidence summary,
- one obvious next action.

Detailed evidence remains available through progressive disclosure.

---

## 2.3 Evidence over AI opinion

Prefer:

> A generated concurrency test reproduced duplicate processing in 3/20 runs.

over:

> The AI thinks this code may have a concurrency problem.

Every review point should carry provenance.

---

## 2.4 Human judgment is final

The system may:

- prioritise,
- classify,
- retrieve precedent,
- investigate,
- reproduce,
- compare.

The reviewer decides whether something matters.

---

## 2.5 Every review should improve future reviews

Accepted review feedback should gradually become reusable repository knowledge.

Desired loop:

```text
Human review comment
      ↓
Candidate convention
      ↓
Repeated evidence
      ↓
Strong repository precedent
      ↓
Automated future detection
      ↓
Eventually explicit analyzer/rule where appropriate
```

---

# 3. Framework Decision

## 3.1 Recommendation: Avalonia UI

Avalonia is the preferred implementation framework.

### Why

- Strong fit for a desktop-first product.
- XAML-based UI model familiar to .NET developers.
- Good control over custom rendering.
- Suitable for highly bespoke editor-style interfaces.
- Cross-platform support for Windows, macOS, and Linux.
- Easier to create a distinctive desktop application than adapting a mobile-first design system.
- Strong compatibility with MVVM.
- Skia-backed rendering is useful for custom visualisations and animations.

### Recommended stack

```text
Avalonia UI
ReactiveUI or CommunityToolkit.Mvvm
.NET 9
ASP.NET Core local daemon
Roslyn
LibGit2Sharp and/or git CLI
SQLite
Microsoft.Data.Sqlite
Dapper or EF Core
gRPC / named pipes / local HTTP
OpenTelemetry
```

---

## 3.2 Uno Platform alternative

Uno is viable if the product later prioritises:

- Windows-native WinUI semantics,
- WebAssembly,
- mobile clients,
- stronger Fluent alignment.

For this application, however, Avalonia is likely better because the design should not look like standard WinUI or a generic enterprise application.

---

# 4. High-Level Architecture

```text
┌──────────────────────────────────────────────────────┐
│                  Desktop Client                      │
│                                                      │
│  Diff editor   Review points   Generated surfaces    │
│  Animations    Navigation      User actions          │
└───────────────────────┬──────────────────────────────┘
                        │ local IPC
                        ▼
┌──────────────────────────────────────────────────────┐
│                 Local Review Engine                  │
│                                                      │
│ Git / GitHub       Roslyn        Static analysis     │
│ Repo index         Retrieval     Review memory       │
│ Change model       JEV routing   Agent coordinator   │
│ Test runner        Worktrees     Evidence store      │
└───────────────┬─────────────────────────┬────────────┘
                │                         │
                ▼                         ▼
        External Models              GitHub API
        JEV                           PRs
        Claude / Codex               Reviews
        OpenRouter/etc.              Comments
                                     Checks
```

---

# 5. Major Components

## 5.1 Desktop Client

Responsibilities:

- render GitHub pull request state,
- render diffs efficiently,
- display review points inline,
- animate between review states,
- show precedent comparisons,
- show generated investigative surfaces,
- capture reviewer interactions,
- submit GitHub comments/reviews,
- display pre-review feedback on the user's own PRs.

The desktop client should not directly orchestrate agents.

---

## 5.2 Local Review Engine

A background .NET process launched with the app.

Responsibilities:

- repository checkout integration,
- diff parsing,
- semantic indexing,
- Roslyn analysis,
- static tool execution,
- change unit generation,
- repository precedent retrieval,
- JEV evaluation,
- agent scheduling,
- agent worktrees,
- review-memory persistence,
- evidence aggregation,
- incremental invalidation.

The engine should be independently restartable.

---

## 5.3 GitHub Adapter

Responsibilities:

- OAuth / GitHub App authentication,
- repository metadata,
- pull requests,
- commits,
- changed files,
- review comments,
- review threads,
- issue links,
- check runs,
- posting review comments,
- submitting review state,
- fetching historical PR discussions.

Prefer GitHub GraphQL for relationship-heavy queries and REST for actions where simpler.

---

# 6. Core Domain Model

## 6.1 PullRequestSnapshot

```csharp
public sealed record PullRequestSnapshot(
    string RepositoryId,
    int PullRequestNumber,
    string BaseSha,
    string HeadSha,
    PullRequestMetadata Metadata,
    IReadOnlyList<ChangedFile> Files,
    IReadOnlyList<ReviewThread> Threads,
    IReadOnlyList<CommitInfo> Commits);
```

Immutable.

A snapshot is keyed by:

```text
repository + PR number + head SHA
```

---

## 6.2 ChangeUnit

The main semantic unit of analysis.

A `ChangeUnit` represents an idea or behaviour that changed, not merely a diff hunk.

```csharp
public sealed record ChangeUnit
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required ChangeKind Kind { get; init; }

    public required IReadOnlyList<SymbolReference> Symbols { get; init; }

    public required IReadOnlyList<DiffRange> DiffRanges { get; init; }

    public IReadOnlyList<string> Concepts { get; init; } = [];

    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
}
```

Example:

```text
ChangeUnit
Title: Invoice claiming
Before: QueueClaimService.TryClaimAsync
After: direct EF query followed by status update
Dimensions:
- concurrency
- persistence
- repository convention
```

---

## 6.3 ReviewPoint

```csharp
public sealed record ReviewPoint
{
    public required string Id { get; init; }

    public required string ChangeUnitId { get; init; }

    public required ReviewPointType Type { get; init; }

    public required string Summary { get; init; }

    public required ReviewSeverity Severity { get; init; }

    public required ReviewPointState State { get; init; }

    public IReadOnlyList<EvidenceReference> Evidence { get; init; } = [];

    public IReadOnlyList<ConventionReference> Conventions { get; init; } = [];

    public IReadOnlyList<InvestigationReference> Investigations { get; init; } = [];
}
```

### Types

```text
PatternDeviation
CorrectnessRisk
BehaviourChange
ArchitectureDrift
MissingCoverage
PotentialOverengineering
Duplication
ErrorHandling
PerformanceRisk
SecurityRisk
HumanDecision
```

---

## 6.4 Evidence

Everything shown to the reviewer must resolve to evidence.

```csharp
public abstract record Evidence
{
    public required string Id { get; init; }
    public required EvidenceSource Source { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}
```

Subtypes:

```text
StaticAnalysisEvidence
RepositoryPrecedentEvidence
ReviewHistoryEvidence
GeneratedTestEvidence
RuntimeEvidence
AgentInvestigationEvidence
GitHistoryEvidence
ArchitectureRuleEvidence
CoverageEvidence
```

---

# 7. Repository Understanding

## 7.1 Roslyn workspace

For .NET repositories:

1. Load solution/project via `MSBuildWorkspace`.
2. Resolve:
   - symbols,
   - references,
   - inheritance,
   - implementations,
   - call relationships,
   - attributes,
   - nullable context,
   - diagnostics.
3. Persist semantic metadata keyed by source hash.

Avoid treating code solely as text embeddings.

---

## 7.2 Repository graph

Persist a local graph:

```text
Symbol
 ├── Calls → Symbol
 ├── CalledBy → Symbol
 ├── Implements → Symbol
 ├── DefinedIn → File
 ├── CoveredBy → Test
 ├── ModifiedBy → Commit
 ├── DiscussedIn → PullRequest
 └── AssociatedWith → Convention
```

Initial implementation can use SQLite relational tables rather than introducing a graph database.

---

## 7.3 Semantic search

Embeddings remain useful for:

- finding semantically similar implementations,
- matching historical review discussions,
- finding relevant ADRs,
- locating conceptually related code where symbol relationships are insufficient.

Recommended architecture:

```text
lexical search
+
symbol graph
+
embedding retrieval
+
reranker
```

Never rely on embeddings alone for repository precedent.

---

# 8. Change Unit Generation

Pipeline:

```text
git diff
   ↓
syntax diff
   ↓
changed symbols
   ↓
dependency neighbourhood
   ↓
semantic clustering
   ↓
ChangeUnits
```

Signals used for clustering:

- same method/type,
- same call chain,
- same business concept,
- same feature area,
- same data-flow transition,
- same test coverage,
- commit grouping.

LLM assistance may be used to name a change unit, but the underlying membership should be deterministic where possible.

---

# 9. Static Analysis Layer

Static analysis executes before model-based review.

## .NET initial integrations

- Roslyn compiler diagnostics
- nullable reference analysis
- dotnet format analyzers
- StyleCop where configured
- custom Roslyn analyzers
- NetArchTest
- dependency direction analysis
- public API compatibility
- test discovery
- coverage delta
- database migration inspection
- secret scanning
- Semgrep
- CodeQL integration where available

The system normalises all findings into evidence.

Example:

```text
Raw Semgrep issue
      ↓
Evidence record
      ↓
Attach to ChangeUnit
      ↓
JEV determines review relevance
```

---

# 10. JEV Attention Engine

JEV should remain almost invisible to the user.

Its responsibility is classification and routing.

It should not act as the final reviewer.

## 10.1 Inputs

Compact structured state:

```json
{
  "changeKind": "background-worker-state-transition",
  "filesChanged": 3,
  "repositoryPrecedent": {
    "queueClaimService": 12,
    "directDbContext": 2
  },
  "historicalReviewSignals": 4,
  "staticFindings": [
    "non-atomic-select-update"
  ],
  "testsChanged": true
}
```

---

## 10.2 Questions

Typed bounded evaluations:

```text
Is this change mechanical?
Does this change observable behaviour?
Does this deviate from repository precedent?
Does it require human judgement?
Does it deserve deeper correctness investigation?
Does it require architecture investigation?
Is repository history likely useful?
Is existing deterministic evidence sufficient?
```

Outputs must be constrained enums or probabilities.

---

## 10.3 Routing

Example:

```text
if mechanical > 0.95
    suppress

if precedent_deviation > 0.75
    retrieve repository precedent

if correctness_investigation > 0.7
    schedule correctness agent

if architecture_investigation > 0.8
    schedule architecture agent

if human_judgement > 0.8
    create candidate review point
```

Thresholds should become adaptive over time.

---

# 11. Agent Orchestration

## 11.1 Specialist agents

Do not implement a generic "review this PR" agent.

Initial roles:

### Correctness Investigator

Goal:

> Find a concrete scenario in which the changed behaviour fails.

Tools:

- source search,
- call graph,
- tests,
- temporary worktree,
- execute test,
- inspect logs.

---

### Repository Pattern Investigator

Goal:

> Compare the changed implementation against analogous implementations in the repository.

Outputs:

- dominant precedent,
- exceptions,
- relevant code examples,
- explanation of why precedent exists if history reveals it.

---

### Architecture Investigator

Goal:

> Detect new dependency directions, layer violations, architectural drift, and unnecessary abstractions.

---

### Test Investigator

Goal:

> Identify changed behaviour lacking executable evidence.

May generate temporary tests.

---

### History Investigator

Goal:

> Determine why the existing implementation or convention exists.

Uses:

- git log,
- blame,
- old PRs,
- review threads,
- ADRs.

---

### Security Investigator

Triggered selectively.

Goal:

> Test changed trust boundaries, authentication, authorisation, sensitive data, injection, secrets, and dependency changes.

---

# 12. Agent Execution Environment

Each mutation-capable agent receives an isolated Git worktree.

```text
repo/
  reviewer/
  .review-agent/
      correctness-001/
      tests-002/
      architecture-003/
```

Agents may:

- modify files,
- add temporary tests,
- run commands.

Agent changes must never affect the user's working tree.

All generated files are ephemeral unless the user explicitly asks to adopt them.

---

# 13. Investigation Contract

Agents must return structured results.

```csharp
public sealed record InvestigationResult
{
    public required string InvestigationId { get; init; }

    public required string Claim { get; init; }

    public required InvestigationOutcome Outcome { get; init; }

    public required IReadOnlyList<Evidence> Evidence { get; init; }

    public IReadOnlyList<Evidence> CounterEvidence { get; init; } = [];

    public IReadOnlyList<SourceReference> RelevantSources { get; init; } = [];

    public GeneratedExperiment? Experiment { get; init; }

    public bool RecommendReviewPoint { get; init; }
}
```

Agents cannot directly create UI warnings.

The review engine decides whether an investigation becomes a visible review point.

---

# 14. Evidence Aggregation

The evidence engine merges multiple sources.

Example:

```text
Potential concurrency issue

Evidence:
✓ Roslyn/data-flow sees select then update
✓ 12 analogous workers use atomic QueueClaimService
✓ historical PR says QueueClaimService prevents duplicate work
✓ generated test reproduced duplicate processing
✕ one short investigator run did not reproduce
```

Do not average this into a single opaque "87% confidence" number.

Expose evidence state:

```text
Verified
Strong evidence
Conflicting evidence
Unverified
Human decision
```

---

# 15. Review Memory

Three memory scopes are required.

## 15.1 Repository memory

Represents conventions inferred from the codebase and its history.

Example:

```text
Background workers should claim queue items using QueueClaimService.
```

---

## 15.2 Team review memory

Derived from historic review conversations.

Example:

```text
Business orchestration is consistently moved out of controllers.
```

---

## 15.3 Personal review memory

Derived from:

- comments the user leaves,
- findings the user dismisses,
- review suggestions they repeatedly make,
- review feedback others leave on the user's PRs.

Example:

```text
User frequently flags interfaces with only one implementation.
```

Personal memory affects prioritisation, not correctness.

---

# 16. Convention Model

```csharp
public sealed record ReviewConvention
{
    public required string Id { get; init; }

    public required ConventionScope Scope { get; init; }

    public required string Statement { get; init; }

    public required IReadOnlyList<EvidenceReference> SupportingEvidence { get; init; }

    public IReadOnlyList<EvidenceReference> ContradictingEvidence { get; init; } = [];

    public required ConventionStrength Strength { get; init; }

    public required DateTimeOffset LastObservedAt { get; init; }
}
```

Strength lifecycle:

```text
Candidate
↓
Recurring
↓
StrongPrecedent
↓
ExplicitRule
```

---

# 17. Learning from Reviews

GitHub events captured:

```text
review comment created
review comment resolved
review requested changes
review approved
suggested change accepted
finding dismissed
author pushed response commit
PR merged
PR reverted
```

The system uses these events to update convention evidence.

Example:

```text
Reviewer repeatedly says:
"Use QueueClaimService here."

PR #1: comment
PR #2: comment
PR #3: accepted fix
PR #4: same feedback

→ Candidate convention becomes StrongPrecedent.
```

---

# 18. Reviewing the User's Own PR

If the logged-in user is the PR author, enable **Before Review** mode.

The system compares the change against:

- team conventions,
- prior feedback received,
- repository precedent,
- common review concerns.

Example:

```text
Before requesting review

1. Retry behaviour lacks integration coverage
   Similar feedback: PR #1718

2. Worker contains orchestration logic
   Similar feedback: 4 previous PRs

3. External call does not demonstrate idempotency
   Similar feedback: PR #1655
```

This should feel like a private pre-flight review.

---

# 19. UX Architecture

## 19.1 Primary layout

```text
┌──────────────┬───────────────────────────────┬───────────────┐
│ Navigation   │                               │ Review point  │
│ / files      │            CODE               │ context       │
│              │                               │               │
│              │                               │ precedent     │
│              │                               │ evidence      │
└──────────────┴───────────────────────────────┴───────────────┘
```

Code must occupy the majority of horizontal space.

---

# 20. Guided Review Mode

Users can toggle:

```text
Normal
Guided
```

Guided mode navigates between review points.

```text
← Previous          Review point 3 / 7          Next →
```

The code editor automatically scrolls to the relevant hunk.

Animations connect navigation transitions spatially.

---

# 21. Inline Review Point

A review point appears as a compact card beneath or beside the affected hunk.

Example:

```text
⚠ Pattern deviation

This worker bypasses QueueClaimService and differs
from established repository precedent.

[Show precedent] [Why this matters] [...]
```

Only the summary is initially visible.

---

# 22. Examine Mode

Selecting a review point transitions into a focused view.

The code remains visible.

The contextual pane transforms based on evidence type.

Examples:

### Pattern deviation

```text
This PR                  Existing precedent

direct DbContext         QueueClaimService
select + update          atomic claim
```

### Concurrency

Animated sequence diagram.

### API behaviour

Before/after request-response examples.

### Architecture

Minimal dependency graph.

### Tests

Behaviour-to-test coverage map.

The generated UI must be constrained to known visual primitives rather than arbitrary model-generated XAML.

---

# 23. Generative UI System

The model selects a presentation schema.

Example:

```json
{
  "surface": "comparison",
  "left": {
    "title": "This PR",
    "source": "InvoiceRetryWorker.cs"
  },
  "right": {
    "title": "Established precedent",
    "source": "EmailWorker.cs"
  },
  "highlights": [
    "queue-claim"
  ]
}
```

Renderer components:

```text
CodeComparisonSurface
SequenceSurface
DataFlowSurface
ArchitectureSurface
TestEvidenceSurface
HistoryTimelineSurface
BeforeAfterBehaviourSurface
```

LLMs never produce executable UI code.

---

# 24. Visual Design

## 24.1 Aesthetic

Target qualities:

- minimal,
- premium,
- highly legible,
- desktop-native,
- calm,
- dense only where code requires density,
- visually distinct from GitHub and VS Code.

Avoid:

- chatbot sidebars,
- gradients everywhere,
- glassmorphism overload,
- oversized AI icons,
- generic "AI assistant" cards,
- dashboard-heavy interfaces.

---

# 25. Motion System

Motion is a core product feature.

It should help maintain spatial context.

## 25.1 Review point navigation

When pressing `Next`:

1. current card subtly contracts,
2. code scrolls using spring interpolation,
3. next relevant lines highlight,
4. context pane morphs into the new evidence surface.

Duration target:

```text
180–320 ms
```

---

## 25.2 Inline → Examine transition

The review card should visually expand into the examine panel.

Use shared-element transitions for:

- title,
- severity badge,
- file reference,
- relevant code range.

---

## 25.3 Evidence arrival

As background analysis completes:

Do not use toast spam.

Instead:

- small indicator softly appears beside relevant code,
- gutter marker animates once,
- optional subtle pulse.

---

## 25.4 Respect reduced motion

All transitions must have reduced-motion equivalents.

---

# 26. Avalonia Animation Implementation

Use:

- transitions,
- `TransformOperations`,
- opacity,
- clipping,
- custom easing,
- composition APIs where available.

For advanced surfaces:

- custom drawing via Avalonia rendering APIs,
- SkiaSharp for diagrams if required.

Create a reusable motion service:

```csharp
public interface IMotionService
{
    Task TransitionAsync(
        Visual source,
        Visual destination,
        MotionPreset preset,
        CancellationToken cancellationToken);
}
```

Motion presets:

```text
NavigateReviewPoint
ExpandFinding
CollapseFinding
RevealEvidence
SwitchInvestigationSurface
OpenPrecedent
```

---

# 27. Diff Renderer

Do not use a browser-based diff for the primary editor if avoidable.

Requirements:

- virtualised rendering,
- syntax highlighting,
- unified and split modes,
- inline review cards,
- gutter indicators,
- minimap review markers,
- smooth large-file scrolling,
- keyboard navigation,
- selectable code.

Potential approaches:

1. Custom Avalonia text renderer.
2. Extend AvaloniaEdit.
3. Monaco embedded in WebView as fallback.

Preferred starting point:

**AvaloniaEdit + custom diff layer**, then replace components if performance/design constraints require it.

---

# 28. Review Gutter

Gutter markers:

```text
red      correctness
amber    questionable / deviation
blue     behavioural
purple   architecture
green    verified
```

Markers should not rely solely on colour.

Use unique shapes/icons.

---

# 29. Keyboard-First Interaction

Core shortcuts:

```text
J / K      next / previous review point
Enter      examine
D          dismiss
C          comment
P          show precedent
E          evidence
F          files
Ctrl+K     command palette
Esc        return to diff
```

---

# 30. Command Palette

Examples:

```text
Show repository precedent
Explain why this is flagged
Try to reproduce this
Find similar implementations
Show previous reviews
Generate a test
Ask agent about selected code
Open in GitHub
```

This replaces persistent chatbot UI.

---

# 31. Contextual Agent Interaction

Chat exists but is transient.

User selects code and invokes:

```text
Ask
```

Possible prompts:

```text
Why is this unusual?
Find precedent.
Try to break this.
Show me how this repository usually does this.
Generate a test.
Trace where this value comes from.
```

Agent responses should preferentially produce artifacts or evidence surfaces rather than long prose.

---

# 32. Persistence

Use SQLite.

Tables:

```text
Repositories
PullRequestSnapshots
ChangedFiles
Symbols
SymbolEdges
ChangeUnits
ReviewPoints
Evidence
Investigations
Conventions
ReviewInteractions
ReviewThreads
Embeddings
CacheEntries
```

---

# 33. Cache Strategy

Cache all expensive analysis using:

```text
repository
commit SHA
tool version
analysis version
```

Example key:

```text
acme/billing-service:
abc123:
roslyn-symbol-index:
v4
```

---

# 34. Incremental Re-analysis

When the PR head changes:

```text
old SHA → new SHA
```

Calculate:

- changed files,
- changed symbols,
- impacted graph neighbourhood,
- invalidated ChangeUnits,
- invalidated investigations,
- relevant tests.

Reuse everything else.

UI should display:

```text
Since your last review

2 findings resolved
1 changed
1 new
```

---

# 35. Agent Scheduler

Use a priority queue.

```csharp
public sealed record InvestigationJob
{
    public required InvestigationType Type { get; init; }

    public required double Priority { get; init; }

    public required string ChangeUnitId { get; init; }

    public required InvestigationBudget Budget { get; init; }
}
```

Priority influenced by:

- JEV attention score,
- user viewport,
- review point order,
- severity,
- user review preferences.

Visible code receives priority over background analysis.

---

# 36. Agent Budgeting

Deep analysis is expensive.

Define budgets:

```text
Tiny
Standard
Deep
```

Example:

```text
Tiny:
  max 1 model call
  no worktree

Standard:
  max 4 model calls
  source search
  optional static command

Deep:
  worktree
  generated tests
  multiple reasoning cycles
```

JEV chooses whether escalation is justified.

---

# 37. Provider and Authentication Abstraction

Do not hard-code Claude, Codex, API-key-only access, or any single commercial model provider.

A core requirement is that users can use coding-agent access already included with supported **Claude / Anthropic** and **ChatGPT / OpenAI** subscriptions where the provider officially supports that workflow, rather than being forced to create a separate API key.

The product must distinguish between:

```text
Model provider
      │
      ├── Subscription-backed coding agent
      │      ├── Claude Code
      │      └── Codex
      │
      ├── Direct API
      │      ├── Anthropic API
      │      ├── OpenAI API
      │      └── OpenRouter
      │
      └── Local / custom provider
```

The architecture must not assume that a provider is only an HTTP completion endpoint.

## 37.1 Agent provider contract

```csharp
public interface IAgentProvider
{
    string Id { get; }

    AgentProviderCapabilities Capabilities { get; }

    Task<AgentAuthenticationState> GetAuthenticationStateAsync(
        CancellationToken cancellationToken);

    Task<AgentSession> StartSessionAsync(
        AgentSessionRequest request,
        CancellationToken cancellationToken);
}
```

Provider capabilities:

```text
InteractiveSessions
StructuredOutput
ToolUse
RepositoryAccess
WorktreeAccess
SubscriptionAuthentication
ApiKeyAuthentication
UsageStatus
ModelSelection
```

## 37.2 Claude subscription integration

Preferred subscription-backed path:

```text
CodeReview
   ↓
ClaudeCodeProvider
   ↓
Official Claude Code CLI / supported local integration
   ↓
User's authenticated Claude subscription
```

Claude Pro and Max subscriptions include Claude Code access, and supported Team / Enterprise seats can also authenticate Claude Code using the user's subscription.

The application must **not** scrape Claude, impersonate an Anthropic client, or extract/reuse private subscription OAuth tokens.

Instead:

1. Detect an installed official Claude Code client.
2. Detect whether Claude Code is authenticated.
3. If necessary, launch the official Claude Code sign-in flow.
4. Invoke Claude Code through a supported local CLI or machine-readable integration.
5. Convert streaming output, tool calls, patches, and results into the common `AgentSession` model.
6. Keep direct Anthropic API access as a separate optional mode.

```text
Claude

● Claude subscription — Connected
  via Claude Code

○ Anthropic API
  Add API key
```

The engine must respect Claude Code's subscription usage limits. It must never silently switch a user from included subscription usage to metered API or usage-credit billing.

## 37.3 ChatGPT subscription integration

Preferred subscription-backed OpenAI path:

```text
CodeReview
   ↓
CodexProvider
   ↓
Official Codex CLI / app-server integration
   ↓
User's authenticated ChatGPT account
```

Codex is included with supported ChatGPT plans and supports signing in with a ChatGPT account.

Support two distinct OpenAI modes:

```text
ChatGPT / Codex subscription
OpenAI Platform API key
```

Implementation:

1. Detect the official Codex client.
2. Query authentication state where supported.
3. Launch the official Codex / ChatGPT sign-in flow when required.
4. Prefer a supported machine-readable surface such as Codex CLI JSON output or app-server integration.
5. Convert events, tool calls, patches, and results into the common `AgentSession` model.
6. Keep OpenAI API access as a separate optional provider.

```text
OpenAI

● ChatGPT subscription — Connected
  via Codex

○ OpenAI API
  Add API key
```

A ChatGPT subscription is not arbitrary OpenAI API quota. Subscription mode means invoking the supported Codex product surface, not reusing a ChatGPT credential against private APIs.

## 37.4 Direct API providers

Direct API mode remains available for users who prefer metered usage or require capabilities not available through subscription-backed coding agents.

Initial direct API providers:

```text
Anthropic API
OpenAI API
OpenRouter
Custom OpenAI-compatible endpoint
```

Credentials must be stored in the platform secure credential store.

## 37.5 Provider routing

Agent roles may choose a provider explicitly or use user-defined routing:

```yaml
agents:
  correctness:
    preferredProvider: codex-subscription
    fallbackProvider: claude-subscription

  architecture:
    preferredProvider: claude-subscription
    fallbackProvider: openai-api

  tests:
    preferredProvider: codex-subscription
```

Fallback from one subscription-backed provider to another may be automatic if enabled.

Fallback from subscription usage to a metered API must always require explicit opt-in.

---

# 38. JEV via OpenRouter

JEV uses **OpenRouter** as the initial supported integration and remains independent of Claude and ChatGPT subscription authentication.

```text
Review Engine
    ↓
OpenRouterJevEvaluator
    ↓
OpenRouter Decisions API
    ↓
TypeSafe JEV
```

Recommended configurable model identifiers:

```text
typesafe/jev-1.13
~typesafe/jev-latest
```

Prefer a pinned JEV version for reproducible evaluations and benchmark results. Offer `latest` as an explicit opt-in.

```csharp
public interface ISystemOneEvaluator
{
    Task<SystemOneResult<TDecision>> EvaluateAsync<TDecision>(
        SystemOneState state,
        SystemOneQuestion<TDecision> question,
        CancellationToken cancellationToken);
}

public sealed class OpenRouterJevEvaluator : ISystemOneEvaluator
{
    // Calls OpenRouter Decisions API.
}
```

Configuration:

```text
JEV

Provider: OpenRouter
API key: ************
Model: typesafe/jev-1.13

[ Test connection ]
```

JEV/OpenRouter credentials are separate from Claude, ChatGPT, Anthropic API, and OpenAI API credentials.

## 38.1 JEV request strategy

Batch related bounded questions into one Decisions API request where practical.

Persist:

```text
selected choice
probabilities
confidence metadata
model/version
question schema version
```

This supports threshold calibration and replayable evaluations.

## 38.2 JEV failure behaviour

If OpenRouter or JEV is unavailable, deterministic analysis and rule-based routing continue to function. JEV availability must never block a review.

---

# 39. Authentication and Connections

Treat each service as an independent connection with separate credentials, billing semantics, usage limits, and status.

```text
Connections

GitHub
● Connected

Claude
● Claude subscription via Claude Code
○ Anthropic API key

OpenAI
● ChatGPT subscription via Codex
○ OpenAI API key

JEV
● OpenRouter API
  typesafe/jev-1.13
```

The UI must clearly distinguish **included subscription usage** from **metered API usage**.

---

# 39A. GitHub Authentication

Recommended:

GitHub OAuth Device Flow initially.

Potential later migration:

GitHub App for team deployments.

Store tokens using platform keychain:

```text
Windows Credential Manager
macOS Keychain
Linux Secret Service
```

Never store raw credentials in SQLite.

---

# 40. Privacy

Repository code may be sensitive.

Settings must expose:

```text
Allow cloud reasoning
Allow code snippets to JEV
Allow code snippets to Claude/OpenAI
Local models only
Store review history locally
Share conventions with team
```

Redaction layer before outbound requests.

---

# 41. Security Boundaries

The local review engine executes code.

All agent command execution must have:

- explicit working directory,
- command allowlist / policy,
- timeout,
- output limits,
- no access to user home by default,
- no inherited secrets unless required.

Long term: container/sandbox support.

---

# 42. Telemetry

Opt-in telemetry only.

Useful product metrics:

```text
time-to-review
review points examined
review points dismissed
review comments created
agent investigations opened
precedent comparisons opened
PR revisit time
false-positive rate
```

Never upload repository content as telemetry.

---

# 43. Observability

Use OpenTelemetry.

Trace:

```text
PR load
repository indexing
static analysis
JEV pass
investigation
test generation
GitHub calls
render latency
```

This product will have complicated latency; traces are essential.

---

# 44. Performance Targets

## Initial PR load

Cached repository:

```text
UI visible: < 1 second
basic diff: < 2 seconds
initial review markers: < 5 seconds
```

Deep agents continue asynchronously.

---

## Diff interaction

```text
scroll target: 60 FPS
review point transition: no dropped frames
navigation response: < 100 ms
```

---

# 45. State Machine

Each PR:

```text
Loading
↓
Indexed
↓
InitialAnalysis
↓
ReviewReady
↓
ReviewInProgress
↓
ReviewSubmitted
```

Each ReviewPoint:

```text
Candidate
↓
Visible
├── Dismissed
├── Commented
├── Examined
└── Resolved
```

---

# 46. Event Bus

The review engine should be event-driven.

Example events:

```text
PullRequestLoaded
RepositoryIndexed
ChangeUnitCreated
StaticEvidenceAdded
JevDecisionProduced
InvestigationScheduled
InvestigationCompleted
ReviewPointCreated
ReviewPointDismissed
ReviewCommentSubmitted
PullRequestHeadChanged
```

Use an internal channel-based event bus initially.

No need for external infrastructure.

---

# 47. Local IPC

Options:

1. Named pipes
2. Unix sockets
3. localhost gRPC

Recommended:

**gRPC over local named pipe / Unix socket where practical.**

Benefits:

- strong typed contracts,
- streaming events,
- clear desktop/daemon boundary.

---

# 48. Streaming Analysis

Client subscribes:

```text
WatchPullRequest(prId)
```

Stream:

```text
SnapshotReady
ReviewPointAdded
ReviewPointUpdated
EvidenceAdded
InvestigationProgress
AnalysisComplete
```

The UI updates progressively.

---

# 49. Minimal Agent Status UX

Do not show an "Agents" dashboard by default.

At most:

```text
Analysing 3 areas…
```

Expanded diagnostics may show:

```text
Pattern investigator
Correctness investigator
Test investigator
```

but only on demand.

---

# 50. Review Point Ranking

Ranking should combine:

```text
correctness impact
behaviour impact
repository deviation
review history relevance
deterministic evidence
human judgement requirement
user preference
```

JEV can help with ranking but must not be the sole signal.

---

# 51. Avoid Generic AI Confidence

Do not display:

```text
AI confidence: 83%
```

Prefer:

```text
Strong repository precedent
Race reproduced
Conflicting evidence
No executable evidence
```

---

# 52. MVP Scope

## Phase 1 — Guided Diff

Ship:

- GitHub login,
- open PR,
- diff viewer,
- Roslyn semantic analysis,
- repository precedent retrieval,
- basic static analysis,
- review points,
- guided navigation,
- GitHub comments,
- clean animations.

Do not ship autonomous agents yet.

---

## Phase 2 — Review Memory

Add:

- ingest historical reviews,
- candidate conventions,
- personal review patterns,
- feedback received on user's PRs,
- Before Review mode.

---

## Phase 3 — JEV Attention Engine

Add:

- typed classification,
- change prioritisation,
- investigation routing,
- adaptive suppression.

---

## Phase 4 — Investigation Agents

Add:

- correctness investigator,
- pattern investigator,
- test investigator,
- isolated worktrees,
- generated tests.

---

## Phase 5 — Generative Surfaces

Add:

- code comparison,
- sequence diagrams,
- behaviour before/after,
- architecture maps,
- test evidence surfaces.

---

## Phase 6 — Team Intelligence

Add:

- shared convention store,
- team-specific review patterns,
- explicit rule suggestions,
- analyzer generation.

---

# 53. Suggested Solution Structure

```text
src/

  CodeReview.App/
      Views/
      ViewModels/
      Controls/
      Motion/
      Theme/
      Diff/
      GeneratedSurfaces/

  CodeReview.Engine/
      PullRequests/
      ChangeAnalysis/
      ReviewPoints/
      Evidence/
      Investigations/
      Memory/

  CodeReview.GitHub/
      Authentication/
      GraphQL/
      Rest/
      Mapping/

  CodeReview.Roslyn/
      Workspace/
      Symbols/
      DataFlow/
      Diagnostics/

  CodeReview.Repository/
      Git/
      Search/
      Graph/
      Embeddings/

  CodeReview.StaticAnalysis/
      Roslyn/
      Semgrep/
      CodeQL/
      Architecture/
      Tests/

  CodeReview.Jev/
      Questions/
      State/
      Routing/

  CodeReview.Agents/
      Orchestration/
      Roles/
      Tools/
      Providers/
      Worktrees/

  CodeReview.Storage/
      SQLite/
      Migrations/
      Repositories/

  CodeReview.Contracts/
      Grpc/
      Events/
      DTOs/

tests/

  CodeReview.Engine.Tests/
  CodeReview.Roslyn.Tests/
  CodeReview.Agents.Tests/
  CodeReview.App.Tests/
  CodeReview.IntegrationTests/
```

---

# 54. Testing Strategy

## Unit tests

Focus on:

- change unit generation,
- review point rules,
- ranking,
- memory updates,
- evidence merging,
- routing decisions.

---

## Golden tests

Store representative PR fixtures.

Assert:

```text
expected ChangeUnits
expected precedent retrieval
expected review points
expected hidden noise
```

These become critical for controlling regressions in AI behaviour.

---

## Agent evaluation suite

Create benchmark PRs containing:

- obvious race condition,
- subtle architectural deviation,
- redundant abstraction,
- missing test,
- false-positive bait,
- intentional repository exception.

Track:

```text
precision
recall
review-value rating
cost
latency
```

---

## UI snapshot tests

Important surfaces:

- normal diff,
- inline finding,
- examine mode,
- before review,
- dark/light themes,
- reduced motion.

---

# 55. Failure Handling

If JEV fails:

- continue using deterministic ranking.

If Claude/Codex fails:

- show deterministic evidence only.

If semantic indexing fails:

- fall back to textual diff.

If GitHub is unavailable:

- cached PR remains viewable.

The product should degrade gracefully rather than block review.

---

# 56. Open Technical Questions

1. AvaloniaEdit performance on very large diffs.
2. Whether Roslyn workspace loading is fast enough for very large monorepos.
3. Best representation for cross-language repositories.
4. Local embeddings versus hosted embedding provider.
5. How aggressively review memory should generalise.
6. Cost/latency profile of JEV per ChangeUnit.
7. Best sandbox model for agent-generated tests.
8. Whether team conventions should remain local or synchronise via cloud service.
9. How to reliably identify code generated mechanically versus semantically meaningful code.
10. Whether review point generation should be fully event-driven or periodically reconciled.

---

# 57. Recommended First Prototype

Build a narrow vertical slice.

Repository:

```text
.NET repository
GitHub PR
5–20 changed files
```

Capabilities:

1. Load PR.
2. Render diff.
3. Use Roslyn to identify changed symbols.
4. Find similar implementations.
5. Produce one type of review point:
   **Pattern deviation**.
6. Show:
   - inline finding,
   - repository precedent,
   - side-by-side comparison.
7. Navigate findings with J/K.
8. Dismiss or post GitHub comment.
9. Animate transitions beautifully.

Do not start with agents.

Prove that:

> repository-aware guided review is genuinely faster and better than standard GitHub review.

Then introduce JEV as the routing layer and agents only after the core interaction feels excellent.

---

# 58. Design North Star

The technical architecture should always serve this interaction:

```text
Reviewer scrolls through real code.

Something important receives a subtle marker.

They stop.

The app explains:
"This differs from how this repository normally solves this."

One click reveals precedent.

Another click can investigate deeper.

The reviewer understands the issue, decides, and moves on.

Everything else disappears.
```

The sophistication should live under the surface.

The product should feel simple.
