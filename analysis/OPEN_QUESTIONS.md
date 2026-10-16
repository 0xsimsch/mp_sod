# Shadows of Doubt multiplayer: open questions

Target architecture: **host-authoritative**. All players generate the same city from a shared seed. The host runs the real simulation (AI, murders, cases). Clients run a thin world and apply host messages (events + state snapshots) over Steam networking (lobbies + `SteamNetworkingMessages`, via the game's bundled Steamworks.NET).

**Answered: city generation is deterministic from the share code** (same machine, two fresh launches). Everything matched except the position of 2 inventory items out of 34,426 objects. Details: `determinism_test/DETERMINISM_TEST.md`. Still open: cross-machine, and the starting state after the pre-sim.

**Status after the night of 2026-10-12/13** (details in `analysis/PERCEPTION_RESULTS.md`, `analysis/B2_RESULTS.md`, `plugins/SodCoop/README.md`):
- **No blockers left in A.** A second player is seen trespassing, chased, attacked and KO'd by citizens, with the host far away. Tick rate (A1) and room loading (A2) are patched and tested in game.
- **Networking runs end to end** over UDP against scripted fake peers in both roles, with up to 3 players. It covers:
  - players: handshake, poses with walk animation, illegal actions, damage forwarding, KO
  - NPC sync: host-authoritative citizens around each client, ~8 KB/s
  - world: doors, runtime spawns/moves/pick-ups/deletes with stable ids
  - time: host clock and host pause
  - the case: discoveries, the case itself and board pins, murder records and deaths
  - conversations: one player per citizen
  - **mid-game join** (host world snapshot loaded automatically on the client)

  Steam lobby creation and Steam messaging (loopback to our own SteamID, byte-identical) work. Only Steam between two real machines is **untested** (needs a second account). How to try it: `plugins/SodCoop/README.md` → "Try it with a friend".
- B1 (join works), B2 (implemented), B3 (client menus don't pause; host pause pauses all), B4 (implemented), B5 (no measurable cost on this PC) are answered and mostly implemented.
- Murders are host-only: clients don't run the murder director, and receive the host's murder records and deaths. Every death in the game needs a murder record (`Human.Death` dereferences it), so records are synced in the game's own save format.
- New finding for the design table: in normal play players **never die**, they get KO'd. Death only exists with permadeath (see the note under "Decided").

Notes in *italics* are inferences from the decompiled structure (`decompiled/cs/`). IL2CPP left method bodies empty, so behavior hasn't been verified unless stated.

---

## A. Blockers (decide whether the approach works)

### A1. AI tick rate centered on one player: **CONFIRMED** (2026-10-12)
`NewAIController` has `AITickRate` (veryLow..veryHigh), `desiredTickRate`, `UpdateTickRate()`. `CitizenBehaviour` keeps a list per rate.
- **Measured:** a citizen 134 m away went from `veryLow` to `veryHigh` in the frame the player teleported next to them, and the AI update interval dropped from ~70 to ~4 game-seconds (about 19×). It's driven by player presence/visibility, not a plain distance threshold, and stays high while the citizen is busy (e.g. chasing the player). See `probe_results/A1_A2_RESULTS.md`.
- **Fix:** patch so "near/visible to the player" means "near/visible to *any* player". The host needs each client's position and view direction.
- **FIXED in SodCoop** (night of 2026-10-12): `Human::UpdateTickRateOnProx` decoded. It's room/location/building/floor based, relative to `Player.Instance`. `Patches/TickRatePatch.cs` takes the best rate over all player bodies and re-rates citizens around a remote player when it changes room. Tested in game. See `analysis/PERCEPTION_RESULTS.md`.

### A2. What does culling disable? **ANSWERED** (2026-10-12)
`GeometryCullingController.UpdateCullingForRoom(NewRoom currentRoom, ...)` works around a single room. Also `RoomsLoader`, `CullingControls`, `LightCullingController`.
- **Citizens:** only renderers are switched off. Colliders, GameObjects and animators stay active at any distance.
- **Rooms:** far rooms are **completely unloaded**: GameObject inactive, 0 colliders, no walls. They load the moment the player arrives and unload about 9 s after they leave.
- **Effect:** on the host, the client's surroundings may have no walls or colliders, which breaks sight raycasts, physics items and doors. The host must keep geometry loaded around every player (run culling per player and merge, or keep a separate "logically loaded" set).
- **Answered:** sight checks *do* rely on loaded geometry. With the remote player's rooms unloaded, citizens in 5 neighbouring rooms saw it through walls (~170 sightings in 75 s).
- **FIXED (prototype) in SodCoop:** `Puppet/RemoteRoomLoader.cs` keeps the remote body's room and its adjacent/above/below rooms loaded via `NewRoom.SetVisible`. Through-wall sightings dropped to **0**, and only the real witness reacted. The host renders those rooms too (cost unmeasured, B5).

### A3. `Player.Instance` assumptions everywhere: **MEASURED** (2026-10-12)
- **509 of 10,847 game methods (~4.7%)** refer to the player: 438 via `Player.Instance` (inlined everywhere, with **0** real call sites, so a Harmony hook on the getter catches nothing) and ~87 via `actor.isPlayer` checks.
- About half are local UI/HUD/first-person code that can stay as is. Roughly **100–150 methods** need multiplayer awareness, mostly AI perception (`Actor::SightingCheck`, `SpottedByPlayer`, `Human::IsTrespassing`), chase/combat, `Human::UpdateTickRateOnProx`, the murder director and world-affecting interactions.
- **Tested with the SodCoop prototype:** a possessed citizen with `isPlayer = true` *is* flagged by the trespass check, but at first witnesses never saw or chased it.
- **SOLVED** (night of 2026-10-12): `SightingCheck` works on any actor. It failed because (1) only `Player::UpdateIllegalStatus` turns trespassing into the `illegalStatus` flags that witnesses read, and (2) `SightingCheck` only looks at `CityData.visibleActors`, which follows the *host's* culling. With both handled for the puppet (and the puppet blocked from acting as an observer itself), **the resident sees P1 trespass and chases it** while the host is far away. Remaining `Player.Instance` leaks inside `SightingCheck` (host KO state, hiding list, loitering timer, search target, crosshair) are listed in `analysis/PERCEPTION_RESULTS.md`.
- Details: `analysis/A3_RESULTS.md`.

---

## B. Technical unknowns

### B1. Joining mid-game
The seed gives the starting city, not the current state (murders, broken doors, moved items, discovered evidence).
- Can we snapshot host state (`SaveStateController`, `StateSaveData`) mid-game without a pause, and load it on a client?
- **Snapshot: YES** (tested, night of 2026-10-12, SodCoop F2): `SaveStateController.CaptureSaveStateAsync(path, true)` called during play took **0.23 s (4 frames, one 161 ms hitch)**, with no pause and the game clock still running, and wrote a **1.0 MB** compressed `.sodb`. Sending it to a joining client is cheap. Steam reliable messages are capped at 512 KB, so it goes in chunks.
- **Load on the client: WORKS** (night of 2026-10-12/13, SodCoop `Net/SnapshotSync.cs`). The host streams the snapshot to a joining client, which writes it as a temporary save and loads it with the game's own loader (`RestartSafeController` + `CityConstructor.StartLoading`), then deletes it. Tested end to end with no exceptions; the client session survives the ~35 s load. Generated cities are sent along (built-in ones aren't needed). The client keeps its own money and hands the host's items (which come with the loaded player) back to the host's body (implemented; untested with a non-empty inventory). `Interactable.worldAssignID` comes along inside the save.

### B2. Stable network IDs
Messages like `DoorOpened(id)` need IDs that match on every machine.
- Citizens, rooms and buildings probably have generation-time IDs. Fine if A-tests confirm determinism.
- **Confirmed for generation time:** all 34,426 interactable IDs matched across runs, including late-created ones in the 10,000,000+ range.
- **Risk:** objects created during play (dropped items, evidence, blood, fingerprints, footprints) may get IDs from local counters that differ between machines.
- **CONFIRMED** (static analysis, night of 2026-10-12): runtime objects use one static counter, `Interactable.worldAssignID` (`AssignIDWorld`), through `CreateWorldInteractable` (56 callers: player items/photos/printouts, murder director, AI drops, bullet holes) plus fingerprints and footprints. Generation-time ids (room-based `roomID*1000+n`, furniture-based negative ids) are deterministic. Fix: the host allocates all world ids and broadcasts spawns. Clients create with a forced id (a prefix on `AssignIDWorld`), or use disjoint per-machine ranges. See `analysis/B2_RESULTS.md`.
- **Part 1 implemented and tested** (SodCoop `Net/SpawnSync.cs`): `CreateWorldInteractable` spawns are replayed on the other machines with the same id (forced), and clients use a disjoint range. A spawned newspaper showed up on the host with the client's id. Part 2 (`Net/ItemSync.cs`): moves, inventory in/out and deletes are replayed by id (tested: spawn → move → delete applied on the host). Part 3 (`Net/ForensicSync.cs`): revealed fingerprints and footprints are replayed with the same id (tested).

### B3. Pausing
Menus, the case board, and possibly conversations and minigames pause the game (`SessionData.play`, `OnPauseUnPause(openDesktopMode)`, `SetTimeSpeed`).
- **Observed in play:** the notebook/case board view and the shop "Buy" menu both fully pause the game ("PAUSED", clock stops). Dialog menus may too.
- Each pause has to become "doesn't pause" or "pauses everyone".
- Some UI code may assume time is stopped while it's open.
- **Mapped and prototyped** (night of 2026-10-12): every pause goes through **`SessionData::PauseGame(showPauseText, delayOverride, openDesktopMode)`** (0x57E760). It sets `play = false`, stops AI (`CitizenBehaviour.GameSpeedChange`), disables Rewired input and shows the "PAUSED" UI. It has **34 call sites**: Esc menu (`InputController.Update`), notebook/case board (`InterfaceController.ToggleNotebook`), map tracking, dialogs (`DialogController.*`: ask about a person, bribes, buy, give, phone keypad), `ActionController` inspect/dial/keypad/buy/sync disks, inventory (`BioScreenController.SetInventoryOpen`), popups/tutorials, apartment purchase, decor edit, call logs. The game also already has `InterfaceController.OpenNotebookNoPause`.
- `SodCoop/Patches/NoPausePatch.cs` (on for clients in a session, F3 for testing) lets `PauseGame` run, then sets `play = true` and restarts the AI. A **host** pause still pauses, and is pushed to every client (TimeSync `Paused` flag; tested both ways). **Tested:** with the case board and the Esc menu open, game time kept advancing at the normal rate (0.0125 h per 5 s), with no exceptions. Input stays locked while the menu is open, as intended.
- Done: time *speed* changes are host-only (clients block `SetTimeSpeed`, sleep/wait via `Player.SetSpendingTimeMode`, `Game.SetFastForward`; the host's clock is pushed every 2 s). A client's Esc menu refuses Load and New Game during a session.

### B4. Interactions with a "current target"
`InteractionController` and the dialog system probably assume one player talking to one citizen.
- What happens when two players talk to the same citizen, or one interrupts the other?
- **Static look** (night of 2026-10-12): starting a conversation is `NewAIController.TalkTo(convoType)` on the *citizen's* controller (0xAE0950). It's almost entirely local to the talking player: `Player.SetSpendingTimeMode`, `Actor.SetInteracting` on both actors, `InteractionController.SetLockedInInteractionMode`, `Player.TransformPlayerController` (turn toward the citizen), `InteractionController.SetDialog`, plus a return-from-locked-in auto-travel. It refuses citizens that are dead/asleep/stunned, fleeing, or in certain reaction states.
- **Implemented and tested** (SodCoop `Net/TalkSync.cs`). Host side, two fake clients: first granted, second "busy" until the first ended. In-game client: the dialog opens only after the host's grant, and ending it releases the citizen. Design as below.
- **Proposed design:** the client runs the dialog UI locally. Before opening it, the client asks the host `TalkRequest(humanId)`. The host grants it if the citizen isn't already talking to anyone (first come first served), marks the citizen as interacting with that player's body (`Actor.SetInteracting`, so the AI stops and faces it), and answers `TalkGranted` or `TalkBusy`. `TalkEnd` releases it. Dialog *outcomes* that change the world (bribes, buying, info that becomes evidence) go to the host as events, like the shared case evidence.

### B5. Host performance
The host simulates everything, culls or loads around 2+ locations, and sends network updates. The game is already CPU-heavy.
- Can a mid-range host handle two active areas?
- **First measurement** (this PC, night of 2026-10-12, SodCoop F1 uncaps fps): host alone **~5.8 ms/frame** (p95 7.2). With a remote player in a far home, its **18 rooms force-loaded** (A2 fix) and its citizens at high tick rates: **~5.6 ms/frame** (p95 7.0). No measurable difference, except a single **~180 ms hitch** when the rooms load. That hitch should be spread over frames (`SetVisible(..., immediateLoad: false)`). Still to measure on a mid-range PC, and with the remote player moving through the city (constant room loading).
- Note: the game runs at a 30 fps cap here (33.3 ms) unless uncapped.

### B6. Game updates and versions
- Patches change native addresses. Patch by method name through the BepInEx IL2CPP interop layer, never by hard-coded RVA.
- Check game build, DLC and mod list when joining.

---

## C. Game design decisions

### Decided (2026-10-12)

| Topic | Decision | What it means for the code |
|---|---|---|
| Mode | **Co-op** | No PvP. Players can't be each other's targets. |
| Time and sleep | **Host controls time** | Only the host can change time speed or skip time by sleeping. Clients' menus, case board and shop must **not** pause the simulation; on clients they become UI-only overlays (see B3). A host pause pauses everyone. |
| Cases | **One shared mission, chosen by the host** | The host's case is authoritative. Case board, evidence and objectives are synced to clients. Evidence a client finds goes to the host, which adds it to the shared case. Clients can't start or accept a main case. |
| Death | **Other players keep playing normally; a dead player stays dead and follows a living player** | Death must not trigger game over or reload for the session. The dead player switches to a spectator camera that follows a living player (no free camera). |
| Crime and blame | **Only the player who committed the crime is blamed** | Wanted status, fines and witness memory are tracked **per player**. Witness/sight code must record *which* player it saw, which ties directly into A3. |
| Player count | **2 for now, built to extend** | See the rules below. |
| Inventory and money | **Per player** | Each player has their own inventory and money, stored on `NetPlayer`. The game only has one player inventory, so a second one has to be added. |
| Jobs and side missions | **Shared** | Side jobs are shared like the main mission: any player can take one and it's synced to everyone. Side job code probably references the player directly. |
| Apartments and property | **Shared** | One home for all players. Ownership stays tied to the host's player; clients get the same access. |
| Knocked out (not dead) | **Affects only the knocked-out player** | KO and hospital stay apply to that player alone. The others keep playing, and the KO must not skip or pause time for the session. |

> **Finding that affects the Death / KO decisions** (static analysis, night of 2026-10-12): in normal play the player **cannot die**. `Player::OnZeroHealthReached` → `Player::TriggerPlayerKO` (knocked out: inventory dropped, possibly detained, fade, wake up later). Real death only happens with **`Game.permaDeath`** ("If you get KO'd in game, the game ends"); `Player.KillPlayer` and the game-over display have no other direct callers. So the "Death" decisions (stay dead, follow a living player) only apply when permadeath is on, and the "Knocked out" decision is the case players will actually hit. Worth confirming that's what you want: e.g. should permadeath be a lobby setting, and is "everyone KO'd at once" a mission fail? Good news for the KO decision: `TriggerPlayerKO` doesn't fast-forward time. It only *cancels* waiting/sleeping (`SetSpendingTimeMode(false)`), so a KO'd host doesn't skip time for the others. Its KO is a local fade, item drop and recovery (`Player.TriggerPlayerRecovery`).

### Rules for writing extensible code

- Never use "the other player". Keep a `PlayerId → NetPlayer` registry, and loop over it everywhere: proximity, tick rate, culling, witnesses.
- Every network message carries the sender or subject `PlayerId`. Include a protocol version in the join handshake.
- Make max players a lobby setting (default 2), not a constant spread through the code.
- Interest management (which citizens and objects get synced) works per player from day one, even if it's only ever two.
- Per-player state (blame, wanted status, death, KO, inventory, money) lives on `NetPlayer`, never in game singletons.

### Still to decide

| Topic | Question |
|---|---|
| Death details | What happens if the **host** dies (session continues with host spectating, or host migration)? Does the mission fail if everyone dies? |
| Rewards | Is mission payout split, copied to each player, or host only? |

---|---|---|
| Time and sleep | Always runs / host controls speed / everyone must agree to skip | Big. "Always runs, no sleep skip" is the simplest. |
| Cases | One shared case and board / per player | Shared means syncing the whole evidence and case system. |
| Inventory and money | Per player / shared | Per player needs a second inventory, which the game doesn't have. |
| Jobs and side missions | Shared / per player / off at first | Side jobs probably reference the player directly. |
| Apartments and property | Shared / each player owns their own | Ownership is probably tied to one player. |
| Being knocked out or killed | What happens to the other player during one player's KO, hospital stay or death? | KO and death flow may need rewriting. |
| Crime and witnesses | Is a crime by player B also blamed on player A? | Ties into A3. |
| Player count | 2 / 4 / more | Two is much easier. |

---

## D. Practical and legal

- **Steam app ID:** the mod would use the game's own app ID (986130) for lobbies and P2P. Works technically, and the game already supports Steam Workshop and mod.io. Still, check the EULA and consider asking ColePowered Games.
- **Existing work:** search Nexus, Thunderstore and the official Discord for earlier multiplayer attempts or developer statements.
  - **Searched** (web, 2026-10-13):
    - [Targendorf/sod_coop](https://github.com/Targendorf/sod_coop): MIT, BepInEx IL2CPP + Harmony, LiteNetLib, host-authoritative. It claims position/animation, NPC state, time and case sync, but its README says the Harmony patches are incomplete. Steam P2P is only planned, there are no releases, and it's early and experimental. Worth a read for ideas, though it doesn't address the perception, tick-rate or culling problems solved here.
    - "Shadows of Doubt MP / Shadows of Multiplayer" (closed source, [Patreon](https://www.patreon.com/ShadowsOfDoubtMP/posts/welcome-to-of-co-158045944) / [Boosty](https://boosty.to/sod_mp)): a playable demo with its own launcher, Discord login, server browser, partner map markers, shared keys. Some features are paywalled.
    - No official ColePowered statement on multiplayer was found. The game is listed as single-player.
