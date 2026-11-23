# Changelog

All notable changes to **Entitas-Flux** are documented here. This is the fork's own
history; upstream Entitas's changelog lives in
[`src/Entitas-Flux/CHANGELOG.Entitas.md`](src/Entitas-Flux/CHANGELOG.Entitas.md) and
describes a different product.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions
are `0.x` while the multi-assembly work that 1.0 is waiting on is unfinished; until then
a minor bump may carry behaviour changes, and each one says so under **Changed**.

## [Unreleased]

### Added
- **`entitas-graph`: a graph of who creates, reads, filters, removes and destroys each
  component**, and which features run which systems in what order. ECS systems never call
  each other, so call graphs and text-based code graphs show them as unrelated; this one is
  built from the compiler's view of the project, through the same project loader and
  generator engine as `entitas-gen`. A chained `CreateEntity.Event().AddX()`, a
  `GameMatcher.X` inside `AllOf`, `isX = false` and a factory that adds components on the
  system's behalf are all resolved by symbol, in either output mode. Accesses made through
  query interfaces, factories and helpers are credited to the calling system as INFERRED,
  naming the code they went through; a generic destroy system is credited with the entities
  it destroys from what they are made of (`Event` + `Ready`). Root features are discovered,
  never named, and every system gets its position under each one.

  Commands: `build`, `who-creates`, `who-reads`, `lifecycle`, `path`, `systems-of`,
  `explain`, `query`, `diff`, `report`, `install`. `build` writes `graph.json` (node-link, close to
  Graphify's format), `GRAPH_REPORT.md` (components nobody reads, read but never created,
  requests without a handler, events without a consumer, flags never cleared, unregistered
  systems…) and `graph.html`, an offline viewer with vis-network inlined. Requests and
  events are recognised by name suffix (`--request-suffix`, `--event-suffix`). It ships in
  the package's `Tools~/` next to `entitas-gen`.

  Made to be asked by AI assistants as much as by people: every answer is capped at about
  2000 tokens (`--budget`, `0` for no cap). A component half the code touches would
  otherwise answer with hundreds of readers (~23 000 tokens). An answer over the cap starts
  with a summary by feature or root, then says what it left out. `who-reads` of an event costs
  ~400 tokens where grep and reading the files that mention it costs ~6000. `graph.json`
  (~1.4 M tokens on that project) is for programs, and the README says so.

  `query <words>` answers with the connected piece of the graph, like Graphify's: the
  best-matching nodes seed a breadth-first walk over data flow (`--depth`, default 2;
  `--limit` seeds, default 5), each edge listed once, each system with its feature and order.
  Names are matched word for word by stem (`placement` finds `PlaceFurnitureByRequestSystem`
  and `FurniturePlacedEvent`), a word in the name counts for more than one in the namespace,
  and words like "how" are skipped. Nodes with more than 40 neighbours over edges written in
  their own code (an `Id`, a position) are marked as hubs and not walked through; a factory's
  inferred edges never make one. A system named after an event that is already a seed gets
  no seed of its own (it is one step out anyway), so `query hire worker` seeds the request
  and the system that handle hiring, where the flow starts. Between names that match equally
  well, a system ranks by what its own code writes (then what its groups collect): `query
  order paid` seeds `PayForOrdersByRequestSystem (creates OrderIsPaidEvent, Paid)`, though
  its name never says "paid". When the subgraph does not fit the budget, each node keeps only
  its strongest links, so what fits stays connected.

  `flow <Component|System>` answers "what happens after it" in one call: requests and events
  to their readers, a `[Watched]` change to the systems reacting to it, components no entity
  keeps — `--depth` system hops (default 4), each step labelled with where its system runs
  and whether it sees the data the same frame or the next. Frames come from feature order
  alone: cleanup systems run after every execute system, a reader waits for `Ready` when the
  events convention adds it, and where order cannot tell, the step says "frame unknown"
  rather than guess. Every hand-off says where it ends (destroyed, removed, cleared). On a
  2000-file project `flow CommitCartRequest` is ~1 900 tokens and includes the second-order
  branches (request → handler → event → statistics) that assistants following events one hop
  at a time had missed.

  A write that happens only under a condition says so — `creates CoinsSpentEvent [event]
  (if currencyTypeId == Coins && amount < 0)`, `!(…)` for an else, `case X` for a switch — in
  `flow`, `explain`, `who-creates` and `graph.html`, and `graph.json` links carry it as an
  optional `condition`. What a system writes through a factory or helper is one `by calling
  Factory.Method:` line after its own writes, with that call's requests and events followed
  beneath it: `flow PlaceFurnitureRequest` dropped from ~2 250 to ~1 400 tokens, and an
  assistant no longer reads the factory as the creator of the system's events. A condition
  names every enclosing clause, outermost first, cut at the tail past ~70 characters; a
  private method called only under a condition passes it to its writes (`PayForCart()` under
  `if (itemCount > 0)` → `if (CanAfford())`); a lambda passed with a `when:`-style bool runs
  under it; an `if`/`else` pair cancels out. `explain` of a system names who else reads the
  requests it handles.

  None of it depends on a project's naming. Requests and events are recognised by what
  happens to their entities as well as by name: a component whose every holder a reader's
  loop destroys (directly, or by marking it for a sweeper) is a request; one created only with
  such a consumed marker and read on its own is an event. A request put on an entity that lives
  on (a cat asked to leave) counts too when its handler's loop takes it off every entity it
  visits and does other work there (`consumed_by_removal` in `graph.json`); cleanup-phase
  removers, "done" flags and components that mirror one the loop skips are left out, and
  requests consumed only under a condition keep needing their suffix, which the report now
  lists. A `goto` that lands on the `Destroy()` skips nothing; a component only a frame-end
  sweep destroys (a tick made every frame) is a one-frame signal, not a request. `flow`, `query` and `explain` follow
  them in projects without `*Request`/`*Event`, and a matching suffix still wins; only the
  report's four request/event sections stay name-based, since a request nobody handles can't
  be found by structure. A `when:`-style helper is read from its body (`if (gate) apply(self);`),
  whatever its parameter is called. A second test project with different names throughout (a
  space port: `DockingOrder`, `Pulse` + `Armed`, `Doomed`, `Fresh`, `.Apply(…, gate:)`) proves
  it; on the production project every answer stayed byte-identical, and with suffixes that
  match nothing `flow CommitCartRequest` still follows the whole chain.

  A helper's writes are credited to a calling system only on branches its arguments can
  reach: constants (enum members, literals, `const`, `nameof`) are followed through
  parameters, locals, return values and the fields an object is built with, so a spawner
  passing `Customer` no longer creates what a factory adds only in `case Worker` — on the
  production project 88 false inferred edges went and 25 conditions got simpler. Enum
  members in conditions are written without their type. `flow` leaves out a system reacting
  to a `[Watched]` change when its group needs a component the changed entity never gets
  (furniture is never a cat; `graph.json` records it on the write as `unseen_by`), a reaction
  that never sees a change names its other triggers, and an answer near its budget drops its
  "also, not followed" lines first. A reader also waits for what the new entity carrying a
  request is made without: for the system adding it (`next frame (waits for Stamp from
  StampParcelsSystem #9)`), or `frame unknown (waits for View, added after an await)` when it
  arrives after an `await`, a coroutine's `yield` or outside the systems — `waits` on the write
  in `graph.json`. Factories injected through an interface the project implements once are
  followed, so `unseen_by` sees past them too. A reader that destroys a request only under a
  condition, when nothing else takes it, says so — `destroys it only once target.hasHueAnimator
  (HueAnimator is added only after an await)` — and the steps after it lose their `[+N]`;
  `added_late` in `graph.json` marks components every write of which comes after an `await`.
  A call through an interface or abstract method now reaches every override, through abstract
  classes too, so a system binding views is credited with what each view's registrars add
  (`who-creates HueAnimator` names the view binders; +430 inferred edges on the production
  project, build time unchanged). What the graph leaves out on purpose, or cannot know from
  the code, is listed in the README under *What it does not see*.

  `graph.html` is redesigned in GitHub's colours, light and dark: a command-palette search
  (`/`, every word must match), filters that double as the legend, back/forward and
  `#node=…` links, hover to fade what a node is not connected to (from the canvas or the side
  panel), a side panel with conditions, placements and CLI commands to copy, a project
  overview (`O`) and keyboard shortcuts (`?`). Every search lays out alike — neighbours in
  rings around the node — and is framed by measuring the nodes with their labels. In a narrow
  window the frame leaves room for the legend and the search list stays on screen; long
  conditions wrap; without a link the page opens on the overview, which says why that node is
  drawn; a page opened in a background tab draws itself when it is shown. Stacked under the
  panel, the ring is flattened to the stage so its labels stay readable.

  `diff` shows what changed in the data flow: the edges each component gained and lost,
  systems added, removed or moved, findings that appeared or went away. `diff --commit X` is
  what one commit changed (its first parent against it; a merge says so), `diff --ref A
  --ref B` compares two refs, `diff --ref A` a ref with the working tree, `diff old.json
  [new.json]` two saved graphs. Each side at a ref is built from `git archive` in a temporary
  folder, so the checkout is never touched; ~4 s for one ref, ~5.5 s for two (built in
  parallel) on a 2000-file project. `diff --commit` of a change that moved three statistics
  systems from one event to another shows exactly those edges, however much changed since.

  `[Watched]` reactions are visible from the system that causes them: `explain` and `query`
  end each create or remove of a `[Watched]` component with the systems reacting to it and
  the requests or events they create; `XChanged` names resolve to the component
  (`who-reads UnpaidChanged` lists the reactions); `path` labels a hop by the edge that
  carries data (reacts or reads before filters). In the report, a flag set in the loop that
  marks its entity for a generic destroyer (`isDestructed = true`) counts as destroyed with
  it, also through a helper called with the loop variable, and flags a setter keeps out of
  its own group with `NoneOf` get their own section ("set once on purpose?"), as do flags set
  only on entities that carry a transient component (one with `[Cleanup]`, or one a system
  takes off every holder, like `JustCreated`), with the reason. `graph.html` draws data
  flow — reads, filters and reactions point from the component into the system — with a
  legend, and frames a found node with its neighbours. The same code always writes the same
  `graph.json`: the sources are walked in parallel but assembled in path order.

  `install` tells the project's AI assistant to use all this instead of grepping: a short
  block in `CLAUDE.md` in the repository root (and `AGENTS.md` when present) naming the tool
  by a path relative to that file — by glob for a UPM install, whose folder name changes
  with every update — plus `entitas-graph-out/` in `.gitignore`. Re-running replaces the
  block and leaves the rest of the file alone, and `build` says when the block was written by
  another version; `uninstall` takes it out.

  Tested on a production project (1906 files plus ~900 generated, 772 components, 747
  systems, 88 features): a build takes 2.2–2.8 s; fresh AI agents answering six ECS questions
  used ~22% fewer tokens with the graph than with grep (half on a broad search, which also ran
  5.5× faster) and invented nothing, where grep-only agents once invented a reader. The tree-sitter graph that missed both producers and
  all 7 consumers of an event component now finds them: 2 creators, 7 readers, all in the
  achievements feature, and the generic system that destroys it.

