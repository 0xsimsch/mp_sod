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
    the mouse-turned body yaw. Now: usage point position, yaw from its look-at point (else frozen at sit-down). Untested.
* - when sitting down. sitting animation is played and then it turns into laying in a floating position above the chair
  - 0.9.54: sit/lie was decided by eye height above the capsule, which is on the seat once seated (<0.7 m → "lying").
    Now the transition name decides first (Bed… → lie, Sit…/Chair… → sit). Untested.

unclear:

- the pins can get into a state that doesnt allow for unpinning
