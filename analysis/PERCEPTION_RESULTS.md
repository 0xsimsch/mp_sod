# Perception for a second player: why citizens ignored P1, and the fix

Static analysis (GameAssembly.dll v42.04) plus live runs with `SodCoop`, 2026-10-12 (night session).
Follows up the open item from `plugins/SodCoop/README.md`: "trespass detection works for P1, but witnesses never react."

## Result

**Citizens now witness, and chase, a second player that's far from the host.** With three small changes in `SodCoop`, the resident of the home P1 was put in saw P1 trespassing, built `seesIllegal[P1]` up to 1.0, and pursued P1 (`ai.persuitTarget = P1`, `P1.persuedBy = 1`). The host was ~100 m away the whole time.

| Run | Change | Result |
|---|---|---|
| before | (coop_run1..3) | `TRESPASS true`, but **0** `SEES-ILLEGAL` / `PURSUED-BY` for P1 |
| 1 | port of `Player::UpdateIllegalStatus` | P1 `illegalStatus = true`, still 0 sightings: P1 not in `CityData.visibleActors` |
| 2 | + keep P1 in `visibleActors` | **42 `SEES-ILLEGAL` in 75 s** from 3 citizens, no pursuit yet |
| 3 | (diagnostics) | resident pursues P1, then P1's own AI starts pursuing *her* |
| 4 | + block AI/perception *by* the puppet | **resident pursues P1 cleanly**, `seesIllegal[P1] = 1.00` |

## How citizen sight works (from the disassembly)

`Actor::SightingCheck` (0x5E76F0, 20 KB) runs per citizen on its AI tick:

