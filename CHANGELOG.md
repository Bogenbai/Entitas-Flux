# Changelog

All notable changes to **Entitas-Flux** are documented here. This is the fork's own
history; upstream Entitas's changelog lives in
[`src/Entitas-Flux/CHANGELOG.Entitas.md`](src/Entitas-Flux/CHANGELOG.Entitas.md) and
describes a different product.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions
are `0.x` while the multi-assembly work that 1.0 is waiting on is unfinished; until then
a minor bump may carry behaviour changes, and each one says so under **Changed**.

## [Unreleased]

## [0.6.3] - 2026-11-24

### Fixed
- **The MCP server did not start on Windows.** `.mcp.json` ran `sh`, and Git for Windows
  does not put `sh` on PATH, so Claude Code got `CONNECTION_CLOSED`. The entry now runs `git`,
  which every Claude Code machine has, with a `!` alias: git runs it in its own sh (on Windows
  the one Git for Windows ships) from the top of the repository, so it also works when Claude
  Code is started in a subfolder. One entry serves macOS, Linux and Windows. `install`
  replaces the 0.6.2 entry; run it once, and `build` says so until then.
- **Replies are plain ASCII over UTF-8 streams.** Started through `cmd /c entitas-graph.cmd
  mcp` on Windows, the server answered `initialize` but its tool list never arrived. That
  list is the first reply with non-ASCII text in it (a dash, an arrow), and the only one over
  4 KB. The server now escapes every non-ASCII character, and reads and writes the raw
  standard streams as UTF-8 instead of going through the console's code page. Each request
  is noted on stderr with its time, which Claude Code keeps in its MCP log. Not yet
  confirmed on Windows.

### Changed
- **`install` pins the wrappers' line endings** in the git root's `.gitattributes`: LF for
  `entitas-graph`, CRLF for `entitas-graph.cmd`. On Windows the MCP server and the Claude
  Code hook both go through the sh wrapper, and sh stops at the carriage return Git for
  Windows puts into text files it checks out. Lines already there are kept;
  `--no-gitignore` leaves both git files alone.

## [0.6.2] - 2026-11-24

### Added
- **`entitas-graph mcp`: the graph's commands as tools of an MCP server**, for assistants
  and subagents without a shell. A Claude Code agent whose `tools:` lists Read, Grep and
  Glob could not run `./entitas-graph`, so it searched by hand; listing
  `mcp__entitas-graph__*` gives it `who_creates`, `who_reads`, `lifecycle`, `systems_of`,
  `explain`, `path`, `query`, `flow`, `report` and `diff` without giving it Bash. The answers
  are the command line's word for word, under the same ~2000-token cap. The server keeps the
  graph in memory between calls and rebuilds it when the sources change.
- **`install` registers the server** in the project's `.mcp.json`, next to the project's own
  servers (`--no-mcp` leaves the file alone). Claude Code asks once to approve it. Claude Code
  finds `.mcp.json` from any folder below it but starts the server in the folder it was
  started in, where neither a relative command nor `${CLAUDE_PROJECT_DIR}` reaches the
  wrapper, so the command asks git for the repository root. Checked with Claude Code 2.1.316
  (`claude mcp list`), started in the root and two folders down.

### Changed
- The install block says what to do for a subagent without Bash: list the server's tools
  in its `tools:`, or put the answer in its prompt. Run `./entitas-graph install` once after
  updating; `build` says so until then.

## [0.6.1] - 2026-11-24

### Fixed
- **The Claude Code hook stopped commands that only named `graph.json`.** It matched the
  call's text, so a `git commit -m` whose message mentioned `entitas-graph-out/` and
  `graph.json` was blocked, and so was a PR body, a note written through a heredoc, a Bash
  call whose description named the file, or a Grep for the folder's name across the project.
  The wrapper still settles every call that never mentions `entitas-graph-out` in plain sh,
  without starting dotnet; the rest now go to the tool (about 100 ms), which reads a Bash
  command word by word. A quoted sentence or a heredoc body is text unless an interpreter
  (python, jq, node…) runs it, commands that never print a file (`ls`, `du`, `wc`, `git`,
  `open`…) pass — `open graph.html` is what the file is for — and `grep`/`rg` given the
  folder still reads it. Run `./entitas-graph install` once after updating: the old wrapper
  keeps deciding in sh until then, and `build` says so.
- **The editor's images shipped in neither the archive nor the package.** The hierarchy
  icons and the header of the Entitas preferences are found by asset label; a project that
  came from the original Entitas still had its own copies, a new one had none. Both now carry
  them — the archive with the original `.meta` files, so an existing copy is replaced by the
  same asset, the package with guids of its own.

### Changed
- **A manual install needs no hand-labelling.** The archive's analyzers come with their
  `.meta`: the `RoslynAnalyzer` label and no platform. Without it Unity imported the source
  generator as an ordinary plugin and nothing was generated — a fresh project failed on
  `[Game]` — unless both DLLs were labelled by hand, as the installation guide asked. The
  other DLLs still come without one: Unity's defaults are right for them, and an existing
  install keeps its own guids, through which prefabs reference `EntityLink`.
- **`Sherlog.dll` moved to `Assets/Entitas/Sherlog/` in the archive**, where the original
  Entitas distribution keeps it, so copying the archive over such an install replaces it
  instead of adding an identical second copy in `DesperateDevs/` (which Unity tolerates;
  delete it if an earlier archive left it there). The package keeps it where it was.
- Verified in Unity 6000.3.6f1 on three new projects: a manual install, the archive copied
  over an install that came from the original Entitas, and the UPM package — its first run
  in a real project. Each compiles, generates, loads the editor assemblies and finds the
  images; the 0.6.0 archive installed by hand does not compile.

