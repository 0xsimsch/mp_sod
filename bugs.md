- new murder case not shown for client
  - 0.9.53 (upstream): the title is the game's own InterfaceController.NewMurderCaseDisplay(), which runs in the host's
    MurderController.OnVictimDiscovery - a method the client skips (CaseIds forwards the discovery to the host),
    so only the case record/objectives reached it. The case record now carries a "fresh" flag (protocol v45): a
    client shows the title when a fresh murder case lands that it doesn't have yet. The welcome batch is sent
    not fresh, so joining players don't see it for already-running cases. Untested in game.
- animations for client for some persons seems to flicker
- after a certain distance threshold is passed the other players body anomations stop render and the head rotates lol
  - 0.9.58: the game's citizen LOD (Human.UpdateLODs) takes distant bodies onto its static LOD meshes and stops
    the animator - wrong for a puppet we drive: it stays clearly visible to the partner. LateApply then multiplied
    its neck offset onto the frozen bone every frame (the head spin). BodyLodPatch skips the LOD pass for possessed
    bodies, RemotePlayerBody.KeepAnimated switches a disabled animator back on in the 0.5 s guard, and LateApply
    only writes the neck while the animator runs. Untested.
  - tested 0.9.58: still happening most of the time, and no LOD switch is visible at that distance - so the freeze
    is not (only) Human.UpdateLODs. The head keeps spinning means our neck write ran, so the animator was still
    "enabled" - something else stopped its output.
  - 0.9.60: KeepAnimated now pins the puppet's animator so no output-stop survives the 0.5 s guard:
    cullingMode AlwaysAnimate (Unity stops animating when it deems the renderers invisible), updateMode Normal
    (AnimatePhysics runs on the throttled physics tick), speed back up unless the game flagged it paused, and
    enabled back on. New CheckFrozenPose watchdog: an arm bone still for 1 s while the body walks logs the whole
    animator state (FREEZE line in sodcoop.log) and rebinds via the game's ForceUpdateAnimationSate.
- on the case board for the client the needle indicator is optically in unpinned state but in reality its pinned after a new pin was created.

* - when lying in bed one floats above the bed and rotating the mouse still rotates the person horizontally. the height offset is like 2x the height of the bed.
  - 0.9.54: sender sent the capsule bottom (already on the mattress) + the in-bed animation lifts the body again; yaw was
    the mouse-turned body yaw. Now: usage point position, yaw from its look-at point (else frozen at sit-down).
  - tested 0.9.55: lying still floats above the bed (hospital bed; its usage point is 0.35 m above the floor).
  - 0.9.56: BEDDIAG log lines measure where the game puts real citizens in beds vs. the partner's body (fix pending data).
  - tested 0.9.58: needs ~half a body length toward the foot end. BEDDIAG: citizens in bed stand exactly on the usage
    point (0.11 m above the floor); the partner was 0.7 m up and 1.3 m off it - once the lie-down moved the capsule onto
    the mattress, the per-pose 1 m check rejected the usage point and sent the capsule. 0.9.59: the seat pose is decided
    once on entry (still standing by the furniture) and kept. Untested.

* - when sitting down. sitting animation is played and then it turns into laying in a floating position above the chair
  - 0.9.54: sit/lie was decided by eye height above the capsule, which is on the seat once seated (<0.7 m → "lying").
    Now the transition name decides first (Bed… → lie, Sit…/Chair… → sit). Untested.

- sitting down crouched still has the player stand and the character is shrunk
  - 0.9.56: the sender ignored the seat while crouched (isCrouched stays set when sitting from a crouch) and the
    receiver kept the 70 % crouch scale once seated. Now the seat transition wins and seated bodies are full size. Untested.
- the client spawned with a person that was constantly in a hunched position, also while moving it didnt have a walking animation
  - 0.9.57: the borrowed citizen's own state wasn't cleared: UpdateMovementSpeed feeds the animator drunk ← Human.drunk
    and sobering up runs in the disabled AI, so a drunk donor stays hunched/stumbling. Now drunk is zeroed (restored on
    release), and paused animation / cuffs / ragdoll / combat are reset; "donor animation state" logs what it had. Untested.

- player needs half a playerdistance of offset to the feet end of the bed

unclear:

- the pins can get into a state that doesnt allow for unpinning

future:

- remove new evidence found messages for other players
