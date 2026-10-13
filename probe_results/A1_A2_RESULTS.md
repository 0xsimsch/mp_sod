# A1 + A2 results: AI tick rate and culling are both centered on the player

Measured 2026-10-12 with the `SodProbe` BepInEx plugin (`plugins/SodProbe/`), game v42.04, city "Casablanca Quarter" (368 citizens). The probe logs every citizen every 3 s. F8 teleports the player next to a far, idle, indoor citizen; F9 teleports back.

## Verdict

| Question | Answer | Effect on multiplayer |
|---|---|---|
| **A1: AI tick rate based on the player?** | **Yes.** A citizen 134 m away went from `veryLow` to `veryHigh` in the frame the player arrived. | The host must compute tick rate against **every** player. Without a patch, citizens near a client player update about 19× too rarely. |
| **A2: what does culling disable?** | **Citizens:** only renderers. Their colliders, GameObjects and animators stay active at any distance. **Rooms:** far rooms are **completely unloaded**: GameObject inactive, 0 colliders, no walls. | On the host, the client's surroundings may have **no walls, floors or furniture colliders**. Sight raycasts, physics items and doors there would break. The host must keep geometry loaded around every player. |

## Evidence

### Controlled teleport (session 2)

Target: citizen 132 "Latrice Bowden", in room 381, 133.8 m from the player. From `session2_teleport_events.log`:

| | Before (far) | Right after teleport | Right after teleporting back |
|---|---|---|---|
| distance | 133.8 m | 6.1 m | 136.0 m |
| tick rate | veryLow | **veryHigh** | veryHigh (she was chasing the player) |
| visible | no | yes | yes |
| room geometry loaded | no | yes | yes, unloaded about 9 s later |
| room GameObject active | **no** | yes | yes |
| room colliders | **0 / 0** | 100 / 103 | **3 / 236** |
| walls exist | **no** | yes | yes |

Time series (`session2_citizen132.csv`; game time in hours):

| Samples | Situation | Rate | AI update interval |
|---|---|---|---|
| 121–131 | player 134 m away | veryLow | ~0.019 h (~70 game-seconds) |
| 132–140 | player in her room | veryHigh | ~0.001 h (~4 game-seconds), **~19× faster** |
| 141–149 | player gone; she **pursued** the player (137 → 123 m) | veryHigh | ~0.001 h |
| 150+ | chase over | veryLow | back to ~0.019 h |

So the tick rate depends on **player presence or visibility, plus what the citizen is currently doing** (an active chase keeps it high even at distance).

### Street baseline (session 1, no teleport)

`session1_street_baseline.txt`: 6,624 citizen rows with the player on the street.
- Rate isn't a simple distance threshold. `veryHigh` and `high` show up out to 170–200 m, while some citizens within 25 m are `veryLow`.
- Visibility is the strongest single predictor: **82%** of citizens the player can't see are `veryLow`, versus 21% of those the player can see.
- Citizens' colliders, GameObjects and animators: **100% active** at every distance. Renderers only follow visibility.
- Room geometry loaded for only **29–40%** of the rooms citizens were in.

### Bonus observation

Teleporting into the apartment set off **Trespassing** (50-credit fine) right away, and the resident switched the player to **Pursued** and chased them. That's the witness and blame system that needs a per-player version (see design decisions).

## What the host needs (input to the design)

1. **Tick rate:** patch `NewAIController.UpdateTickRate` / `SetDesiredTickRate` so "near/visible to the player" means "near/visible to **any** player". Visibility is camera-based, so the host needs each client's position and view direction.
2. **Room loading:** `GeometryCullingController.UpdateCullingForRoom(currentRoom, …)` loads around one room. The host must load the **union** of the rooms around every player. Two options:
   - run the culling tree once per player and merge the results, or
   - keep a "logically loaded" set (colliders on) for rooms near any player, separate from what the host renders.
3. **Host cost:** two fully loaded areas. Performance still needs measuring (B5).
4. **Clients:** they can keep their own local culling and rendering. They don't simulate AI, so their own room unloading doesn't matter for correctness.

## Reproduce

```
plugins/SodProbe/  dotnet build -c Release   (copies DLL into BepInEx/plugins)
in game: F8 = teleport to a far idle indoor citizen, F9 = back
python3 -I plugins/SodProbe/analyze.py <sodprobe.csv>
```