## [0.6.0] - 2026-11-23

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
  (~1.4 M tokens on that project) is for programs, and the documentation says so.

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
  the code, is listed on the [ECS graph](https://github.com/Bogenbai/Entitas-Flux/wiki/ECS-Graph#what-it-does-not-see)
  wiki page under *What it does not see*.

  A jump nested in another `if` before a write makes the write conditional too:
  `ForbidViewAttachByRequest` destroys its request only once `!(targetEntity != null &&
  targetEntity.isLoadingView)`, and `flow` says it waits. A reaction is no longer presented as
  certain when the call making the `[Watched]` change also takes off a component the reactor
  needs: it is marked `(the same call may take off CatAnimator, which it needs)` and recorded
  as `may_miss`, so detaching a view no longer reads as playing confetti. Lateness is decided
  per call path: `added_late` means every way is late, the new `sometimes_late` that some are
  (`(HueAnimator can arrive after an await)`), and `waits` counts only the systems that can add
  the component to that entity — when some add it at once and others late, both are listed
  (`added_by`, `late_by`) and the frame is unknown.

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
  with every update — plus `entitas-graph-out/` in `.gitignore`. It also writes two short
  commands next to that file, `./entitas-graph` (sh) and `entitas-graph.cmd` (cmd and
  PowerShell), which find the tool and the Unity root themselves, so the block names
  `./entitas-graph who-reads <Component>`, and a package update needs no new `install`. For
  Claude Code it adds a hook to `.claude/settings.json`, next to the project's own, that stops
  any read of `graph.json` or `graph.html` and says what to ask the graph instead
  (`--no-claude-hook` skips it). The block also tells the
  assistant to open the files an answer names before changing code, since links through calls
  and frames are inferred and DI, scenes and prefabs are not in the graph. Re-running replaces the
  block and leaves the rest of the file alone, and `build` says when the block was written by
  another version; `uninstall` takes it out.

  Tested on a production project (1906 files plus ~900 generated, 772 components, 747
  systems, 88 features): a build takes 2.2–2.8 s; fresh AI agents answering six ECS questions
  used ~22% fewer tokens with the graph than with grep (half on a broad search, which also ran
  5.5× faster) and invented nothing, where grep-only agents once invented a reader. The tree-sitter graph that missed both producers and
  all 7 consumers of an event component now finds them: 2 creators, 7 readers, all in the
  achievements feature, and the generic system that destroys it.
- **Shift+F6 on a component, then regenerate, is a complete rename in file mode.** An IDE
  rename of the class renames the class and every reference to the type, the generated files
  included, but not the members the generator derives from its name (`AddHealth`, `hasHealth`,
  `GameMatcher.Health`, `IAnyHealthListener.OnAnyHealth`, …) — so the next regeneration
  deleted them from under the code still using them. Now the files about to be deleted give
  the rename away (they still declare the old API but already reference the new class), and
  the guess is verified by generating as if the class still had its old name, which must
  reproduce the generated files on disk exactly. Unity then asks before deleting anything —
  *"Component 'Health' looks renamed to 'Hp' — update usages of AddHealth, hasHealth, …?"*
  with *Update usages*, *Regenerate only* and *Not now* — and `entitas-gen` prints the rename
  and its usages, writes nothing and exits 1 until it is re-run with `--apply-renames` or
  `--ignore-renames`. The usages are rewritten by the same engine as `[RenameTo]`, in the
  declaring assembly and in every assembly that references it, before Unity compiles. Nothing
  is rewritten when the pairing is ambiguous (several components renamed, added or deleted in
  one generation): generation goes on as before, with a warning. A component deleted while
  another is added is not a rename, and a rename nothing uses is regenerated without a
  question. The check only runs when a generation is about to delete files, so adding or
  editing components costs nothing extra.

### Changed
- The README is short again: what the framework does, with a picture or a few lines of code per
  feature. The guides moved to the [wiki](https://github.com/Bogenbai/Entitas-Flux/wiki): installation, code
  generation and its options, generated files, renaming, debug hooks and the ECS graph.
- `entitas-rename` (and *Tools → Entitas → Rename Component…*) stops with an explanation when
  an IDE rename of another component is still waiting for that decision, instead of planning
  against generated files that are about to change. It no longer looks for usages in
  `.Player.csproj` twins, which are the same assemblies compiled with player defines.

### Fixed
- ***Generate Automatically* turned itself off, and its checkmark did not show its state.**
  The setting lived in EditorPrefs, which on macOS is one store for the whole machine: every
  running editor works from the copy it read at launch and writes that whole copy back with
  its next preference, so another editor open on another project could switch it off here.
  Generation then stopped and the next compile used stale generated files (`ENT0004`,
  `CS1061`). It is now kept per project and per user, in `UserSettings/EditorUserSettings.asset`
  (out of version control in Unity's `.gitignore`, so it is not imposed on a team). Unity
  writes that file when the project is saved or closed; a crash before then falls back to the
  default, on, which is the safe side. The old machine-wide value is not carried over, since it
  may belong to any project on the machine: the switch starts on in every project, and a
  project that had it off needs it turned off once more. The checkmark follows the setting on
  every change and for a few seconds after every load, because Unity rebuilds its menus some
  frames after an editor assembly changes and clears it; before, it was set only while the
  menu was being drawn, so it read unchecked in a fresh editor and anything checking it
  without opening the menu got a stale answer.

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

[Unreleased]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.6.3...HEAD
[0.6.3]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.6.2...v0.6.3
[0.6.2]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.6.1...v0.6.2
[0.6.1]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.6.0...v0.6.1
[0.6.0]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.5.0...v0.6.0
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