## [0.5.0] - 2026-11-22

### Added
- **Generated code as ordinary files:**
  `[assembly: EntitasGeneration(Output = GenerationOutput.Files, OutputPath = "Assets/Code/Generated")]`.
  The generated API becomes real `.cs` files, so IDE navigation and any tool that reads
  source (code graphs, linters, search) see it — a tree-sitter graph of a production project
  showed a component with 2 producers and 7 consumers as having none, because only generated
  code connects them. It is the same code from the same generator: a file is exactly what the
  source generator would emit plus one marker line, which a test proves byte for byte on the
  whole snapshot (and on a 920-file production project). The source generator stands down in
  this mode, so nothing is defined twice. The default stays the source generator; a project
  that does not set `Output` sees no difference.

  Files are written one per component per context, at the paths the generator has always
  used. Only files whose content changed are rewritten (line endings do not count), files of
  a renamed or deleted component are deleted with their `.meta`, folders left empty go too,
  and nothing without the Entitas-Flux marker is ever touched. New files get a `.meta` whose
  GUID is derived from the path, so two branches generating the same component do not
  conflict. `OutputPath` defaults to a `Generated` folder next to the file declaring the
  contexts, which is always inside the right assembly.
- **Unity editor tooling for it:** *Tools → Entitas → Generate*, *Check Generated Files* and
  *Generate Automatically* (on by default). Automatic generation finishes before Unity
  compiles the change: a watcher sees the IDE's save within milliseconds, the asset database
  is held (`StartAssetEditing`) while `entitas-gen` runs, so the refresh the IDE asks for
  waits, and releasing it imports the component and its generated files together — one
  compilation, already complete, whether a component was added and used in the same save,
  deleted, or changed. Nothing is written or deleted while Unity compiles. A change Unity
  compiled first anyway (made with Unity closed, or imported by a refresh that started
  earlier) gets its second compilation started automatically. Generation reads source, so it
  works while the project does not compile. Batchmode entry points:
  `EntitasCodeGeneration.GenerateFromCommandLine` and `CheckFromCommandLine`.
