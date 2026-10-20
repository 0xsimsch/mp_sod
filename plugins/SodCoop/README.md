# SodCoop: development notes

This file is the development log, oldest steps first, newest versions in the dated sections. For an overview, install and build steps see the [README](../../README.md) at the top of the repository.

## Step 1: second player as a separate body

Goal of this step: answer **"if the host has a second body flagged `isPlayer`, does the game's own perception/crime code treat it like a player?"** (from `analysis/A3_RESULTS.md`). No networking yet. The second player is faked by replaying your own movement.

## How the second player works

- `PlayerRegistry` holds every player (`P0` = host/local, `P1`… = remote). `MaxPlayers` is in `BepInEx/config/mp_sod.coop.cfg` (default 2).
- A remote player's body is a **possessed citizen** (`RemotePlayerBody.Possess`):
  - `isPlayer = true`
  - its AI is skipped (`AiTickPatch` blocks `AITick` / `UpdateTickRate`)
  - it's moved by an `IPoseSource`, either `GhostReplaySource` (your movement, 3 s late, 1.2 m to the side) or `FixedPoseSource`. A network-fed source plugs in here later.
- The donor citizen is never the current murderer or victim, and never someone within 30 m of you (so nobody vanishes in front of you).
- `PerceptionLog` only logs: when citizens see the remote body doing something illegal, pursue it, consider it trespassing, or when your camera spots it.

## Keys (in game)

> **Changed 2026-10-13:** the test keys used to be F4–F9 and F12. **F5 is the game's quicksave**, so pressing it overwrote the player's "Quick Save" during the night's tests. **F12 is Steam's screenshot key.** The keys now avoid both.

| Key | Action |
|---|---|
| Insert | add P1 as a ghost next to you |
| Home | toggle P1 frozen / following |
| End | trespass test: put P1 inside a far home with an awake resident |
| Delete | remove P1 (body goes back to the game; `isPlayer` restored) |
| PageUp | teleport yourself into P1's room |
| PageDown | toggle "P1 is doing an illegal action" (stand-in for a networked lockpick/search) |
| F10 | toggle keeping rooms around remote players loaded (A2 fix, on by default) |
| F11 | start a network session as set in `[Net]`; while one runs, log its status. Shift+F11 (Steam host): invite dialog |
| Ctrl+F11 | test: pin a found evidence item to the active case |
| Ctrl+Delete | leave the network session |
| Ctrl+Insert | test: talk to the nearest citizen (conversation arbitration) |
| Ctrl+Home | test: Steam messaging loopback |
| Ctrl+PageUp / Ctrl+PageDown / Ctrl+End | test: uncap fps (B5) / save-state snapshot timing (B1) / no-pause mode (B3) |

Output: `BepInEx/sodcoop.log` (plus the normal `LogOutput.log`). Also check `Player.log` for game exceptions caused by `isPlayer = true` on a non-`Player` actor.

## Try it with a friend (first real two-machine test)

**Friend's install:** `python3 -I bundle/make_bundle.py` writes `dist/SodCoop-<version>-friend.zip` (~38 MB): BepInEx 6 IL2CPP as installed here, `SodCoop.dll`, a config with `Mode = JoinSteam` / `JoinLobby = 0`, and `SodCoop-README.txt` (install, join, what logs to send). The friend unzips it into their game folder. On Linux/Proton they also need the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`. `BepInEx/interop` is left out because it's generated from the game's code, so their first start takes a few minutes longer. Pass `--with-interop` to include it. Build the plugin first.

**Host:** `[Net] Mode = HostSteam` (or press **Alt+F11** outside a session to swap host/join; saved to the config), load your game, press **F11**. That creates the lobby and sets rich presence `connect = +mp_sod_lobby <id>`. Optionally press **Shift+F11** to send an invite.

**Friend**, any of these:
- **F11** in the main menu or in a loaded game. With `JoinLobby = 0` it looks through Steam friends playing this game for a lobby (their `connect` rich presence, else the lobby in their game info) for 20 s, and joins it.
- "Join game" in the friends list, or accept the invite.
  - **Game not running:** Steam starts it with `+mp_sod_lobby <id>` or `+connect_lobby <id>`, and the mod joins as soon as the main menu is up.
  - **Game running:** Steam sends callbacks 337/333 instead. The game's Steamworks.NET drops callbacks nobody registered, and `Callback<T>` for those structs isn't compiled into the build. So `Net/SteamJoinRequests.cs` takes a `Callback<GamepadTextInputDismissed_t>` (an instantiation the game uses), also files it under 333/337 in `CallbackDispatcher.m_registeredCallbacks`, and reads the raw callback from `CallbackDispatcher.m_pCallbackMsg`.
  - Tested with Ctrl+Home: a fake 337 sent through the dispatcher's table started a join for its lobby.
- `JoinLobby = <id>` from the host's log, then F11.

No save of their own is needed. The host's world (and the generated city, if any) comes over the network and loads by itself (~35 s). Joining from the main menu is tested against `fake_host.py`.

**Logs:** `BepInEx/sodcoop.log`; the previous run's log is kept as `sodcoop.prev.log`. During a Steam session the log records every change in a peer's session state:
- `NET steam session with peer:…: connected ping=…ms quality=… relayed|direct [description]`
- an end reason when a session closes
- the relay network status (`100` = ready)

