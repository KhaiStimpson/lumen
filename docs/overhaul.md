# Kelpie Review overhaul — design and plan

Status: draft, revised after review · Written: 2026-11-27 · Replaces: `docs/review-triage-plan.md` (Phases 5–7),
the AI parts of `docs/plan-v2.md`, and the Map (`docs/map.md`). Mockups: `docs/design/overhaul/mockups.html`.

## Why

Kelpie Review was built to make large AI-generated pull requests manageable. It doesn't do that yet:

- The AI parts (JEV, the fast-model walkthrough, investigation agents) can't be used at work, so the app has to
  stand on its deterministic analysis alone.
- The triage tiers (Critical / Worth a look / Skim / Skip) failed on every count:
  - The ranking wasn't trusted, so everything got read anyway.
  - The plan pane was one more thing to manage.
  - Hunks were the wrong unit.
  - Even after mechanical removal, too much was left. On andrew-crm#58, 3,228 of 3,887 lines were new code, and
    all of it landed in Skim.
- Peer patterns answer "what is unusual", not "what needs judgement".

What worked: the **review client** (inbox, threads, comments, pending review, submit), **mechanical detection**
(0 false positives across a 123-commit sweep), and **Roslyn code links** (what uses what).

Research (four surveys, 2026-11-27) agrees with this:

- Reviewers miss far more past a few hundred lines.
- File order alone changes what gets caught.
- Splitting a changeset into cohesive parts helps reviewers.
- The best non-AI tools hide noise they can *prove* is noise, track what you've seen per revision, and flag
  risky paths by rule.

## The core

> Shrink a big pull request to what you actually have to read, and present it as **feature slices**, inline in
> the diff.

No rankings, no scores, no separate plan pane. The diff itself gets shorter and is grouped the way the code is
built.

**Trust rule:** anything that might change behaviour or security stays visible. Folding is only ever for code
that is proven mechanical, or that matches a short, explicit list of boilerplate shapes. A false fold is the
worst bug this effort can ship, because it brings back the distrust that killed the tiers.

### Inputs

- **GitHub pull requests**: the existing review client.
- **Local branches**: a clone path plus a base ref, for reviewing an agent's output before there's a PR. The
  head is either a branch or **the working tree, including uncommitted changes**. Same slicing, marks and flags;
  no posting.
  - **Read-only on your clone.** Committed content is read with `git show`. Uncommitted changes are snapshotted
    into a tree object through a private index (`GIT_INDEX_FILE` pointing at a file under `%LOCALAPPDATA%\Lumen`),
    so the snapshot has a hash like any commit. The tool never checks out, stashes, or writes to your working
    tree or index.
  - Storage keys on a **review target** (a PR, or clone path + base + head kind) instead of a PR number.
  - "Since my last look" uses a snapshot hash stored locally.

### Feature slices

A **slice** is the code behind one entry point. Its nodes are files and members.

**Entry points (seeds):**

- Razor pages: the main case at work.
- WebForms pages and controls.
- API controllers and minimal-API routes.
- Hosted services, middleware and filters.
- As a last resort, a changed test class with no other seed, together with the code it tests.

**What a Razor Pages slice holds:** the page is `Pages/**/X.cshtml` plus its `X.cshtml.cs` `PageModel`, and its
handlers (`OnGet`, `OnPostSave`…) are member nodes. Growing from there, the slice takes:

- partials and view components, plus the small JS files that belong to those partials and tag helpers (see
  jQuery below);
- the scripts the page loads (`<script src>`, `asp-src-include`, `@section Scripts`);
- the services the `PageModel` injects, whether as a class or through an interface:
  - an interface with exactly one implementation in the repo is followed to that implementation;
  - with more than one, each implementation is a "possible link" the reviewer can open, and is never merged in;
- from those services, on to repositories, the `DbContext` and entities;
- the `[BindProperty]` and input models, and the tests that touch any of these.

Pages link to each other through `asp-page`, `RedirectToPage` and `Url.Page`. Those links are shown, but they
don't merge slices.

**WebForms:** `.aspx`/`.ascx` + code-behind, the controls it registers, its scripts, and the services the
code-behind uses.

**jQuery (generic, best effort).** The JS setup at work is custom, so linking stays generic:

- A per-page script joins that page's slice.
- Small scripts for partials and tag helpers join wherever their partial or tag helper is used. A script used
  widely goes to Shared.