- ***Tools → Entitas → Rename Component…*** — preview and apply a component rename from Unity,
  through the same engine as the `[RenameTo]` quick fix.
- **`entitas-gen` and `entitas-rename` ship inside the package** (`Tools~/`, which Unity does
  not import) and run on the .NET runtime Unity ships. Before, both existed only as projects
  in this repository: using them meant cloning it and `dotnet run`.
- **`entitas-gen --check`** exits 1 when generated files are out of date — for CI.
- **`ENT0004`** — in file mode, the compiler reports generated files that no longer match the
  components (missing, outdated, no longer generated), e.g. after editing a component in an
  IDE with Unity closed. **`ENT0005`** — generated files left in an assembly that generates
  with the source generator: it stands down and says so instead of producing hundreds of
  `CS0102`.

### Changed
- **The source generator is 35–45% cheaper per keystroke.** Discovery snapshotted every type
  in the assembly; it now snapshots only components and attributed types — the only ones
  that can yield anything. Measured A/B on a 1933-file project (median of 15 edits):
  **153–166 → 102–106 ms** for an edit outside components, **141–150 → 79–84 ms** for an
  edit in a component file. Generated output is unchanged (the snapshot did not move).
- **`entitas-gen` rewritten for the file mode.** It finds the projects that declare contexts
  by itself, syncs instead of wiping its output folder (the old `entitas-gen <csproj> <dir>`
  form still works, without the wipe), parses and discovers in parallel and is tuned for cold
  starts: **2.3 s → 0.7 s** on the same project.
