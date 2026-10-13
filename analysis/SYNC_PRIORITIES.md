# What still needs syncing: priority list (2026-10-13)

**Status at 0.7.0:** P0 1–7 and P1 8–18 are implemented but untested in game (see plugins/SodCoop/README.md, "0.7.0"). Not done: P2 and P3. Pending decisions: see the end of this file (0.7.0 defaults to paying every player the full reward).

Source: a scan of the decompiled game in four areas (player actions and crime, citizen behaviour, world, progression and UI), checked against the mod's code at 0.6.3. None of it is tested in game. "Breakage" is inferred from what the code does and doesn't sync. Effort: S = hours, M = about a day, L = several days.

Already synced, for reference: player poses (with look direction and held item), citizens near clients (posture and KO), doors, items (spawn/move/inventory/delete, physics resting places, switches flipped by players), time and the pause rule, the case and case board, evidence and facts, murders and deaths, side jobs, prints, conversation locking, damage in both directions, mid-game join, map markers.

## P0: the co-op loop breaks without these

| # | What | Breakage now | Key game hooks | Effort |
|---|---|---|---|---|
| 1 | **Objective completion through the host** | Triggers are evaluated per machine against the *local* player. A client reaching the address completes only its own copy and runs `OnCompleteAction` (job phase, hand-in) locally, so the worlds drift apart. | `Objective.Complete/SetProgress/Cancel`, `ObjectiveTrigger.Trigger`; client sends "objective triggered", host completes and broadcasts, clients skip `OnCompleteAction` | M |
| 2 | **Case hand-in and resolution through the host** | A client hand-in resolves only its own copy. `MurderController.OnCaseSolved` runs on the client while the host's killer carries on. The host's result never reaches the client. | `ActionController.HandInCase`, `ResolveController.Submit*`, `Case.Resolve/SetStatus`, `MurderController.OnCaseSolved`, `RevealResolveController` | M |
| 3 | **Citizens switching lights, TVs and appliances near clients** | `ItemSync` only sends switches the local player flipped. Rooms near a client show the client's own simulation (lights on in an empty flat). | send host switch changes made by citizens within 60 m of a client, the same way `DoorSync` does doors; suppress the client's local switching there | S |
| 4 | **Client's local AI hunting the client player** | Citizens beyond 40 m run the client's own AI. They can spot, chase or report the client and call police the host doesn't know about. When the host takes them over they drop the chase. | client: block `Actor.SightingCheck`, `NewAIController.SetPersue/Investigate`, `GameplayController.CallEnforcers` for citizens the host doesn't drive | S–M |
| 5 | **Wanted/fines/pursuit back to the client, and keys/guest passes on the host** | The host's citizens chase the client, but the client gets no "wanted"/fine/pursuit HUD. The host ignores the client's keyring and guest passes, so a client with a key counts as an intruder. | `StatusController.AddFineRecord/SetWantedInBuilding`, `NewBuilding.wanted`, `Player.AddToKeyring`, `GameplayController.AddGuestPass` | M |
| 6 | **Alarms, cameras, sentry guns, breakers (host-authoritative)** | Each security system only "sees" its own local player. Alarms, lockdowns and sentry fire happen on one side only, and a breaker turned off by one player still powers the other's cameras. | `NewBuilding/NewAddress.SetAlarm/SetTargetMode`, `SecuritySystem.SetActive/OnInvestigate`, `BreakerController`, `ActionController.Sabotage` | M |
| 7 | **Where the murder victim lies** | `DeathMsg` has no position. A victim far from the client dies where the client's own AI had put them, which can be another room or building than the host's crime scene. | add position and rotation to `DeathMsg`, teleport before `Murder` | S |

## P1: very visible, mostly cheap