- Scripts loaded by `_Layout` (site-wide) never join a page slice.
- Links count as membership only when they're unambiguous: a literal URL or `Url.Page(...)` that resolves to a
  handler (`$.post('?handler=Save')` → `EditModel.OnPostSaveAsync`, matched with the page's `@page` route
  template), or an `#id` selector declared in exactly one view.
- Anything looser (`data-url` attributes, concatenated URLs, class selectors) shows as a **possible link** and
  never moves code into a slice.

**How slices are built:**

1. **A new link resolver.** The existing `CodeLinkFinder` matches bare type names and drops any name declared in
   more than two files, which breaks on Razor Pages, where every page has an `EditModel` or `IndexModel`.
   - The resolver qualifies names by namespace, `using`s and folder, and works at member level.
   - It reads from a repo-wide index (an extended `RepositoryTypeIndex`), not just the changed files.
   - New parsers: a `.cshtml` reader (directives, tag helpers, `<partial>`, `<script>`, `asp-*`, `@section`) and
     a JS reader (AJAX calls, `#id` selectors, `$(document).on`). Neither exists today.
2. **Seed and grow.** Seed one slice per changed entry point, then grow along the edges, with a depth cap.
3. **Shared.** A file reached from three or more slices goes to **Shared**, reviewed once. The threshold gets
   tuned on the fixture.
4. **Unlinked.** Changed code no seed reaches goes to **Unlinked**, which is never hidden. It has two groups:
   - "Used outside this change", with a count of unchanged callers;
   - "Nothing uses it".
5. **Large slices** are split into sub-sections by handler (e.g. `OnGet` / `OnPostSave`), each with its own
   progress.
6. **Reading order** inside a slice is by dependency: entities and input models → data → services →
   `PageModel`/code-behind → `.cshtml` → JS → tests.