- Both CLIs run on any .NET 6 or newer (`RollForward=LatestMajor`); a machine with only
  .NET 8 or 10 used to get "You must install or update .NET".
- The project loader behind both CLIs resolves asmdef references (`ProjectReference` →
  `Library/ScriptAssemblies`) and honours the project's `LangVersion`, so a component field
  typed from another asmdef is named the way the compiler names it.

### Fixed
- **Renaming a component left the views implementing its event listener broken.** The
  rename followed `IAnyHealthListener` → `IAnyHpListener` but not the `OnAnyHealth` methods
  implementing it, so every listener stopped compiling (`CS0535`, or `CS0539` for explicit
  implementations), in the IDE quick fix and in `entitas-rename` alike.
- **The editor assemblies did not load in a fresh project.** `Entitas.Unity.Editor` and
  `Entitas.VisualDebugging.Unity.Editor` reference `DesperateDevs.Unity.Editor`, which was
  never shipped, so Unity skipped both ("Unable to resolve reference") — no Entitas menu, no
  entity inspector. Projects only worked when they still carried a copy from the original
  Entitas distribution. It now ships in `Assets/Entitas/DesperateDevs/Editor`
  (`Editor/DesperateDevs` in the package), where such a copy already lives, so it is
  overwritten rather than duplicated.

## [0.4.1] - 2026-09-10

