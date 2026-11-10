- new murder case not shown for client
  - 0.9.53 (upstream): the title is the game's own InterfaceController.NewMurderCaseDisplay(), which runs in the host's
    MurderController.OnVictimDiscovery - a method the client skips (CaseIds forwards the discovery to the host),
    so only the case record/objectives reached it. The case record now carries a "fresh" flag (protocol v45): a
    client shows the title when a fresh murder case lands that it doesn't have yet. The welcome batch is sent
    not fresh, so joining players don't see it for already-running cases. Untested in game.
- animations for client for some persons seems to flicker
- after a certain distance threshold is passed the other players body anomations stop render and the head rotates lol
- on the case board for the client the needle indicator is optically in unpinned state but in reality its pinned after a new pin was created.

* - when lying in bed one floats above the bed and rotating the mouse still rotates the person horizontally. the height offset is like 2x the height of the bed.
  - 0.9.54: sender sent the capsule bottom (already on the mattress) + the in-bed animation lifts the body again; yaw was
    the mouse-turned body yaw. Now: usage point position, yaw from its look-at point (else frozen at sit-down).
  - tested 0.9.55: lying still floats above the bed (hospital bed; its usage point is 0.35 m above the floor).
  - 0.9.56: BEDDIAG log lines measure where the game puts real citizens in beds vs. the partner's body (fix pending data).

* - when sitting down. sitting animation is played and then it turns into laying in a floating position above the chair
  - 0.9.54: sit/lie was decided by eye height above the capsule, which is on the seat once seated (<0.7 m → "lying").
    Now the transition name decides first (Bed… → lie, Sit…/Chair… → sit). Untested.

- sitting down crouched still has the player stand and the character is shrunk
  - 0.9.56: the sender ignored the seat while crouched (isCrouched stays set when sitting from a crouch) and the
    receiver kept the 70 % crouch scale once seated. Now the seat transition wins and seated bodies are full size. Untested.
- the client spawned with a person that was constantly in a hunched position, also while moving it didnt have a walking animation

unclear:

- the pins can get into a state that doesnt allow for unpinning
