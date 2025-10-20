# Entitas Flux
**Entitas Flux** is a fork of the great and terrible [Entitas Framework](https://github.com/sschmid/Entitas).  
I created it to add features missing from the original Entitas that I believe should be there, and to support newer Unity versions.  
Don’t expect major changes or a big redesign like [Entitas Redux](https://github.com/jeffcampbellmakesgames/Entitas-Redux). Updates will (or will not) come slowly and only when I need a feature.

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
This attribute simplifies deffered reactivity.  
When component X is marked with `[Watched]` attribute changes it value with `ReplaceX(..)`, the entity gets a XChanged marker component.  
These markers live for one frame: they can notify systems this frame, then get removed so the logic doesn’t repeat next frame.  
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



### More features coming soon (or not)