### Changed
- **`Group.GetEntities(buffer)` copies with one `Array.Copy` instead of one `Add` per
  entity.** The buffered overload has no snapshot cache to fall back on, so every call
  paid a capacity check, a bounds check and a version bump per element — about ten times
  the cost of the copy itself, and enough that switching a project from `GetEntities()`
  to the buffered overload made its loading slower. The list is now grown to the group's
  size in one step rather than doubling its way up from its initial capacity, and filled
  through `AddRange` over a cached view of the storage, so a fill still allocates
  nothing.

  Measured as 100 reads of a 10k-entity group: **4,029 → 1,230 µs** while the group is
  stable, **4,094 → 1,255 µs** with a membership change before every read. The new
  `GroupReadBenchmarks` puts the three ways of reading a group side by side;
  `benchmarks/README.md` has the table.

## [0.4.0] - 2026-09-06

### Added
- **A visual debugging mode that does not create a GameObject per entity.**
  `[assembly: EntitasGeneration(VisualDebugging = VisualDebuggingStyle.SingleGameObject)]`
  swaps the generated context observer for one that registers every context on a single
  `Entitas Debug` GameObject; its inspector browses the contexts, their groups and their
  entities (searchable, paged, multi-select) and edits components through the same
  `EntityDrawer` as before. The default stays `VisualDebuggingStyle.EntityGameObjects` —
  the classic observer, one GameObject per context plus one per entity — which is what
  makes the editor crawl once a context holds thousands of entities.

  The entity list is ordered by creation index and re-read twice a second rather than
  every repaint, so entities that live a single frame no longer make it jump; `Live` and
  `Refresh` freeze it entirely while you click through one. The classic mode is
  untouched, and `EntityLink`'s "Show entity" finds its entity under either one.

## [0.3.0] - 2026-08-28

Performance work in the runtime core, found with a profiler rather than guessed at.

### Changed
- **Components are stored without the array covariance check.** Arrays of a reference
  type are covariant, so every write into the entity's `IComponent[]` was type-checked at
  runtime by the JIT — a fifth of the time spent changing a component. They now live in a
  one-field struct wrapper, which turns the write into a plain field store.
- **Groups use a sparse set instead of a `HashSet`.** Membership changes on every
  component add and remove of every entity a group watches, and hashing plus bucket
  probing was ~27% of that path. Entities carry a dense index — assigned per entity object
  and reused with it, so it stays bounded by the peak number of live entities — and a
  group addresses a flat array with it.

  Measured over 10k entities: component churn with one group **459 → 355 µs**, with three
  groups **884 → 702 µs**, iterating a group and reading a component **31.9 → 22.9 µs**.

- **`IGroup.GetEnumerator()` returns `GroupEnumerator<TEntity>`** instead of
  `HashSet<TEntity>.Enumerator`. `foreach` binds by pattern and does not notice; code that
  names the type explicitly does. Compiling a real 2243-file project against the new
  runtime produced exactly the same diagnostics as against the old one.
- **An `IEntity` implementation that does not derive from `Entitas.Entity` can no longer
  be added to a group**, and says so with an exception instead of misbehaving. Generated
  entities always derive from `Entity`.

Group enumeration order changed, as it may whenever the storage does. It was never
guaranteed — `HashSet` gave an arbitrary order too.

