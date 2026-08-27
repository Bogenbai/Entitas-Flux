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

## Architecture

The key layers of the solution (`src/Entitas-Flux/Entitas.sln`):

**Core Runtime** (`src/Entitas-Flux/src/Entitas/`) — The ECS framework itself: Entity, Context, Group, Matcher, Collector, EntityIndex, Systems. This is what ships in Unity at runtime.

**Code Generation Attributes** (`src/Entitas-Flux/src/Entitas.CodeGeneration.Attributes/`) — Attributes like `[Game]`, `[Watched]`, `[Unique]`, `[DontGenerate]`, `[Cleanup]`, and `[ContextDefinition]` that users put on component classes / the assembly. These are the lightest dependency — only attribute definitions, no logic.

**Source Generator** (`src/Entitas-Flux/src/Entitas.SourceGenerator/`) — A Roslyn `IIncrementalGenerator` that generates the ECS API (contexts, entities, matchers, component accessors, events, cleanup/watched systems) at **compile time**. It replaced the previous Jenny CLI pipeline: there is no separate generation step and no committed `Generated/` folders — consuming projects reference it as an analyzer (`OutputItemType="Analyzer"`). It is a netstandard2.0 analyzer depending only on `Microsoft.CodeAnalysis.CSharp`. Internals: `src/Discovery/` (semantic-model data providers that build the data model from the current compilation) → `src/Generators/` (the ported string-template generators) → `EntitasIncrementalGenerator` (wires them and emits each fragment via `AddSource`).

**Distribution** — `build.sh` produces the Unity DLL layout in `Artifacts/`; `package.sh <version>` turns that into a UPM package (`Artifacts/package/`) with generated `.meta` files whose GUIDs are derived from the asset path, so they stay stable across releases. The release workflow publishes the package as the root of the generated `upm` branch (`…git#upm`, pinnable as `#upm/vX.Y.Z`).

**Standalone CLIs** — `Entitas.SourceGenerator.Cli` (`entitas-gen`, writes the generated code to disk as real .cs files) and `Entitas.Rename` (`entitas-rename`, renames a component and every identifier derived from it). Both turn a Unity-generated `Assembly-CSharp.csproj` into a Roslyn `Compilation` through the shared `ProjectLoader` (`src/Entitas.SourceGenerator.Cli/src/ProjectLoader.cs`, linked into the rename tool) and then drive the same generator engine.

**Unity Integration** — `Entitas.Unity`, `Entitas.Unity.Editor`, `Entitas.VisualDebugging.*` projects provide Unity inspector/editor tooling.

**Migration** — `Entitas.Migration` and related projects handle upgrading between Entitas versions.

### Source Generator Pipeline

Code generation follows a discovery → generator pipeline, all inside the C# compiler:
- **Discovery** (`src/Discovery/`) reads the current `Compilation` via `SemanticModel`/`ISymbol` (NOT external `.csproj` parsing) to build the data model (`ComponentData`, `ContextData`, `EntityIndexData`, `CleanupData`, `WatchedCleanupData`). Contexts come from `[assembly: ContextDefinition("…")]` (first = default), replacing the old Jenny `Contexts = …` config.
- **Generators** (`src/Generators/`) are verbatim ports of the legacy templates; each emits `CodeGenFile` fragments.
- `EntitasIncrementalGenerator` projects the compilation into `GenerationInput` (`src/GenerationInput.cs`) — a value holding only strings, bools and the data POCOs — and hangs generation off THAT. Roslyn re-runs the projection per keystroke but compares it by value, so an edit that does not change components, their attributes or the generation config skips generation entirely. `IncrementalityTests` pin both directions (cached on an unrelated edit, re-run on a component change). Anything added to the model must stay equatable and free of symbols/syntax, or the cache silently stops working.
- `EntitasGenerators.Generate(GenerationInput)` resolves the active generator set (canonical set adjusted for the assembly's config attributes), runs each generator, and emits each fragment as its own `AddSource` partial-class unit (the compiler merges the partials — there is no file-merge step).
- **Failures are reported, never swallowed**: a generator that throws yields `ENT0100` naming it and the reason; a discovery failure yields `ENT0101`. Both are errors — see `EntitasDiagnostics`. `entitas-gen` prints them and exits non-zero; `RenameEngine` refuses to plan a rename when generation failed, since the identifier map would be wrong.
- Equivalence to the original Jenny output is locked by `GoldenEquivalenceTests` against the frozen `tests/JennyBaseline/` snapshot. With no config attributes present, output is byte-identical to the canonical default.

### Customizing generation without rebuilding the framework

Consumers customize generation via assembly-level attributes (in `Entitas.CodeGeneration.Attributes`); the generator reads them from the compilation by full metadata name (no extra reference needed). With none present, behavior is the canonical default.

- `[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic)]` — swaps the plain `ComponentEntityApiGenerator` for `AtomicComponentEntityApiGenerator` (one or the other, never both — they emit conflicting entity members). `EntityApiStyle.Plain` (default) keeps the canonical plain API.
- `[assembly: EntitasGeneration(IgnoreNamespaces = true)]` — drops the namespace from generated component names (e.g. `My.Game.HealthComponent` → `Health` instead of `MyGameHealth`).
- `[assembly: EntitasGeneration(DebugHooks = true)]` — injects `Entitas.EntitasDebugHooks.OnAdd/OnReplace?.Invoke(this, index, value)` at the top of every generated `Add`/`Replace` (via `DebugHookInjector`; runs only when on, so default/golden output stays byte-identical). `EntitasDebugHooks` is a runtime class (`src/Entitas/src/EntitasDebugHooks.cs`) holding two `Action<IEntity,int,object>` delegates; assign one and breakpoint inside to catch a specific mutation — the call stack shows which system did it. Replaces the old "edit the generated ReplaceX + breakpoint" workflow; works with atomic + `ENTITAS_DISABLE_REACTIVITY` (the hook fires before the in-place mutation). Debug only — turn off for release; a null handler costs one null-check.
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

### Flux-Specific Features

Generators live in `src/Entitas-Flux/src/Entitas.SourceGenerator/src/Generators/`.

- **Atomic components**: Single-field (`Value`) components can get simplified property access (`entity.CurrentHealth` instead of `entity.currentHealth.Value`) via `AtomicComponentEntityApiGenerator`. It is NOT wired by default — the canonical config uses the plain `ComponentEntityApiGenerator`. Opt in per-assembly with `[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic)]` (it supersedes, not coexists with, the plain generator). See "Customizing generation" above.
- **Watched components**: `[Watched]` causes `{Component}Changed` marker components plus `HasChanged(...)` query methods and a cleanup system. Handled by `WatchedComponentGenerator`, `WatchedEntityHasChangedGenerator`, and `WatchedCleanupSystem(s)Generator` (all wired).
- **Safe removal**: `SafeRemove{Component}()` methods generated by the component entity-API generator.

### Compiler Defines

- `ENTITAS_DISABLE_REACTIVITY` — Disables default reactivity for performance
- `ENTITAS_HIDE_STANDARD_MEMBERS` — Hides lowercase standard members in atomic components

## Conventions

- Target frameworks: `netstandard2.1` for libraries, `net6.0` for executables and tests
- Testing: xUnit with FluentAssertions; test files live in `tests/` subdirectories alongside `src/` within each project
- Private fields use `_camelCase`; classes and interfaces use standard C# naming (`PascalCase`, `IPrefix`)
- Shared build configuration is centralized in `src/Entitas-Flux/Directory.Build.props`
- Unity version: 2022.3.62f2 (2022.3 LTS; required for Roslyn 4.x / `IIncrementalGenerator` support)