**F11** during a session prints a status line with those plus every sync counter. Leave with **Ctrl+Delete**. Developer diagnostics (DIAG every 3 s, PERF outside sessions) are now off unless `[Debug] VerboseLog = true`.

## Two-machine fixes (2026-10-13, afternoon, 0.3.x–0.5.0)

From the first real tests with a friend (protocol v23):
- **World transfer over Steam** is paced on Steam's send buffer (`SnapshotSync`); refused sends are retried. A second copy of the world within one session is ignored.
- **Overlay** (`SessionOverlay.cs`, Ctrl+F10): role, lobby, peers with ping/relay, world transfer progress, who is in a menu.
- **Alt+F11** swaps host/join; F11 refuses to host without a loaded world.
- **Pausing** (`Patches/PauseSync.cs`): the world pauses only while every player is in a menu.
- **Case board**: player-drawn strings, note text, sticky notes (`Net/NoteSync.cs`), pin and string colours (`CaseRecordSync`).
- **Side jobs** (`Net/JobSync.cs`): host-owned; jobs go out in save form on every change; a client taking a job paper asks the host to accept. Case events waiting for a job's facts are retried (`CaseSync.RetryPending`).
- **Citizens**: bed/sofa/left-right posture in the pose flags; a client's hits on citizens are applied on the host (`CitizenDamage`).
- **Remote bodies**: only borrowed from a ready world, re-created after a reload, kept visible and AI-off every 0.5 s.
- **Map markers** (`Puppet/MapMarker.cs`, 0.5.1): each remote player has a tinted copy of the map's player icon, tracked by the map itself on the remote body.
- **Objects** (0.6.0): resting place of objects a player carried/threw/knocked, on/off states flipped by a player (`ItemSync`), pin positions after a drag (`CaseRecordSync`).
- **Look and hands** (0.6.1, protocol v25): poses carry camera pitch/yaw and the held item; `RemotePlayerBody.LateApply` turns the neck after the Animator and keeps a carried object in front of the body; drawn items get a visual-only copy in the right hand. `SetCarryItemType(1)` and the hand rotation are guesses.
- **Diagnostics**: forwarded damage logs the client's health before/after (report: clients take no damage although hits arrive).

## 0.9.37

- The splash screen still showed the old art: "Press Any Button" is its own scene (`level0`, 12 objects: its own `Background Image` + `Logo` + `PressAnyButtonText`) with a separate copy of the `menuBG` sprite - `MainMenuController.backgroundImage` only owns the main-menu panel's copy (in `level1`). `MenuBackground` now sweeps every scene on load: any `Image` whose sprite or `RawImage` whose texture is still named `menuBG` gets the bundled one (plus an immediate sweep of the active scene at plugin load, and the `EnableMainMenu` catch-up).

## 0.9.32: bundled menu background

- The menu's backdrop is replaced with our own image: `Assets/menu_bg.png` is embedded in `SodCoop.dll` and set as `MainMenuController.backgroundImage.sprite` (`Patches/MenuBackground.cs`), so a fresh mod install gets it with no extra files. The backdrop is that UI Image behind every menu panel - a still, not a live city view (`analysis/MENU_RESULTS.md`, settled by extracting `menuBG` from `sharedassets0.assets`).
- Applied in `MainMenuController.Start` (before the menu's first drawn frame, so even the splash never shows the vanilla one) with an `EnableMainMenu` catch-up; the texture+sprite are rebuilt if Unity collected the previous menu scene's copy. Vanilla background stays if the resource is missing or decoding fails (`MENU background failed: …`, logged once).
- Log: `MENU background texture ready: 2048x1152 from 2536 KB png`, then `MENU background replaced with the bundled image`.

## 0.9.26: walking again after closing the board

- Report: the client couldn't walk (could still look, use and drag). `Player.OnPauseChange` (0x44D3D0) saves `fps.enableMovement` into `pausedRememberPlayerMovement` on pause and restores it on resume. In co-op the world runs behind the board, so a transition with movement off (searching the victim) finished while the board was open and turned movement on; closing the board then restored the saved "off". While the pause holds the movement, `EnablePlayerMovement` from anything else now only updates the saved value, so the resume applies it (single player's order). Logged as "movement turned on/off behind the menu".
- Safety net: movement off for 3 s with no pause, menu, transition, seat/search/computer lock, dialog, phone, cutscene, hiding, vent, bed, KO or death is turned back on and logged ("nothing holding it").

## 0.9.24: menu footer offset cache measures once, never from its own writes

- 0.9.19's per-launch "re-validation" measured the bar's distance to the column AFTER cached placement had already moved the bar - it measured our own write and stored that (log: cached bottom -4.9, then "updated" to -81.8), parking the footer ~80 px too low. Gaps are now determined ONCE per game run, always before the first placement, and final; a re-opened menu follows the run's gaps without re-measuring.
- Cache keys bumped to `sodcoop.menuGapTop.v2`/`menuGapBottom.v2` (ignores poisoned v1 values) and paired with `menuGapRes` - a resolution change re-measures legitimately.

## 0.9.19: menu header/footer lines follow the co-op buttons

