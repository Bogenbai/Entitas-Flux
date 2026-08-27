# Entitas Flux
**Entitas Flux** is a fork of the great and terrible [Entitas Framework](https://github.com/sschmid/Entitas).  
I created it to add features missing from the original Entitas that I believe should be there, and to support newer Unity versions.  
Don’t expect major changes or a big redesign like [Entitas Redux](https://github.com/jeffcampbellmakesgames/Entitas-Redux). Updates will (or will not) come slowly and only when I need a feature.

![CI](https://github.com/Bogenbai/Entitas-Flux/actions/workflows/ci.yml/badge.svg)
![Release](https://github.com/Bogenbai/Entitas-Flux/actions/workflows/release-on-tag.yml/badge.svg)

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
Generated code isn't on disk anymore, so renaming a component used to mean fixing every `entity.isCanAttack` and `GameMatcher.CanAttack` by hand. Mark it instead, and let the IDE do all of it:
```cs
[RenameTo("AbleToAttack")]
[Game] public class CanAttack : IComponent { }
```
`Alt+Enter` on the attribute → *"Rename component 'CanAttack' to 'AbleToAttack' and update all usages"*. See [Renaming a component](#renaming-a-component--renameto).

### Defines
`ENTITAS_DISABLE_REACTIVITY` - partially disables Entitas' default reactivity. Gives small performance boost.
`ENTITAS_HIDE_STANDARD_MEMBERS` - hides standard generated component members that start with a lowercase letter in atomic components.

### More features coming soon (or not)

## How to use
> **Requires Unity 2022.3+** (Roslyn 4.x, needed for the incremental source generator). Verified against 2022.3 LTS and Unity 6.

### If you start fresh
Just create a new repo using [THIS](https://github.com/Bogenbai/Entitas-Flux-Template) as a template.

### If you already have Entitas in the project

**Package Manager (recommended).** *Window → Package Manager → + → Install package from git URL*:

```
https://github.com/Bogenbai/Entitas-Flux.git#upm
```

or pin a version: `https://github.com/Bogenbai/Entitas-Flux.git#upm/v0.1.0`. Everything arrives configured — the analyzer DLLs already carry the `RoslynAnalyzer` label, so there is nothing to set up by hand.

Then:
1. **Delete Jenny and the old Entitas.** Remove the old `Jenny/` folder, the `*.CodeGeneration.Plugins.dll`s, any `JennyRoslyn.properties`, any committed `Generated/` folders, and the Entitas DLLs you previously copied into `Assets/` — none of them are used anymore, and a leftover copy will clash with the package.
2. Add an `EntitasGeneration.cs` declaring your contexts and options — see [Configuring generation](#configuring-generation--entitasgenerationcs).

<details>
<summary><b>Manual install</b> (no Package Manager)</summary>

Download the `Entitas-Flux-vX.X.X` archive from [Releases](https://github.com/Bogenbai/Entitas-Flux/releases) and copy the DLLs, which are organized by destination folder:

```
// Assets/Entitas/Entitas:
Entitas.dll
Entitas.CodeGeneration.Attributes.dll
Entitas.Unity.dll
Entitas.VisualDebugging.Unity.dll
// Assets/Entitas/Entitas/Editor:
Entitas.Migration.dll
Entitas.Migration.Unity.Editor.dll
Entitas.Unity.Editor.dll
Entitas.VisualDebugging.Unity.Editor.dll
// Assets/Entitas/Entitas/Analyzers:
Entitas.SourceGenerator.dll      ← the Roslyn source generator
Entitas.CodeFixes.dll            ← IDE quick fixes, e.g. [RenameTo]
```

**The analyzer DLLs are special.** Select **both** `Entitas.SourceGenerator.dll` and `Entitas.CodeFixes.dll` in Unity, and in the Inspector: add the asset label **`RoslynAnalyzer`**, and uncheck **all** platforms under "Select platforms for plugin" (Any Platform off, Editor off). Apply. This is what makes Unity feed them to the compiler instead of trying to load them as managed plugins.

Then do steps 1–2 above.
</details>

## Code generation
Entitas-Flux generates the whole ECS API — contexts, entities, matchers, component accessors, events, and the cleanup/watched systems — at **compile time** with a Roslyn [incremental source generator](https://learn.microsoft.com/en-us/dotnet/csharp/roslyn-sdk/source-generators-overview). It replaced the old Jenny pipeline, so:

- **No separate generation step.** The generator runs inside the C# compiler on every build.
- **No committed `Generated/` folders.** The generated code lives in the compiler's output, not on disk.
- **Components are discovered from the compilation** — any class implementing `IComponent`, plus its context/`[Watched]`/`[Unique]`/etc. attributes. No external config files.
- **IDE-native.** Rider/Visual Studio see the generated API immediately; Cmd/Ctrl-click navigates straight into it.
- **Incremental.** Generation is keyed on the components, their attributes and your generation config — nothing else. Editing a method body or a comment re-runs nothing.
- **Loud when it breaks.** A generator that fails reports `ENT0100` naming itself and the reason, instead of silently leaving a hole in the API for you to find via a hundred errors in your own code.

> Want the generated files on disk anyway (e.g. to read them, diff them, or step through with a breakpoint)? The repo ships an `entitas-gen` CLI that writes the same output to real `.cs` files from your `Assembly-CSharp.csproj`. It's optional — the analyzer is the default path.

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

### Disabling built-in generators — `[DisableEntitasGenerator("X")]`
Repeatable. Removes any built-in generator whose class short-name matches `"X"` case-insensitively **or starts with** it (prefix match):
```cs
[assembly: DisableEntitasGenerator("Event")]            // all Event* generators
[assembly: DisableEntitasGenerator("ContextObserver")]  // just ContextObserverGenerator
```
> `[Event]` listener components are synthesized during discovery, so only disable `Event` generators in assemblies that don't use `[Event]`.

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

## Renaming a component — `[RenameTo]`
One component name fans out into a lot of generated API: `GameMatcher.CanAttack`, `entity.isCanAttack`, `AddCanAttack`, `ReplaceCanAttack`, `SafeRemoveCanAttack`, `CanAttackChanged`, `ICanAttackListener`… With Jenny you could rename those in the generated files and let the IDE propagate it. Now that generation happens inside the compiler, those symbols live in read-only documents — the IDE renames the class and leaves every usage broken.

So mark the component instead:
```cs
using Entitas.CodeGeneration.Attributes;

[RenameTo("AbleToAttack")]
[Game] public class CanAttack : IComponent { }
```
Then `Alt+Enter` on the attribute → **"Rename component 'CanAttack' to 'AbleToAttack' and update all usages"**. The component, every identifier derived from it, all their usages, and the attribute itself are dealt with in one step:
```cs
// before                                    // after
attacker.isCanAttack = true;                 attacker.isAbleToAttack = true;
GameMatcher.CanAttack                        GameMatcher.AbleToAttack
entity.hasCanAttackChanged                   entity.hasAbleToAttackChanged
```

A few things worth knowing:

- **The attribute is inert.** Generation ignores it completely, so the project keeps compiling between marking a component and applying the rename. Mark five components today, rename them tomorrow.
- **Other assemblies are covered.** Usages in your test asmdef or an Editor assembly are rewritten too — anything that references the assembly declaring the component.
- **Same-named members are safe.** A `monster.Health` on some unrelated class of yours is left alone: every usage is resolved through the compiler, not matched by text.
- **One component at a time.** There is deliberately no *Fix All*: each rename is planned against the current state of the code, so applying several at once would use stale positions.
- **After updating the DLLs, restart the IDE.** It keeps analyzer assemblies loaded in-process; until it restarts you may get the old ones (the quick fix silently disappears from `Alt+Enter`).

### From the terminal
The same rename runs headless — for CI, for bulk work, or when the IDE isn't cooperating. It reads your `Assembly-CSharp.csproj`, so run it from the Unity project root:
```bash
# dry run: prints the identifier map, every usage, and the file move
dotnet run --project src/Entitas-Flux/src/Entitas.Rename/src -- CanAttack AbleToAttack -r <unity-project>

# the same with -a applies it
```
| Option | Effect |
| --- | --- |
| `-a`, `--apply` | Write the changes (without it, nothing is written). |
| `-r`, `--root <dir>` | Where to look for the csproj. Defaults to the current directory. |
| `-p`, `--project <csproj>` | Use this csproj instead of searching for one. |
| `-s`, `--include-strings` | Also rewrite occurrences in string literals and comments. |
| `-k`, `--keep-file-name` | Don't rename the component's `.cs` file. |

Unlike the quick fix, the CLI also renames the component's file (`CanAttack.cs` → `AbleToAttack.cs`) together with its `.cs.meta`, via `git mv` when the file is tracked, so the Unity asset GUID survives.

> The CLI sees your project exactly as `Assembly-CSharp.csproj` describes it. If Unity hasn't regenerated it lately, files missing from it are invisible to the rename — it will tell you when the csproj looks stale, and *Edit → Preferences → External Tools → Regenerate project files* fixes it.

## License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
