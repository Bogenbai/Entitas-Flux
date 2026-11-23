# Entitas Flux
**Entitas Flux** is a fork of the great and terrible [Entitas Framework](https://github.com/sschmid/Entitas).  
I created it to add features missing from the original Entitas that I believe should be there, and to support newer Unity versions.  
Don’t expect major changes or a big redesign like [Entitas Redux](https://github.com/jeffcampbellmakesgames/Entitas-Redux). Updates will (or will not) come slowly and only when I need a feature.

![CI](https://github.com/Bogenbai/Entitas-Flux/actions/workflows/ci.yml/badge.svg)
![Release](https://github.com/Bogenbai/Entitas-Flux/actions/workflows/release-on-tag.yml/badge.svg)

What changed in each version: [CHANGELOG.md](CHANGELOG.md).

## Contents

- [Installation](#installation)
- [Features](#features)
  - [Atomic components](#atomic-components)
  - [Watched attribute](#watched-attribute)
  - [Friendly Component Dropdown](#friendly-component-dropdown)
  - [Safe component removal](#safe-component-removal)
  - [Renaming components](#renaming-components)
  - [Defines](#defines)
- [Code generation](#code-generation)
  - [Where components live](#where-components-live)
  - [Component indices are not stable](#component-indices-are-not-stable)
  - [Diagnostics](#diagnostics)
- [Configuring generation](#configuring-generation--entitasgenerationcs)
- [Generated code as files](#generated-code-as-files--output--generationoutputfiles)
- [Debugging a mutation](#debugging-a-mutation--debughooks)
- [Renaming a component](#renaming-a-component)
- [Mapping the ECS — entitas-graph](#mapping-the-ecs--entitas-graph)
- [License](#license)

## Installation

> **Requires Unity 2022.3+** (Roslyn 4.x, needed for the incremental source generator). Verified against 2022.3 LTS and Unity 6.

**Package Manager (recommended).** *Window → Package Manager → + → Install package from git URL*:

```
https://github.com/Bogenbai/Entitas-Flux.git#upm
```

or pin a version: `https://github.com/Bogenbai/Entitas-Flux.git#upm/v0.1.0`. Everything arrives configured — the analyzer DLLs already carry the `RoslynAnalyzer` label, so there is nothing to set up by hand.

<details>
<summary><b>Manual install</b> (no Package Manager)</summary>

Download the `Entitas-Flux-vX.X.X` archive from [Releases](https://github.com/Bogenbai/Entitas-Flux/releases) and copy the DLLs, which are organized by destination folder:

```
// Assets/Entitas/Entitas:
Entitas.dll
Entitas.CodeGeneration.Attributes.dll
Entitas.Unity.dll
Entitas.VisualDebugging.Unity.dll
// Assets/Entitas/DesperateDevs:
DesperateDevs.*.dll               ← third-party assemblies Entitas is built against
Sherlog.dll
// Assets/Entitas/DesperateDevs/Editor:
DesperateDevs.Unity.Editor.dll    ← the editor assemblies below need it
// Assets/Entitas/Entitas/Editor:
Entitas.Migration.dll
Entitas.Migration.Unity.Editor.dll
Entitas.Unity.Editor.dll
Entitas.VisualDebugging.Unity.Editor.dll
// Assets/Entitas/Entitas/Analyzers:
Entitas.SourceGenerator.dll      ← the Roslyn source generator
Entitas.CodeFixes.dll            ← IDE quick fixes, e.g. [RenameTo]
// Assets/Entitas/Entitas/Tools~:
entitas-gen.dll, entitas-rename.dll, …  ← run by the editor; Unity ignores "~" folders
```

**The analyzer DLLs are special.** Select **both** `Entitas.SourceGenerator.dll` and `Entitas.CodeFixes.dll` in Unity, and in the Inspector: add the asset label **`RoslynAnalyzer`**, and uncheck **all** platforms under "Select platforms for plugin" (Any Platform off, Editor off). Apply. This is what makes Unity feed them to the compiler instead of trying to load them as managed plugins.
</details>

Then add an `EntitasGeneration.cs` declaring your contexts and options — that one file is all the setup there is:

```cs
using Entitas.CodeGeneration.Attributes;

[assembly: ContextDefinition("Game")]    // the first one is the DEFAULT context
[assembly: ContextDefinition("Input")]
```

See [Configuring generation](#configuring-generation--entitasgenerationcs) for the rest of the options.

> **Coming from Entitas or an older Entitas-Flux?** Delete the old `Jenny/` folder, the `*.CodeGeneration.Plugins.dll`s, any `JennyRoslyn.properties`, any committed `Generated/` folders, and the Entitas DLLs previously copied into `Assets/`. None of them are used anymore, and a leftover copy will clash with the package.

## Features
### Atomic components
Components that have a single field are generated with a single property, which simplifies access to the value:
```cs
[Game] public class CurrentHealth : IComponent { public float Value; }
// access:
entity.CurrentHealth
// instead of:
entity.currentHealth.Value
```
> Atomic access is opt-in per assembly — enable it with `[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic)]`. See [Code generation](#code-generation).


### Watched attribute
This attribute simplifies deferred reactivity.  
When component `X` is marked with the `[Watched]` attribute and its value is changed (via `ReplaceX(...)`/`AddX(...)`/`RemoveX()`, the entity receives an `XChanged` marker component.   
These markers live for one frame: they notify systems during that frame and are then removed so the logic doesn’t repeat on the next frame.  
```cs
[Game, Watched] public class Wallet : IComponent { public Dictionary<CurrencyTypeId, int> Value; }
// entity.ReplaceWallet(newValue);
// will cause entity.isWalletChanged to be `true`
```
It will also generate a `GameWatchedCleanupSystems` feature that removes all those `Changed` component on cleanup. You should put it in your systems order, usually it fits well right before `GameCleanupSystems`.

### Friendly Component Dropdown
In the original Entitas adding components via inspector is a pain because there’s no search bar. **Entitas Flux** has one.
<details>
  <summary>📸 Show large screenshot</summary>

  <div align="center">
    <img src="https://github.com/user-attachments/assets/bfa51c31-c62c-4291-98c3-de965bb38552" alt="My screenshot" width="900">
  </div>
</details>

### Safe component removal
Sometimes you just remove component `X` if it exists on the entity.
Without extra `if`s or matcher checks, call `SafeRemoveX()`:
```cs
entity.SafeRemoveBoxCollider2D();
entity.SafeRemoveCollider2D();
```
Under the hood it does this:
```cs
if (hasBoxCollider2D) 
    RemoveComponent(GameComponentsLookup.BoxCollider2D);
```
### Renaming components
One component name fans out into `entity.isCanAttack`, `GameMatcher.CanAttack`, `AddCanAttack`… Renaming the class in the IDE leaves all of those behind, so rename the component instead — from the IDE, from Unity or from a terminal, and every derived identifier and usage follows:
```cs
[RenameTo("AbleToAttack")]
[Game] public class CanAttack : IComponent { }
```
`Alt+Enter` on the attribute → *"Rename component 'CanAttack' to 'AbleToAttack' and update all usages"*, or *Tools → Entitas → Rename Component…* in Unity. See [Renaming a component](#renaming-a-component).

### Defines
`ENTITAS_DISABLE_REACTIVITY` - partially disables Entitas' default reactivity. Gives small performance boost.
`ENTITAS_HIDE_STANDARD_MEMBERS` - hides standard generated component members that start with a lowercase letter in atomic components.

### More features coming soon (or not)

## Code generation
Entitas-Flux generates the whole ECS API — contexts, entities, matchers, component accessors, events, and the cleanup/watched systems — at **compile time** with a Roslyn [incremental source generator](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/source-generators-overview). It replaced the old Jenny pipeline, so:

- **No separate generation step.** The generator runs inside the C# compiler on every build.
- **No committed `Generated/` folders** by default. The generated code lives in the compiler's output, not on disk — unless you [ask for files](#generated-code-as-files--output--generationoutputfiles).
- **Components are discovered from the compilation** — any class implementing `IComponent`, plus its context/`[Watched]`/`[Unique]`/etc. attributes. No external config files.
- **IDE-native.** Rider/Visual Studio see the generated API immediately; Cmd/Ctrl-click navigates straight into it.
- **Incremental.** Generation is keyed on the components, their attributes and your generation config — nothing else. Editing a method body or a comment re-runs nothing.
- **Loud when it breaks.** Failures are reported where you can see them instead of leaving a hole in the API for you to find via a hundred errors in your own code — see [Diagnostics](#diagnostics).

> Want the generated code on disk as ordinary files — for navigation, external code-analysis tools, diffs or breakpoints? Set `Output = GenerationOutput.Files`: see [Generated code as files](#generated-code-as-files--output--generationoutputfiles). Same code, same generator.

### Where components live

**All components must live in the assembly that declares the contexts.** Generation runs
per assembly, into the one with the `[assembly: ContextDefinition("…")]`, and components
anywhere else are not discovered — you get `ENT0003` instead of silence.

Systems, on the other hand, can live wherever you like: they only *use* the generated API,
so any assembly referencing the one that generated it works.

The reason is a C# rule rather than a decision: the generated `GameEntity` is a partial
class, and a partial class cannot be extended from another assembly. Lifting this needs a
different shape of generated API (extension methods) and component indices assigned at
runtime instead of compile time — a rewrite of the runtime, not a setting. If you split a
Unity project into asmdefs, keep the components together with the contexts and put the
feature code around them.

### Component indices are not stable

`GameComponentsLookup.Health` is a compile-time constant, and the numbers are handed out
alphabetically within a context. Adding a component shifts the index of every component
after it in the alphabet.

That is fine for everything the framework does with them, but it means **the numbers must
never be persisted** — not in save files, not on the wire, not in assets. Store the
component's type or name and resolve it at load time. A save written before adding
`ArmorComponent` and read after it would silently deserialize into the wrong components.

### Diagnostics

| ID | Severity | Meaning |
| --- | --- | --- |
| `ENT0002` | Warning | This assembly declares components but no context, so **nothing is generated for them**. Quick fix: *Declare the "Game" context for this assembly*. |
| `ENT0003` | Warning | This component is in a different assembly than the contexts, so **nothing is generated for it** — see [Where components live](#where-components-live). No quick fix: declaring a context here would generate a second, parallel set of contexts instead of helping. |
| `ENT0001` | Warning | A component is marked `[RenameTo("…")]` and the rename has not been applied yet — see [Renaming a component](#renaming-a-component). |
| `ENT0004` | Warning | `Output = GenerationOutput.Files`, and the generated files no longer match the components (missing, outdated, or no longer generated). Regenerate: *Tools → Entitas → Generate*, or `entitas-gen`. |
| `ENT0005` | Warning | Files written by the file mode are still in an assembly that generates with the source generator. The generator stands down until they are gone — otherwise every type would be defined twice. *Tools → Entitas → Generate* deletes them. |
| `ENT0100` | Error | A generator threw; it names itself and the reason. The API it owns will be missing. |
| `ENT0101` | Error | Entitas could not build its data model from the assembly. Nothing was generated. |

`ENT0002` exists because generation is opt-in: without an `[assembly: ContextDefinition("…")]` the generator emits nothing at all, and that silence used to be the most confusing way to meet this framework — you write a component, `GameEntity` does not exist, and nothing says why. Assemblies that merely *use* the generated API (a systems asmdef, a test assembly) declare no components and are left alone.

## Configuring generation — EntitasGeneration.cs
Create one file (any name) with assembly-level attributes. This replaces the old `JennyRoslyn.properties`. Example:
```cs
using Entitas.CodeGeneration.Attributes;

[assembly: ContextDefinition("Game")]    // the first one is the DEFAULT context
[assembly: ContextDefinition("Input")]
[assembly: ContextDefinition("Meta")]

[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic, IgnoreNamespaces = true)]
```
With **no** attributes present at all, you get the canonical default (Plain entity API, namespaces kept, every built-in generator on).

### Contexts — `[ContextDefinition("Name")]`
Declares a context; repeat it for each one. The **first** declared context is the default for components that don't specify a context attribute (e.g. a plain `[Game]`-less component). Replaces Jenny's `Contexts = Game, Input, …`.

### Options — `[EntitasGeneration(...)]`
| Option | Default | Effect |
| --- | --- | --- |
| `EntityApi` | `EntityApiStyle.Plain` | `Atomic` gives single-field components the simplified `entity.CurrentHealth` accessor (see [Atomic components](#atomic-components)). `Plain` keeps `entity.currentHealth.Value`. One or the other — never both. |
| `IgnoreNamespaces` | `false` | Drops the namespace from generated component names: `My.Game.Health` → `Health` instead of `MyGameHealth`. |
| `DebugHooks` | `false` | Emits runtime debug hooks on every `Add`/`Replace` so you can breakpoint a specific mutation — see [Debugging a mutation](#debugging-a-mutation--debughooks). |
| `VisualDebugging` | `VisualDebuggingStyle.EntityGameObjects` | `SingleGameObject` debugs every context from one `Entitas Debug` GameObject instead of a GameObject per entity. |
| `Output` | `GenerationOutput.SourceGenerator` | `Files` writes the generated code to `.cs` files instead of generating it inside the compiler — see [Generated code as files](#generated-code-as-files--output--generationoutputfiles). |
| `OutputPath` | `""` | Where `Files` writes, relative to the Unity project root (e.g. `"Assets/Code/Generated"`). Empty: a `Generated` folder next to the file declaring the first context. |

### Disabling built-in generators — `[DisableEntitasGenerator("X")]`
Repeatable. Removes any built-in generator whose class short-name matches `"X"` case-insensitively **or starts with** it (prefix match):
```cs
[assembly: DisableEntitasGenerator("Event")]            // all Event* generators
[assembly: DisableEntitasGenerator("ContextObserver")]  // just ContextObserverGenerator
```
> `[Event]` listener components are synthesized during discovery, so only disable `Event` generators in assemblies that don't use `[Event]`.

## Generated code as files — `Output = GenerationOutput.Files`
By default the generated API exists only inside the compiler. That is fast and needs no setup, but nothing outside the compiler can see it: an IDE's *Find Usages* on `AddHealth` or `GameMatcher.Health` struggles, tools that read source files (code graphs, linters, search) see none of it — a tree-sitter graph of a production project showed a component with 2 producers and 7 consumers as having none, because the link between them lives only in generated code. Ask for files instead:

```cs
[assembly: EntitasGeneration(Output = GenerationOutput.Files)]
// or choose the folder (relative to the Unity project root):
[assembly: EntitasGeneration(Output = GenerationOutput.Files, OutputPath = "Assets/Code/Generated")]
```

The generated code is then ordinary `.cs` files in your project, and the source generator stands down so nothing is defined twice. It is **the same code** — the same generator writes it; a file is what the source generator would have emitted plus one marker line — so switching is invisible to the rest of your code.

- **One file per component per context**, at the same paths the generator has always used: `Game/Components/GameHealthComponent.cs`, `Game/GameEntity.cs`, `Events/Interfaces/IAnyCoinsListener.cs`…
- **Only what changed is rewritten.** An unchanged component costs Unity no reimport and no recompile; line endings converted by a git checkout do not count as a change.
- **Nothing is left behind.** Rename or delete a component, or move `OutputPath`, and its files go too, with their `.meta` and any folder left empty.
- **Nothing that is not ours is touched.** Only files starting with the Entitas-Flux marker are ever overwritten or deleted.
- **Stable `.meta` files.** A new file's GUID is derived from its path, so two people generating the same component on two branches do not conflict.
- **Commit the files.** They are what the project compiles from now; CI builds and fresh clones need them.
- `OutputPath` must lie inside the folder of the assembly that declares the contexts (the same asmdef), or the files would compile into another assembly. Leaving it empty always satisfies this.

### In Unity
| Menu | What it does |
| --- | --- |
| *Tools → Entitas → Generate* | Brings the files up to date now. |
| *Tools → Entitas → Generate Automatically* | On by default: regenerates by itself when a file declaring components (or the config) changes. |
| *Tools → Entitas → Check Generated Files* | Reports what is out of date without writing. |

Automatic generation finishes **before** Unity compiles the change, so a save costs one compilation that already has the new API: add a component and use it in the same save, delete one, change a field — one compile, no errors. It works like this: Unity notices the save within milliseconds and holds its asset database while `entitas-gen` runs (a refresh asked for meanwhile — Rider asks ~500 ms after a save, focusing Unity does too — simply waits), then releases it, and that refresh imports the component and its generated files together. On a ~2000-file project a run takes about 0.7 s; files that declare no component never trigger one, and a run that changes nothing costs no compile.

Unity has no hook between noticing a changed script and compiling it, so a change that reaches Unity before Entitas sees it — made while Unity was closed, or imported by a refresh that started first — is compiled once against the old generated files; Entitas then regenerates and starts the second compilation itself. With Unity closed, `entitas-gen` (for instance from a git `post-merge` hook) avoids even that.

Generation reads source, not compiled assemblies, so it works while the project does not compile — which is exactly when you need it (you just added a component and nothing uses its API yet, or you just renamed one).

The work is done by `entitas-gen`, which ships inside the package (`Tools~/`) and runs on the .NET runtime Unity itself ships — nothing to install.

### From a terminal or CI
Run from the Unity project root (it reads the `.csproj` files Unity's IDE packages write):
```bash
dotnet Library/PackageCache/com.bogenbai.entitas-flux*/Tools~/entitas-gen.dll           # UPM install
dotnet Assets/Entitas/Entitas/Tools~/entitas-gen.dll                                    # manual install
dotnet Assets/Entitas/Entitas/Tools~/entitas-gen.dll --check                            # CI: exit 1 when files are stale
```
| Option | Effect |
| --- | --- |
| `-r`, `--root <dir>` | Unity project root (default: current directory). |
| `-p`, `--project <csproj>` | Generate for this project only. Default: every project that declares a context. |
| `--check` | Write nothing; exit 1 when a generated file is missing, outdated or no longer generated. |
| `-v`, `--verbose` | List every file written or deleted, and where the time went. |

Any .NET 6 or newer runs it. Inside Unity, batchmode works too:
```bash
Unity -batchmode -projectPath . -executeMethod Entitas.Unity.Editor.Generation.EntitasCodeGeneration.GenerateFromCommandLine -quit
Unity -batchmode -projectPath . -executeMethod Entitas.Unity.Editor.Generation.EntitasCodeGeneration.CheckFromCommandLine -quit
```
(Unity refuses to run `-executeMethod` on a project that does not compile unless you add `-ignoreCompilerErrors`; `entitas-gen` has no such restriction.)

### Going back
Remove `Output = GenerationOutput.Files` and run *Tools → Entitas → Generate* once (automatic generation does it for you): the generated files are deleted and the source generator takes over. Until they are gone it reports `ENT0005` instead of defining every type a second time.

> **Do not edit generated files** — the next generation overwrites them. That includes renaming a generated member such as `AddHealth` in the IDE: the file is rewritten from the component's name, the old name comes back, and every usage you renamed breaks. Rename the component instead — see below.

## Debugging a mutation — DebugHooks
Previously you'd open the generated `ReplaceX()` method, add `if (id == 123)`, and drop a breakpoint. With compile-time generation there's no file to edit, so enable hooks instead:
```cs
[assembly: EntitasGeneration(/* ..., */ DebugHooks = true)]
```
Now every generated `Add`/`Replace` calls a runtime delegate. Subscribe once (e.g. from a bootstrap) and put your breakpoint inside the handler:
```cs
using Entitas;

EntitasDebugHooks.OnReplace = (entity, index, value) =>
{
    if (index == GameComponentsLookup.CurrentHealth && entity is GameEntity ge && ge.isPlayer)
    {
        // ← breakpoint here. `value` is the new value; the call stack shows WHICH system did it.
    }
};
```
- Works even with `ENTITAS_DISABLE_REACTIVITY` + atomic — the hook fires from inside `ReplaceX` *before* the in-place value mutation, where component-change events don't.
- `EntitasDebugHooks` lives in the runtime (not generated code), so the IDE always resolves it — no fiddling with partial methods.
- A `null` handler costs a single null-check. **Turn `DebugHooks` off for release builds.**

## Renaming a component
One component name fans out into a lot of generated API: `GameMatcher.CanAttack`, `entity.isCanAttack`, `AddCanAttack`, `ReplaceCanAttack`, `SafeRemoveCanAttack`, `CanAttackChanged`, `IAnyCanAttackListener` and the views implementing its `OnAnyCanAttack`… An IDE rename of the class (Shift+F6, F2) changes the class and leaves all of those behind — in either output mode, since the generated names come from the component's name. So rename the **component**, with one of the three fronts below. They share one engine, so they agree on the result:

```cs
// before                                    // after
attacker.isCanAttack = true;                 attacker.isAbleToAttack = true;
GameMatcher.CanAttack                        GameMatcher.AbleToAttack
entity.hasCanAttackChanged                   entity.hasAbleToAttackChanged
public void OnAnyCanAttack(…)                public void OnAnyAbleToAttack(…)
```

### In the IDE — `[RenameTo]`
1. Mark the component:
   ```cs
   using Entitas.CodeGeneration.Attributes;

   [RenameTo("AbleToAttack")]
   [Game] public class CanAttack : IComponent { }
   ```
2. `Alt+Enter` on the attribute → **"Rename component 'CanAttack' to 'AbleToAttack' and update all usages"**. The class, every derived identifier, every usage in every assembly that references this one, and the attribute itself are dealt with in one step.
3. With `Output = GenerationOutput.Files`, the generated files are rewritten too, so the project compiles straight away; when Unity next gets focus, automatic generation moves them to their new names (`GameCanAttackComponent.cs` → `GameAbleToAttackComponent.cs`). Without Unity open, run `entitas-gen`.
4. Rename the component's file yourself if you want it to follow (`CanAttack.cs`, with its `.meta`). The two fronts below do that for you.

### In Unity — *Tools → Entitas → Rename Component…*
Type the component (`CanAttack`, `CanAttackComponent` or `My.Game.CanAttack`) and the new name. **Preview** lists the identifier map and every usage; **Rename** applies it, moves the component's `.cs` with its `.meta` (via `git mv` when tracked, so the asset GUID and history survive), regenerates the files in file mode, and refreshes.

### From a terminal
Run from the Unity project root:
```bash
# dry run: the identifier map, every usage, and the file move
dotnet Library/PackageCache/com.bogenbai.entitas-flux*/Tools~/entitas-rename.dll CanAttack AbleToAttack
```
```bash
# the same with -a applies it (and regenerates the files in file mode)
dotnet Library/PackageCache/com.bogenbai.entitas-flux*/Tools~/entitas-rename.dll CanAttack AbleToAttack -a
```
With a manual install the tool is at `Assets/Entitas/Entitas/Tools~/entitas-rename.dll`. Any .NET 6 or newer runs it.

| Option | Effect |
| --- | --- |
| `-a`, `--apply` | Write the changes (without it, nothing is written). |
| `-r`, `--root <dir>` | Where to look for the csproj. Defaults to the current directory. |
| `-p`, `--project <csproj>` | Use this csproj instead of searching for one. |
| `-s`, `--include-strings` | Also rewrite occurrences in string literals and comments. |
| `-k`, `--keep-file-name` | Don't rename the component's `.cs` file. |

### Worth knowing
- **The attribute is inert.** Generation ignores it, so the project keeps compiling between marking a component and applying the rename. Mark five components today, rename them tomorrow.
- **Same-named members are safe.** A `monster.Health` on some unrelated class of yours is left alone: every usage is resolved through the compiler, not matched by text.
- **One component at a time.** There is deliberately no *Fix All*: each rename is planned against the current state of the code, so applying several at once would use stale positions.
- **After updating the DLLs, restart the IDE.** It keeps analyzer assemblies loaded in-process; until it restarts you may get the old ones (the quick fix silently disappears from `Alt+Enter`).
- The terminal and Unity fronts see the project as the `.csproj` files describe it. Unity's menu re-syncs them first; from a terminal, if a component seems missing, let Unity regenerate them (*Preferences → External Tools → Regenerate project files*).

## Mapping the ECS — entitas-graph
In an Entitas project the links that matter run through components, not calls. One system adds `PlaceFurnitureRequest`. A second groups on it and adds `FurniturePlacedEvent`. A third reacts to that. None of them calls another, so a call graph or a text-based code graph (tree-sitter, [Graphify](https://github.com/Graphify-Labs/graphify) and the like) shows three unrelated classes.

`entitas-graph` builds the graph Entitas actually has. It reads the project the way the compiler does, through the same project loader and generator engine as `entitas-gen`. So `CreateEntity.Event().AddFurniturePlacedEvent(…)`, a `GameMatcher.X` inside `AllOf`, `isX = false` and `entity.x.Value` are resolved by symbol, not matched by name. It works in either output mode and needs no generated files on disk.

Run it from the Unity project root (it reads the `.csproj` files Unity's IDE packages write):
```bash
dotnet Library/PackageCache/com.bogenbai.entitas-flux*/Tools~/entitas-graph.dll build
```
With a manual install the tool is at `Assets/Entitas/Entitas/Tools~/entitas-graph.dll`. Any .NET 6 or newer runs it. A ~1900-file production project (770 components, 750 systems, plus ~900 generated files in memory) builds in 2–3 s.

### What it records
Nodes are components (with their contexts, fields and `[Unique]`, `[Event]`, `[Watched]`, `[Cleanup]`, `[EntityIndex]`), systems, features, and other types of yours that touch components (queries, factories, services). Edges:

| Edge | Comes from |
| --- | --- |
| `creates` | `AddX`, `ReplaceX`, `isX = true`, an atomic `X = …`, `AddComponent(GameComponentsLookup.X, …)` — also through a factory the system calls |
| `reads` | `GameMatcher.X` in a group or a collector trigger (`AllOf`, `AnyOf`, `.Added()`, a plain matcher), `entity.x`, `hasX`, `isX`, `GetEntityWithX` |
| `filters` | `NoneOf` |
| `removes` | `RemoveX`, `SafeRemoveX`, `isX = false` |
| `destroys` | `Destroy()` on an entity of a group; it counts against the components of the group's `AllOf` |
| `reacts` | a read of a [`[Watched]`](#watched-attribute) component's `XChanged` marker |
| `contains` | a feature's `Add(…)`, in registration order |

Every edge is **EXTRACTED** (written in the system itself) or **INFERRED**. An inferred edge reached the system through code it calls: a query interface, a factory, a helper method up to four calls deep. Its `through` names that code. Destroys can be inferred from what an entity is made of, too: a generic `DestroyEventsSystem` that destroys every `Event` + `Ready` entity is credited with destroying each event created with the `Event` flag, once something marks those `Ready`. So is what a loop marks for such a destroyer: a system that sets `isProcessingStoring` and `isDestructed` on the same entity in one loop has the generic `Destructed` destroyer credited with destroying `ProcessingStoring`, and with what the loop's group requires. That holds within one loop only, never across groups or systems.

Features are walked from the roots, the features nobody registers. Every system gets its position in each root. The tool knows no names of yours. Roots, order, groups, conditions and reactions come from the code, and so do requests and events: a component is a request when every entity holding it is destroyed by the loop of a system reading it, directly or by marking it for a sweeper (`isDoomed = true`), and an event when it is created only on entities carrying such a consumed marker (`Make.Pulse().AddShipDocked(…)`, `CreateEntity.Event().AddX(…)`) and read on its own. An entity destroyed only sometimes, or from outside a loop over its group, is no message. A request put on a live entity is one too when a reader's loop takes it off every entity it visits and does something else there (writes another component, makes or destroys an entity), unless that loop is a cleanup system's or the component only mirrors another one (put on while the entity has it, taken off once it has not); a request consumed only under a condition is left to its name on purpose, since a hint that lives until a window opens looks the same, so keep the suffix. Names count too, and win where they match: `*Request` and `*Event` by default, `--request-suffix` and `--event-suffix` to change them. `graph.json` keeps both (`role` by name, `message` by structure). The report's findings about requests and events go by name only, because a request nobody handles is never consumed and only its name says what it is.

### Commands
These outputs come from the test fixture in `src/Entitas.Graph/tests/Fixture`.

`lifecycle` follows a component from creation to destruction, per root feature in execution order:
```text
$ entitas-graph lifecycle FurniturePlacedEvent
FurniturePlacedEvent  [Game, data, event]
  created by 1 · read by 1 · filtered by 0 · removed by 0 · destroyed by 1

GameplayFeature:
  #3    creates                PlaceFurnitureByRequestSystem  (FurnitureFeature)
  #10   reads                  UnlockAchievementSystem  (GameplayFeature)

LateFeature:
  #1    destroys               DestroyEventsSystem  (LateFeature)  — via created with Event + FurniturePlacedEvent; Ready by MarkEventsReadySystem
```

`who-creates` and `who-reads` give every site, with the system's place in the features. A `*` marks an inferred edge:
```text
$ entitas-graph who-creates Furniture
Furniture — created by 2 (1 direct, 1 inferred)
  PlaceFurnitureByRequestSystem  [Flag*]  FurnitureFeature #2, GameplayFeature order 3
      creates via Flag (FurnitureFactory.CreateFurniture)  Assets/Infrastructure.cs:32
  FurnitureFactory  [Flag]  [type]
      creates via Flag  Assets/Infrastructure.cs:32
```

`path` gives the shortest data-flow chain between two systems or components:
```text
$ entitas-graph path RequestPlacementSystem UnlockAchievementSystem
RequestPlacementSystem → UnlockAchievementSystem: 4 hops
  RequestPlacementSystem --creates--> PlaceFurnitureRequest  [EXTRACTED] via Add  Assets/Systems/FurnitureSystems.cs:17
  PlaceFurnitureRequest --read by--> PlaceFurnitureByRequestSystem  [EXTRACTED] via Get  Assets/Systems/FurnitureSystems.cs:38
  PlaceFurnitureByRequestSystem --creates--> FurniturePlacedEvent  [EXTRACTED] via Add  Assets/Systems/FurnitureSystems.cs:39
  FurniturePlacedEvent --read by--> UnlockAchievementSystem  [EXTRACTED] via AllOf  Assets/Systems/EventSystems.cs:15
```

`flow` answers "what happens after X" in one call. From a component it starts at the systems taking it, from a system at what that system writes. It follows hand-offs: a request or an event to the systems reading it, the change of a [`[Watched]`](#watched-attribute) component to the systems reacting to its `XChanged` marker, and a component no entity keeps (`[Cleanup]`, or taken off every entity) to its readers. A reacting system whose group leaves the component out (`NoneOf`) takes its removal only, so an add does not reach it, and the answer counts it instead. Nor does a change reach a reacting system whose group needs a component the changed entity never gets: furniture is never a `Cat`, so when `FurnitureFactory.CreateFurniture` clears `Moving` on the furniture it builds, the cat animation reacting to `MovingChanged` is left out, and the answer says `+1 reacting to MovingChanged need Cat, which this entity never gets`. The tool leaves a system out only when that is certain: the entity is made right there or by a factory, handed to a helper by the writing system itself, one of a group's entities or the one a primary index finds; and the component is missing from everything such an entity can be made with and is put on entities nowhere but where they are made. A component an existing entity can get later, such as an animator a view adds, keeps the system in, and so does any entity whose origin is not known. State a system writes, and hubs like `Id` or `Event`, are listed on an `also, not followed` line. Each system and component is expanded once and referred to after that. `--depth` counts system hops (default 4, at most 8).

Every step says when its system sees the data, counted from the system above it under one root feature: `same frame` when it runs after that system, `next frame` when it runs before it. A cleanup system runs after every execute system of its root, whatever its number. A reader whose group also requires `Ready`, which `MarkEventsReadySystem` adds to every event at the start of the next frame, waits for it; the tool takes that from the event's inferred destroy (`created with Event + X; Ready by MarkEventsReadySystem`). A step is `frame unknown` when its system runs under another root, when the start is created outside the systems (by a window, say), or when a system in between may remove the data first. It is `never sees it` when the generated cleanup clears a `[Watched]` marker before the reacting system runs; when that system reacts to other changes too, the note names them, since one of those may start it instead: `never sees it (RemoveCookingChangedGameSystem #308 clears CookingChanged first; other trigger: ProductStandAnimatorChanged)`. `[+N]` adds the frames up from the step marked `[+0]`. Each hand-off also says where it ends: who destroys or removes it, which cleanup clears the marker, or that nothing does. Under a system, hand-offs that lead further come before those that end with their takers. Takers that hand nothing on (a sound, a statistic) come last. Once an answer passes 95% of the budget, the `also, not followed` lines go first; if it is still too long, the takers that hand nothing on share one line per hand-off, so the main chain is cut last. A system that runs under several roots is followed under the first, and the others are noted only where a frame differs.

A create or remove that happens only under a condition says which, in the code's own words: `creates CoinsSpentEvent [event] (if till.Till < 0)`, and for the `else` branch `(if !(till.Till < 0))`. It names every condition around the line in its method, outermost first: an `if` or `else`, a `case`, `?:`, `&&`, `||`, `?.`, a `catch`, an `if (…) continue;` before it, and the argument that decides whether a lambda runs. That is `when:` in `.With(x => x.isExcludedFromEarningsRate = true, when: request.isExcludedFromEarningsRate)`: the helper's body runs the lambda only `if (when)`, so the argument passed for `when`, by name or by position, is the condition. Whatever the parameter is called: `.Apply(x => …, gate: c)` is conditional on `c` when `Apply` says `if (gate) apply(self);`, `.Unless(x => …, skip: c)` on `!c` when it returns early `if (skip)`, and a helper that runs the lambda either way adds no condition. A helper that stores the lambda or hands it on adds none either. Only a helper from a DLL, whose body the tool cannot read, goes by the parameter's name: `when`, `if`, `condition`, `onlyIf` or `predicate`. A private method called only under conditions passes them on to what it writes: `Execute` calls `PayForCart()` only if the cart has items and can be paid for, so `PayForCart`'s writes say `(if itemCount > 0 && _cartQuery.CanAfford())`, and so do those of the private methods it calls, up to three calls deep. One call to it without a condition, the method handed out as a delegate (`OnAdded += Pay`), or a method other code can call (anything not private, a Unity message such as `OnEnable`, an attributed method), and its writes count as unconditional. What a call passes decides the branches of the helper it calls: `CatFactory.CreateCat(snapshot)` adds `WornHatGuids` only in `case Worker`, so a spawner passing a snapshot built with `RoleTypeId.Customer` is not credited with it, and one built with `RoleTypeId.Worker` creates it without the `case`. A `case X when c` that matches leaves `c`. The value is followed from a constant (an enum member, a literal, a `const`, `nameof`) through parameters handed on to further helpers, locals, what a project method returns, and the fields and auto-properties an object is built with, as long as nothing assigns them afterwards. Where it is not certain (a parameter or local assigned anywhere in its method, a field written outside its constructors and object initializers, an earlier `case` that may take the value), the clause stays as written. Enum members are written without their type (`currencyTypeId == Coins`, `case Worker`), unless the clause has another name like it (`_kind == Kind.One && _shape == Shape.One`). Past 70 characters the tail is cut, innermost first: ` && …` says more conditions follow. A guard a method starts with (`if (!ready) return;`) does not count, because every line after it shares it. A line with several sites is conditional only when all of them are, and sites in an `if` and in its `else` keep only what they share. `explain` and `who-creates` show the same condition on their lines.

What a system writes by calling a factory or a helper goes on one line after the system's own writes: `by calling FurnitureFactory.CreateFurniture: creates 31 (ChangeHueRequest, Cooking, RescanPathfindingRequest, …)`. The system creates them, by making that call, and the line carries the call's condition when it has one. Requests and events made that way are followed under that line. A `[Watched]` change made that way is followed when the call replaces, removes or flags the component and some system reacts to it, also when that system never sees the change: `flow HireRequest` lists `removes Interacting` under `by calling CatFactory.ConvertToWorker`, with `CatsAnimationSystem … never sees it`, because the generated cleanup clears the marker first. An `AddX` there sets the initial value of what the factory builds, so it is only named.
```text
$ entitas-graph flow CommitCartSystem
flow CommitCartSystem  [system; ShopFeature #4, GameplayFeature order 15] — 3 systems, 2 hops; frames under GameplayFeature
  each frame is counted from the system above; [+N]: frames after the step marked [+0]
CommitCartSystem  #15 (ShopFeature) [+0]  Assets/Systems/ShopSystems.cs:55
  removes Unpaid  [Watched → UnpaidChanged]  ShopSystems.cs:64
    reacted to by SendPurchaseEventOnPaidSystem  #16 (ShopFeature)  same frame [+0]  ShopSystems.cs:75
      creates ItemPurchasedEvent  [event]  ShopSystems.cs:83
        read by CountPurchasesSystem  #17 (ShopFeature)  next frame (waits for Ready from MarkEventsReadySystem #1) [+1]  ShopSystems.cs:94
        ends: destroyed by DestroyEventsSystem*  LateFeature #1  (created with Event + ItemPurchasedEvent; Ready by MarkEventsReadySystem)  EventSystems.cs:54
      also, not followed: creates Receipted, Event*
    ends: UnpaidChanged cleared by RemoveUnpaidChangedGameSystem  not in any feature, generated
```
With conditions and a factory:
```text
$ entitas-graph flow OpenShopSystem
flow OpenShopSystem  [system; ShopFeature #10, GameplayFeature order 21] — 4 systems, 2 hops; frames under GameplayFeature
  each frame is counted from the system above; [+N]: frames after the step marked [+0]
OpenShopSystem  #21 (ShopFeature) [+0]  Assets/Systems/ShopSystems.cs:153
  creates Till  [transient]  ShopSystems.cs:166
    read+removed by SettleTillSystem  #22 (ShopFeature)  same frame [+0]  ShopSystems.cs:177
      creates CoinsSpentEvent  [event]  (if till.Till < 0)  ShopSystems.cs:184
        read by CountCoinsSystem  #24 (ShopFeature)  next frame (waits for Ready from MarkEventsReadySystem #1) [+1]  ShopSystems.cs:210
        ends: destroyed by DestroyEventsSystem*  LateFeature #1  (created with CoinsSpentEvent + Event; Ready by MarkEventsReadySystem)  EventSystems.cs:54
      creates CoinsEarnedEvent  [event]  (if !(till.Till < 0))  ShopSystems.cs:186
        read by CountCoinsSystem  #24 (ShopFeature)  next frame (waits for Ready) [+1]  ShopSystems.cs:211  (see above)
        ends: destroyed by DestroyEventsSystem*  LateFeature #1  (as above)  EventSystems.cs:54
      also, not followed: creates Event*; removes Till
  by calling ShopkeeperFactory.Create: creates 3 (ShopOpenedEvent, Event, Shopkeeper)  (if _keepers.count == 0)  Infrastructure.cs:65
    creates ShopOpenedEvent  [event]  Infrastructure.cs:66
      read by GreetCustomersSystem  #23 (ShopFeature)  next frame (waits for Ready) [+1]  ShopSystems.cs:197
      ends: destroyed by DestroyEventsSystem*  LateFeature #1  (as above)  EventSystems.cs:54
```
On a 2000-file project, `flow CommitCartRequest` takes ~1 850 tokens, without its `also` lines. It shows the payment reaching the wallet the next frame, because `ChangeCurrencyByRequestSystem` runs at #187 and the cart is committed at #200. The purchase events follow in the same frame, through the systems reacting to `Unpaid`. The coins-spent statistic is updated two frames after the commit, if coins were spent: `CoinsSpentEvent` is created only when the amount is negative, `CoinsEarnedEvent` only when it is positive. `flow PlaceFurnitureRequest` takes ~1 800 tokens; the ~50 components the handler sets through `FurnitureFactory.CreateFurniture` are one line, under which the factory's requests and `[Watched]` changes are followed.

`systems-of` lists what a feature runs, in order, with nested features expanded:
```text
$ entitas-graph systems-of GameplayFeature
GameplayFeature (root) — 24 systems
    1. MarkEventsReadySystem  [Execute]
    2. FurnitureFeature (feature)
         1. RequestPlacementSystem  [Execute]
         2. PlaceFurnitureByRequestSystem  [Execute]
         3. CountFurnitureSystem  [Execute]
    …
```

`query` answers a question in words with the connected piece of the graph around it, like Graphify's `query`. The nodes whose names match the words best are the seeds (`--limit`, default 5). Names are matched word by word, by stem, so `placement` finds `PlaceFurnitureByRequestSystem` and `FurniturePlacedEvent`, `cat` does not find `Indicator`, and words like `how`, `does` and `on` are skipped. A word in the name counts for more than one in the namespace, which every system of a feature shares. Among names that match equally, a system whose own code names the words its name lacks comes first: it creates or removes a component named with them, or, a weaker sign, its groups collect one. So `query order paid` seeds `PayForOrdersByRequestSystem`, whose name says only "orders" but which creates `OrderIsPaidEvent` and sets `Paid`, and the head says why: `PayForOrdersByRequestSystem (creates OrderIsPaidEvent, Paid)`. Only the system's own lines count, and only components whose own names have the words: not what a factory or a query does for it, not a factory itself (it sets every component of what it builds), not a hub such as `Cat`, and not what it destroys, so a generic `DestroyEventsSystem` gains nothing from the events it is credited with. Then the name with the fewest other words wins (`PlaceFurnitureByRequestSystem` before `EndFurniturePlacementOnBuildModeStopSystem`), then the one with more edges written in its own code. A reader of an event already among the seeds gets no seed of its own unless its name matches a word the event does not; it is one step out from the event anyway. So on a 2000-file project, `query hire worker` seeds `WorkerHiredEvent` together with `HireRequest` and `HireByRequestSystem`, where hiring starts, and not the three systems named after the event, such as `SendWorkerHiredAnalyticsSystem`. From the seeds it walks data flow two steps out (`--depth`, 1 to 4): a component leads to whoever creates, reads or destroys it, a system to the components it touches. Each node lists its links to the nodes after it, so every edge appears once, on the side nearer the seeds. Feature registration is not walked; it shows as the system's place. A hub is a node with more than 40 neighbours over edges written in the code: an `Id`, a position, a factory. It is reached but not walked through, and marked as such. Inferred edges do not count toward a hub, because a system whose factory call adds 50 components is often the very system the question is about. Link lines name the file; a system's header carries its full path.
```text
$ entitas-graph query placed event --limit 1
query "placed event": 12 nodes match; seeds: FurniturePlacedEvent (also: PlaceFurnitureByRequestSystem, RequestPlacementSystem, Event, DestroyEventsSystem, PlaceFurnitureRequest, MarkEventsReadySystem, … 5 more)

Seeds:
  FurniturePlacedEvent  [component; Game, data, event]
    created by PlaceFurnitureByRequestSystem  via Add  FurnitureSystems.cs:39
    read by UnlockAchievementSystem  via AllOf  EventSystems.cs:15
    destroyed by DestroyEventsSystem  via Destroy* (created with Event + FurniturePlacedEvent; Ready by MarkEventsReadySystem)  EventSystems.cs:54

1 step out:
  PlaceFurnitureByRequestSystem  [system; FurnitureFeature #2, GameplayFeature order 3]  Assets/Systems/FurnitureSystems.cs:22
    creates Event  via Flag* (CreateEntity.Event)  Infrastructure.cs:21
    creates Health  via Add* (FurnitureFactory.CreateFurniture)  Infrastructure.cs:33
    …
    reads PlaceFurnitureRequest  via Matcher, Get  FurnitureSystems.cs:31
  DestroyEventsSystem  [system; LateFeature #1, LateFeature order 1]  Assets/Systems/EventSystems.cs:41
    reads Event  via AllOf  EventSystems.cs:48
    …
  UnlockAchievementSystem  [system; GameplayFeature #3, GameplayFeature order 10]  Assets/Systems/EventSystems.cs:6
    reads Ready  via AllOf  EventSystems.cs:16

2 steps out:
  Event  [component; Game, flag]
  …
```
When the whole subgraph does not fit the budget, the walk follows only each node's strongest links: to a seed, written in the system itself rather than inferred through a factory, to a request or an event, to what the words name. So the part that fits is still one connected piece, and a `… N more links` line counts the rest. On a 2000-file project, `query furniture placement` seeds the system that places furniture with its request and its event, then the systems that cancel and place a piece, followed by the readers of that event and the systems that create the request, in ~2000 tokens. Words that match nothing exit with 4 and the labels closest to them.

| Command | Answers |
| --- | --- |
| `build` | Builds the graph and writes `graph.json`, `GRAPH_REPORT.md` and `graph.html`. |
| `who-creates <Component>` / `who-reads <Component>` | Every site that creates it, or reads, reacts to or filters on it. |
| `lifecycle <Component>` | Create → read → remove/destroy, per root feature in execution order. |
| `systems-of <Feature>` | The systems a feature runs, in order. |
| `explain <Name>` | One node and every connection it has; for a system, also who else reads the requests it handles. |
| `path <A> <B>` | The shortest data-flow chain from A to B. |
| `flow <Component\|System>` | What happens after it: the systems its requests, events and `[Watched]` changes reach, as a tree, each step labelled same frame, next frame or frame unknown. |
| `query <words…>` | The connected subgraph around the nodes the words match best: seeds first, then what is one and two data-flow steps away. |
| `report` | Prints `GRAPH_REPORT.md`. |
| `diff <old.json> [<new.json>]`, `diff --ref <git-ref>` | What changed in the data flow between two versions: the edges each component gained and lost, systems added, removed and moved, findings. See [below](#comparing-two-versions--diff). |
| `install` | Tells the project's AI assistant to use these commands: a short block in `CLAUDE.md` (and `AGENTS.md`), see [For AI coding assistants](#for-ai-coding-assistants). |
| `uninstall` | Takes that block out again. |

Names may drop the `Component` suffix. The `XChanged` marker of a [`[Watched]`](#watched-attribute) component is no node of its own; its name stands for the component. `who-reads XChanged` lists the systems reacting to X's changes, `who-creates XChanged` whoever adds or removes X, `lifecycle XChanged` both in execution order, and `explain`/`path` take it as X. Wherever a system creates or removes a `[Watched]` component, `explain` and `query` name the systems reacting to it on that line (`removes Unpaid … → reacted to by …`), and `who-creates`, `who-reads` and `lifecycle` of the component list them at the top. A name that matches several nodes is never guessed: the tool lists them and exits with 3; an unknown name exits with 4 and suggestions. The queries reuse `graph.json` and rebuild it first when a `.cs` file is newer.

**Every answer is capped at about 2000 tokens** (`--budget`; `0` turns the cap off). Most answers are far below it: on a 2000-file project, `who-reads` of an event is ~400 tokens. But a component most of the code touches (an `Id`, a position) has hundreds of readers, and uncapped `who-reads` of one was ~23 000 tokens. An answer over the budget starts with a summary of its shape (by feature, by root, or by relation), lists the entries that fit, and says what it left out. Here the same answer is cut on purpose, with a tiny budget:
```text
$ entitas-graph who-reads Selected --budget 40
Selected — read by 4 (4 direct, 0 inferred)
  reacted to by (through SelectedChanged, on every add, replace or remove): HighlightOnSelectedSystem
  by feature: FurnitureFeature 3, (unregistered) 1
  SelectSystem  [NoneOf]  FurnitureFeature #4, GameplayFeature order 5
      filters via NoneOf  Assets/Systems/FurnitureSystems.cs:63

… 3 more systems and types not shown — the answer is capped at ~40 tokens (--budget 0 prints everything).
```

| Option | Effect |
| --- | --- |
| `-r`, `--root <dir>` | Unity project root (default: current directory). |
| `-p`, `--project <csproj>` | The project declaring the contexts (default: found under the root). |
| `-o`, `--out <dir>` | Output folder (default: `entitas-graph-out` in the root — `install` adds it to `.gitignore`). |
| `--request-suffix <s>`, `--event-suffix <s>` | How requests and events are recognised by name (default: `Request`, `Event`). Where no name matches, they are recognised by structure. |
| `--budget <tokens>` | Cap each answer at about this many tokens (default: 2000; `0` = no cap). |
| `--limit <n>` | Seed nodes for `query` (default: 5). |
| `--depth <n>` | Steps `query` walks from the seeds (1 to 4, default: 2); system hops `flow` follows (1 to 8, default: 4). |
| `--no-html` | Skip `graph.html`. |
| `--ref <git-ref>` | For `diff`: build a side from this branch or commit (through `git archive`; the checkout is not touched). Once: the old side, against the working tree. Twice: old, then new. |
| `--commit <commit>` | For `diff`: the commit's first parent against the commit. |

### Comparing two versions — diff
A code review shows which files changed. It does not show that three systems stopped reading one event and now read another. `diff` shows that: the edges each component gained and lost, the systems added, removed or moved, and the report findings that appeared or went away.
```bash
entitas-graph diff --ref main              # the working tree against a branch or commit
entitas-graph diff --ref main --ref topic  # one branch or commit against another
entitas-graph diff --commit 49904c1d       # what one commit changed: its first parent against it
entitas-graph diff old.json [new.json]     # two saved graphs; new defaults to the current project
```

`--ref` builds a graph from that commit's sources. They are taken with `git archive` into a temporary folder, which is deleted afterwards, on Ctrl+C too. Your checkout, index and stashes stay as they are, and nothing is written into the project. The `.csproj` lists today's files, so the tool maps it to the commit. It drops the files the commit does not have yet. It adds the files the commit had and that are gone now, when they sat in or below a folder of the same assembly (no `.asmdef` and no `Editor` folder in between). With two refs it maps the `.csproj` to each commit separately. References and defines come from today's `.csproj` and `Library/`, for both sides. On a 2000-file project a `diff --ref` takes about 4 s, or 6 s when the current graph has to be rebuilt as well. Two refs are built in parallel, in about 5.5 s.

`--commit X` is short for `--ref X^ --ref X`. A single `--ref X^` compares with the working tree, so it shows what X changed only until a later commit touches the same code; `--commit` shows it at any time. A merge commit is compared with its first parent, and the answer says so. A root commit has no parent and is an error.

Here is what one commit in a production project changed — it made three statistics systems count purchases instead of placements:
```text
$ entitas-graph diff --commit 49904c1d
old: 49904c1d^ (commit 3afbaa9cc5)
new: 49904c1d (commit 49904c1de3)
nodes: none added or removed
edges: reads +3 −8
6 components changed · 0 registrations moved · findings unchanged

Components (6):
  FurnitureAddedToCafeEvent  −3
    − read by UpdateFurniturePlacedStatSystem (AllOf)
    − read by UpdatePlantsPlacedStatSystem (AllOf, Get)
    − read by UpdateWallDecorationsPlacedStatSystem (AllOf, Get)
  ShopPurchaseEvent  +3
    + read by UpdateFurniturePlacedStatSystem (AllOf, Get)
    + read by UpdatePlantsPlacedStatSystem (AllOf, Get)
    + read by UpdateWallDecorationsPlacedStatSystem (AllOf, Get)
  Id  −2
    − read by UpdatePlantsPlacedStatSystem (EntityIndex)
    − read by UpdateWallDecorationsPlacedStatSystem (EntityIndex)
  …
```

An edge is its source, target and relation. A moved line, a read moved into a helper or a different `via` is not a change. Components with the most changes come first. A `*` marks an inferred edge, followed by what it went through. Systems, features and other types are listed as `+ X  FurnitureFeature #5`, `− Y  (was FurnitureFeature #7)` and `~ Z: FurnitureFeature #3 → #4`. A system counts as moved only when its features changed or its order among its siblings did. Inserting one system renumbers every system after it, so the merely renumbered are counted on one line, not listed. The diff goes through the same token budget as every other answer. A cut diff names everything that changed right after the counts.

### What it writes
- **`graph.json`**: node-link JSON in the shape Graphify uses (`nodes`, `links` with `source`, `target`, `relation`, `confidence`, `source_file`, `source_location`), plus Entitas fields: `via` (`Add`, `AllOf`, `Flag`…), `context`, `through`, `order` for a feature's systems, `placements` for a system's position under each root. `transient_removed_by` on a component names the systems that take it off every entity holding it, and `only_while` on a flag's `creates` link names the transient component the setter's group requires. `role` is a component's role by name; `message` is its role by structure, with `consumed_by` (the systems whose loops destroy or mark every entity holding a request, or take it off every one, when `consumed_by_removal` is true) or `carried_by` (the marker an event rides on). `unseen_by` on a system's `creates` or `removes` link of a `[Watched]` component lists the systems reacting to it that can never see that write, each with a component its group `needs` that the written entity never gets; it is there only where that is certain. `graph` holds the schema version, the commit it was built at and the conventions used.
- **`GRAPH_REPORT.md`**: counts, root features, how many requests and events the names and the structure each find (and which requests only their name does), the most connected components, then the findings:
  - components nobody reads;
  - components read but never created;
  - requests without a handler;
  - requests nobody destroys or removes;
  - events without a consumer;
  - events destroyed by hand;
  - `[Watched]` components nobody reacts to;
  - state flags set on existing entities and never cleared (a flag set in the loop that marks its entity for a generic destroyer is destroyed with it, so it is not listed);
  - flags set once on purpose, probably: the system setting them leaves them out of its own group with `NoneOf`, or loops over a group that requires a transient component, so each entity is marked once, the usual "done" marker. A component is transient when it has `[Cleanup]`, or when a system takes it off every entity that holds it: `isJustCreated = false` (or `RemoveX()`, or `RemoveComponent` with its index) on every entity of a group of `JustCreated` alone, with no condition or `continue` before it. A `NoneOf` in that group is allowed only for what the same loop sets. So `FloorRestored`, set in a loop over `AllOf(Floor, …, JustCreated)`, is listed here, and the line names the system that removes `JustCreated`. A setter that reacts to a `[Watched]` change stays in the list above, because the change comes again and the flag still holds the first outcome;
  - systems, or generated features such as `GameEventSystems`, that no feature registers;
  - `Destroy()` calls the tool could not trace to a group.

  These are leads, not errors. A component read but never created may be added from data, from a prefab's component list for example, which the tool cannot see.
- **`graph.html`**: the graph in a browser, offline, in GitHub's light or dark colours (following the system, or `T`). Search with `/` — every word must match, results ranked like a command palette, `↑` `↓` `Enter` to pick. The view frames the node with its neighbours one or two steps out (`1` `2`, or `3` for everything), labels included; hovering a node, or its row in the side panel, fades everything it is not connected to. Arrows follow the data: from a system to what it creates, removes or destroys, from a component into the systems that read, filter on or react to it, from a feature to the systems it runs; dashed ones are inferred. Filters for kinds and relations double as the legend. The side panel lists who creates, reads and destroys a node, each write with its condition, the feature and order a system runs at, and the CLI commands to ask about it, ready to copy. `Alt`+`←` / `→` go back and forward, the address (`#node=…`) links to a node, `O` gives the project at a glance and `?` the shortcuts. It inlines [vis-network](https://visjs.github.io/vis-network/) 9.1.9 (Apache-2.0 or MIT; its licence ships next to the tool) and loads nothing from the network.

Out of scope: dependency injection, scenes, prefabs and other assets. The graph covers code in the assembly that declares the contexts.

### For AI coding assistants
An assistant answering "who uses this component?" with grep reads every file that mentions it. It still misses what goes through factories and query classes, and it never learns the order systems run in. The commands answer the same question for a fraction of the tokens. Measured on a 2000-file production project:

| What the assistant reads | Tokens |
| --- | --- |
| `who-reads` or `lifecycle` of an event | ~350–400 |
| `path` between two systems | ~110 |
| `query` about a topic (`cart commit`): the connected piece of the graph | ~1 950 |
| `flow` of a request (`CommitCartRequest`): every system after it, with its frame | ~1 850 |
| `diff --ref` across one commit | ~250 |
| The same question by grep, then reading the 8 files that mention the event | ~5 900 |
| `GRAPH_REPORT.md`, once, for an overview | ~3 900 |
| Any answer, at most | ~2 000 (the budget) |
| `graph.json` | ~1 400 000 — never let an assistant read it; it is for programs |

`install` tells the project's assistant so. Run it once from the Unity project root (or pass `-r`):
```bash
dotnet Library/PackageCache/com.bogenbai.entitas-flux*/Tools~/entitas-graph.dll install
```
It writes a short block into `CLAUDE.md` in the repository root: the nearest folder above the Unity project that holds `.git`, because that is where assistants run, or the Unity root when there is none. When the project has an `AGENTS.md` there, the block goes into it too; a `CLAUDE.md` that pulls `AGENTS.md` in with `@AGENTS.md` is left alone, so the block is not loaded twice. `--target <file>` (repeatable) writes to other files instead, `.github/copilot-instructions.md` for example.

The block names the tool that ran `install`, by a path relative to the file's folder, with `-r` pointing at the Unity root when that is a different folder. A UPM install is named by glob, because the package folder's name changes with every update. The block sits between `<!-- entitas-graph:begin -->` and `<!-- entitas-graph:end -->`: running `install` again replaces it and leaves the rest of the file as it was, line endings included. Run it again after switching between a manual and a UPM install, and after updating the package: the block lists the commands, and `build` says when the block was written by another version. `install` also adds `entitas-graph-out/` to the repository's `.gitignore` unless a line there already covers it (`--no-gitignore` skips that). `entitas-graph uninstall` takes the block out again and deletes a file that held nothing else.

With the Unity project in `src/cat-caffeine` and a manual install, the block reads:
```md
<!-- entitas-graph:begin -->
## ECS data flow
This is an Entitas project. To find who creates, reads, filters, removes or destroys a
component, what a feature runs and in what order, or how data gets from one system to
another, run entitas-graph from this file's folder instead of searching the code:

    dotnet src/cat-caffeine/Assets/Entitas/Entitas/Tools~/entitas-graph.dll who-reads <Component> -r src/cat-caffeine

Commands: who-creates, who-reads, lifecycle <Component>; systems-of <Feature>; path <A> <B>;
explain <Name>; query "<words>" (the connected subgraph around the matching nodes);
flow <Request|System> (what happens after it: the systems it reaches, each in which frame);
diff --commit <sha> (what one commit changed); diff --ref <branch> (changes since a branch, for reviewing it);
report (= `src/cat-caffeine/entitas-graph-out/GRAPH_REPORT.md`, an overview worth reading once).
Each answer is capped at ~2000 tokens. Never read graph.json: ~1.4 M tokens on a 2000-file project, it is for programs.
<!-- entitas-graph:end -->
```

## License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