- The main/Esc menu's red header and footer lines (`EmbelishmentTop`/`EmbelishmentBottom`, siblings of the buttons in `MainMenuPanel`) sat at fixed spots, so our added Host/Join (and Invite/Leave) buttons pushed the button column past the footer into it. They now hug the visible column top/bottom on every canvas frame; a `Refresh` hides our own copies and measures the game's untouched column edge once, then re-places. Follows Host/Join ↔ Invite/Leave state changes. (Note: `topBarRend`/`bottomBarRend` are the screen-edge vignette gradients, *not* these lines.)
- Offsets are cached in `PlayerPrefs` after a measured capture, so placement starts from frame one (no wrong-position flash) and a re-measurement silently updates them. See `analysis/MENU_RESULTS.md`.

## 0.9.14: switch resync after every client flip

- Host: a client's switch flip is applied, then the switch's resulting state on the host goes to every client, the flipper included (instead of relaying the client's message to the others). A client whose flip didn't take on the host is set back to the host's state. Log on a mismatch: `ITEM switch #id …: P1 set it to X, the host has Y; resyncing`.
- Receivers skip a switch update that matches what they already have (no second click sound for the flipper).

## 0.9.13: board resync after every client edit

- Host: after merging a client's board edit, it sends the merged case to everyone right away (the editor included), so all boards show the host's state and the edit is confirmed. Log: `CASE board edit … merged: N pins; resyncing everyone`.
- Client: a case record that arrives while the left mouse button is down is held until release (at most 10 s), so the rebuild doesn't cut off a drag. The newest record per case wins.

## 0.9.12: case board edits survive the host's case updates

- Seen (client log, 0.9.10): the host sent the same 16 KB case three times in 26 s while the client was pinning; each copy rebuilt the client's board, so pins not sent yet (1 s debounce) were lost and drags were cut off.
- Host: a case is only sent again when its record changed (`CaseRecordSync._lastSent`). Joiners still get every case.
- Client: an unchanged record is not applied again. A changed one that arrives while our own board edit waits keeps our pins on the new copy, and the edit still goes out (`KeepLocalBoard`).
- Both caches are cleared on session start/end and world reloads.
- Log: client `CASE board edit for N … sent: k pins`, `kept our k pins`; host `CASE board edit … merged` as before.

## 0.9.11: client light switches reach the host

- A switch flipped by the local player is always sent; the 3 m reach check (meant for the client's own citizens) no longer applies to it. A light switch's stored position can be metres from the player's feet, and one switch also flips the room's other lights, so some of a client's switches never reached the host.
- `Interactable.OnInteraction` counts as a local action too (switches flipped there have no interactor). If 0.9.11 crashes at startup, put `LocalInteraction` into `BepInEx/sodcoop_skip_patches`.
- Log: `ITEM switch #id … sent` (sender), `… from P1 applied` or `no such object here` (receiver).

## 0.9.10: objectives complete only on the host (protocol v38, untested in game)

- A client's case objectives never progress or complete by themselves. When the client's own check finds a trigger met, it sends a **fact** to the host: case, objective, trigger name and type (`ObjectiveSync.TriggerPatch`, resent every 3 s while the condition holds).
- The host judges each fact by trigger type (`ObjectiveSync.Judge`):
  - **location** triggers (go to node/room/address, explore crime scene, escape location/building): accepted only if the client's body is there on the host;
  - **shared state** (pins, switches, doors, removals, case status, ransom, pause): ignored, because the host's own check sees it;
  - **everything else** (player actions the host can't see: opened a file, used a computer, a call, own inventory…): accepted on trust.
- An accepted fact runs the host's own `Trigger`. The host sends progress (new) and completion, which clients apply with world effects suppressed.
- Anything else that completes a case objective directly on a client is dropped and logged once (`tried to complete here directly`).
- Log lines: client `OBJ … met here; told the host`, host `OBJ fact from P1: … accepted (…)` / `rejected: body is not at …`.

## 0.9.0: world systems and persistence (protocol v35, untested in game)

- `Net/ElevatorSync.cs` (msg 48): elevators keyed by bottom tile id; client button presses go to the host; host calls replayed and car state streamed near remote players.
- `Net/GadgetSync.cs` (msg 49): a client's handcuffing (`NewAIController.SetRestrained`) and grenades/flash bombs/incapacitators (`Toolbox.ExplodeGrenade`) replayed on the host.
- `Net/BloodSync.cs` (msg 50): spatter polled from `GameplayController.spatter`; host's go to clients, clients' own near their player go to the host; other local spatter removed on clients.
- `Net/NewsSync.cs` (msg 51): the host writes the newspaper; `CleanupController.TrashUpdate` off on clients.
- `Net/Profiles.cs`: a client's money, lockpicks, social credit, passcodes, sync disks, apartments and inventory presets kept per host+city in `BepInEx/sodcoop_profiles/`, captured before loading the host's world and restored after.
- `Net/RestSync.cs` (msg 52): time only speeds up while every player sleeps/waits/is KO'd (everyone KO'd counts too); a KO with someone awake waits out the game's KO time at normal speed.
- Story mode: F11 warns (does not block yet) when hosting a non-sandbox game.

## 0.8.0: side jobs, phones, dialog, case ids, pick-ups (protocol v32, untested in game)

- `Net/CaseIds.cs`: case ids collided (a client's case got the host's next id; the murder case and a side-job case were both 4 and overwrote each other). Victim discovery on a client asks the host (msg 47); cover-up/kidnapping cases host-only; other client-made cases use ids from player id × 1,000,000.
- `Net/JobSync.cs`: clients don't run job logic (loops, fail/complete/end, post, spawn items); job log only on change.
- `Net/DialogSync.cs` (msgs 45/46): client dialog choices and dialog ends replayed on the host with the client's body and result; job dialog options de-duplicated after each job record.
- `Net/PhoneSync.cs` (msg 44): calls created by the host only; mirrored on clients near the phone, for players or jobs; client answer/hang-up/dial performed on the host with the client's body.
- `Puppet/Doubles.cs` (msg 43, 0.7.4): the host assigns which citizen stands for which player; each machine hides its own stand-in.
- Items: carried objects stop their physics on pick-up (no wait until a thrown object settles); every ActionController Take*/PickUp by the local player reports a removed object; `SafeDelete` reported like `Delete`. Throws (0.7.3) carry velocity and spin.
- Crouch (0.7.5): kneel idle when still, squashed walk when moving (citizens have no crouch animation).

## 0.7.0: the P0/P1 batch from analysis/SYNC_PRIORITIES.md (protocol v27, untested in game)

- `Net/WorldSync.cs` (msg 35): citizen-made switches near clients, weather (host-owned), alarms/target modes/security investigate (host-owned, blocked on clients), breakers (extra state value), broken windows (keyed by rough position), door jam/wedge, police tape, barging.
- `Net/CitizenEventSync.cs` (msgs 37/38) + `Patches/ClientPerception.cs`: citizen attack/throw/block, speech and barks, reaction/alertness/awareness, outfits, held props; NPC sync radius 40 m indoors / 70 m outdoors with 10 m hysteresis; released citizens re-plan. Clients' own citizens never pursue/investigate players or call enforcers.
- `Net/ObjectiveSync.cs`, `Net/StatusSync.cs`, `Net/Notify.cs`, `Net/ExtraWire.cs` (msgs 39-42): objectives completed by the host (clients forward triggers, replay completion without side effects), hand-in answers and `Case.Resolve` on the host then replayed (PayEveryone = true), trespass verdict from the client (keys, guest passes), pursuit/wanted/fines back to the client, partner notifications via `InterfaceController.NewGameMessage`.
- Murder victim position in `DeathMsg`; hiding/vent/flashlight flags in player poses (remote flashlight = copy of the game's `flashLightObject`); `Patches/MurderGuard.cs` keeps the murder director off player bodies.
- Steam send limits raised at startup (min 1 MB/s, max 16 MB/s, buffer 8 MB; before/after logged). Snapshot pacing raised to match.
- Harmony patches are applied per class (`Plugin.PatchEach`): a patch that doesn't resolve is logged as `PATCH … not applied` instead of stopping the mod from loading.

## Networking (added 2026-10-13, night session)

`Net/` holds a polled, callback-free layer:

| File | What |
|---|---|
| `Protocol.cs` | Wire format (`Hello`, `Welcome`, `Reject`, `Pose`, `IllegalAction`, `Damage`, `Bye`, `Ping`), version 1. Pure .NET, no Unity types. |
| `ITransport.cs`, `UdpTransport.cs` | Packet transport. UDP is for LAN and same-machine tests. |
| `SteamNative.cs`, `SteamTransport.cs` | Direct P/Invoke into the game's `steam_api64.dll` flat API (`ISteamNetworkingMessages`, interface versions checked against its exports). No IL2CPP Steamworks.NET interop, so no managed callbacks. |
| `SteamLobby.cs` | Friends-only lobby. API-call results are polled through `ISteamUtils`. Rich presence `connect = +mp_sod_lobby <id>`, so Steam's "Join game" launches the friend's game straight into the lobby. |
| `NetSession.cs` | Host/client logic. Host: Hello → id + borrowed body driven by `Puppet/NetworkPoseSource` (100 ms interpolation buffer); relays poses; forwards damage to the owner (`Patches/DamageForward.cs`). Client: Hello until Welcome, 20 Hz poses, illegal-action changes, applies forwarded damage to its own `Player`. 10 s timeout. |
| `NetConfig.cs` | `[Net]` in `BepInEx/config/mp_sod.coop.cfg`: `Mode` = HostSteam / JoinSteam / HostUdp / JoinUdp, `UdpPort`, `JoinAddress`, `JoinLobby`, `PlayerName`. |

### Protocol at a glance (v18)

| # | Message | Direction | Reliable | Purpose |
|---|---|---|---|---|
| 1–3 | Hello / Welcome / Reject | c→h / h→c | yes | handshake: protocol + game build check, player id, max players, city id |
| 10 | Pose | both (host relays) | no | player body, 20 Hz |
| 11 | IllegalAction | c→h | yes | client is lockpicking/searching (witnesses react) |
| 12 | Damage | h→c | yes | a citizen hit your body on the host |
| 13 | DoorState | both | yes | door open/lock, keyed by `doorDictionary`, 60 m interest |
| 14 | TimeSync | h→c | no | clock, speed, host pause (2 s + on change) |
| 15 | PlayerKO | both (relayed) | yes | KO / recovery of a player |
| 16 | CaseEvent | both (relayed) | yes | evidence/fact found, discoveries, key merges |
| 17 | Spawn | both (relayed) | yes | runtime world interactable, same id everywhere |
| 18 | Item | both (relayed) | yes | move / inventory / delete by id |
| 19 | CitizenPoses | h→c | no | NPC sync: 40 m, 10 Hz, delta-filtered, ≤ 50 per packet |
| 20 | Bye | both | yes | leaving |
| 21 | PlayerLeft | h→c | yes | another player left |
| 22 | Death | h→c | yes | citizen killed (full replay when the murder record exists) |
| 23 | MurderRecord | h→c | yes | murder director state (game's JSON form) |
| 24 | CaseRecord | both (host picks active) | yes | whole case (JSON) on welcome, activation, objective, pin/unpin |
| 25 | SnapshotChunk | h→c | yes | joining client: the host's generated city file (if any), then the save-state snapshot (16 KB chunks, paced, hashed) |
| 29 | Forensic | both (relayed) | yes | revealed fingerprint/footprint, same id everywhere |
| 26–28 | TalkRequest / TalkReply / TalkEnd | c→h / h→c / c→h | yes | one conversation per citizen (B4) |
| 30 | Ping | both | h→c yes / c→h no | opens the Steam session from the host side; client keepalive every 2 s |

### Tested (same machine, fake client)

`analysis/tools/fake_client.py` speaks the protocol over UDP. Host with `Mode = HostUdp` + F11, then `python3 -I analysis/tools/fake_client.py 127.0.0.1:47615 25 --illegal-after 18`:

- Handshake: `WELCOME id=P1 city='Casablanca Quarter|wK08ZyOM1xmZWTL1'`. The host borrowed a citizen as P1's body.
- The host streamed ~15 poses/s. The fake client walked a circle around the host and the body followed on the host's screen.
- The client sent `IllegalAction(active)` → host `ILLEGAL-STATUS P1 -> True`. **Within 2 s a citizen attacked P1's body, and the host forwarded the hit as `DAMAGE 0.68 from citizen 237` to the client** instead of hurting the borrowed citizen.

Client side, against `analysis/tools/fake_host.py 47615 25 --damage-after 15` (game: `Mode = JoinUdp`, F11):

- `NET welcomed as P1`. The local player moved from P0 to P1, and the city mismatch was detected and logged (expected with the fake).
- The host's P0 poses spawned a borrowed-citizen body for the host, which walked its circle around us. The game streamed 337 poses in 25 s.
- `Damage` → applied to the local `Player` (`NET damage 0.30 from citizen -1`).

Steam (this machine only): `Mode = HostSteam` + F11 got the SteamID through `ISteamUser` and **created a friends-only lobby**. **Messaging loopback (Ctrl+Home):** a Hello and a Ping sent to our own SteamID through `ISteamNetworkingMessages` came back **byte-identical with the right sender SteamID**. That confirms `SendMessageToUser`, `ReceiveMessagesOnChannel`, the `SteamNetworkingMessage_t` offsets and `Release`. P/Invoke into the game's `steam_api64.dll` works under Proton. F12 (now Ctrl+Delete) left it again.

### Door sync (first world state, protocol v2)

`Net/DoorSync.cs` + `DoorState` message: door open amount (`ajar`) and lock state, keyed by `CityData.doorDictionary` (generation-time, so identical on every machine; 1,197 doors in the test city). Postfixes on `NewDoor.SetOpen` / `SetLocked` report changes. A client only reports its own player's changes. The host applies them with the client's body as the actor (so witnesses blame the right player) and sends every door change within 60 m of a client to that client. Changes applied from the network aren't echoed.

Tested with `fake_client.py --open-door 221001 --door-after 6`: the host's door went `ajar 0.00 → 1.00` from the client's request. In the other direction, citizens' door traffic near the client arrived at **263 events in 40 s** (about 7/s within 60 m in a busy block).

### Time (protocol v3)

`Net/TimeSync.cs`: the host sends `TimeSync(gameTime, timeSpeed, leapYearCycle)` every 2 s. A client snaps to it via `SessionData.SetGameTime` when it drifts more than 0.02 game hours, and copies the speed. On a client, `SessionData.SetTimeSpeed`, `Player.SetSpendingTimeMode(true)` (sleep/wait) and `Game.SetFastForward(true)` are blocked (design: only the host controls time). **Pausing (protocol v9), as decided:** **since 0.4.0 the world pauses only while every player is paused** (menu, case board, dialog). `Patches/PauseSync.cs`: each player's own pause is tracked from `PauseGame`/`ResumeGame`, clients report it (`PauseState`, protocol v20), the host decides and sends it in TimeSync. `play` follows the world, except inside `InputController.Update`, which sees the player's own state (otherwise a paused player with the world running gets stuck: `ResumeGame` starts with `if (play) return;`). A *host* pause pauses everyone: `TimeSync` carries a `Paused` flag and is sent the moment the host pauses or resumes, and clients stop/start their simulation without the pause UI or the input lock. Tested: the host's Esc menu → `paused=True` on the client within a frame. F (resume) → `paused=False`. A client joining during the pause got it straight away. All session timers use `Time.unscaledTime`, so networking keeps running while the game is paused.
Tested against `fake_host.py --game-time 46.0`: `TIME corrected by 2.139 h to host time 46.000`, after which the client stayed in step.

### KO (protocol v4)

The client reports its own `Player.TriggerPlayerKO` / `TriggerPlayerRecovery` as `PlayerKO`. Other machines put that player's body into the citizen KO state (`NewAIController.SetKO`, ragdoll) and back out (`SetRagdoll(false)` + animator on, since getting up is normally driven by the disabled AI). Poses and illegal status are ignored while it's down. Tested with `fake_client.py --ko-after 8 --recover-after 15`: body KO'd (animator off), recovered and walked on, no exceptions.

### Shared case knowledge (protocol v5)

`Net/CaseSync.cs` + `CaseEvent`. Evidence and facts are generated from the same city everywhere, so discoveries go over the network as identifiers and are replayed: `CaseComponent.SetFound(true)` (evidence by `evID`, facts by `Fact.GetIdentifier()`), `Evidence.AddDiscovery` and `Evidence.MergeDataKeys`. The host relays. Replays are idempotent and never echoed. Cascades (the game finding related facts) happen on each machine by itself.

Tested: the fake client's `EvidenceFound('ResidentRoster1')` → found on the host (1,372 → 1,373 of 24,295, `applied=1 unknown=0`). The host's own discoveries reached the client, but those were runtime-created **time evidence** (`time|43.893|…`) that a client wouldn't have. Time evidence is now **rebuilt from its id** on the receiver (`EvidenceCreator.GetTimeEvidence(string)`; tested: `createdOnDemand=1 applied=1 unknown=0`). Other runtime evidence (items, prints) needs spawn sync first (B2), and until then counts as `unknown` on the receiver.

**The case itself (protocol v14):** `Net/CaseRecordSync.cs` sends the host's `Case` objects (`[Serializable]`: pins, objectives, resolve questions) as JSON: all cases on Welcome, then a case again (debounced 1 s) on `SetActiveCase` or `AddObjective`. The client rebuilds them like a save load (`Objective.Setup(case)`), replaces its copy, rebuilds the board's case buttons (`UpdateCaseControls`) and follows the host's active case.
Tested: the host's active case went out as 22 KB of JSON ("New Murder Case", id 1). Replayed to the game client as a new case "Host Case" (id 55), it showed up on the board, selected, with its 2 objectives (screenshot: `probe_results/client_host_case_board.png`). No exceptions.
**Board edits:** pin/unpin (`CasePanelController.PinToCasePanel` / `UnPinFromCasePanel`) on *any* machine marks the active case dirty, and it's re-sent a second later. A client's edit goes to the host, which applies it and passes it on (last writer wins; only the host picks the active case). Tested with the Ctrl+F11 test key on the host: pinned "Building1" → the client got the case again ~1 s later (+213 chars, 1 pin). No exceptions.
Not yet: string/note edits that don't go through pin/unpin, and resolving/closing a case.

### Runtime spawns (protocol v6, B2 fix part 1)

`Net/SpawnSync.cs` + `Spawn`. A postfix on `InteractableCreator.CreateWorldInteractable` sends preset name, owner/writer/receiver humanIDs, transform, passed vars and dds override. The receiver recreates the object with the **same id**, forced through a prefix on `Interactable.AssignIDWorld`, then restores its own counter. Clients allocate their own creations from `0x40000000 + playerId << 24`, so ids never collide with the host's counter.
Tested: `fake_client.py --spawn Newspaper` → the host created a newspaper at its feet as `#1090519041` (P1's range), no exceptions. The host also sent its own runtime spawns.
Not yet: `passedObject` (logged when present).

**Fingerprints and footprints (protocol v18):** `Net/ForensicSync.cs` hooks their own creators (`CreateFingerprintInteractable` / `CreateFootprintInteractable`, used when a player reveals prints) and replays them with the same world id (shared `SpawnSync.WithForcedId`), the print data going as the game's serialized JSON. Tested: a fake client's fingerprint and footprint were recreated on the host as `#1090519051` / `#1090519052`. No exceptions.

### Item lifecycle (protocol v7, B2 fix part 2)

`Net/ItemSync.cs` + `Item`: `Interactable.MoveInteractable`, `SetInInventory`, `SetAsNotInventory` and `Delete` are replayed by id (looked up in `CityData.savableInteractableDictionary`). A player holder travels as a player id, since each machine represents a player by a different human. Tested with `fake_client.py --spawn Newspaper --move-delete`: spawn, move and delete were all applied on the host (`applied=2 missed=0`). The host's own item traffic near the client was ~2.5 events/s.

**Client guard:** clients still simulate their own citizens, so a client only reports spawns/item changes involving its own player (holding, dropping, or within 3 m). Doors already only report the client's own player. Measured with the game as client against `fake_host.py`: in 25 s it sent only poses and 3 case events, no item, spawn or door noise. This goes away once clients run a thin world without AI.

### NPC sync (protocol v8): host-authoritative citizens around each client

Host: every 0.1 s, per client, the citizens within **40 m** of that client's body go out as `CitizenPoses` (humanID, position, yaw, flags), at most 50 per packet (~1 KB, below the MTU). Client (`Puppet/NpcPuppets.cs`): each streamed citizen gets its AI component disabled and its `AITick`/`SightingCheck` blocked (perception is the host's job). It's driven through `Puppet/BodyDriver.cs` (shared with player bodies: transform, node/room bookkeeping, walk animation) with a 100 ms interpolation buffer. After 3 s without updates it gets its own AI back.

Tested:
- Host → fake client in a busy block: **202 distinct citizens in 20 s, ~21.6 KB/s per client** (about 100 citizens in range per update).
- `fake_host.py --npcs 10,11,12` → the game client drove those three citizens (`npcDriven=3`, 303 poses applied), no exceptions.

Delta filter: a citizen is only resent when it moved more than 5 cm or turned more than 5°, with a 1 s keepalive for idle ones. That brought it down to **~7.6 KB/s per client** with the same coverage (193 citizens in 20 s).

Protocol v11 adds each citizen's **idle animation state** (sitting, sweeping, phone, …), **arms state** (typing, smoking, reading, cuffed, …) and **KO**. The client applies changes only, and a KO'd puppet gets up again when released. Host stream with states: ~8.2 KB/s per client. Tested both roles, no exceptions.

### Deaths (protocol v12)

`Net/DeathSync.cs`: clients no longer run the murder director (`MurderController.Update` skipped), so murders only start on the host. The host reports every `Human.Murder` (victim, killer, murder id, weapon id). Player killers are sent as player ids, since every machine's own `Player` shares one humanID.
**Limit found by testing:** `Human.Death`'s constructor dereferences the `MurderController.Murder` record right away (`test rsi; je → NullReferenceException` at 0xDF16A4), so **every death needs the murder record**, and a client can't call `Human.Murder` without the host's. Until murder records are synced, clients replay a death **visually** (dead, corpse ragdoll, AI off). Tested with `fake_host.py --kill 10`: `DEATH citizen 10 … visual only`, no exceptions. **Murder records (protocol v13):** `Net/MurderSync.cs` sends the host's `MurderController.Murder` records in the game's own serialized form (`[Serializable]` + `JsonUtility`, as in saves): all active murders on Welcome, and each one again on `SetMurderState`. The client rebuilds them with `JsonUtility.FromJson` + `LoadSerializedData`, which is what loading a save does. (`FromJsonOverwrite` fails through the interop: missing `Il2CppSystem.Object.GetType`.) With the record present, `DeathSync` does the **full `Human.Murder` replay** (death record, body interactable, time of death).
Tested:
- Host → fake client: the save's active murder came through: `SerialKiller`/`RedGumsKiller`, victim 216, murderer 275, 893 chars.
- Client ← fake host, with that record renumbered to id 77 (not on the client): `MURDER record 77 applied: state=unsolved …`, then `DEATH citizen 10 murdered by 275 (murder 77) — full replay`. No exceptions.

Also missing in NPC sync: in-bed state, widening the radius outdoors. Side jobs still run locally on clients.

### Mid-game join (protocol v15, B1)

`Net/SnapshotSync.cs`. When a new client is welcomed, the host captures a save-state snapshot **during play** (`CaptureSaveStateAsync`, ~0.2 s, no pause) and streams it at 4 × 16 KB chunks per frame with an FNV-1a hash. The client writes it next to the player's saves as `MP Join.sodb` (never over them) and **loads it automatically** like the Load screen's Start does (`RestartSafeController.saveStateFileInfo` + `loadSaveGame`, `loadingCity` menu component → `CityConstructor.StartLoading`). Borrowed bodies and NPC puppets are released first, a 2 s keepalive keeps the session alive during the ~35 s load, and the temporary save is deleted once the joined world runs.
`Core/World.cs`: `World.Ready` (city loaded, no pre-sim, game started) gates every event reporter, applier and per-frame driver. It also detects world reloads, so caches holding old objects reset. Without it, a join load sent **12,329 bogus case events** (loading marks evidence found) and threw in `RemoteRoomLoader` (rooms of the destroyed world). With it: 32 events, 0 exceptions.
Tested:
- Host → fake client: 1.1 MB in 0.9 s, hash ok.
- Fake host → game client: received, auto-loaded, joined world running after ~35 s, host body re-created, temp save removed, no exceptions. (Manual Load → Start of the same file also worked.)
Generated cities: the host also sends its city file first (built-in cities like "Casablanca Quarter" are in every install's `StreamingAssets/Cities` and are skipped). The client writes it only if missing. Tested with a test city file: arrived byte-identical before the save, and the load and cleanup completed.
Per-player state: the client **keeps its own money** across the join (tested). The loaded `Player` holds the **host's** items, the same world objects the host holds. So after the load the client empties its non-static slots (watch, fists, coin and so on stay), with item sync suppressed, and puts those items into the host body's inventory once that body exists. Implemented. In the test save the player carried no such items, so the hand-over itself is still untested.

### Conversations (protocol v16, B4)

`Net/TalkSync.cs`: one conversation per citizen, first come first served. A client's `NewAIController.TalkTo` is intercepted. The client sends `TalkRequest`, and the dialog only opens once the host answers `granted`. The host locks the citizen to that player's body (`Actor.SetInteracting`, so its AI stops for them), refuses other players, including its own local player, and unlocks on `TalkEnd` (the client's `OnReturnFromTalkTo`) or when the player drops.
Tested:
- Host side, two fake clients asking for citizen 147: A granted, B **busy**, A ends, B granted, B leaves → lock released.
- In-game client side (Ctrl+Insert test key = the game's own `TalkTo` on the nearest citizen): the client asked first, the dialog opened only after the grant, and ending the conversation sent `TalkEnd`.

### Robustness pass (protocol v10)

A review of the network code found lifecycle bugs, all fixed and re-tested in both roles:
- The host now broadcasts `PlayerLeft`, so clients release that body. **Tested with 3 players** (host + 2 fake clients): clients saw each other's poses (~200 each) and the leave.
- A client resets when its host leaves or times out: player bodies, NPC puppets, and a host pause that would otherwise freeze it.
- The client adopts the host's `MaxPlayers`. Sequence numbers reset on rejoin and re-welcome. Illegal state is re-sent after a KO.
- Spawns: the forced id is single-use, the counter is restored on failure, and a spawn's own item events aren't echoed.
- Puppet citizens on a client are never picked as player bodies. A released KO'd body gets up again.
- On clients, other players' bodies don't take local damage or run perception (host's job).
- Clients only accept packets from their host. UDP prefers IPv4 and swallows send errors. Each per-frame subsystem has its own error isolation (rate-limited logs).
- F10 off releases forced rooms. The door index resets with the session. `EvidenceLocation.MergeDataKeys` (an override) is synced too.

### Not tested yet

- **Steam between two machines** (relay/NAT, session acceptance via the lobby Ping): needs a second Steam account with the game. See "Try it with a friend". Tested on this machine:
  - messaging loopback, relay status 100, and the connection-info offsets (Ctrl+Home)
  - the friend search ending with "no friend is hosting" after 20 s
  - joining from the main menu over UDP
- A real second game as client: the client's view of the host (the host's poses drive a borrowed citizen there too) and damage applied to a real client `Player`.

## Test plan

1. Start a sandbox city, press **Insert** (was F5), and walk around. Check that P1 shows up beside you, follows 3 s behind, and doesn't crash anything. In the log, expect `SPOTTED-BY-LOCAL-PLAYER`.
2. Press **End** (was F7). P1 is now standing in a stranger's home. Watch the log for about 30 s:
   - `TRESPASS … result=True`: the citizen-side trespass check counts P1 as trespassing.
   - `SEES-ILLEGAL observer=citizen … target=P1`: the resident witnessed it.
   - `PURSUED-BY …`: the resident chases P1.
   All three means the existing code mostly works for extra players, as A3 suggested.
3. Press **Delete** (was F4) and confirm the citizen goes back to normal life (AI ticking again).

What a failure would tell us:
- Exceptions in `Player.log` right after Insert: code casts "isPlayer actors" to `Player`. Those spots must be patched.
- `TRESPASS` true but no `SEES-ILLEGAL`/`PURSUED-BY`: perception only looks at `Player.Instance`. We'd need to patch `Actor::SightingCheck` to also consider remote bodies.

## Known limits of this prototype

- Walk animation: `RemotePlayerBody.UpdateAnimation` feeds the animator's `moveSpeed`/`walkAnimSpeed` from the pose speed, the same way the citizen AI does (calibrated: real citizens use `normSpeed = moveSpeed / movementRunSpeed`). The donor's idle/arms/bed state is reset on possess. Crouch, jump and look pitch aren't animated yet.
- Borrowing a citizen removes them from their daily routine while possessed. A real spawn path (prefab + `CitizenCreator`) comes later.
- World-state sync covers doors only. Everything else is still simulated per machine; only bodies, illegal actions, damage and doors cross the network.

## Build

Unit tests for the protocol and UDP transport (no game needed): `cd plugins/SodCoop.Tests && nix shell nixpkgs#dotnet-sdk_8 -c dotnet run` (39 checks).

```
nix shell nixpkgs#dotnet-sdk_8 -c dotnet build -c Release   # copies SodCoop.dll into BepInEx/plugins
```

## Results (2026-10-12)

Logs: `probe_results/coop_run1.log`, `coop_run2.log`, `coop_run3.log`.

| Check | Result |
|---|---|
| Possess a citizen as P1, `isPlayer = true` | ✅ works; **0 exceptions** in `Player.log` over 3 runs |
| Ghost follows the local player | ✅ after fixes (see below). The body slides in whatever pose it had (e.g. sitting). |
| `Human.IsTrespassing` for P1 in a stranger's home | ✅ **`result=True, escalation=2`**, so the citizen-side trespass logic already handles extra players |
| Witnesses react to P1 (far from host, room unloaded) | ❌ nothing, even with nearby citizens forced to `veryHigh` |
| Witnesses react to P1 (host in the same home, geometry loaded) | ❌ **nothing for P1**. In the same 11 s, citizen 259 logged ~40 `SEES-ILLEGAL` and ~20 `PURSUED-BY` against **P0 (host)**, standing 6 m from P1. |
| F4 release | ✅ citizen restored, no errors |

**Conclusion:** trespass *detection* works for extra `isPlayer` actors, but the **witness/perception system only considers the real `Player`**. The A3 hypothesis ("perception mostly works on any isPlayer actor") is **wrong for sightings**. We need a perception patch:
- `Player` overrides `SightingCheck`, and `Actor::SightingCheck` contains 14 `isPlayer` checks. Next step is reading both in Ghidra to see whether citizens look for the player, or the player "announces" itself to nearby citizens (in which case remote bodies need the same call).

Fixes made during testing:
- Blocking `AITick` alone isn't enough: the body kept **walking its last planned route** (path following runs in `NewAIController`'s own Update). The AI component is now disabled while possessed.
- Pose sources now pin the body every frame instead of applying once.
- Added `BOOST`: citizens within 15 m of a remote body are forced to `veryHigh` (crude stand-in for the A1 fix).
- Added F8: teleport the local player into P1's room.