### Added
- Benchmarks comparing the fork against [Morpeh](https://github.com/scellecs/morpeh) in
  one process and one table, benchmarks that isolate group maintenance from component
  plumbing, and a profiling target for `dotnet-trace` with no benchmark harness in the
  trace. See `benchmarks/README.md`.

## [0.2.0] - 2026-08-27

### Added
- **`ENT0003`** — a component in a different assembly than the contexts now says so.
  Generation runs per assembly, into the one declaring the contexts, so such a component
  was silently ignored: no generated API, no explanation. There is no quick fix on
  purpose — declaring a context in that assembly would generate a second, parallel set of
  contexts rather than help.

### Changed
- README documents two constraints that were previously folklore: components must live in
  the assembly that declares the contexts, and `ComponentsLookup` indices are not stable
  across builds and must never be persisted.

## [0.1.2] - 2026-08-27

### Fixed
- **A fresh install did not compile.** The generated `Feature` class calls
  `DesperateDevs.Extensions`, and `Entitas.dll` is built against `DesperateDevs.Caching`
  and `.Reflection` — none of which were ever shipped. Existing projects worked only
  because they still carried those DLLs from the original Entitas distribution. The four
  DesperateDevs assemblies and Sherlog now ship in `Assets/Entitas/DesperateDevs`
  (`Runtime/DesperateDevs` in the package), the folder an Entitas project already has, so
  an existing installation overwrites its copies instead of duplicating assemblies.
- **`[Watched]` did nothing with the default entity API.** The marker assignment was
  wired into the atomic API only, so with `EntityApiStyle.Plain` the `{X}Changed`
  component and its cleanup systems were generated and nothing ever set the flag.
- **`[Watched]` did not compile inside a namespace.** The marker class is named from the
  flattened component name while the data model named it from the short one, so every
  generated reference to the marker pointed at a type that did not exist.
  `IgnoreNamespaces = true` makes the two coincide, which is why projects using it never
  hit this.

### Added
- Behaviour tests that compile the generated output into a real assembly, load it and
  exercise it — atomic value access, `SafeRemove`, the watched marker lifecycle, debug
  hooks. Both `[Watched]` bugs above were found the first time they ran.
- The benchmark project is in the repository and built by CI (not run there: shared
  runners share their CPU, so their timings are noise). See `benchmarks/README.md`.

### Changed
- The generator's reference output is our own committed snapshot (`tests/Snapshot`,
  accept a change with `ENTITAS_UPDATE_SNAPSHOT=1`) instead of the frozen Jenny baseline,
  which also froze Jenny's quirks. Comparison is now file by file rather than a
  normalized multiset of lines.
- The fork's version is no longer upstream Entitas's `1.14.1`; the release workflow
  stamps assemblies with the tag being built.
- CI lost its weekly cron: GitHub auto-disables cron workflows in repositories quiet for
  ~60 days, and it disables the whole workflow, which silently killed PR checks between
  June and August.

### Removed
- `Entitas.Migration` and its Editor tooling — migrations between Entitas versions from
  before this fork existed, referenced by nothing and shipped as two Editor DLLs.

## [0.1.1] - 2026-08-27

### Added
- **`ENT0002`** — a component in an assembly that declares no context now gets a warning
  on its own declaration, with a quick fix that declares one. Generation is opt-in, and
  its silence was the most confusing way to meet the framework: you write a component,
  `GameEntity` does not exist, and nothing tells you why. The check stays quiet for
  assemblies that only consume the generated API and for assemblies that do not reference
  Entitas.

### Changed
- README leads with installation, has a table of contents and documents all four
  diagnostics. The `Entitas-Flux-Template` route is gone — the template is legacy, and
  with the UPM package a fresh project and an existing one take the same two steps.

## [0.1.0] - 2026-08-27

### Added
- **UPM package** (`com.bogenbai.entitas-flux`), published to the `upm` branch on every
  release: `https://github.com/Bogenbai/Entitas-Flux.git#upm`, pinnable as `#upm/vX.Y.Z`.
  Asset GUIDs are derived from each asset's path so they stay stable across releases.
- **`ENT0100` / `ENT0101`** — generation failures are reported instead of swallowed. A
  generator that throws names itself and the reason; a discovery failure reports rather
  than crashing the compiler. Previously a broken generator quietly left a hole in the
  API and you saw only a hundred unexplained `CS1061`s in your own code.
- **Unity 6 support**, verified against `6000.3.6f1`; 2022.3 LTS remains the build target.

### Changed
- **Code generation is incremental.** It used to re-run discovery and all ~25 generators
  over the whole assembly on every keystroke, then hand the compiler every generated file
  again — on a 2247-file project, ~90 ms per keystroke plus 1412 files to re-parse.
  Generation now hangs off a value model Roslyn compares by content, so an edit that
  touches no component, attribute or generation config skips generation entirely.
- Discovery no longer reads symbols directly; each type is flattened into a `TypeSnapshot`
  of plain values, which is what makes the model comparable at all.

## [0.0.7] - 2026-08-27