Every edge carries its reason in words ("page loads `orders-edit.js`", "`EditModel` injects `IOrderService` →
`OrderService`"), so a slice can always explain why a file is in it.

**Fallback:** if the repo isn't Razor/WebForms-shaped, or slicing fails or runs past its time budget, the review
opens as today's file list with the mechanical folds. Slices appear when they're ready.

### Collapsing

Collapsed code is still in the diff, folded behind one line that gives the reason. A key or click opens it, and
the opened fold always shows every line. There are two kinds, labelled differently:

- **Mechanical · Proven (✓).** The existing classifiers: rename, move, formatting, imports, comments, ripple,
  generated (including `.designer.cs`), lockfiles, migrations (destructive operations stay expanded). New in
  this effort: vendored front-end code (`*.min.js`, `*.min.css`, `wwwroot/lib/**`, `libman.json`). The safety
  invariant is unchanged: any doubt means not mechanical.
  - Folding is an explicit `Fold` property, not `Tier == Skip`. Proven-but-worth-seeing changes (ripple call
    sites, cross-type moves) stay open, as `DiffFolds` does today.
- **Boilerplate · Not verified.** New code that matches one of a short list of shapes. Only these fold:
  - DI registrations: `services.Add{Scoped|Transient|Singleton}<…>()` calls with no lambda body. The summary
    names each lifetime, e.g. "3 registrations: 2 scoped, 1 singleton".
  - DTO and view-model classes made only of auto-properties with no initialiser, `{ get; set; }` or `init`, and
    validation or display attributes. The summary lists every attribute.
  - Mapping: blocks of plain `a.X = b.X` assignments, or AutoMapper `CreateMap<A,B>()` with no `ForMember`,
    `Ignore` or `ReverseMap`.
  - Test arrange blocks made only of object construction and assignments, with no mock `Setup`/`Returns`.

  **Never folded, whatever the shape:**
  - Bindable properties, new or changed: anything on a `[BindProperty]` model, a handler parameter model, or an
    entity.
  - Property initialisers.
  - Computed or expression-bodied getters.
  - Non-validation attributes (`[BindProperty(SupportsGet = true)]`, `[JsonIgnore]`, `[Column]`, `[Authorize]`…).
  - Razor markup, including `asp-for` fields and hidden inputs.
  - Any region that contains a flag.
  - Edits to existing boilerplate: these fold only if the change is purely added or removed members that would
    themselves fold.

### Tracking

- **Marks at file and member grain.** A mark records a fingerprint of the *normalised changed lines* of one file
  (or one member in a large file), ignoring line numbers and surrounding context.
  - A slice counts as reviewed when all its members are marked.
  - Slices merging or splitting, or a file moving into Shared, need no special handling.
  - When a push changes one file, only that file's mark clears.
- **"Mark reviewed" says what's still open.** If a slice has open flags or summarised folds nobody has opened,
  the button says so ("3 flags, 2 folds not opened"), and a second press confirms.
- **GitHub "Viewed" sync.** On a PR, once every slice a file belongs to is reviewed, the file's Viewed box is
  ticked on GitHub. If a push clears the mark, GitHub clears Viewed on its own when the file changes.
- **Since my last look** compares each file's patch against its own merge-base: `merge-base..old` against
  `merge-base'..new`. A rebase onto a newer `main` therefore doesn't flood the view.
  - If the old head no longer exists, it falls back to comparing file contents and says so (the app already
    shows "History was rewritten…").
  - Only slices with changed members are listed, each reduced to the range diff.

### Red flags

Deterministic facts, each shown inline on the line and counted on its slice. They have no tier and no score,
and they never reorder anything.

A flag's identity is (kind, member, normalised line). That identity lets "since my last look" show a flag as
**Fixed** or **New**.

| Flag | Detection |
|---|---|
| Weakened tests | test method deleted; `[Fact(Skip=…)]`, `[Ignore]`, `[Skip]` added; asserts removed with no replacement; an assert made vacuous (`Assert.True(true)`, expected value replaced with the actual); `.only`/`xit` in JS tests |
| Antiforgery off | `[IgnoreAntiforgeryToken]` added; `asp-antiforgery="false"`; `IgnoreAntiforgeryTokenAttribute` added in `Program.cs` conventions; a jQuery POST that sends neither a serialised form nor a `RequestVerificationToken` header |
| Authorisation loosened | `AuthorizeFolder`/`AuthorizePage` removed, `AllowAnonymousTo*` added, `FallbackPolicy` changed (`Program.cs`); `[Authorize]` removed from a `PageModel` or controller; `[AllowAnonymous]` added |
| Over-posting | a new property becomes bindable (on a `[BindProperty]` model or an entity bound directly) |
| Guard or validation removed | data-annotation attribute removed; `ModelState.IsValid` check removed; a null or argument guard removed with no `ThrowIfNull`, `?.` or `??` replacing it on the same expression |
| Raw output | `@Html.Raw`, `new HtmlString(`, and jQuery `.html(`, `.append(` or `$(…)` built by concatenation |
| Raw SQL | `FromSqlRaw`/`ExecuteSqlRaw` with interpolation or concatenation |
| Query safety | `IgnoreQueryFilters()` added |
| Swallowed errors | an empty `catch`, or a `catch` that neither logs nor rethrows; an empty jQuery `.fail(…)` |
| Warnings silenced | `#pragma warning disable`, `[SuppressMessage]`, analyser severity lowered in `.editorconfig` |
| Dependencies and build | NuGet package added or upgraded; new `<script src>` to a CDN; `.csproj`/`Directory.Build.*` changes other than package versions; pipeline YAML |
| Security config | CORS, cookie, HTTPS/HSTS or authentication options changed in `Program.cs` or `appsettings*.json` (`appsettings` edits elsewhere are not flagged) |

Flag text says only what its detector found. Cross-references like "Notes are typed in by users" appear only
when a detector produces them.

## What gets cut

In place, on a branch:

- **Projects:** `Lumen.Agents`, `Lumen.Jev`, `Lumen.Models` and their test projects; `GitAgentWorktreeFactory`.
- **Proto RPCs:**
  - `SetOpenRouterKey`, `RemoveOpenRouterKey`, `SetCloudflareCredentials`, `RemoveCloudflareCredentials`
  - `UpdateConnectionSettings`, `GetConnections`
  - `StartWalkthrough`, `SetReviewPointState`, `SetTriageGroupAcknowledged`
  - `PostReviewCommentRequest.review_point_id`
  - the `Tier` fields, replaced by `Fold` plus reason
- **Engine:** walkthroughs, investigation coordinator, connections command and probe, Cloud AI and JEV settings,
  review-point pipeline and `RuleBasedAttentionPolicy`. Bump `TriageRunner.ClassifierVersion` so cached triage
  JSON with tiers is recomputed.
- **Analysis:** `PeerPatternDetector`, `PeerDeviationExplainer`, tier assignment and summary text, and
  `ReviewSensitivity` and its presets. Keep the classifiers and `mechanical_paths`. Red flags get their own
  detector interface; the review-point `IChangeDetector`/`Candidate` types go.
- **Domain:** `Walkthroughs.cs` goes, after `FileLink`/`LinkKind` move to their own file (the new resolver uses
  them).
- **Storage:** a forward migration that drops `AttentionEvaluations`, `Investigations`, `Walkthroughs` and
  `ReviewInteractions`, and re-keys tables on review target.
- **App:**
  - Connections section, `CloudLimitsView`, `DailyUsageMeter`
  - walkthrough, `ReviewCardView`, `ExamineView`, `ReviewTrayView`
  - Review plan pane and `ReviewPlan`, tier heat in the file tree
  - Map tab (`Lumen.App/Map`, `MapViewModel`, `PullRequestViewModel.Map.cs`; move its since-last-look logic out
    first)
  - Review settings that only tune peer patterns
- **Settings:** drop unknown keys from `settings.json`; delete stored credentials for removed services.
- **Docs:** README sections on AI, JEV and the Map; mark `docs/review-triage-plan.md` and `docs/map.md` as
  superseded.

Kept: the inbox, PR conversation/commits/checks, threads, comments, pending review, split view, find, command
palette, the Gauge UI, `Lumen.Roslyn/Triage/*` classifiers, `RepositoryTypeIndex` (extended), storage, and the
fixture replay and capture tooling.

## UI

The mockups show five screens. Changes the review asked for, still to be made in the mockups:

- **Right pane** collapsible, and off by default below about 1400px wide. Even when collapsed, the flag list
  stays reachable from the slice header.
- **Fold label** reads "Boilerplate · Not verified". The bar segments for to-read, boilerplate and mechanical
  must reach 3:1 contrast against each other.
- **Inbox:** show "4,212 changed" alone until a review has been analysed. When the head has moved since
  analysis, mark the "to read" figure as out of date.
- **"Mechanical only"** lists groups ("rename `GetOrder` → `GetOrderAsync`, 12 sites"), not files.
- **Unlinked** shows its two groups (used outside this change, nothing uses it).
- **"Since my last look"** gets a state for rewritten history.
- **Keys** to be rechecked against existing bindings (`F`, `L`, `K` and `Space` are taken). Add previous file,
  previous flag, "open all folds in slice", and comment.
- **Local branches:** *Copy comments* copies them as a list to hand back to the agent. This came from the
  mockups and still needs your OK.

## Plan

Each phase ends green (`dotnet build` + `dotnet test`) with screenshots for anything visible, and lands on
`integration/overhaul` via its own branch. Nothing reaches `main` or GitHub without a go-ahead.

### Phase 0: Fixture and answer key

- [ ] Synthetic .NET 10 Razor Pages + jQuery app as the base commit, under `tests/fixtures/razor-pages-app/`. It
      should include:
      - Areas, partials with their own small JS files, a tag helper with its own JS, and a view component
      - `?handler=` AJAX
      - EF Core
      - services injected both as classes and through interfaces
      - a test project
- [ ] A large AI-style change on top (40+ files, 3,000+ lines): several features at once, new DTOs, DI
      registrations, mapping code, a few proven-mechanical changes, and planted flags written for Razor Pages
      (`[IgnoreAntiforgeryToken]` added, a removed `AuthorizeFolder`, a new bindable `IsAdmin`, a skipped test, a
      vendored jQuery plugin, an unrelated rewrite).
- [ ] Deliberate near-misses:
      - duplicate `EditModel` names in different folders
      - an interface with two implementations
      - a `Url.Page` built from variables
      - a shared site-wide script
      - a DTO with one initialiser
      - a mapping block with one `Ignore`
- [ ] **The answer key is written before Phase 3, by a separate agent that never sees the slicer.** It covers
      the expected slices (files and members), what may fold, and every planted flag.
- [ ] Record it as both a PR-shaped and a local-branch fixture.
- [ ] Baseline: changed lines, and lines left after mechanical folding alone.

### Phase 1: Strip back

- [ ] Delete everything in the cut list; regenerate the proto; storage migration; classifier version bump.
- [ ] Replace `Tier` with `Fold` + reason; `DiffFolds` keeps "proven but stays open".
- [ ] README and docs updated. Test count recorded before and after.

### Phase 2: Review targets and local input

- [ ] A `ReviewTarget` key (PR, or clone + base + head kind) through the domain, engine and storage.
- [ ] Local sessions read through `git show` and private-index snapshots of the working tree. Tests prove the
      user's working tree and index are untouched.
- [ ] App: a "Local branches" section on the inbox home screen, "Open local branch" from the go-to box, posting
      controls hidden.

### Phase 3: Link resolver

- [ ] Repo-wide index with qualified names and member nodes, built incrementally and cached per commit.
- [ ] C# edges: constructor injection (class, interface → single implementation, `IEnumerable<T>` as a possible
      link), `[FromServices]`, `DbContext` sets, entity use.
