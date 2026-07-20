# FishNet 4.7.2 × Unity 6000.5 compatibility patch

**Why:** Unity 6.5 (6000.5, June 2026) made two API removals compile-time errors (CS0619), and
FishNet **4.7.2R (Apr 2026) — the latest release at patch time — predates them**, so a stock import
fails to compile (`FishNet.Runtime` never builds; the console then also shows a misleading
`Failed to find entry-points / Failed to resolve assembly: Unity.FishNet.Codegen` ILPP error, which
is pure fallout). Applied 2026-07-18 to the local project (`C:\Users\amedi\HollowPines`).

**Drop this patch** when a FishNet release with native Unity 6.5 support exists — delete the shim
file and reimport clean.

## Part 1 — `Scene.handle` is now a `SceneHandle` struct (was `int`)

Shim: [`FishNetUnity65Compat.cs`](FishNetUnity65Compat.cs) → copy into `Assets/FishNet/Runtime/`.
Defines `Scene.HandleInt()` (global namespace, so call sites need no `using`), truncating
`handle.GetRawData()` to the session-unique int FishNet stores/compares.

Then replace `X.handle` → `X.HandleInt()` at the int-context sites (all marked
`// Unity 6.5 patch` in the project; `SceneHandle == SceneHandle` comparisons stay untouched):

| File (under `Assets/FishNet/Runtime/`) | Sites |
| --- | --- |
| `Serializing/SceneComparer.cs` | `GetHashCode` return |
| `Serializing/Helping/Comparers.cs` | the two `!= 0` checks |
| `Managing/Scened/SceneLookupData.cs` | ctor `Handle =`; `result.HandleInt() != 0` |
| `Managing/Scened/UnloadedScene.cs` | ctor `Handle =`; `GetScene()` compare |
| `Managing/Scened/SceneManager.cs` | 8 sites (pending-loads ×2, handle caches ×4, `GetScene(int)` compare, requested-handles add) |
| `Plugins/ColliderRollback/Scripts/RollbackCollection.Threaded.cs` | 1 site (dormant `#if FISHNET_THREADED_COLLIDER_ROLLBACK`) |

## Part 2 — `Object.GetInstanceID()` removed (`GetEntityId` replaces it)

`Observing/NetworkObserver.cs` used `GetInstanceID() < 0` to detect "this condition is a clone we
`Instantiate`d, not the original ScriptableObject asset" before destroying it. `EntityId`
explicitly forbids sign/bit-layout assumptions, so the patch tracks clones directly:

- new field `_instantiatedConditions` (HashSet), populated at the `Instantiate(condition)` site;
- destroy check becomes `destroyed && _instantiatedConditions.Remove(item)`;
- set cleared alongside `_observerConditions.Clear()`.

Strictly more precise than the old sign check (only ever destroys what FishNet itself created).