1. Loops over **`CityData.visibleActors`** (a `List<Actor>`), not over all actors. For each actor it skips:
   - itself, and anything with `isSeenByOthers == false`
   - players while `Game.invisiblePlayer`
   - actors in an air vent
   - players while `Player.Instance.playerKOInProgress` (**host's KO state, even for a remote player**)
   - actors whose `ai.restrained` is set
   - hiding actors, unless `isPlayer` and in `Player.Instance.spottedWhileHiding` (**host's list**)
2. Then range (`GameplayControls.citizenSightRange`), field of view, and `Actor::ActorRaycastCheck` (physics raycast).
3. For a seen actor it reads **that actor's own flags**: `illegalStatus` (0x68), `illegalAreaActive` (0x61), `illegalActionActive` (0x60), `trespassingEscalation` (0x64). If illegal, it calls `AddToSeesIllegal(target, focus)`, which adds `focus` to `seesIllegal[target]` (clamped 0..1). At 1.0 → `NewAIController::SetPersue`.
4. `NewAIController::SetPersueTarget` calls `target.AddPersuedBy(pursuer)` (vtable slot 0x468).

Who maintains those inputs:
- `Actor::SetVisible(vis)` (0x5E3AA0) sets `visible` and **adds/removes the actor in `CityData.visibleActors`**. It's driven by the host's culling. So an actor the host isn't rendering can't be seen by anyone.
- `Player::UpdateIllegalStatus` (0x461D20) is the only place that turns trespassing into the flags: `isTrespassing = illegalAreaActive = IsTrespassing(currentRoom, out trespassingEscalation, true)`, then `illegalStatus = illegalAreaActive || Game.everywhereIllegal || illegalActionActive || forceTarget`, then `SetStealthMode(illegalStatus)`. `Actor::UpdateIllegalStatus` (citizens) only ORs the flags and never computes trespassing.
- `Actor::SetStealthMode` adds the actor to `CitizenBehaviour.actorsInStealthMode` (per-frame visibility/light updates). When `isPlayer`, it also writes `Player.Instance` field 0x818 (host-only side effect).

## The fix (in `plugins/SodCoop`)

1. **`RemotePlayerBody.UpdateIllegalStatus`**: port of `Player::UpdateIllegalStatus` for the puppet, every 0.25 s. `IllegalActionActive` is a property that the network will set from the client's `InteractionController.SetIllegalActionActive`. F9 toggles it for testing.
2. **`RemotePlayerBody.KeepPerceivable`**: keep the body in `CityData.visibleActors`. Don't touch `visible`: `SetVisible` returns early on an unchanged value, so faking it would stop the model from showing again.
3. **`Patches/AiTickPatch.cs`**: the puppet is still a `Citizen` in `citizenDirectory`, so citizen-wide loops still call its `SightingCheck`. Block `Actor.SightingCheck`, `NewAIController.SetPersue` and `SetPersueTarget` for remote bodies (plus the existing `AITick`/`UpdateTickRate` blocks).

## A1 and A2 fixes (same night, tested)

**A1, tick rate** (`Patches/TickRatePatch.cs`). `Human::UpdateTickRateOnProx` (0x767720) decoded: relative to `Player.Instance`, same room → veryHigh, same game location → high, same building and floor → medium, same building (or both outside) → low, else veryLow. It runs on a citizen's own room change and from `Player::OnRoomChange` for citizens around the host. The patch (only active while a remote player exists) uses the **best rate over all player bodies**. A postfix on `Human.OnRoomChange` for remote bodies re-rates citizens in the building/location the body left or entered (54–58 citizens per move in tests). This replaces the old `BOOST` hack.

**A2, room loading** (`Puppet/RemoteRoomLoader.cs`). Every 1 s: the remote body's room plus `adjacentRooms`, `aboveRooms` and `belowRooms` (11 rooms in the test) get `NewRoom.SetVisible(true, true, true, true)`. Rooms no longer needed are released unless they're in the host's `GeometryCullingController.currentRoomsCullingTree`. F10 toggles it.

| P1 in a stranger's home, host ~100 m away, 75 s | room loading off (control) | room loading on |
|---|---|---|
| `SEES-ILLEGAL` from the resident (same room) | 9 | 56 |
| `SEES-ILLEGAL` from citizens in **other rooms** | **170 from 5 citizens** (137, 138, 205, 286, 4) | **0** |
| P1 room colliders | 68/68 (partly loaded) | 129/129 |

So without loaded geometry, citizens in neighbouring rooms saw through the walls, and the real witness hardly ever got a look. With the rooms loaded, perception behaves like single player. Some churn remains: 6 of the 11 rooms were unloaded again within a minute (host culling or the unload queue) and re-forced on the next tick. Cost: the host renders those rooms too. This needs measuring for B5.

## Still wrong / next
- **Host-state leaks in `SightingCheck`** when the seen actor `isPlayer` (these read `Player.Instance` instead of the seen actor):
  - `Player.Instance.playerKOInProgress` (skip check)
  - `Player.Instance.spottedWhileHiding` (hiding check)
  - `Player.Instance.currentGameLocation` and its `playerLoiteringTimer` vs `GameplayControls.loiteringTrespassThreshold`. Loitering is tracked for the host only.
  - `Player.Instance.searchInteractable` (reaction to the player searching something)
  - `InterfaceController.CrosshairReaction` (host HUD flashes when a citizen reacts to *any* player)

  Options: a transpiler isn't practical on IL2CPP. Either replace `SightingCheck` wholesale (large), or for each citizen's check temporarily swap `Player._instance` when only one player is in range (hacky, but covers all `Player.Instance` reads). Decide after the A2 work.
- **Combat reaches P1 (tested):** after ~2 min the resident caught P1: 13 × `StartAttack(P1)`, 8 × `Citizen.RecieveDamage(0.20)` on the puppet (health 1.55 → 0.14), then `NewAIController.SetKO(true)` on the puppet. So witness → chase → attack → KO all work against a second player with no extra patches. **To do:** prefix `Citizen.RecieveDamage` for remote bodies and forward `(amount, attacker, position, direction)` to the owning client, which applies it to its own `Player` (`Player.RecieveDamage`). The client's own KO flow then runs (design decision: KO affects only that player), and the host mirrors it by putting the puppet in KO/ragdoll when the client reports it. Don't let the puppet take citizen damage or citizen KO.
- `MurderController::CitizenHasSeenBody` is called from `SightingCheck` with `MurderController.Instance`. That's fine (host-authoritative).

## Tools written for this

`analysis/tools/` (needs `capstone` + `pefile`; on NixOS: `nix-shell -p "python3.withPackages(ps: [ps.capstone ps.pefile])"`):

- `meta.py`: reads `global-metadata.dat` (v31) and the IL2CPP code/metadata registrations. Resolves **every** method RVA (all assemblies + generic instances) and decodes metadata-usage globals (class, method, field, string literal, generic method).
- `sodis.py 'Class::Method'`: annotated disassembly (call targets, class/string/field refs, field offsets of the method's class chain). `--list` with a trailing `*` lists methods.
- `xref.py CityData 0x180` (methods using a class + field offset) and `xref.py --call 0x5E3AA0` (direct callers).
