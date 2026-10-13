# A3 results: how much code assumes a single player?

Static analysis of `GameAssembly.dll` (v42.04), 2026-10-12. No game running needed.

## Answer

**509 of 10,847 game methods (~4.7%) refer to the player**, across ~170 classes. Most of them are local-only (HUD, camera, first-person items, menus). Those can stay as they are, because every machine still has its own local `Player`. A rough estimate of the methods that actually need multiplayer awareness on the host is **~100–150**, concentrated in AI perception, crime and the world-affecting parts of interaction.

That's a manageable patch surface for Harmony. The bigger cost is understanding each of those methods (native code, read in Ghidra), not the number of hooks.

## Method

1. `Player.get_Instance` (RVA `0x44C390`) loads the `Player` class pointer from global `0x3D97150` and returns `static_fields->_instance`.
2. **The getter is inlined everywhere: 0 real call sites.** So hooking `Player.Instance` with Harmony would catch **nothing**. Each use has to be handled where it's used.
3. `player_refs.py` scans the `il2cpp` and `.text` code sections for RIP-relative loads of the Player class global (inlined `Player.Instance`, `is Player` checks, Player statics) and maps each hit to its method using Cpp2IL's RVA/length table.
4. `isplayer_refs.py` disassembles methods of the AI, crime and interaction classes and counts byte reads at `[obj+0x1B]` (`Actor.isPlayer`). This is an estimate: other classes can also have a bool at `0x1B`, but the hits found (sighting, trespassing, chase, attack) are clearly genuine.

| Pattern | Methods | Sites |
|---|---|---|
| `Player.Instance` / Player class (inlined) | 438 | 1,017 |
| `actor.isPlayer` checks (AI/crime/interaction classes only) | 87 | 157 |
| **Union** | **509** | |

11 Player-global hits fall outside known game methods (probably shared generic code), so they're not attributed.

Output: `out/player_refs.json`, `out/player_refs_by_class.tsv`, `out/isplayer_refs.json`.

## By system

| System | Methods | Needs multiplayer change? |
|---|---|---|
| Interaction, doors, items (`ActionController` 33, `InteractionController`, `Interactable`, `NewDoor`, keypads, computers) | 114 | **Partly.** The UI side stays local. World effects (door open/close, lockpick done, item taken, sabotage) become **network events** from the acting player. |
| Status, HUD, UI, map (`StatusController` 35, `ChapterIntro` 18, `MapController`, `InterfaceController`, dialog) | 100 | **Mostly no.** Local player's UI. Exceptions: case objectives and dialog with shared citizens. |
| World, time, culling, audio (`SessionData`, `NewRoom`, `Game`, `SnailController`, `CityConstructor`, `AudioController`) | 73 | **Partly.** Time is host-only (per the design). Room culling priority (`NewRoom::GetRoomCullingCachePriority`, `RebuildCullingTree`) has to consider every player (A2). Audio stays local. |
| **AI, perception, chase, combat** (`NewAIController`, `Actor`, `Human`, `Citizen`) | 55 | **Yes. This is the core.** See the list below. |
| Player body, camera, first person (`Player` 22, `FirstPersonController`, `FirstPersonItemController`) | 52 | **No** for the local player. Remote players need a separate puppet. |
| Crime, murder, cases (`MurderController`, `Case`, `NewGameLocation`) | 16 | **Yes.** Victim and murderer picking, cover-up calls, escalation and objectives become host-authoritative and per player. |
| Apartment, property | 10 | Depends on the open "apartments" decision. |
| Other (65 classes, 1–3 each: telephone, speech, security cams, upgrades, vehicles, …) | 89 | Review case by case. |

### The AI and perception methods (highest priority)

Perception / witness, i.e. *what the citizen thinks it saw*:
- `Actor::SightingCheck` (14 isPlayer checks)
- `Actor::SpottedByPlayer`, `Actor::HeardByPlayer`, `Actor::HearIllegal`
- `Actor::AddToSeesIllegal` / `RemoveFromSeesIllegal`, `Actor::UpdateOverallVisibility`
- `Human::IsTrespassing`

Reaction / pursuit / combat:
- `Actor::AddPersuedBy` / `RemovePersuedBy`
- `NewAIController::InstantPersuitCheck`, `PersuitUpdate`, `ChaseLogic::UpdateLastSeen`, `ChaseLogic::GenerateProjectedNode`
- `NewAIController::Investigate`, `TrackingSpookCheck`, `TriggerReactionIndicator`, `OnNewTrackTarget`, `UpdateTrackedTargets`
- `NewAIController::StartAttack`, `AttackUpdate`, `IsMuggingValid`, `UpdateHumanDrawnWeapon`
- `Citizen::RecieveDamage`

Update rate (A1):
- `Human::UpdateTickRateOnProx`, the proximity-based tick rate. Patch it to use the nearest player.

Conversation:
- `NewAIController::TalkTo`, `OnReturnFromTalkTo`
- `Human::DDSParticipantConditionCheck` (14 checks), `Human.ConversationInstance::EndConversation`, `Human::SetInConversation`

Murder director:
- `MurderController::PickNewVictim` and `PickNewMurderer` (probably exclude the player as victim or murderer; must exclude **all** players)
- `MurderController::SpawnPlayerTaunt`, `TriggerCoverUpTelephoneCall`, `TriggerCoverUpSuccessCall`, `ExecuteSniperShot`

## What this means for the design

1. **Represent remote players as real `Actor`s on the host.** Most perception code works on `Actor`s and branches on `isPlayer`. If each remote player is a host-side `Human`/`Actor` puppet with `isPlayer = true`, the existing sight, hearing, trespass and chase code largely *works for them as is*. Then only the places that use `Player.Instance` (the singleton) need fixing, so they look up "which player" instead.
2. **Blame per player** (design decision): the witness entry points above (`SightingCheck`, `AddToSeesIllegal`, `AddPersuedBy`) are where to record *which* player was seen. That attaches crimes to the right `NetPlayer`.
3. **Harmony can't patch `Player.Instance` globally**, since it's inlined. Each relevant method needs either a prefix/postfix that swaps in the right player context, or a transpiler-style rewrite. On IL2CPP that's harder, so prefer replacing whole methods where needed.
4. **Leave the UI/HUD/first-person half alone.** Each client keeps a normal local `Player`. That's about half the hits.

## Caveats

- Code that cached the player in a field or local earlier (e.g. `target == someStoredPlayer`) isn't caught unless it also reads `Player.Instance` or `isPlayer`.
- Other single-instance controllers (`CameraController`, `InteractionController.Instance`, `FirstPersonController`) also encode "one viewpoint". Visibility checks that use the *camera* need each client's view direction on the host.
- The `isPlayer` scan was limited to ~17 classes. A full-binary pass with type tracking (Ghidra) would give an exact number.

## Next steps

1. Load `GameAssembly.dll` into Ghidra with Cpp2IL/Il2CppDumper names applied, and read the ~25 AI/perception methods above first.
2. Prototype: spawn a host-side `Citizen`-based puppet with `isPlayer = true` and check whether citizens spot it, chase it and flag trespassing. That would confirm point 1.