### Added
- **`[RenameTo("NewName")]` + IDE quick fix** — renames a component and every identifier
  the generator derives from it (`hasX`, `AddX`, `GameMatcher.X`, `XChanged`, listeners),
  across the declaring assembly and every assembly that references it. The attribute is
  inert until applied, so the project keeps compiling in between.
- **`entitas-rename` CLI** — the same engine headless, for CI or bulk work; it also moves
  the component's `.cs` with its `.cs.meta` so the Unity asset GUID survives.

## [0.0.6] - 2026-06-21

### Added
- **Roslyn incremental source generator** replacing the Jenny CLI pipeline: the full ECS
  API is generated at compile time, with no separate generation step and no committed
  `Generated/` folders.
- Per-assembly generation config via `[ContextDefinition]`, `[EntitasGeneration]` and
  `[DisableEntitasGenerator]`.
- Opt-in debug hooks: `[assembly: EntitasGeneration(DebugHooks = true)]` routes every
  generated `Add`/`Replace` through `EntitasDebugHooks`, so a specific mutation can be
  caught with a breakpoint.
- `entitas-gen` CLI for writing the generated code to disk.

### Changed
- Runtime optimizations: O(n) `RemoveAllComponents`, faster group updates, unique
  accessors, entity/matcher hot paths, and ref-counting in entity indices instead of
  `SafeAERC`.
- Unity 2022.3.62f2 is the minimum (Roslyn 4.x is required for `IIncrementalGenerator`).

### Fixed
- Multi-context entity interfaces failed to compile on regeneration.

### Removed
- The Jenny CLI, its plugin projects, every `Jenny.properties` and the committed
  `Generated/` folders.

## [0.0.5] - 2026-03-24

### Added
- `HasEntityIndex` / `RemoveEntityIndex` on `IContext` and `Context`.
- Additional API for watched components, and `[Watched]` markers now also trigger from
  Unity inspector edits.

### Fixed
- Watched components were generated for one context only.
- Generated `[Watched]` component and cleanup-system order was not deterministic.
- Primary-index components sharing a name across contexts generated broken code.
- `EntityIndexGenerator` generated for `[DontGenerate]` components.

## [0.0.4] - 2025-11-24

### Added
- `ENTITAS_DISABLE_REACTIVITY` and `ENTITAS_HIDE_STANDARD_MEMBERS` compiler defines.

### Fixed
- Multi-context `[Watched]` components could not be generated.
- `[Watched]` and `[Cleanup]` could not be used on the same component.

## [0.0.3] - 2025-10-23

### Changed
- The release workflow packs the built folders into an archive.

## [0.0.2] - 2025-10-23

### Fixed
- Components could not be added to multiple selected entities.
- The component menu could not be closed.
- Build scripts excluded the Editor DLLs; DLLs are now sorted into folders.

## [0.0.1] - 2025-10-20

First release of the fork.

### Added
- **Atomic components** — single-field components get direct property access
  (`entity.CurrentHealth` instead of `entity.currentHealth.Value`).
- **`[Watched]`** — a changed component marks its entity with a `{X}Changed` marker that
  lives for one frame, plus the cleanup systems that remove them.
- **Safe component removal** — `SafeRemoveX()` instead of guarding every `RemoveX()`.
- **Searchable component dropdown** in the Unity inspector.

[Unreleased]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.5.0...HEAD
[0.5.0]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.4.1...v0.5.0
[0.4.1]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.4.0...v0.4.1
[0.4.0]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.3.0...v0.4.0
[0.3.0]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.2.0...v0.3.0
[0.2.0]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.1.2...v0.2.0
[0.1.2]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.1.1...v0.1.2
[0.1.1]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.1.0...v0.1.1
[0.1.0]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.0.7...v0.1.0
[0.0.7]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.0.6...v0.0.7
[0.0.6]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.0.5...v0.0.6
[0.0.5]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.0.4...v0.0.5
[0.0.4]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.0.3...v0.0.4
[0.0.3]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.0.2...v0.0.3
[0.0.2]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.0.1...v0.0.2
[0.0.1]: https://github.com/Bogenbai/Entitas-Flux/releases/tag/v0.0.1
