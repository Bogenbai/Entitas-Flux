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

### More features coming soon (or not)

## How to use
Setup is a bit clunky right now (as it always was with Entitas). I’ll try to simplify it later.
If you’re starting fresh I would suggest to do this:
1. Clone [Match-One](https://github.com/sschmid/Match-One).
2. Make sure the game runs and code generation works.
3. Go to [Releases](https://github.com/Bogenbai/Entitas-Flux/releases) and download the DLLs.
4. Replace the corresponding DLLs in the `Entitas` and `Jenny` folders.
5. Update your JennyRoslyn.properties with DataProviders and Generators **Entitas Flux** provides ([JennyRoslyn.properties](https://github.com/Bogenbai/Entitas-Flux/blob/master/Examples/JennyRoslyn.properties) example)
5. Hope it works :)

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.
