# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Entitas-Flux is a C# fork of the Entitas ECS (Entity Component System) framework for Unity. It adds features on top of the original Entitas: atomic components, watched components (`[Watched]` attribute), safe component removal, and a searchable component dropdown in the Unity inspector.

## Build & Test Commands

All commands run from the repo root.

```bash
# Build (Release, outputs to src/Entitas-Flux/Artifacts/)
./src/Entitas-Flux/build.sh

# Build Debug
./src/Entitas-Flux/build.sh Debug

# Run all tests
dotnet test ./src/Entitas-Flux/Entitas.sln --configuration Release

# Run a specific test project
dotnet test ./src/Entitas-Flux/src/Entitas/tests/Entitas.Tests.csproj --configuration Release

# Run a single test by name
dotnet test ./src/Entitas-Flux/Entitas.sln --filter "FullyQualifiedName~ContextTests"
```

There is no separate lint command; code style is enforced via ReSharper `.dotsettings` files.

Other scripts:

```bash
# Assemble the UPM package from Artifacts/ (build.sh must have run first)
./src/Entitas-Flux/package.sh 0.1.2

# Accept an intended change to generated output, then read the diff
ENTITAS_UPDATE_SNAPSHOT=1 dotnet test ./src/Entitas-Flux/src/Entitas.SourceGenerator/tests

# ECS graph of a Unity project (-o keeps the output out of that project)
dotnet src/Entitas-Flux/src/Entitas.Graph/src/bin/Release/entitas-graph.dll build -r <unity-project> -o /tmp/graph

# Benchmarks (never in CI — see benchmarks/README.md)
dotnet run -c Release --project benchmarks/Entitas.Benchmarks -- --filter '*CoreBenchmarks*'
```

## Releasing

Everything after the tag push is automated by `.github/workflows/release-on-tag.yml`:
build + test, the GitHub release with the zipped artifacts, and the `upm` branch that
consumers install from. The steps before it are not, so:

1. Work on a branch named `release/X.Y.Z` (or a topic branch). **Never name a branch
   `vX.Y.Z`** — it collides with the tag of the same name and every `git push origin
   vX.Y.Z` then fails with `matches more than one`.
2. **Add the version's section to `CHANGELOG.md`** before tagging. The changelog is the
   fork's own history (`src/Entitas-Flux/CHANGELOG.Entitas.md` is upstream Entitas's) and
   it ships inside the package, where Unity's Package Manager renders it as a tab. Update
   the compare links at the bottom too.
3. Open a PR to `master` and let CI pass.
4. Merge with a merge commit, then pull `master`.
5. Tag and push: `git tag -a vX.Y.Z -m "…" && git push origin refs/tags/vX.Y.Z`. The tag
   is what stamps the version — `FluxVersion` flows from it into the assemblies and
   `package.json`, so nothing needs bumping by hand.
6. Write the release description with `gh release edit vX.Y.Z --notes-file …`, mirroring
   the changelog section. The workflow's own body is just "Automated release for tag …".
7. Verify what shipped, not just that the workflow was green: the release asset exists,
   the `upm` branch carries the new `package.json` version, and anything the release
   claims to fix is actually in the package.

Do not release from a dirty tree: `build.sh` packs whatever is in `Artifacts/`.

## Architecture

The key layers of the solution (`src/Entitas-Flux/Entitas.sln`):

**Core Runtime** (`src/Entitas-Flux/src/Entitas/`) — The ECS framework itself: Entity, Context, Group, Matcher, Collector, EntityIndex, Systems. This is what ships in Unity at runtime.

**Code Generation Attributes** (`src/Entitas-Flux/src/Entitas.CodeGeneration.Attributes/`) — Attributes like `[Game]`, `[Watched]`, `[Unique]`, `[DontGenerate]`, `[Cleanup]`, and `[ContextDefinition]` that users put on component classes / the assembly. These are the lightest dependency — only attribute definitions, no logic.

