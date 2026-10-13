# B2: where runtime object IDs come from

Static analysis, GameAssembly.dll v42.04, night of 2026-10-12. Tools: `analysis/tools/sodis.py`, `xref.py`.

## Answer

Interactables get their `id` (`Interactable.id`, field 0x10) from one of three schemes:

| Scheme | Code | Formula | Used by |
|---|---|---|---|
| **World** | `Interactable::AssignIDWorld` (0x940E20) | `id = Interactable.worldAssignID` (a **static counter**). Skips ids already in `CityData.savableInteractableDictionary` or `CityData.metaObjectDictionary`, then `worldAssignID = id + 1`. | `CreateWorldInteractable`, `CreateDoorParentedInteractable`, `CreateFurnitureSpawnedInteractable`, **`CreateFingerprintInteractable`**, **`CreateFootprintInteractable`** |
| Room-based | `InteractableCreator::GetRoomBasedInteractableID` (0x98E120) | `room.roomID * 1000 + room.interactableAssignID` | generation only: lights, books, furniture-spawned (thread-safe path) |
| Furniture-based | `Interactable::AssignFurnitureBasedID` (0x9413C0) | `-(furniture.id * 30) - furniture.integratedIDAssign++` (negative) | no direct callers left (inlined into generation) |

The room and furniture schemes are deterministic from the city (the determinism test matched all 34,426 ids). **The world counter is the problem.** `CreateWorldInteractable` alone has **56 callers**, and many of them run during play:

- player actions: `FirstPersonItemController` (throw coin/grenade, place tracker/grenade, take picture, consumables), `Player.OnCompleteDrink`, printouts from computer apps (`DatabaseApp`, `SalesRecordsApp`, `SurveillanceApp`, `VMailApp`), `DialogController.BuyBriefcase`, `InteractionController.OnPlayerLookAtChange`
- the murder director: `MurderController.ExecuteSniperShot`, `SpawnPlayerTaunt`, `Murder.SetMurderState`, `PlaceCallingCard`, `GenerateGraffiti`
- citizens and world: `NewAIAction.DropItemAtEnd`, `Human.SpawnInventoryItems`, `NewBuilding.TriggerNewLostAndFound`, `NewDoor.SetJammed`, `Toolbox.Shoot` / bullet holes, `BreakableWindowController.AddBulletHole`
- forensics: fingerprints (`PrintController.PrintConfirmed`, `BioScreenController.OnScanComplete`) and footprints (`FootprintController.PrintConfirmed`)

So two machines **will** hand out the same world id to different objects as soon as their players do different things.

## Recommendation

1. **The host owns everything the simulation creates** (murder director, citizens, forensics). Clients don't simulate these anyway in the host-authoritative design. The host broadcasts `Spawn(id, preset, position, owner, …)`, and clients create the object with the host's id. Implementation: a prefix on `Interactable.AssignIDWorld` that uses a "forced next id" when one is set.
2. **Player-created objects** (thrown coins, photos, printouts, trackers) are created **on the host** from a client request, which gives one id space and one authority. Fallback if the round trip feels laggy: disjoint id ranges per machine (host keeps the game's counter, client *k* allocates from `0x40000000 + k * 0x01000000`). Then a client can create locally, and the host accepts the id as is.
3. Mid-game join (B1) has to carry `Interactable.worldAssignID` along with the save state.