- [ ] `.cshtml` reader: pairing, `@page` routes, `@model`, partials, view components, tag helpers, layouts,
      scripts, `asp-page`/`asp-page-handler`, `RedirectToPage`/`Url.Page`, form handler → `OnPost{Handler}`.
- [ ] JS reader: literal and `Url.Page` AJAX URLs → handlers; `#id` selectors; everything else a possible link.
- [ ] WebForms pairing and `<%@ Register %>`.
- [ ] Every edge carries a reason. Unit tests for each convention, including the near-misses.

### Phase 4: Slices, engine and app

- [ ] Seeds, growth with a depth cap, Shared, Unlinked (two groups), handler sub-sections, reading order.
- [ ] Engine and proto: slices, edges, possible links and folds over the stream. Cached per (head, merge-base,
      slicer version).
- [ ] Time budget: on a 60-file PR, files show at once and slices arrive within an agreed limit (measured on
      the fixture). Falls back to the file list when the budget is exceeded or slicing fails.
- [ ] App: a slice list as the default left pane (file tree one toggle away), one slice at a time, "why this
      file is here" on each header, collapsible right pane.
- [ ] **Done when:**
      - slice membership reaches agreed precision and recall against the answer key;
      - no slice is over an agreed size;
      - Unlinked + Shared stay under an agreed share of to-read lines;
      - andrew-crm#58 replays without regressions;
      - **you have run it on a real work PR at work and reported where slices looked wrong** (nothing leaves
        work), and those cases are fixed or recorded.