**Source Generator** (`src/Entitas-Flux/src/Entitas.SourceGenerator/`) — A Roslyn `IIncrementalGenerator` that generates the ECS API (contexts, entities, matchers, component accessors, events, cleanup/watched systems) at **compile time**. It replaced the previous Jenny CLI pipeline: there is no separate generation step and no committed `Generated/` folders — consuming projects reference it as an analyzer (`OutputItemType="Analyzer"`). It is a netstandard2.0 analyzer depending only on `Microsoft.CodeAnalysis.CSharp`. Internals: `src/Discovery/` (semantic-model data providers that build the data model from the current compilation) → `src/Generators/` (the ported string-template generators) → `EntitasIncrementalGenerator` (wires them and emits each fragment via `AddSource`).

**Distribution** — `build.sh` produces the Unity DLL layout in `Artifacts/`; `package.sh <version>` turns that into a UPM package (`Artifacts/package/`) with generated `.meta` files whose GUIDs are derived from the asset path, so they stay stable across releases. The release workflow publishes the package as the root of the generated `upm` branch (`…git#upm`, pinnable as `#upm/vX.Y.Z`). The two CLIs ship in the package's `Tools~/` (Unity ignores `~` folders, so no `.meta`, no import, no Roslyn clash) and the editor runs them with Unity's own .NET runtime.

**Standalone CLIs** — `Entitas.SourceGenerator.Cli` (`entitas-gen`, the writer behind `Output = GenerationOutput.Files`), `Entitas.Rename` (`entitas-rename`, renames a component and every identifier derived from it) and `Entitas.Graph` (`entitas-graph`, the ECS graph — see below). All three turn Unity's csproj files — or, for the editor, a JSON request built from `CompilationPipeline` — into a Roslyn `Compilation` through the shared `ProjectLoader`, `GenerationHost` and `GeneratedFileWriter` (in `src/Entitas.SourceGenerator.Cli/src/`, linked into the other two) and then drive the same generator engine. Every run is a cold process, so all are tuned for start-up (parallel parse/discovery, quick JIT) — keep it that way.

**Unity Integration** — `Entitas.Unity`, `Entitas.Unity.Editor`, `Entitas.VisualDebugging.*` projects provide Unity inspector/editor tooling.

**Migration** — `Entitas.Migration` and related projects handle upgrading between Entitas versions.

### Source Generator Pipeline

