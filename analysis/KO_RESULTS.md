# Shadows of Doubt: how the player knockout (KO) works, read from the disassembly

Sources: `sodis.py` and `xref.py` output, plus field offsets from `decompiled/cs/Assembly-CSharp/*.cs`. Many virtual calls go
through IL2CPP slot thunks (`sub_6a50`, `sub_2ee0`, `sub_82a0`, `sub_8180`, with `ecx` = slot). I resolved those slots
from the metadata vtable with the scratch script `vt.py`. The cache lists `Player::Update` as rva 0x4521d0 with length 0x395c.
That length is wrong: the KO code runs on to about 0x456cf6. I read that part by disassembling raw ranges (scratch script
`rawdis.py`).

Player fields used in KO (offsets on `Player`):

| off | field | off | field |
|---|---|---|---|
| 0x7b4 | `playerKOFadeOut` (private) | 0x878 | `playerKOInProgress` (public) |
| 0x7b5 | `paidFines` | 0x7b8 | `KOTime` (float) |
| 0x7bc | `KOTimePassed` (float) | 0x7c0 | `KORecovery` |
| 0x7c1 | `dirtyDeath` | 0x7c8 | `debtPayment` (LoanDebt) |
| 0x820 | `lastDmgFrom` | 0x6a0 | `bed` (Interactable, private) |
| 0x628 | `spendingTimeMode` | 0x629 | `autoTravelActive` |
| 0x6e4 | `transitionActive` | 0x6f8 | `currentTransition` |
| 0x780 | `movementOnTransitionComplete` | 0x5c0 | `fps` (FirstPersonController) |
| 0x888 | `claimedAccidentCover` | 0x1e9 | `isCrouched` |

Other fields used: `InterfaceController.fade` @0x3cc (current fade 0..1), `fadeTarget`-ish @0x3c0, `fadeTime` @0x3c4,
`fadeAudio` @0x3c8; `InteractionController.dialogMode` @0x178, `interactionMode` @0x70, `activeInteractionAction` @0x110,
`spawnedProgressControllers` @0x168, `carryingObject` @0x88, `mugger` @0x1c0, `debtCollector` @0x1c8,
`lockedInInteraction` @0xf8; `SessionData.enableUserPause` @0x1e, `play` @0x20, `currentTimeSpeed` @0xa0,
`gameTimePassedThisFrame` @0x78 (double); `GameplayControls.playerKO` @0x168 (PlayerTransitionPreset),
`koTimePass` @0x41c; `InterfaceControls.fastForwardArrow` @0x1a0, `actionInteractionDisplay` @0x440;
`FirstPersonController.enableMovement` @0x18, `enableLook` @0x19.

---

## 1. `Player.TriggerPlayerKO(Vector3 KODirection, float RollMP, bool forceDirtyDeath)` (rva 0x465830)

Callers: `Player.AddHealth`, `Player.SetHealth`, `Player.OnZeroHealthReached`, `Game.ForcePlayerDirtyDeath`,
`FirstPersonController.PlayerOutOfWorldCheck`.

The steps run in this order:

1. **Guard:** if `playerKOInProgress` is set, it logs "Player KO triggered but it's already active so ignoring..." and returns.
2. **Guard:** if `InteractionController.dialogMode` is set, it logs "...ignoring as we're in dialog mode..." and returns.
3. If `BioScreenController.isOpen` is set, it calls `SetInventoryOpen(false, false, true)` to close the inventory.
4. Log "Player: Player KO!".
5. If `spendingTimeMode` is set, it calls **`SetSpendingTimeMode(false)`**. KO turns spending-time mode OFF, never on.
6. If `autoTravelActive` is set, it calls `EndAutoTravel()`.
7. If `InteractionController.carryingObject` is not null, it calls `carryingObject.DropThis(false)`. This drops the
   carried physics object.
8. If `Player.currentBuilding` is not null, it calls `StatusController.SetDetainedInBuilding(currentBuilding, false)`.
9. `AchievementsController.notAScratchFlag = true`.
10. **State:** `playerKOInProgress = true`; `KORecovery = false` and `dirtyDeath = false` (one 16-bit store at 0x7c0);
    `paidFines = false`; `debtPayment = null`.