| # | What | Breakage now | Key game hooks | Effort |
|---|---|---|---|---|
| 8 | **Citizen attack animations** | Damage arrives with no swing, no weapon and no shot. | `CitizenAnimationController.AttackTrigger/ThrowTrigger/BlockTrigger`, `NewAIController.currentWeaponPreset` | S–M |
| 9 | **Speech bubbles and shouts** | Citizens the host drives never speak on the client: no "get out!", no warnings or screams. | host: hook `SpeechController.Speak/TriggerBark` for citizens near clients; client replays with its own `Speak` | M |
| 10 | **Weather** | Each machine rolls its own weather. Within minutes one player has rain and the other a clear night. | clients skip `SessionData.ExecuteWeatherChange`; host sends `SetWeather` values with TimeSync | S |
| 11 | **Broken windows** | Smashed on one side, intact (and solid) on the other. | `BreakableWindowController.BreakWindow/SetBroken`, keyed by position like the save does | S |
| 12 | **Notifications about the partner** | Nothing says "X found evidence", "X took a job", "X was knocked out", "case solved". | `InterfaceController.NewGameMessage` on the existing receive paths | S |
| 13 | **"Spotted"/awareness for the client** | The client gets no awareness icons, so stealth plays blind. | host sends `reactionState`/`alertness`/target player; client calls `InterfaceController.AddAwarenessIcon` | M |
| 14 | **Citizen props and outfits** | Smoking or reading with an empty hand; a coat worn in bed, pyjamas outside. | `NewAIController.UpdateHeldItems`, `CitizenOutfitController.SetCurrentOutfit` | S |
| 15 | **Hiding and vents** | The host's citizens still spot a client hiding in a closet or crawling through a vent. | `Player.SetHiding`/`OnCrawlIntoVent` → flags in the pose, set `isHiding` / skip perception | S |
| 16 | **Partner's flashlight** | It doesn't show on the remote body. | a `FirstPersonItemController.flashlight` bit in the pose, a spot light on the body | S |
| 17 | **Door wedges/jams, barging, police tape** | Opening and locking are synced, but a jammed door opens freely on the other side and tape shows on one side only. | `NewDoor.SetJammed/Barge/SetPoliceTape` | S |
| 18 | **Citizens popping in at the 40 m edge** | Far citizens run the client's diverged AI, so they jump when the host takes them over and U-turn when released. | wider radius outdoors, a hysteresis band, re-plan the AI on release | M |

## P2: shared systems, medium effort

| # | What | Breakage now | Effort |
|---|---|---|---|
| 19 | **Elevators** | Each machine runs its own car. A player rides a car the other sees elsewhere and floats in the shaft. Host-authoritative: clients send button presses, host streams floor/position/doors. | M |
| 20 | **Telephones** | Ringing, answering and call logs happen on one side only. That matters for side jobs (calling the poster). | M |
| 21 | **Blood, wounds, spatter** | Fight and crime-scene blood exists on one side only, and blood evidence is missing for the client. | M |
| 22 | **Newspapers; trash cleanup** | Headlines can differ. The client's own cleanup may delete items the host still has. Host generates the paper; clients turn `CleanupController.TrashUpdate` off. | S |
| 23 | **Dialog outcomes (bribes, persuasion, guest passes, mugging)** | Effects stay on the machine that ran the dialog. | M |
| 24 | **Handcuffs, arrests, crime scenes, gadgets (codebreaker, flash bomb)** | A citizen the client cuffs or stuns keeps walking on the host. | M |
| 25 | **KO/wake-up time skip; sleeping together** | A KO may skip time, which clients are blocked from doing. Sleep is refused for clients without telling them in game. Option: "both in bed → host skips time". | M |

## P3: persistence and large features

| # | What | Effort |
|---|---|---|
| 26 | **Per-player progress**: the client's money, inventory, upgrades, social credit and apartment are lost at session end, because the host save only holds the host. Also check: the join snapshot probably gives the client the host's upgrades, lockpicks and passcodes. Fix: a per-player profile kept by the host, keyed by SteamID. | M–L |
| 27 | **Apartments, furniture, decor, hotel rooms** (`PlayerApartmentController`, `FurnitureLocation` isn't an interactable) | L (hotel: S) |
| 28 | **Witness memory**: sightings of a remote player are stored against the borrowed citizen; "have you seen X?" answers come from the client's own memory | L |
| 29 | **Dialog consistency**: random answers and "already told you" state are per machine | L |
| 30 | **Story mode**: chapters are scripted around one player. Short term: refuse to host outside sandbox (S); real support is L+ | S / L |
| 31 | Smaller items: vmail/message-thread ids, TV schedule, known passcodes, achievements | S each |

## Decisions needed (design, not code)

- **Rewards:** split between players, or does everyone get the full reward? Is social credit shared (host's) or per player?
- **Knowledge:** should keypad codes and passwords one player finds become known to both?
- **Story mode:** block it for now (recommended) or plan for it?
- **Sleeping/waiting:** a vote ("both in bed") or a host-only decision?

## Suggested order

1. Quick wins for immersion and correctness: 3, 7, 10, 11, 12, 14, 15, 16, 17 (all S).
2. Core correctness: 1, 2 (needs the reward decision), 4.
3. Stealth/crime loop: 5, 6, 8, 9, 13.
4. Shared systems: 19, 20, 21, 22, then the rest.
