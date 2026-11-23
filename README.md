# Entitas Flux
**Entitas Flux** is a fork of the great and terrible [Entitas Framework](https://github.com/sschmid/Entitas).  
I created it to add features missing from the original Entitas that I believe should be there, and to support newer Unity versions.  
Don’t expect major changes or a big redesign like [Entitas Redux](https://github.com/jeffcampbellmakesgames/Entitas-Redux). Updates will (or will not) come slowly and only when I need a feature.

![CI](https://github.com/Bogenbai/Entitas-Flux/actions/workflows/ci.yml/badge.svg)
![Release](https://github.com/Bogenbai/Entitas-Flux/actions/workflows/release-on-tag.yml/badge.svg)

**Entitas without the code-generation chore.** The ECS API is generated inside the C# compiler as you type: no Jenny, no generation step, no `Generated/` folders to commit. On top of that come the features below. Unity 2022.3 LTS or newer, Unity 6 included.

📖 **Guides:** [Wiki](https://github.com/Bogenbai/Entitas-Flux/wiki) · **What's new:** [CHANGELOG](CHANGELOG.md)

## Features

### Code generation inside the compiler
Add a component, and `entity.AddHealth(…)`, `GameMatcher.Health` and the rest exist on the next keystroke. Your IDE sees them at once and Ctrl-click goes straight into the generated code. Only a change to components or their attributes re-runs generation. When something is wrong, for example a component in the wrong assembly or a missing context, you get one clear diagnostic instead of a hundred errors in your own code.
```cs
[assembly: ContextDefinition("Game")]   // EntitasGeneration.cs: the only setup there is
[Game] public class Health : IComponent { public int Value; }
```
→ [Code generation](https://github.com/Bogenbai/Entitas-Flux/wiki/Code-Generation)

### Atomic components
Components with a single field get a single property, so the value is one step away:
```cs
[Game] public class CurrentHealth : IComponent { public float Value; }
// access:
entity.CurrentHealth
// instead of:
entity.currentHealth.Value
```
Opt in per assembly with `[assembly: EntitasGeneration(EntityApi = EntityApiStyle.Atomic)]`.

### Watched attribute
Deferred reactivity without a reactive system per component. Change a `[Watched]` component and its entity gets an `XChanged` marker for one frame, which the generated `GameWatchedCleanupSystems` then removes:
```cs
[Game, Watched] public class Wallet : IComponent { public Dictionary<CurrencyTypeId, int> Value; }
// entity.ReplaceWallet(newValue);
// will cause entity.isWalletChanged to be `true`
```
Put `GameWatchedCleanupSystems` in your systems order, usually right before `GameCleanupSystems`.

### Friendly Component Dropdown
In the original Entitas, adding components in the inspector is a pain because there’s no search bar. **Entitas Flux** has one.
<details>
  <summary>📸 Show large screenshot</summary>

  <div align="center">
    <img src="https://github.com/user-attachments/assets/bfa51c31-c62c-4291-98c3-de965bb38552" alt="The component dropdown with a search bar" width="900">
  </div>
</details>

### Safe component removal
Remove a component only if it is there, without `if`s or matcher checks:
```cs
entity.SafeRemoveBoxCollider2D();
// instead of:
if (entity.hasBoxCollider2D) entity.RemoveBoxCollider2D();
```

### Renaming a component, not just its class
One component name fans out into `entity.isCanAttack`, `GameMatcher.CanAttack`, `AddCanAttack`… so rename the component, and every derived identifier and usage follows:
```cs
[RenameTo("AbleToAttack")]
[Game] public class CanAttack : IComponent { }
```
Press `Alt+Enter` on the attribute in Rider or Visual Studio, or use *Tools → Entitas → Rename Component…* in Unity. With generated files on disk, a plain Shift+F6 on the class works too: the next regeneration recognises the rename and offers to update the usages.
→ [Renaming components](https://github.com/Bogenbai/Entitas-Flux/wiki/Renaming-Components)

### Generated code as files, when you want them
One line turns the same generator into ordinary `.cs` files: for external tools, diffs or breakpoints. Unity regenerates them on save, before it compiles, so one save costs one compile.
```cs
[assembly: EntitasGeneration(Output = GenerationOutput.Files)]
```
→ [Generated code as files](https://github.com/Bogenbai/Entitas-Flux/wiki/Generated-Code-as-Files)

### Catching who changed a component
Turn on debug hooks and put one breakpoint in a handler. Every `Add`/`Replace` of the component you care about stops there, and the call stack shows which system did it.
```cs
[assembly: EntitasGeneration(DebugHooks = true)]

EntitasDebugHooks.OnReplace = (entity, index, value) =>
{
    if (index == GameComponentsLookup.Health) { /* breakpoint here */ }
};
```
→ [Debug hooks](https://github.com/Bogenbai/Entitas-Flux/wiki/Debug-Hooks)

### Seeing how data flows: entitas-graph
In ECS, systems never call each other. One adds a request, another picks it up and fires an event, a third reacts, and to a call graph or a text search they are unrelated classes. `entitas-graph` reads your project the way the compiler does. It tells you who creates, reads, filters, removes and destroys each component, which feature runs each system and in what order, and what happens after a request, frame by frame.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/wiki/Bogenbai/Entitas-Flux/images/entitas-graph-dark.png">
  <img src="https://raw.githubusercontent.com/wiki/Bogenbai/Entitas-Flux/images/entitas-graph-light.png" alt="graph.html: a system's neighbours, with the conditions of its writes in the side panel" width="900">
</picture>

```bash
./entitas-graph who-reads FurniturePlacedEvent   # every reader, with its feature and position
./entitas-graph flow PlaceFurnitureRequest      # what happens after it, and in which frame
./entitas-graph diff --ref master               # what this branch changed in the data flow
```
- **An offline `graph.html`**: search, filter, and click through the systems around any component.
- **Built for AI assistants.** One `install` tells Claude, Codex and the like to ask the graph instead of grepping. On a ~1900-file production game they answered data-flow questions with about a third fewer tokens, and the graph pointed at an ordering bug that text search had missed.
- **Fast:** about 3 seconds for ~750 systems, rebuilt by itself when the code changes.

→ [ECS graph](https://github.com/Bogenbai/Entitas-Flux/wiki/ECS-Graph) · [Commands](https://github.com/Bogenbai/Entitas-Flux/wiki/ECS-Graph-Commands) · [For AI assistants](https://github.com/Bogenbai/Entitas-Flux/wiki/ECS-Graph-for-AI-Assistants)

### Defines
`ENTITAS_DISABLE_REACTIVITY` partially disables Entitas' default reactivity, for a small performance boost.  
`ENTITAS_HIDE_STANDARD_MEMBERS` hides the standard lowercase members of atomic components.

## Installation
*Window → Package Manager → + → Install package from git URL*:
```
https://github.com/Bogenbai/Entitas-Flux.git#upm
```
Then add an `EntitasGeneration.cs` declaring your contexts:
```cs
using Entitas.CodeGeneration.Attributes;

[assembly: ContextDefinition("Game")]    // the first one is the default context
[assembly: ContextDefinition("Input")]
```
Pinning a version, installing by hand, and moving over from Entitas or Jenny: [Installation](https://github.com/Bogenbai/Entitas-Flux/wiki/Installation).

## Documentation
All the details are in the [Wiki](https://github.com/Bogenbai/Entitas-Flux/wiki):
[Installation](https://github.com/Bogenbai/Entitas-Flux/wiki/Installation) ·
[Code generation](https://github.com/Bogenbai/Entitas-Flux/wiki/Code-Generation) ·
[Generated code as files](https://github.com/Bogenbai/Entitas-Flux/wiki/Generated-Code-as-Files) ·
[Renaming components](https://github.com/Bogenbai/Entitas-Flux/wiki/Renaming-Components) ·
[Debug hooks](https://github.com/Bogenbai/Entitas-Flux/wiki/Debug-Hooks) ·
[ECS graph](https://github.com/Bogenbai/Entitas-Flux/wiki/ECS-Graph)

## License
This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
