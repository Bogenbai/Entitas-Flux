# Entitas.CodeFixes

IDE-only companion to `Entitas.SourceGenerator`: the quick fix that carries out a
pending `[RenameTo]`.

## The workflow

```csharp
[RenameTo("AbleToAttack")]
[Game] public class CanAttack : IComponent { }
```

`Alt+Enter` on the attribute → **"Rename component 'CanAttack' to 'AbleToAttack' and
update all usages"**. The component, every identifier the generator derives from it
(`isCanAttack`, `AddCanAttack`, `GameMatcher.CanAttack`, `CanAttackChanged`, listeners …)
and all their usages are rewritten, and the attribute is removed.

`[RenameTo]` is inert on its own: generation ignores it, so the project keeps compiling
between adding the attribute and applying the fix. It only records the intended name.

## How the pieces fit

| Piece | Assembly | Role |
| --- | --- | --- |
| `RenameToAttribute` | `Entitas.CodeGeneration.Attributes` | Carries the new name. |
| `RenameToAnalyzer` (ENT0001) | `Entitas.SourceGenerator` | Warns that a rename is pending; the fix hangs off this diagnostic. |
| `RenameEngine` | `Entitas.SourceGenerator` | Works out *which* identifiers change and where they are used. |
| `RenameToCodeFixProvider` | `Entitas.CodeFixes` | Applies the plan from the IDE. |
| `entitas-rename` | `Entitas.Rename` | The same engine from the terminal, for CI or bulk work. |

The IDE and the CLI share one engine, so they cannot disagree about the result.

This is a separate assembly because `CodeFixProvider` lives in
`Microsoft.CodeAnalysis.Workspaces`, which IDEs have and the compiler does not — keeping
it out of the generator DLL means the compile path never has to resolve Workspaces.

## Deploying to a Unity project

`./build.sh` puts both DLLs in `Artifacts/…/Analyzers/`. Copy them next to each other in
the Unity project and give **both** the `RoslynAnalyzer` asset label with every platform
unchecked:

- `Entitas.SourceGenerator.dll` — generator + ENT0001
- `Entitas.CodeFixes.dll` — the quick fix

`Entitas.CodeGeneration.Attributes.dll` must be up to date too, or `[RenameTo]` will not
resolve.

## Other assemblies

Usages are rewritten in the declaring assembly **and** in every assembly that references
it — an Editor assembly, a test asmdef, another asmdef. There the generated code is not
visible as source, so an identifier is accepted when its symbol comes from the declaring
assembly and belongs either to the component or to a type the generator declares
(`GameEntity`, `GameMatcher`, `GameComponentsLookup`, …). A same-named member of a local
type is left alone.

Those usages are collected **before** the declaring assembly is rewritten — afterwards the
old names no longer resolve there.

## No Fix All

Every rename is planned against the current state of the code, so applying several at
once would use edits computed against stale text. Rename one component at a time.