11. If `FirstPersonItemController.smokingActive != 0`, it calls `ForceCanelSmoking()`.
12. `InteractionController.UpdateNearbyInteractables()`.
13. `dirtyDeath = true` if the mugger is `lastDmgFrom`, or if the debt collector is `lastDmgFrom`. In the debt-collector
    case, when the collector's `job.employer` is set, it also sets `debtPayment = GameplayController.debt.Find(...)`.
    It also sets `dirtyDeath = true` if `forceDirtyDeath` is true. Then `lastDmgFrom = null`.
14. `SessionData.enableUserPause = false`. This is a raw field write; the game later undoes it with `SetEnablePause(true)`.
    If `!SessionData.play`, it calls `SessionData.ResumeGame()`.
15. `Actor.SetInteracting(null)`.
16. **`ReturnFromTransform(true, true)`** ends any running transition (bed, chair, hiding and so on) immediately.
17. **`FirstPersonItemController.ForceHolster()`** holsters the held item.
18. **`TransformPlayerController(GameplayControls.playerKO, exit: null, interactable: null, lookAt: null, false, false, 0, useAdditionalLookMultiplier: true, KODirection, RollMP, writeReturnPosition: true)`**
    plays the "fall down" camera transition. Inside it (0x45f69a and on): if `!preset.retainMovementControl`, it calls
    `EnablePlayerMovement(false)`, sets `fps.m_StickToGroundForce = 0` and `fps.m_GravityMultiplier = 0` ("Disable
    gravity"), and calls `EnableCharacterController(false)`. It calls `EnablePlayerMouseLook(false, true)` **only when
    `lookAt != null`**, and for KO it is null. It sets `transitionActive = true` and `transitionTime = 0`, and copies
    `movementOnTransitionComplete` and `restoreHolsterOnTransitionComplete` from the preset.
    While `transitionActive` is set, `FirstPersonController.Update`/`RotateView` skip `MouseLook.LookRotation`, so the
    mouse does nothing during the fall.
19. `playerKOFadeOut = false`; `KOTime = GameplayControls.koTimePass`; `KOTimePassed = 0`.
20. For every `NewBuilding` in `CityBuildings.buildingDirectory`: `wantedInBuilding = 0`.
21. **`InterfaceController.Fade(1.0, 2.0, false)`** fades to black over 2 s. `Fade` stores target, time and audio, then
    restarts the `FadeGame` coroutine.
22. `SetActionDisable("Get In", true)`, then "Get Up", "Enter" and "Sleep..." with `true`.
23. `InteractionController.UpdateInteractionText()`.

What it does not touch: time speed (no `SetTimeSpeed` here), audio (Fade is called with `fadeAudio = false`),
crouch (`ReturnFromTransform` only picks the standing or crouched height from `isCrouched`), and
`lockedInInteraction` (`ReturnFromTransform` calls `InteractionController.CancelInteractionAction`).

### `FadeGame` coroutine (`InterfaceController.<FadeGame>d__219.MoveNext` 0xa95da0)

- It activates the overlay GameObject.
- It steps `fade` (0x3cc) toward the target by **`Time.deltaTime / fadeTime`**. This is scaled time, so it stalls
  when `timeScale` is 0.
- Each step it calls `fadeOverlay.CanvasRenderer.SetAlpha(curve.Evaluate(fade))` and `UpdateDOF`.
- When it reaches a target of 0, it deactivates the overlay and calls `InteractionController.InteractionRaycastCheck`.
- It sets a flag on SessionData (offset 0x18) while fading.

`Player.Update` reads **`InterfaceController.fade`**, the float. It never reads the overlay's alpha.

---

## 2. Recovery: the KO branch of `Player.Update` (starts at 0x455090)

Earlier in Update (0x452715): if `!SessionData.play` and `playerKOInProgress` and `!MainMenuController.mainMenuActive`,
it calls `SessionData.ResumeGame()`. The game cannot stay paused during a KO.

Pseudocode, which follows the disassembly exactly:

```
if (playerKOInProgress) {
  if (InterfaceController.fade >= 1 && !playerKOFadeOut) {          // ---- A: first frame fully black
     foreach a in witnessesToIllegalActivity: a.ai.CancelCombat()
     citizensArrestActive = false
     if (KORecovery) {                                               // second blackout after paying fees
        ResetHealthToMaximum(); AddBlackedOut(-1); AddBrokenLeg(-1); AddBleeding(-1); AddBruised(-1);
        AddHeadache(-1); AddBlackEye(-1); AddSick(-1); AddPoisoned(-1,null); AddBlinded(-1); isCrunchingDatabase=false
     } else {
        ReturnFromTransform(true, true)    // ends the KO fall transition -> OnTransitionComplete (see below)
        if (!paidFines) {
           if (!dirtyDeath) StatusController.PayActiveFines()
           else if (debtPayment == null) AddMoney(-max(200, money*0.5), "Mugged")
           else DebtPayment / ShortDebtPayment(...)
           foreach a in persuedBy: a.RemoveFromSeesIllegal(player, 0)
           paidFines = true
        }
        // choose where to wake up
        if (!dirtyDeath) { bed = home bed (with upgrade 9) or a random GameplayController.hospitalBeds entry }
        else             { bed = null; teleport to Human.FindSafeTeleport(random room from CityData.roomDirectory) }
        Teleport(bed.node ...)                                    // vslot 7
        if (bed) bed.OnInteraction(<lie-down action>, player, true, 0)   // puts the player INTO the bed
        SetHealth(maxHealth * (dirtyDeath ? 0.25 : 0.5)); AddBlackedOut(-1); AddBrokenLeg(-1);
        AddBleeding(-1); AddPoisoned(-1,null); AddBlinded(-1)
     }
     ClearSeesIllegal(); InteractionController.UpdateInteractionText()
     playerKOFadeOut = true; log "Player: Teleport to bed after KO..."
  }
  else if (playerKOFadeOut) {                                        // (any fade value)
     if (KOTime > KOTimePassed) {                                    // ---- B: waiting
        if (SessionData.currentTimeSpeed != veryFast(3)) {           // "first frame" block, see pitfalls
           SessionData.SetTimeSpeed(veryFast)                        // <<< THE KO TIME SPEED-UP
           InterfaceControls.fastForwardArrow.gameObject.SetActive(true)
           if (!Game.permaDeath) InteractionController.SetInteractionAction(0, KOTime, 1.0, "ko", ...)  // progress bar
           if (!KORecovery) { if (!permaDeath) { NewGameMessage(Strings.Get("ui.gamemessage","ko")); TutorialTrigger("ko") }
                              else { ExecuteGameOverDisplay(); maybe DeleteCurrentSaveGame() } }
        }
        if (!SessionData.play) ResumeGame()
        KOTimePassed += (float)SessionData.gameTimePassedThisFrame   // GAME hours, not real time
        foreach p in InteractionController.spawnedProgressControllers: p.SetAmount(KOTimePassed)
        if (permaDeath && KOTimePassed >= KOTime*0.5) -> load "Main" scene (game over)
        else if (KOTimePassed >= KOTime) {                           // ---- C: end of wait (runs ONCE)
           SessionData.SetTimeSpeed(normal)                          // <<< speed-up reverted here
           fastForwardArrow.gameObject.SetActive(false)
           InterfaceController.Fade(0.0, 6.0)                        // 6 s fade back in
           destroy + remove every spawnedProgressController
           InterfaceControls.actionInteractionDisplay.gameObject.SetActive(false)
           InteractionController.CompleteInteractionAction()         // clears activeInteractionAction
           if (KORecovery || upgrade6 >= 1 || (upgrade8 > 0 && !claimedAccidentCover)) {
              ResetHealthToMaximum(); AddNourishment(1); AddHydration(1); AddHygiene(1); AddEnergy(1);
              AddBlackedOut/BrokenLeg/Bleeding/Bruised/Headache/BlackEye/Sick/StarchAddiction/Drunk/Poisoned/Blinded(-1)
           } else {
              if (!dirtyDeath) StatusController.SetDetainedInBuilding(currentBuilding, true)
              AddNumb(0.75); 25%: AddHeadache(0.5); 25%: AddBruised(0.5)
           }
        }
     } else if (InterfaceController.fade <= 0) {                     // ---- D: wake up
        log "Player: Wake up from KO"
        playerKOInProgress = false
        InteractionController.UpdateNearbyInteractables()
        SessionData.SetEnablePause(true)
        // decides KORecovery: story mission before chapter 31 -> 1; dirtyDeath -> 0;
        //   upgrade8 && !claimedAccidentCover -> KORecovery=1, claimedAccidentCover=1, AddMoney(.., "Accident cover");
        //   upgrade6 >= 1 -> 1
        if (!KORecovery) { if (bed) { bed.SetCustomState2(true, player, false,false,false);
                                       InteractionController.SetDialog(true, bed) } }   // hospital "pay fees" dialog
        else { ClearAllDisabledActions(); KORecovery = false }
        InterfaceController.NewGameMessage(time + date + building name)
        SetActionDisable("Get In"/"Get Up"/"Enter"/"Sleep"/"Sleep...", false)
        InteractionController.UpdateInteractionText()
     }
  }
}
```

**Clock.** `KOTimePassed` counts **game hours**: it adds `SessionData.gameTimePassedThisFrame`, a double cast to float.
`KOTime` comes from `GameplayControls.koTimePass`, which is in game hours too. So the real-time length of a KO depends
entirely on the time speed. It is short only because branch B forces `veryFast`. If time is paused (gameTimePassed is 0),
the counter does not move.

**`TriggerPlayerRecovery()`** (0x466b80). Its only caller is `DialogController.PayMedicalFees`.

- **Verified:** the first instruction after the static init is `cmp byte [rbx+0x878],0 ; jne ret`. It returns at once
  whenever `playerKOInProgress` is set.
- It is not an "end KO" function. It **starts** a second, shorter blackout: log "Player Recovery!",
  `playerKOInProgress = true`, `UpdateNearbyInteractables()`, `KORecovery = true`, `enableUserPause = false`,
  `ResumeGame` if paused, `SetInteracting(null)`, `ForceHolster()`, `playerKOFadeOut = false`, `KOTime = 0.5`,
  `KOTimePassed = 0` (one 64-bit store), `Fade(1.0, 2.0)`, `SetActionDisable(Get In/Get Up/Enter/Sleep..., true)` and
  `UpdateInteractionText()`.
- It calls no transition.
- The rest runs through `Player.Update` with `KORecovery = 1`: at black, full heal and status reset; then 0.5 game hours
  at veryFast; then fade-in; then wake with `ClearAllDisabledActions()`.
- If the mod called it **after** a KO had ended, it would trigger a fresh black-out with a heal.

**What the game's recovery restores, and what it leaves set:**

- **Restored:**
  - At A: `ReturnFromTransform` → `OnTransitionComplete` re-enables the CharacterController and gravity
    (`m_StickToGroundForce = 7`, `m_GravityMultiplier = 2`), calls `fps.InitialiseController`, and calls
    `EnablePlayerMovement(movementOnTransitionComplete)` and `EnablePlayerMouseLook(true, false)`. The last two run
    **only if `!InteractionController.interactionMode`**.
  - At C: time speed (back to normal), fast-forward arrow, fade, progress bar, `activeInteractionAction`.
  - At D: `playerKOInProgress`, pause enabling, and the disabled actions.
- **Left set or changed:**
  - `playerKOFadeOut` stays true; it is reset on the next KO.
  - `KOTime`, `KOTimePassed`, `dirtyDeath`, `paidFines` and `debtPayment` keep their values.
  - `lastDmgFrom` stays null, every building's `wantedInBuilding` stays 0, and `notAScratchFlag` stays true.
  - The detained-in-building status is re-set at C.
  - Money, fines and debt have been paid.
  - The player has been **teleported and placed into a bed** (hospital or home), or into a random room for a dirty death.
  - On a normal wake the game opens a **dialog with the bed** (`SetDialog(true, bed)`; "pay medical fees" → `TriggerPlayerRecovery`).
    The player then stays in the bed or dialog state until they choose to get up.

---

## 3. Where time is sped up

- `TriggerPlayerKO` never speeds time up. If anything it **turns spending-time mode off**.
- `SetSpendingTimeMode` itself returns at once while `playerKOInProgress` is set (0x472dd3), so sleep or wait can't be
  entered during a KO.
- **Speed-up:** `Player.Update` branch B, at 0x455c95: `SessionData.SetTimeSpeed(3 /*veryFast*/)`. It is guarded by
  `currentTimeSpeed != veryFast` and also shows `fastForwardArrow`.
- **Revert:** `Player.Update` branch C, at 0x4561b3: `SessionData.SetTimeSpeed(1 /*normal*/)`. It runs once, on the frame
  `KOTimePassed` first reaches `KOTime` inside branch B.
- The mod's comment in `Net/RestSync.cs` ("…knocked out all go through `SetSpendingTimeMode(true)`…") is wrong for KO.
  The `HostRest` postfix never sees a KO.

---

## 4. Ending a KO early: what to reset, and the pitfalls

### Why 0.9.35 (fill `KOTimePassed = KOTime` and clear the overlay alpha) broke

1. **Filling the counter skips branch C.**
   - The check `KOTime > KOTimePassed` comes before the increment, and C sits inside B, after the increment.
   - With `KOTimePassed >= KOTime` already set, Update goes straight to the wake check D.
   - So none of C runs: no `SetTimeSpeed(normal)`, no **`Fade(0, 6)`**, no `CompleteInteractionAction`, no
     progress-bar teardown, no `actionInteractionDisplay`/`fastForwardArrow` hide.
2. **D needs `InterfaceController.fade <= 0`.**
   - Only C issues `Fade(0)`. `fade` therefore stays at 1.0, and clearing `fadeOverlay`'s CanvasRenderer alpha does not
     change `fade`.
   - So the player **never wakes**: `playerKOInProgress` stays true for good.
   - As a result: `TriggerPlayerKO`/`TriggerPlayerRecovery`/`SetSpendingTimeMode` are all ignored, Update keeps forcing
     `ResumeGame`, and the mod's "fill again every 10 s" does nothing.
   - **KoView's `LocalKnockedOut` stays true, so the partner camera (depth `main+10`) keeps drawing over first person.**
     Meanwhile the real first-person camera has already had mouse look re-enabled at A. To the user this looks exactly
     like "mouse look broke": the mouse turns an invisible camera.
3. **Stale interaction action.** Without `CompleteInteractionAction`, `activeInteractionAction` stays true and
   `SetInteractionAction` returns early from then on. Later timed actions (lockpick, search and so on) break, and the "ko"
   bar and `actionInteractionDisplay` stay on screen.
4. **Other effects that can look like broken look or movement:**
   - **During the fall** (before A), `transitionActive` is true and FPC skips `LookRotation`. Movement and the
     CharacterController are off and gravity is 0.
     Clearing `playerKOInProgress` without `ReturnFromTransform(true, true)` leaves all of that in place.
   - `OnTransitionComplete` re-enables look and movement only when `!InteractionController.interactionMode`.
   - After a normal wake the player is **lying in a bed with a bed dialog open** (`SetDialog`) until "Get up".
     That is normal game behaviour, but it locks control.
5. **KoView clones `Camera.main` with `Instantiate`.** All Awake/OnEnable methods on the clone run *before* KoView
   destroys its MonoBehaviours. If `CameraController` is on that GameObject, its Highlander `Awake` (0x5d9540) either
   destroys the clone's GameObject (when `Instance` already exists) or sets `CameraController.Instance = clone`. In the
   second case, mouse look (`MouseLook.LookRotation` uses `CameraController.Instance.transform`) and `ReturnFromTransform`
   would use a destroyed object. Any other camera script that registers itself somewhere has the same risk.
   I did not verify in-game which components sit on the main camera; check for this.
6. **Time-speed fight in co-op.**
   - Block B's "first frame" body runs **every frame** while `currentTimeSpeed != veryFast`.
   - On a client, `TimeSync.BlockSpeed` swallows `SetTimeSpeed`. On the host, `RestSync.HostTick` forces `normal` back.
   - Every frame this repeats `fastForwardArrow.SetActive(true)`, a Log, `SetInteractionAction` (a no-op after the first
     call), **`NewGameMessage("ko")`** and **`TutorialTrigger("ko")`**. On the host the game also calls
     `SetTimeSpeed(veryFast)` each frame and the mod sets it back.
   - Fixes:
     - Harmony-guard this: for example, skip `NewGameMessage`/`TutorialTrigger` during a KO.
     - Or let the host and client report `veryFast` to this check.
     - Or accept the spam.
7. `FadeGame` uses scaled `Time.deltaTime`. If the world is paused (`timeScale` 0) the fade never finishes, and D waits
   on it. Update force-resumes during a KO, but the mod's PauseSync could fight that.

### Recommended early end (let the game run its own C and D)

When `playerKOInProgress && playerKOFadeOut && KOTime > KOTimePassed`:

```
p.KOTimePassed = Mathf.Max(p.KOTimePassed, p.KOTime - 1e-5f);   // just BELOW KOTime
```

Repeat this every frame until C has run. You can detect that by `!InteractionController.activeInteractionAction`,
`!fastForwardArrow.activeSelf`, or "the fade target went to 0".

- On the next unpaused frame the increment crosses `KOTime` inside B, and the game runs all of C:
  speed back to normal (blocked on clients, which is fine), arrow off, `Fade(0, 6)`, bars destroyed,
  `CompleteInteractionAction`, status effects.
- D follows once `fade` reaches 0, about 6 s later. To shorten that, call `InterfaceController.Instance.Fade(0f, 0.5f)`
  right after C.
- Stop KoView and `ClearBlackout` once C has run. Otherwise the alpha clearing hides the fade-in, which does no harm,
  but KoView must not depend on `playerKOInProgress` alone.
- Never fill the counter while `!playerKOFadeOut`. A has not happened yet (no teleport or heal), and after A the counter
  would already be full, which skips C again.

### Manual teardown, if you can't wait for the game

Mirror C and then D, in this order:

1. If `!playerKOFadeOut` (still in the fall), call `ReturnFromTransform(true, true)`. It needs `!interactionMode`
   to restore look and movement; otherwise also call `EnablePlayerMouseLook(true)` and `EnablePlayerMovement(true)`.
   Skipping A also skips the fines/teleport/heal, which may be what co-op wants.
2. `SessionData.SetTimeSpeed(normal)` on the host or when allowed; `InterfaceControls.Instance.fastForwardArrow.gameObject.SetActive(false)`.
3. `InterfaceController.Instance.Fade(0f, t)`. Use the method, not the alpha: it has to drive `fade` to 0, deactivate the
   overlay and call `InteractionRaycastCheck`/`UpdateDOF`.
4. Destroy every `InteractionController.spawnedProgressControllers[i].gameObject` and clear the list;
   `InterfaceControls.Instance.actionInteractionDisplay.gameObject.SetActive(false)`;
   `InteractionController.Instance.CompleteInteractionAction()`. Call it **while `playerKOInProgress` is still true**: it
   takes the KO path (0x73210e → clears `activeInteractionAction`, calls `SetIllegalActionActive(false)` and the callback).
5. Optional status effects as in C.
6. `playerKOInProgress = false`; `InteractionController.UpdateNearbyInteractables()`; `SessionData.SetEnablePause(true)`.
7. `ClearAllDisabledActions()` (or `SetActionDisable(x, false)` for "Get In", "Get Up", "Enter", "Sleep", "Sleep...");
   `KORecovery = false`.
8. `InteractionController.UpdateInteractionText()`.
9. Optional hygiene: `playerKOFadeOut = false`, `KOTimePassed = 0`.

Check afterwards that `fps.enableLook`, `fps.enableMovement`, `transitionActive == false`, the CharacterController, and
the cursor lock (`InputController.SetCursorLock(true)`, which `EnablePlayerMouseLook(true)` does) are all restored. The
game still has to finish the fade in step 3 for the screen to clear.
