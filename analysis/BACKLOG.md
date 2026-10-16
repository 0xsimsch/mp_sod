# Backlog

## Host's body shows only head, hands and feet on the client (0.9.4)

**Seen:** first minutes of a fresh session. The client looks at the host's stand-in (overlay: `body of P0: citizen 3,
2 m, visible=True`). Only the head, the two hands and the two shoes render, floating where they should be. The torso,
arms, legs and clothes are missing. Position and pose look right, and `visible=True`, so this isn't the
`SetVisible`/room bug that `RemotePlayerBody.KeepShown` already guards against.

**Likely cause (not verified):** a citizen's clothes are separate meshes that `CitizenOutfitController` loads and
unloads on demand (`LoadCurrentOutfit` / `RemoveCurrentOutfit`, `loadedOutfit`, `meshesLOD`/`UpdateLODs`,
`CombineSkinnedMeshes`). Head, hands and feet seem to be the always-present base parts. On the client, citizen 3's
outfit is not loaded (or was removed) when we borrow it. Possible reasons:
- it is unloaded while out of range or culled, and the game reloads it on a path we skip: AI disabled, `SetVisible`
  forced by us instead of by the culling/room change;
- `NpcPuppets.Forget` in `NetSession.SpawnBody` caught it in the middle of a host-driven
  `SetCurrentOutfit(..., forceRefresh)` from `CitizenEventSync.ApplyState`;
- the save-based world load (`MP Join.sodb`) gives citizens a `currentOutfit` but no loaded meshes until something
  triggers a load.

**Logging added (uncommitted 0.9.4 build):** the overlay's body line now has an `outfit:` line under it. The log
records the state when the body is taken over and each time it changes (`body outfit: …`).
Normal case (2026-10-16 rejoin, same citizen 3, clothes shown): `current=casual loaded=casual, renderers 40/40,
skinned 0/0`. Citizens use plain MeshRenderers, no skinned meshes, so watch the `renderers` count and whether
`loaded` matches `current`. The bug did not happen on that rejoin, so it is intermittent. Waiting for a bad case.

**Fix to try:**
1. Confirm first. Log `outfitController.currentOutfit`, `loadedOutfit` and the count of enabled
   `SkinnedMeshRenderer`s under the body in the overlay or event log. `SodProbe` already counts renderers per citizen.
2. In `RemotePlayerBody.Possess` and in `KeepShown` (when the body is near and not `Hidden`): if the outfit isn't
   loaded, or the renderer count is suspiciously low, force a reload with
   `outfitController.SetCurrentOutfit(currentOutfit, false, false, true)` or `LoadCurrentOutfit()`. Throttle it so it
   doesn't run every tick.
3. Check whether the host sees the same problem on client bodies. That path borrows citizens the same way.
4. Check that `Doubles.Hide`/`UnhideAll` don't leave a citizen in the same state when it's shown again.

## Client freezes at "SNAPSHOT loading the host's world…" (0.9.4)

**Seen:** 2 of 3 joins on 2026-10-16 (20:48 and 20:51). The game window stops drawing. The plugin log ends at
`SNAPSHOT loading the host's world…`; on the join that worked, PERF and TIME lines kept coming during the ~30 s load.
During the 20:51 freeze, the process used about 0% CPU, all 166 threads were asleep (so it was stuck, not slowly
loading), and Unity's `Loading.Preload` thread was blocked in `anon_pipe_read`. `Player.log` shows nothing after the
main menu (it shows nothing after that on good runs either).

**Guess (not verified):** the join started too soon after the main menu appeared (20:51 run: menu at :34, join at
:36, load at :47; the 20:48 run was also early, at frame ~517). The menu is still doing its own background work then
(mod.io/workshop queries, `UnloadUnusedAssets`). `LoadJoinSave` switching to `loadingCity` at that moment may
deadlock Unity's async loading.

**Fix to try:** test first by waiting about 30 s on the menu before joining. If that avoids it, `LoadJoinSave`
should wait until the menu has settled before switching to `loadingCity`: no pending `AsyncOperation`, and a few
seconds since `MainMenuController.Start`. If it still freezes, add TRACE lines around `EnableMainMenu`,
`SetMenuComponent` and `CityConstructor.StartLoading`, and grab the main thread's stack with `winedbg` while it hangs.

## Crime-scene street gone for both players after picking up the mission documents (0.9.5)

**Seen (client log, 2026-10-16):**
- 20:59:28.616: case 1 "New Murder Case" arrives on the client with 2 active objectives.
- 20:59:28.643, 27 ms later: the client's freshly built copy of 'Explore Reported Crime Scene: 402 Lagoon Hotel'
  completes by itself, and the client asks the host to complete it.
- The host apparently didn't act on that request: there's no completion until 21:00:15. Then "Host completed an
  objective" (the host's own completion, when the host picked up the documents).
- 21:00:16: the case comes back as "Scope-Out Assassin" with 5 active objectives. That's the killer's moniker
  (`MonikerVoyeurKiller`), so the rename is the game's own story progress.
- From then on, neither player sees the street in the objective text or on the map.

**Open:**
- Is this the game's normal flow? In single player, does picking up the documents complete "Explore Reported Crime
  Scene" and remove the address? Check in a solo game.
- If not, the host's `sodcoop.log` around the pickup will show the cause, e.g. "OBJ P1 completed … completing it for
  everyone".
- Why does the client's objective complete 27 ms after `SetupObjectives` rebuilt it? That premature request is a bug
  either way. If the host ever accepts it, it removes the crime scene for everyone, the same failure 0.9.3 fixed.
  Maybe the trigger checks a condition that is already true on the client, or `SetupObjectives` runs the check
  loop at once.

**0.9.10:** clients no longer complete objectives or ask the host to. They send facts, and the host checks location facts against the client's body (see `plugins/SodCoop/README.md`, 0.9.10). The premature 27 ms completion would now be a rejected fact ("body is not at …"). Still open: whether the host's own completion at the document pickup is the game's normal flow (check solo).
