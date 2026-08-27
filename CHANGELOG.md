# Changelog

All notable changes to **Entitas-Flux** are documented here. This is the fork's own
history; upstream Entitas's changelog lives in
[`src/Entitas-Flux/CHANGELOG.Entitas.md`](src/Entitas-Flux/CHANGELOG.Entitas.md) and
describes a different product.

The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/). Versions
are `0.x` while the multi-assembly work that 1.0 is waiting on is unfinished; until then
a minor bump may carry behaviour changes, and each one says so under **Changed**.

## [Unreleased]

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

[Unreleased]: https://github.com/Bogenbai/Entitas-Flux/compare/v0.2.0...HEAD
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
