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

Automatic generation runs in the background — the editor never waits for it. It notices a component saved from your IDE immediately, typically before you switch back to Unity, so the refresh that follows compiles the component and its generated code **together, once**. On a ~2000-file project a run takes about 0.7 s; files that declare no component never trigger one.

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

## License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