### Phase 5: Boilerplate folding

- [ ] The listed shapes only, each with near-miss tests (an initialiser, a computed getter, `SupportsGet`, a
      mapping `Ignore`, a registration inside an `if`, a mock `Setup` in arrange).
- [ ] Bindable properties never fold; flagged regions never fold.
- [ ] A hand-read sweep of every boilerplate fold on the fixture and a commit history. Folds wrongly hiding
      logic or security: **0**, recorded like the Phase 2 mechanical sweep.
- [ ] Measured: lines left open by default (target agreed before tuning).

### Phase 6: Tracking

- [ ] Member and file fingerprints on normalised changed lines; slice "reviewed" worked out from them.
- [ ] "Mark reviewed" shows open flags and folds not yet opened; a second press confirms.
- [ ] GitHub Viewed sync when every slice a file belongs to is reviewed.
- [ ] Since-my-last-look per file against its merge-base, with the rewritten-history fallback.
- [ ] Progress: "4 of 9 slices reviewed, 2 changed since you marked them".

### Phase 7: Red flags

- [ ] One detector per row of the table, each with tests including near-misses (a guard converted to `?.`,
      catch-and-log, `appsettings` edits outside security config).
- [ ] Flag identity, and Fixed/New in since-my-last-look.
- [ ] A safety sweep on the fixture and a commit history: every flag read by hand, false positives recorded.

## Decisions log

All answered 2026-11-27:

- **Framework:** .NET 10 Razor Pages first, WebForms second.
- **Fixture:** synthetic, with the answer key written first by a separate agent. Before slicing is called done,
  you run it on real work PRs at work.
- **Home screen:** the inbox, with a Local branches section.
- **Local input:** includes uncommitted changes. Read-only on the clone.
- **DI:** both class and interface injection are used at work.
- **JS:** one script per page, plus many small scripts for partials and tag helpers. The setup is custom, so
  linking stays generic and best-effort.
- **Viewed sync:** slice marks tick GitHub's Viewed boxes.
- **Bindable properties:** always visible, never folded.

## Open questions

- Is three slices the right threshold for Shared? Tune on the fixture.
- Copy comments for local branches (from the mockups): keep?
