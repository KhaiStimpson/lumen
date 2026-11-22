# The Map

The **Map** tab (`M`) shows a pull request as the system it builds, not as a list of files. It answers two questions
before you read a line: *how does this change fit together?* and *where are the lines that need reading?*

- **Blueprint** (`B`) places the change's **components** (for example "Nightly scheduler", "Ingestion pipeline",
  "Contact data model") by how they use each other: callers on the left, what they call on the right, connectors
  between them. Each box holds its files.
- **Terrain** (`T`) places the same boxes by size: every component and file is sized by lines added.
- Switching between them **morphs** each box and tile from one place to the other, so a file can be followed between
  the two views. Reduced motion jumps instead.
- **Lenses** (`L` cycles) colour the files: **Attention** (the walkthrough's risky areas in amber, open threads as blue
  badges), **Since my review** (what changed after the commit you last reviewed), **Tests** (code a test in the pull
  request exercises, and code none does).
- Selecting a file shows its component's role, what the component **uses** and is **used by** (with the walkthrough's
  labels), which tests exercise the file, its risks and threads. `Enter` or double-click opens it in the diff; a risk
  opens at its line.
- Tests and generated files stay off the map and are counted below it.

The Map never waits on the model. Without a walkthrough it still shows a terrain of the change grouped by folder; the
change groups stand in when the model didn't name components; links come from the code whether or not the model ran.

## How it works

```text
changed C# files at head ─► CodeLinkFinder (Roslyn syntax) ─► file links: implements, depends on, references, tests
                                                                     │
file summaries (walkthrough map step) ──────────────────────────────┤
                                                                     ▼
                        walkthrough/components (one fast-model call): components with title, role, layer, paths,
                        plus labels / extra edges by index
                                                                     │
                        WalkthroughGrounding.GroundComponents: paths must be in the diff, drawable and in one
                        component; edges are the code's links rolled up to components (strongest kind wins); a
                        model edge the code doesn't show either way is kept as inferred (drawn dashed)
                                                                     ▼
                        Walkthrough { components, edges, file_links }  ─► app: MapViewModel ─► MapLayout ─► MapCanvas
```

**Links are found in the code, not guessed.** `CodeLinkFinder` (in `Lumen.Roslyn`) parses the changed C# files at the
head commit, syntax only like the rest of Lumen's Roslyn work, so it needs no restore or build. A file links to another
when it names a type declared there: through a base type (**implements**), a constructor or primary-constructor
parameter, including inside generics like `IEnumerable<IEnrichmentProvider>` (**depends on**), or anywhere else
(**references**). A test file's links are what it **tests**, which is where "Tested by" comes from. A type name declared
in more than two files is too ambiguous to link by.

**Components come from one more model call.** After the per-file summaries, the walkthrough runs a fourth reduce step
next to grouping and risk picking. It gets the summaries and the code's links and returns at most 16 components (a
title, a role in 15 words, a layer: entry, ui, core, integration, data, config or other) and optional labelled edges.
The answer is grounded like the groups and risks already are, and falls back to the change groups if the call fails.
"Since last review" walkthroughs skip it. The prompt version is `walkthrough/v2`, so cached v1 walkthroughs are
replaced on the next open.

**Contracts (additive).** `Walkthrough` gains `components` (`WalkthroughComponent`: id, title, role, layer, paths),
`edges` (`WalkthroughEdge`: from, to, kind, inferred, label) and `file_links` (`FileLink`: from, to, kind).

**Layout** (`Lumen.App/Map/MapLayout.cs`, pure geometry, unit-tested):

- *Terrain* is a squarified treemap: components by total lines, then files inside each box (a floor of 6 lines keeps
  one-line files clickable).
- *Blueprint* is a layered graph drawing. Columns are the longest path from the components nothing uses (cycles broken
  where a depth-first search first meets them). An implementation is drawn from the interface it implements, so
  providers sit after the seam their callers go through. Components with no links go to the column their layer
  suggests. Within a column, boxes are ordered by a few barycentre sweeps to untangle the connectors, and share the
  height by the square root of their lines. Connectors are orthogonal; each box spreads its ports along its side and
  each source gets its own channel in the gap. Configuration and wiring (DI registration, settings) use nearly
  everything, so their outgoing links aren't drawn; they still show beside the Map.
- Both layouts place the same keys, so `MapGeometry.Lerp` morphs one into the other.

**Drawing** (`MapCanvas`) is one custom control drawn in one pass, like the diff's overview ruler, with hit-testing for
selection and hover. The morph runs on animation frames for 460 ms with a cubic ease-out; connectors fade in on arrival
at Blueprint and out on leaving it. While the Map shows, it takes the walkthrough pane's width and gives it back after.

## Limits

- Links are C# only today. Razor views, configuration and other languages get only the model's inferred edges.
- A link means "uses", which is close to but not the same as data flow: the model's labels ("proposes fields",
  "writes activities") carry the meaning, and inferred edges are dashed so they read as the model's view.
- The layout is recomputed for each head; positions are not yet kept stable across pushes.