Code generation follows a discovery → generator pipeline, all inside the C# compiler:
- **Discovery** (`src/Discovery/`) reads the current `Compilation` via `SemanticModel`/`ISymbol` (NOT external `.csproj` parsing) to build the data model (`ComponentData`, `ContextData`, `EntityIndexData`, `CleanupData`, `WatchedCleanupData`). Contexts come from `[assembly: ContextDefinition("…")]` (first = default), replacing the old Jenny `Contexts = …` config.
- **Generators** (`src/Generators/`) are verbatim ports of the legacy templates; each emits `CodeGenFile` fragments.
- `EntitasIncrementalGenerator` projects the compilation into `GenerationInput` (`src/GenerationInput.cs`) — a value holding only strings, bools and the data POCOs — and hangs generation off THAT. Roslyn re-runs the projection per keystroke but compares it by value, so an edit that does not change components, their attributes or the generation config skips generation entirely. `IncrementalityTests` pin both directions (cached on an unrelated edit, re-run on a component change). Anything added to the model must stay equatable and free of symbols/syntax, or the cache silently stops working.
- `EntitasGenerators.Generate(GenerationInput)` resolves the active generator set (canonical set adjusted for the assembly's config attributes), runs each generator, and emits each fragment as its own `AddSource` partial-class unit (the compiler merges the partials — there is no file-merge step).
- **Generation is per assembly, into the one declaring the contexts.** Components elsewhere are not discovered (`ENT0003` says so instead of failing silently); systems can live anywhere. Lifting this needs extension-method APIs plus runtime component-index registration — the generated `GameEntity` is a partial class and partials cannot cross assemblies. It is a documented constraint, not a bug to fix casually.
- **File mode** (`Output = GenerationOutput.Files`): the format lives in `GeneratedFiles` (the source generator's text plus a marker line), the writer in the CLI (analyzers may not do IO). Three invariants: discovery skips trees carrying the marker (generated code declares components of its own — `XChanged`, listener components — and would be generated twice); the source generator emits nothing in file mode and reports stale files as `ENT0004`, and emits nothing in default mode while marker files remain (`ENT0005`); only marker files are ever overwritten or deleted. `FileModeTests`, `GeneratedFileWriterTests` and the file-mode snapshot test pin these. Discovery snapshots only components and attributed types (`EntitasDiscovery.GetRelevantTypes`) — snapshotting every type was a third of the per-keystroke cost.
- **Failures are reported, never swallowed**: a generator that throws yields `ENT0100` naming it and the reason; a discovery failure yields `ENT0101`. Both are errors — see `EntitasDiagnostics`. Separately, `MissingContextDefinitionAnalyzer` reports `ENT0002` on components in an assembly with no `[assembly: ContextDefinition]` (generation is opt-in, and its silence is the framework's most confusing failure mode); its quick fix lives in `Entitas.CodeFixes`. The condition is deliberately narrow — an assembly that only consumes the generated API declares no components and is never flagged. `entitas-gen` prints them and exits non-zero; `RenameEngine` refuses to plan a rename when generation failed, since the identifier map would be wrong.
- Equivalence to the original Jenny output is locked by `GoldenEquivalenceTests` against the frozen `tests/JennyBaseline/` snapshot. With no config attributes present, output is byte-identical to the canonical default.

### Customizing generation without rebuilding the framework

Consumers customize generation via assembly-level attributes (in `Entitas.CodeGeneration.Attributes`); the generator reads them from the compilation by full metadata name (no extra reference needed). With none present, behavior is the canonical default.

- `[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic)]` — swaps the plain `ComponentEntityApiGenerator` for `AtomicComponentEntityApiGenerator` (one or the other, never both — they emit conflicting entity members). `EntityApiStyle.Plain` (default) keeps the canonical plain API.
- `[assembly: EntitasGeneration(IgnoreNamespaces = true)]` — drops the namespace from generated component names (e.g. `My.Game.HealthComponent` → `Health` instead of `MyGameHealth`).
- `[assembly: EntitasGeneration(DebugHooks = true)]` — injects `Entitas.EntitasDebugHooks.OnAdd/OnReplace?.Invoke(this, index, value)` at the top of every generated `Add`/`Replace` (via `DebugHookInjector`; runs only when on, so default/golden output stays byte-identical). `EntitasDebugHooks` is a runtime class (`src/Entitas/src/EntitasDebugHooks.cs`) holding two `Action<IEntity,int,object>` delegates; assign one and breakpoint inside to catch a specific mutation — the call stack shows which system did it. Replaces the old "edit the generated ReplaceX + breakpoint" workflow; works with atomic + `ENTITAS_DISABLE_REACTIVITY` (the hook fires before the in-place mutation). Debug only — turn off for release; a null handler costs one null-check.
- `[assembly: EntitasGeneration(VisualDebugging = VisualDebuggingStyle.SingleGameObject)]` —
  swaps `ContextObserverGenerator` for `ContextObserverSingleObjectGenerator`, so
  `Contexts.CreateContextObserver` registers the context on the single
  `Entitas.VisualDebugging.Unity.EntitasDebugBehaviour` GameObject instead of constructing
  a `ContextObserver` (one GameObject per context, plus a child per entity — the editor
  slows to a crawl at a few thousand entities). `EntitasDebugInspector` draws the same
  stats, groups, Create/Destroy buttons and `EntityDrawer` component editing, with the
  entity list searchable, paged and multi-selectable inside the inspector. Default is
  `VisualDebuggingStyle.EntityGameObjects` (the classic observer); both generators share
  the `"ContextObserver"` disable prefix.
- `[assembly: EntitasGeneration(Output = GenerationOutput.Files, OutputPath = "Assets/…")]` — writes the generated code to files instead (see "File mode" above); `OutputPath` defaults to `Generated` next to the file declaring the first context. The Unity side is `Entitas.Unity.Editor/src/Generation/` (menu, automatic regeneration, rename window). Automatic regeneration must finish before Unity compiles the change — Unity 6 runs no managed code between noticing a changed script and compiling it (AssetPostprocessor callbacks for scripts come after the compile; `compilationStarted` does not hold the compiler back), so `GenerationScheduler` wins the race instead: the `FileSystemWatcher` sees an IDE save within ~20 ms, the asset database is locked with `StartAssetEditing` (which defers an explicit `AssetDatabase.Refresh()`, unlike `DisallowAutoRefresh`), `entitas-gen` runs, and unlocking lets the waiting refresh compile everything once. It never runs while `EditorApplication.isCompiling`, and refreshes itself only after a compile it lost to. The scheduler is Unity-free and unit-tested (`Entitas.Unity.Editor/tests`); keep it that way.
- `[assembly: DisableEntitasGenerator("X")]` (repeatable) — removes any built-in generator whose CLASS short name matches `"X"` case-insensitively **or starts with** `"X"` (prefix match). So `"Event"` disables all `Event*` generators, `"Watched"` disables all `Watched*`, and `"ContextObserver"` disables just `ContextObserverGenerator`. Note: `[Event]` listener components are synthesized at discovery time, so disabling `Event` generators is intended for assemblies that don't use `[Event]`.

Example:

```csharp
[assembly: Entitas.CodeGeneration.Attributes.ContextDefinition("Game")]
[assembly: Entitas.CodeGeneration.Attributes.EntitasGeneration(EntityApi = EntityApiStyle.Atomic)]
[assembly: Entitas.CodeGeneration.Attributes.DisableEntitasGenerator("ContextObserver")]
```

### Power-user recipe: running your own generators

The engine is **public and reusable** inside the single analyzer DLL — there is no separate Core assembly. A power-user references `Entitas.SourceGenerator.dll` from their own analyzer/`IIncrementalGenerator` and reuses the public surface: `EntitasDiscovery.Discover(compilation)` + `DiscoveryResult`, the data POCOs (`ComponentData`, etc.), `AbstractGenerator` / `CodeGenFile` / `CodeGeneratorData`, the concrete `src/Generators/`, `CodeGeneratorExtensions`, and `EntitasGenerators.All()` (raw canonical set, ignores config) / `EntitasGenerators.Default(compilation)` (config-adjusted set). Emit via `spc.AddSource(...)`; `EntitasIncrementalGenerator.BuildHintName(file, takenSet)` derives a unique hint name the same way the framework does.

```csharp
var result = EntitasDiscovery.Discover(compilation);             // public discovery
var data = result.Components.Cast<CodeGeneratorData>().ToArray();
foreach (var gen in EntitasGenerators.Default(compilation))      // built-ins
    Emit(spc, taken, gen.Generate(data));
Emit(spc, taken, new MyGenerator().Generate(data));             // your AbstractGenerator
```

### Renaming a component

Generated code no longer lives on disk, so an IDE rename of a component class does not reach the members the generator derives from its name (`GameMatcher.Health`, `entity.health`, `hasHealth`, `AddHealth`, `SafeRemoveHealth`, `HealthChanged`, `IAnyHealthListener`, …) — those are declared in read-only generated documents. The supported way to rename is `[RenameTo]`:

```csharp
[RenameTo("Hp")]
[Game] public class Health : IComponent { public int Value; }
```

`Alt+Enter` on the attribute → *"Rename component 'Health' to 'Hp' and update all usages"*. The attribute is inert (generation ignores it), so the project keeps compiling between marking and applying. See `src/Entitas.CodeFixes/README.md`.

The same engine runs from the terminal when the IDE is not an option:

```bash
# dry run: identifier map, every usage, the file move
dotnet run --project src/Entitas-Flux/src/Entitas.Rename/src -- Health Hp -r <unity-project>

# the same with -a applies the changes
```

Options: `-a/--apply`, `-r/--root <dir>` (where to look for the csproj, default cwd), `-p/--project <csproj>`, `-s/--include-strings` (also rewrite string literals and comments), `-k/--keep-file-name`. The CLI is deliberately not installed as a global tool: the identifier map comes out of the generator engine, so a tool version that drifts from the generator the project compiles with would compute the wrong names.

`RenameEngine` (`src/Entitas.SourceGenerator/src/Rename/`) is shared by both fronts, and its identifier map is **not** hand-maintained: it calls `EntitasGenerators.Generate` twice — on the compilation as-is, and on a copy where only the component's declaration (plus any `[ComponentName("…")]` argument) is renamed — and diffs the *declared* names of both outputs. What the generator stops declaring, paired with what it starts declaring, is the map. New generators or changed naming rules need no change here; anything the diff cannot pair is reported as a warning instead of being renamed silently. Usages are then resolved through a semantic model of the project **plus the generated trees**, so only members that really come from generated Entitas code (or the component type itself) are rewritten — `monster.Health` on an unrelated type is left alone. The CLI additionally moves the component's `.cs` together with its Unity `.cs.meta` (via `git mv` when tracked) so the asset GUID survives.

Usages are also rewritten in assemblies that merely *reference* the declaring one (Editor assemblies, test asmdefs), where the generated API is visible only through an assembly reference: `ExternalRename.CollectEdits` accepts an identifier whose symbol comes from the declaring assembly and belongs to the component or to a generator-declared type (`GameEntity`, `GameMatcher`, `GameComponentsLookup`, …). These are collected before the declaring assembly is rewritten, since afterwards the old names stop resolving. The code fix walks the solution's referencing projects; the CLI compiles the sibling csprojs that mention the declaring one and wires them to its live compilation via `ToMetadataReference()`.

### The ECS graph — entitas-graph

`src/Entitas.Graph/` answers "who creates / reads / filters / removes / destroys component X, and in which feature at which position" from semantics, not text. The pipeline (`GraphBuilder.Build`):

- **Prepare** (`Analysis/BuildContext.cs`): `GenerationInput.From` + `EntitasGenerators.Generate` give the generated trees (in file mode, the marker files already in the compilation). Generated API members are classified **by the generated file that declares them** (`{Context}/Components/{Context}{Name}Component.cs` → `Add`/`Replace`/`Remove`/`SafeRemove`/`has`/flag/atomic/matcher; the lookup file → index constants; `Contexts.cs` → entity-index methods), so no naming rule is re-implemented here. `[ThreadStatic] ignoreNamespaces` must be set on the calling thread before `ComponentName()`.
- **Walk** (`Analysis/TreeWalker.cs`, parallel per tree): only identifiers whose text is a generated member name are bound — that filter is what keeps ~2800 trees at 2–3 s. Companion components (`XChanged`, listener components) are folded into the component they serve. Entities are traced (`foreach` over a group → that group; a local's initializer; a factory call) so `Destroy()` and group writes know what they act on.
- **Assemble** (`GraphBuilder.Assemble`): edges aggregate by key. INFERRED edges come from two places: `Propagate` (accesses made in non-system types, helpers and query classes credited to the calling systems through the call graph, interface dispatch included, depth ≤ 4, `through` names the method) and `InferDestroysFromShapes` (a generic destroyer's `AllOf` ⊆ the components an entity was created with plus those a group write adds). `FeatureTree.Place` finds roots (non-generated features nobody registers) and gives each system its order under each root — no project names anywhere; requests/events only by suffix.
- **Output** (`Output/`): `EcsGraph.ToJson` (Graphify-like node-link, `schema_version` 1, must round-trip byte for byte), `GraphQueries` (the commands; names are resolved, never guessed — exit 3 ambiguous, 4 not found; every answer goes through `Render`, which caps it at a token budget, default 2000, and leads a cut answer with a summary — assistants are the main readers, so keep new output inside it), `GraphReport`, `GraphHtml` (vis-network 9.1.9 embedded as a resource with its licence banner; `</` in the data is escaped; reads/filters/reacts are drawn component → system, `DrawnComponentToSystem`, while edges stay stored system → component; the page is styled with Primer's colours from CSS variables the script reads too, so the canvas follows the theme; `frame()` measures `getBoundingBox()` (labels included) after the first draw and moves without animation, because vis-network's own `stabilization.fit` overrode it and a background tab never finishes an animation — check layout changes in a visible browser and with two searches in a row).
- **Destroys by marking and `[Watched]` reactions.** INFERRED destroys also come from `InferDestroysFromMarking`: one foreach (`Access.Loop`) whose writes include a destroyer's AllOf credits it with the other components written there and those the group requires (minus `GroupShape.Changes`, folded `XChanged` requirements) — never across loops, groups or systems; one edge per destroyer and component. `TreeWalker.LoopOfArgument` traces a write on a helper's parameter to the caller's foreach, one level, only when all calls in the type agree. In the queries `XChanged` resolves to X as `Resolve`'s last fallback, and writes of a `[Watched]` component name their non-generated reactors inline (`ReactedToBy`).
- **Transient guards.** `TransientComponents` marks a component transient when it has `[Cleanup]` or a system removes it, unconditionally (`Access.EveryEntity`), in a loop over a group of it alone (a NoneOf only for what that loop sets). `TransientGuards` puts `only_while` on a flag's EXTRACTED group creates when every loop of that setter requires such a component; the report then lists the flag as set once, like the NoneOf guard. Keep it conservative: on Cat Caffeine, loosening either restriction marks wrong components transient.
- **`flow`** (`Output/FlowWalk.cs`, text in `Output/FlowQuery.cs`, a partial of `GraphQueries`) walks hand-offs only: requests and events to their readers, transient components, `[Watched]` writes to their reactors (`Reactors` drops those whose `NoneOf`/`AllOf` excludes this kind of change). State and hubs go on an "also" line; each node is expanded once, breadth-first. Frames come from placements: `Slot` gives (phase, order), Cleanup-only systems after every Execute one; gates come only from the `through` of shape-inferred destroys ("…; Ready by MarkEventsReadySystem"); removers in between are checked; anything else is "frame unknown" — never guess. Past 95% of the budget the "also" lines go first, then endings fold, then the bare level — the main chain is cut last. `graph.json` does not record which components share one group, so "same frame" is the earliest a taker can see the data. Verify label changes against real systems' `GetGroup` code (on Cat Caffeine, build mode runs `BuildingModeFeature` alone in Update).
- **Conditions and calls.** `TreeWalker.ConditionOf` records every clause around each create, remove, destroy and call, outermost first, as a `Condition` (`Analysis/Guards.cs`). Loops and lambdas are not conditions, but a lambda passed with a `bool` argument whose parameter is named `when`/`if`/`condition`/`onlyIf`/`predicate` runs under it; an `if` that only throws and the guards a method starts with are skipped. `CallerGuards` passes a private method's in-type call conditions to its writes and calls when every call is conditional — never for non-private or attributed methods, Unity message names, method-group uses or calls from field initializers (Unity events, `[Inject]` and delegates are invisible to the call graph); complementary calls cancel; at most 3 calls deep. `Guards.Format` drops covered ways, merges `p && a` / `p && !a` into `p`, and renders once per edge at ~70 chars (full, then tail-cut) — never cut a clause earlier, or merging stops working. `called` names the system's own call leading to `through`. In `flow`, `Calls` puts inferred-only writes on one line per group of called methods; requests, events and any `[Watched]` write (not `AddX`) some system reacts to are followed under it, with a never-sees note; transient writes only when a known frame sees them. Keep conditions on writes only: a read under a condition is still a read.
- **Constant arguments** (`Analysis/ConstantArguments.cs`): each way `Propagate` walks to a method carries `Bindings` (constants followed through parameters, never-reassigned locals, return values, and members an object is built with — `TreeWalker.NoteWritten`), bound only for parameters a clause mentions and after the cheap `MayCarry` check, since binding a caller's body is the cost. A clause that can't hold drops the way (no way left, no edge); one that must hold is dropped; unsure stays. Sound before complete: anything assigned elsewhere, virtual or interface members, an earlier case that may match → unknown. Enum members are written without their type unless two names in a clause collide.
- **Unseen reactors** (`Analysis/UnseenReactors.cs`, origins from `Analysis/EntityOrigins.cs`): a `[Watched]` write records in `unseen_by` the reactors whose group's AllOf needs a component the written entity certainly never gets — absent from every creation it can come from and only ever added on entities being made. Unknown origin, delegate-used helpers, DLL callbacks, or any computed-index `AddComponent`/`CopyTo` in the project → no exclusion. Its `Reach` copies `Propagate`'s call reach without constant pruning, which only makes it keep more reactors. `flow`'s never-sees note names the reactor's other reacts edges.
- **Messages by structure** (`Analysis/Messages.cs`): only steady loops count — a foreach in the system's own Execute/Cleanup (or a method one call away), not under a condition, nested or behind a guard other than its own group's emptiness (`TreeWalker.IsSteady`). Sweeps destroy every entity (no NoneOf); arms set components unconditionally (NoneOf only what they set). Consumed = holding it alone leads to a sweep; markers (marking or carrying) are no messages; event-like is carried only by consumed components and collected without the carrier. `GraphNode.MessageRole` (suffix first, structure second) is what the queries use; the report's four request/event sections stay name-based. On Cat Caffeine 58/58 events and 70/88 requests agree and every query output is byte-identical — loosening the guard rule turns UI hints into requests. Helper conditions (`HelperConditions.cs`) come from the helper's body; the parameter-name list is only for helpers from a DLL. No project name may appear in the graph's logic: `tests/FixtureNeutral/` (a space port, loaded through `FixtureFolder.Neutral`) shares no names with Cat Caffeine or `Fixture/` and must keep passing.
- **Determinism.** Trees are walked in parallel into an array in path order (`GraphBuilder.Build`), never collected as they finish: registration order, `through` texts and which of equal sites an edge keeps all follow access order, and the same code must write the same `graph.json` (`The_graph_does_not_depend_on_the_order_the_sources_come_in`, which needs the fixture's `DebugFeature` registering a system from another file).
- **`query`** (`Output/QuerySubgraph.cs`, `Output/NameWords.cs`) is a subgraph, not a search. Seeds rank by: words matched (whole camel-case words by rough stem, or a word's start; name or namespace) → words matched in the name → fewest other name words (kind words like System/Request/Event excluded) → EXTRACTED degree. `ChooseSeeds` gives no slot to a reader of a seeded event whose matched words (`MatchedTerms`, a bit per query word) the event matches too — it is one step out anyway — so the request and handler that start a flow get seeds; a general "skip neighbours of a better seed" rule was tried and drops the request and event from `furniture placement`. Between equal name matches, a **system** ranks by the query words its name lacks but components it creates/removes in its own code name, then those its groups collect (`Index.MeasureCode`: EXTRACTED only, component labels only, no hubs, no destroys, no types — counting types made `OrderFactory` a seed for `order paid`); a seed won this way names its witnesses in the head (`X (creates A, B)`). `EchoesEvent` compares name terms only. Then a breadth-first walk without `contains`. Hubs (> 40 distinct neighbours over EXTRACTED edges, `hubThreshold`) are reached but not walked through — a factory's inferred edges must never make the central system a hub. Each node lists only links to nodes after it, so every edge appears once. Over the budget it is re-walked with 12/8/6 links per node from the same prepared index (scores computed once per query), so the part shown stays connected; a write to a `[Watched]` component someone reacts to counts as a hand-off, like a request or event.
- **`diff`** (`Output/GraphDiff.cs`, `RefSnapshot.cs`): edges compare by (source, target, relation) only; a system counts as moved only when its features or its order among its siblings changed. `--ref` extracts the commit's `.cs` files with `git archive <commit>:<prefix>` (run from the repository top) into a temp folder, maps today's csproj onto them (`RefSnapshot.MapSources`) and compiles against today's references. It never runs checkout, worktree or stash — users run it on their working copy. `--ref` is repeatable (old, then new); `--commit X` is X's first parent against X (a merge says so; a root commit is an error). Two refs build in parallel, each mapping today's csproj onto its own commit with today's references and defines. `DiffRequest` picks the sides from the arguments alone; `DiffCommandTests` runs the command through the entry point against a temporary repo with Console redirected, so it sits in a non-parallel collection.
- **`install`** (`Setup/`): a marker-delimited block in CLAUDE.md/AGENTS.md at the git root. `InstructionBlock` and `GitIgnore` are pure (string in, string out); `AssistantSetup` plans every file before writing any. A PackageCache tool path is written as a `name*` glob because the folder's hash changes with every update. Keep the block ≤ 15 lines — it is loaded into every assistant session.

Tests run against `tests/Fixture/` — a small Unity-like project compiled with the real generator, in both output modes. Cat Caffeine is the real-world check (expected: `FurnitureAddedToCafeEvent` with 2 creators, 7 readers in the achievements feature, `DestroyEventsSystem` as inferred destroyer); run it **with `-o` outside that project** — the default output folder is inside the Unity root.

### Flux-Specific Features

Generators live in `src/Entitas-Flux/src/Entitas.SourceGenerator/src/Generators/`.

- **Atomic components**: Single-field (`Value`) components can get simplified property access (`entity.CurrentHealth` instead of `entity.currentHealth.Value`) via `AtomicComponentEntityApiGenerator`. It is NOT wired by default — the canonical config uses the plain `ComponentEntityApiGenerator`. Opt in per-assembly with `[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic)]` (it supersedes, not coexists with, the plain generator). See "Customizing generation" above.
- **Watched components**: `[Watched]` causes `{Component}Changed` marker components plus `HasChanged(...)` query methods and a cleanup system. Handled by `WatchedComponentGenerator`, `WatchedEntityHasChangedGenerator`, and `WatchedCleanupSystem(s)Generator` (all wired).
- **Safe removal**: `SafeRemove{Component}()` methods generated by the component entity-API generator.

### Compiler Defines

- `ENTITAS_DISABLE_REACTIVITY` — Disables default reactivity for performance
- `ENTITAS_HIDE_STANDARD_MEMBERS` — Hides lowercase standard members in atomic components

## Performance-sensitive internals

Two pieces of the runtime look odd until you know why; both came out of a CPU profile and
both are covered by `GroupStorageTests`:

- **`Entity` stores components in `ComponentSlot[]`, not `IComponent[]`.** Arrays of a
  reference type are covariant, so every store into an `IComponent[]` costs a runtime
  type check (the JIT's `StelemRef`) — a fifth of the component-change path. Writing to a
  field of a struct element has no such check. Do not "simplify" it back.
- **`Group` stores entities in a sparse set keyed by `Entity.denseIndex`.** A `HashSet`
  was ~27% of that same path. The dense index is per context, so membership checks verify
  the entity's identity in the slot, not just that the slot is taken — two contexts number
  their entities from zero and would otherwise be confused for each other.

Benchmarks and the profiling target live in `benchmarks/` — measure before and after, and
prefer the allocation column: timings on a busy machine move by tens of percent.

## Conventions

- Target frameworks: `netstandard2.1` for libraries, `net6.0` for executables and tests
- Testing: xUnit with FluentAssertions; test files live in `tests/` subdirectories alongside `src/` within each project. Generator changes need more than a text comparison: `FluxBehaviorTests` compiles generated output into a real assembly, loads it and runs it — that is what caught `[Watched]` generating perfectly and doing nothing. Behaviour tests run in both output modes (`OutputModes.Both`). `tests/Snapshot` is the approved output (`ENTITAS_UPDATE_SNAPSHOT=1` to accept a change, then read the diff)
- Private fields use `_camelCase`; classes and interfaces use standard C# naming (`PascalCase`, `IPrefix`)
- Shared build configuration is centralized in `src/Entitas-Flux/Directory.Build.props`
- Unity version: 2022.3.62f2 (2022.3 LTS; required for Roslyn 4.x / `IIncrementalGenerator` support)
