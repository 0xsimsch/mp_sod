# SodCoop co-op test protocol

A full two-machine test pass over everything the mod syncs, every fixed bug that could come
back, and every area that is knowingly *not* synced (to check it drifts quietly instead of
breaking the game). Written for the dev + regular test partner; steps are terse and assume you
know the game and can read `BepInEx/sodcoop.log`.

Target build: **mod ≥ 0.9.44, protocol v43, game v42.04**. Both machines must run the *same*
mod build — the handshake rejects mismatches, so a mismatch is a CONN test, not setup slack.

## How to use this

- Run the **Smoke round** first (list at the bottom). Only then the area tables.
- One table = one section, usually one in-game sitting. Fill `Res` per row:
  `P` pass · `F` fail · `!` fail, known issue (link the defect) · `~` drift/observation, not a bug ·
  `-` not applicable this run.
- Record a **Run header** per pass (copy the block at the end), one defect entry per finding.
- Both machines press **F11** once before quitting a failing session (writes ping/relay/state to
  the log). On any `F`: keep `sodcoop.log` **and** `sodcoop.prev.log` **and** `LogOutput.log`
  from *both* machines, plus game screenshots.
- "Log:" cells quote the log-line prefixes to grep for; they are the mod's real strings.
- The world is host-authoritative. Unless stated otherwise, "expected" means: both machines
  end in the same state, and the acting side's action leaves its log line on the *other* side.

### Run header (copy per pass)

```
Date:            Testers:                 Mod: 0.9.44   Protocol: v43   Game: v42.04
Host  OS: (Linux/Proton … | Windows …)   Steam: direct/relay, ping: … ms
Client OS: (Linux/Proton … | Windows …)  Steam: direct/relay, ping: … ms
City: (name / generated seed):           Start state: (fresh sandbox day X, hh:mm)
Round: (Smoke | Connection | Case loop | …):
Summary: P __  F __  ! __  ~ __  - __     Defects filed: DEFECT-__ …
```

---

## 0. Setup (once per machine, recheck after every update)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| SET-1 | Game version | Main menu bottom-right shows v42.04 | — | |
| SET-2 | Mod loaded | `SoD Co-op 0.9.44 loaded (protocol v43, MaxPlayers=2)` | `sodcoop.log` | |
| SET-3 | Same build both sides | Identical version+protocol lines | both logs | |
| SET-4 | Proton only: launch option | `WINEDLLOVERRIDES="winhttp=n,b" %command%` set; `winhttp.dll` next to the exe | Steam props | |
| SET-5 | First start after unzip | Long black screen while `BepInEx/interop` builds, then menu; mod buttons appear | menu | |
| SET-6 | Menu looks | Co-op buttons added; bundled background replaces vanilla (splash *and* menu); red header/footer lines hug the button column | log: `MENU added …`, `MENU background replaced`; no `MENU background failed` | |
| SET-7 | At 2 resolutions/DPI | Header/footer offsets measured once, no flash of misplaced bars on menu open | log: `MENU bar offsets captured` / `bars use cached offsets` | |
| SET-8 | Two real accounts | Both accounts own the game (Family Sharing can't run two copies). Same Steam friends group | — | |
| SET-9 | Host world ready | Fresh sandbox save, a live murder case preferred. Note city name/seed | — | |
| SET-10 | Config | Host `Mode=HostSteam`, client `Mode=JoinSteam`, `JoinLobby=0` | `BepInEx/config/mp_sod.coop.cfg` | |
| SET-11 | Profiles dir state | For "first join" tests, start with empty `BepInEx/sodcoop_profiles/` | filesystem | |
| SET-12 | No story | Both test in sandbox only; story chapters are unsupported (see DRIFT-4) | — | |

## 1. Connection and lobby (CONN)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| CONN-1 | Host: load world, F11 | Lobby created, overlay flips to HOST; F11 refuses without a loaded world | log: `NET lobby … entered` / refusal | |
| CONN-2 | Client at main menu: **Join Friend** button | Finds the hosting friend within ~20 s and starts the join | log: `NET found … friend lobby(s)` | |
| CONN-3 | Same via F11 on client | Identical result | — | |
| CONN-4 | Friend list → "Join game" (client game **running**) | Join starts in the running game | log: `NET lobby … joined`, `STEAM … → lobby …` | |
| CONN-5 | "Join game" with client game **stopped** | Steam cold-starts the game; it auto-joins once the menu is up | log: `joining Steam lobby … from the command line` | |
| CONN-6 | Host Shift+F11 invite, accept | Same join path as CONN-4 | both logs | |
| CONN-7 | `JoinLobby=<id>` (host's log id) + F11 | Direct join, skips friend search | log: `NET lobby … entered` | |
| CONN-8 | Alt+F11 swaps host/join | Mode swaps and is persisted to the config | log: `Alt+F11: … [mode]` | |
| CONN-9 | Nobody hosts, client F11 | Search ends after 20 s with "no friend is hosting", game unaffected | log | |
| CONN-10 | Client tries to join while **already** in a session | Refused with a clear message | log: `STEAM already in a session` | |
| CONN-11 | One-sided mod version mismatch | Client shows/gets a rejection, not a hang | log: `NET rejected by host: …` | |
| CONN-12 | Different game builds (if testable) | Rejected at handshake (build check) | as above | |
| CONN-13 | Ctrl+Delete (client) leaves | Host removes the body, citizen released; client back to solo | log: host `NET … dropped`, `released citizen` | |
| CONN-14 | Ctrl+Delete (host) leaves | Client session resets fully (see ROB-2) | — | |
| CONN-15 | Ctrl+F10 | Overlay hides/shows | log: `Ctrl+F10: session overlay = …` | |
| CONN-16 | F11 in session | Status block with ping/relay per peer + sync counters | log: `F11: SoD Co-op …` | |
| CONN-17 | Overlay content | Role, peers with ping + relayed/direct, world-transfer progress, who has a menu open | eyes | |
| CONN-18 | Steam offline mode on client | Clean "not found" failure; no crash, no half-join | — | |

## 2. Join and world snapshot (JOIN)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| JOIN-1 | Join from main menu into a running sandbox | Transfer overlay counts up, world auto-loads in ~30–60 s, then playing | log: `SNAPSHOT … loading the host's world…` → `joined world is running` | |
| JOIN-2 | Temp save hygiene | `MP Join.sodb` appears during load, deleted after | log: `removed the temporary 'MP Join' save` | |
| JOIN-3 | Built-in city | Not re-sent | log: `SNAPSHOT city …: already have it` | |
| JOIN-4 | **Generated city** host, fresh client | City file sent before the save, load succeeds | log: `receiving the host's city … (… KB)` | |
| JOIN-5 | Join mid-game (host day 3, populated case) | Client lands in the same moment: clock, case, board, weather | overlay + case board compare | |
| JOIN-6 | Host is **paused** (Esc) during the join | Joined world starts paused for the client, resumes when host unpauses | TimeSync pause flag | |
| JOIN-7 | Host opens menus/case board during the 35 s transfer | Transfer completes anyway; held events replay after load | log: `JOIN holding events …`, `replayed … held event(s)` | |
| JOIN-8 | Client keeps own money | After join, client money ≠ host money | log: `SNAPSHOT kept our own money` | |
| JOIN-9 | Own upgrades/disks | Client's sync disks restored, not the host's | log: `PROFILE restored from …`, `PROFILE captured our own state …` | |
| JOIN-10 | Host held items | Host's carried items sit on the host puppet, client's hands empty | both screens | |
| JOIN-11 | Leave + rejoin (same session) | Second join works, no duplicate bodies, no second world transfer | log: `SNAPSHOT ignoring a second copy` | |
| JOIN-12 | Rejoin after client crash (kill game, restart, F11) | Host re-adds cleanly; old body released first | log: `NET … gets a new body` | |
| JOIN-13 | Join while a **second** client is present (`MaxPlayers=3`) | Three players, each sees the other two; see CIT-15 for talk locks | config + overlay | |
| JOIN-14 | Join while host is in KO-recovery | World loads; KO state of host visible on client puppet | — | |
| JOIN-15 | Overlay says what's happening during every phase of JOIN-1 | Never a dead screen without a status line | eyes + `SNAPSHOT {LastStatus}` | |

## 3. Bodies, movement, animation (BODY)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| BODY-1 | Walk / run / walk backwards | Partner puppet follows smoothly (~100 ms buffer), correct gait, no rubber-banding | eyes | |
| BODY-2 | Look up/down/around (fast) | Puppet head/torso tracks | eyes | |
| BODY-3 | Crouch still / crouch-walk | Kneel idle / squashed walk, no standing slide | eyes | |
| BODY-4 | Jump, land, fall down a floor | Puppet follows via pose; check floor snapping | log: `POSE capsule bottom implausible` (should be rare+recovered) | |
| BODY-5 | Draw items: gun, knife, bottle, newspaper, food, briefcase | Puppet holds a visual copy, correct item | eyes | |
| BODY-6 | Eat/drink/interact animations | Puppet poses plausibly (activity flags) | eyes | |
| BODY-7 | Sit on bench / chair; get up | Puppet sits and leaves seat | log (sender): `POSE seated on …` | |
| BODY-8 | Lie on bed / sleep pose | Syncs; partner in bed visible lying | eyes | |
| BODY-9 | Flashlight on/off/during darkness | Light cone visible on the puppet only while on | eyes | |
| BODY-10 | Map: both players' markers | Tinted player icons, tracked live; names visible | log: `MAP marker added for P…` | |
| BODY-11 | Partner 200 m away indoors, room unloaded | Puppet stays visible where it should be; rooms around remote bodies stay forced-loaded | log: `ROOMS forced …`; toggle F10 to confirm it's the mechanism | |
| BODY-12 | You are never your own puppet (no doppelgänger) | Own stand-in hidden on your machine | log: `DOUBLE hid citizen …` | |
| BODY-13 | After session end / partner leave | Hidden stand-in shows again, walks off | log: `DOUBLE showed … hidden citizen(s) again` | |
| BODY-14 | Puppet is never the murder victim or killer | Compare puppet's citizen id vs murder record victim/murderer | log: `MURDER record … victim=… murderer=…` vs `possessed citizen …` | |
| BODY-15 | Name tag over partner | Readable, correct name | eyes | |
| BODY-16 | Partner teleports far (lift, vent chain, jailhouse exit) | Puppet catches up < ~1 s, no permanent float/sink | log: `POSE player transform y=…` | |

## 4. Time, pause, sleep (TIME)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| TIME-1 | Play 30 min real time | Clocks match to displayed minute | client log occasional `TIME corrected by … h` (should be rare, < 0.02 h) | |
| TIME-2 | Only host opens Esc menu | World keeps running (only host paused); client sees world live, overlay marks host "in menu" | log: `PAUSE …` + overlay | |
| TIME-3 | Only client opens Esc | Host world keeps running | same, other side | |
| TIME-4 | Both open Esc | World fully stops for both | — | |
| TIME-5 | Host case board open, client moves | Client can move; client's own board pauses them individually | — | |
| TIME-6 | Host "wait here" while client active | Time does NOT fast-forward for anyone | RestSync rule | |
| TIME-7 | Both sleep/wait | Fast-forward starts; either waking stops it | log: `REST … is sleeping/waiting` | |
| TIME-8 | One player KO while other awake | KO recovers after ~20 s real time at normal game speed | log: `REST KO: waiting … at veryFast pace, time stays at normal speed` | |
| TIME-9 | Both KO / KO+sleeping partner | Time may skip; after wake, clocks resync | TIME-1 check | |
| TIME-10 | Host saves & quits to menu *then* Ctrl+Delete vs Ctrl+Delete directly | Client ends cleanly either way (see ROB-2) | — | |
| TIME-11 | Host uses sleep-skip while client is mid-dialog / mid-board | No corruption; skip applies when client is idle again, or is blocked — record actual behavior | ~ observation | |
| TIME-12 | Midnight / day rollover during a session | Case generation, news, murders continue on host and reach the client | LONG-3 | |

## 5. Case loop (CASE)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| CASE-1 | Host creates/finds murder case at session start | Client shows same case, objectives, address | log: `CASE record …` | |
| CASE-2 | **Client** finds the victim body first | Client asks host; host opens the murder case; both get it | log: `CASE found the victim: asking the host…` | |
| CASE-3 | Host finds the victim | Discovery replays on client | log: `CASE … discovered the victim: running the discovery here` | |
| CASE-4 | Evidence both directions: scan a fingerprint, reveal footprint, pick up receipt/document | Fact/evidence appears on the other machine, same case | log: `FORENSIC …`, `CASE … applied` | |
| CASE-5 | Time evidence (corrupt alibi etc.) found by client | Rebuilt from id on host | applied, `unknown=0` in F11 counters | |
| CASE-6 | **Client** walks to objective address (location trigger) | Host verifies client's body position, completes for everyone | host: `OBJ fact from P1: … accepted`; client: `… met here; told the host` | |
| CASE-7 | Client uses computer/opens file at objective (trust trigger) | Accepted on trust, completes for everyone | `OBJ fact from … accepted (…)` | |
| CASE-8 | Objective never completes twice / client never completes alone | Client-direct completions dropped | client: `OBJ '…' tried to complete here directly; left to the host` | |
| CASE-9 | **Regression — crime scene street**: both "Explore Reported Crime Scene", host picks up the documents | Street stays in objectives/map unless solo game also removes it; compare with solo behavior | BACKLOG §crime-scene; objectives list on both | |
| CASE-10 | Objective progress partial (e.g. 2/3 substeps) on either side | Progress shown identically both sides | eyes | |
| CASE-11 | Host board edit: pin evidence, draw string, change pin/string color, sticky note with text | ≤ 1–2 s later identical on client | log: `CASE board edit for … sent` / host `merged … resyncing everyone` | |
| CASE-12 | Client board edit (same set) | Host merges + resyncs; **including while host is mid-edit**: no lost pins | log: `CASE record …: kept our N pins` | |
| CASE-13 | Drag pins simultaneously, both sides | Drags are never cut off by incoming updates (held ≤ 10 s); after both stop, boards converge | eyes + `CASE board edit … not sent: unchanged` | |
| CASE-14 | Active case follows host switching selection; client selection does NOT move host | one-way follow | eyes | |
| CASE-15 | **Hand-in by client** (resolve UI, accuse correct culprit) | Host applies answers, resolves once; `PayEveryone`: **both** players get the reward money | log: `OBJ P1 handed in case … with N answer(s)`, `OBJ case … resolved; told the clients` | |
| CASE-16 | Hand-in by host | Client gets the resolution, same case status text | same | |
| CASE-17 | Wrong-accusation hand-in (client picks wrong suspect) | Rejected/feedback identically both sides; case stays active | eyes | |
| CASE-18 | Client discovers an unrelated crime → own case | Gets an id in the client's high range; does **not** overwrite the murder case | log: `CASE created our own case …` | |
| CASE-19 | Evidence discovered before its case arrives (fast client) | Event held, applied when the case lands | log: `CASE waiting: …` → `CASE applied … earlier event(s)` | |
| CASE-20 | New case auto-generated by a fresh murder during the session | Both machines get the new case | log on both | |
| CASE-21 | Client kidnaps a citizen (job) | Kidnap/cover-up case handled host-side only | JOB section, `CASE` host logs | |
| CASE-22 | Case board opened **during** host snapshot transfer to a third player | No corruption, transfer finishes | JOIN-7 | |

## 6. Side jobs, phones, dialog (JOB)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| JOB-1 | Host takes a newspaper job | Job on host; client who picks the same paper later doesn't double-take | log: `JOB took job …` | |
| JOB-2 | Client takes the job paper | Client asks host; job accepted for everyone | client `JOB took job …; asking the host`, host `JOB … accepting it for everyone` | |
| JOB-3 | Both read job details | Same text/phases both sides | eyes | |
| JOB-4 | Client makes the intro phone call | Call placed on host, rings mirrored | log: `PHONE … dialled …; placed on the host`, `PHONE … rings here` | |
| JOB-5 | Goose-chase call | Handled as job logic on host | log: `PHONE job …: goose-chase call answered here` | |
| JOB-6 | Dialog with employer (client) | Choices replayed on host with client's body; no duplicated options each time the record arrives | log: `DIALOG P1 '…' with citizen … replayed`, `DIALOG job …: removed N repeated dialog option(s)` | |
| JOB-7 | Job phase changes (collect item, deliver, break-in) complete on host | Both machines see progress; item syncs | JOB/WORLD lines | |
| JOB-8 | Job payout (cash item or transfer) | Reward lands for the **player who did it** — record whether the other player also gets it (open design decision) | ~ decision check | |
| JOB-9 | Job fail/end while client is offline in the session? (skip if only 2p) | Host logic runs alone; client resyncs on return | JOB-11/JOIN-11 | |
| JOB-10 | Phone answered *by client* on a ringing public phone | Host performs the answer with client's body | log: `PHONE … answered call …; told the host` | |
| JOB-11 | Client hang-up mid-call | Mirrored | `PHONE … hung up call` | |
| JOB-12 | Client-only phone the host can't see (far away, unloaded on host?) | Either applied host-side or clean `no such telephone` — never a desync/crash | log | |
| JOB-13 | Dialog bribery (client bribes a citizen) | Result (info/money) — money per player; info per player → `~` drift, no crash | ~ observation | |
| JOB-14 | Random small-talk answers (same citizen, both machines, same question) | Answers may differ (known drift) but must never hang/crash | DRIFT-3 | |

## 7. Citizens, perception, conversation (CIT)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| CIT-1 | Stand in a busy square as client | Citizens stream in, walk normal routines, sit, work, talk | poses/anim states visible | |
| CIT-2 | Watch the sync edge (walk out of ~40 m indoors / ~70 m outdoors) | No pop/U-turn at release; hysteresis band works | eyes; log `NPC re-plan` | |
| CIT-3 | Re-enter the radius | Same citizens re-appear on the same positions | eyes | |
| CIT-4 | Citizen idle/arms states: sitting, sweeping, smoking, reading, typing, cuffed, bed left/right | Correct state on the puppet | eyes | |
| CIT-5 | Outfit changes (nightwear, work wear) on puppets | Applied from host | eyes | |
| CIT-6 | Speech: host-driven citizen near client shouts ("Stop!", screams) | Speech bubble/bark on client | CitizenEventSync (bark lines) | |
| CIT-7 | Awareness icons: client sneaks behind a host-driven citizen | Client gets the awareness icon/pursuit HUD it wouldn't in unsynced play | client HUD | |
| CIT-8 | **Conversation lock**: host talks to citizen X; client tries X too | Client refused | host: `TALK … busy` | |
| CIT-9 | Client A (in 3p) holds a citizen; client/host try same | busy; after `TalkEnd` (walk away, Esc), lock frees | `TALK … finished` | |
| CIT-10 | Client disconnects mid-conversation | Lock released on host shortly after | host log, CIT-8 retried | |
| CIT-11 | Client's far citizens (own sim, >radius) never chase/report the client or call police | No phantom enforcers | client log: `CIT blocked a local enforcer call` | |
| CIT-12 | Witness sees client pickpocket (within radius) | Host-side witnesses blame the client's body; `ILLEGAL-STATUS` active while illegal action held | log: `ILLEGAL-STATUS P1 -> True (…)`, `SEES-ILLEGAL observer=… target=…` | |
| CIT-13 | Attack anim: host citizen punches client | Client sees the swing (attack replay) then takes damage | FIGHT-1 | |
| CIT-14 | Citizen possessed as partner's body leaves routine; after Ctrl+Delete returns to life | No permanent missing citizen | log: `released citizen` | |
| CIT-15 | Two players interrogate the *same* suspect about the case sequentially | Second must wait (CIT-8); answers from that citizen's memory may differ per machine → `~` | DRIFT-2 | |
| CIT-16 | Crowd density: near a client in a busy block, frame rate stays sane | No NPC-streaming stutter (p95 ms reasonable) | log: `PERF avg … p95 …` | |

## 8. Crime and stealth (CRIM)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| CRIM-1 | Client walks into a stranger's home (no key) | Trespass escalation host-side: resident yells, enforcers after client; client gets wanted/fine HUD | log: `TRESPASS body=… result=True escalation=…`, client `STATUS wanted in building …`, `STATUS fined for …` | |
| CRIM-2 | Client uses own keyring / guest pass at the same door | Not trespassing (verdict from client respected) | no `result=True` | |
| CRIM-3 | Client lockpicks a lock, witnessed | Same crime flow as solo; blame lands on client | `ILLEGAL-STATUS` + witnesses | |
| CRIM-4 | Client bashes a window (near witnesses) | Broken on both machines, glass, noise, witnesses react | log: `WORLD window at … broken (from …)` | |
| CRIM-5 | Host bashes a window far from any client | Client still learns it (loads broken later) | log: `recorded broken (not loaded here; it loads broken)` | |
| CRIM-6 | Barge a door + door wedge + police tape, either side | Synced both ways (0.9.29) | WORLD/door lines | |
| CRIM-7 | Hide in a closet / crawl a vent while citizens pass | Host-driven citizens must NOT spot the hider (hiding flag) | no pursuit while hidden | |
| CRIM-8 | Pickpocket a citizen's key, both players target the same citizen | Item ownership resolves on host; no dupe | ItemSync log | |
| CRIM-9 | Knock a citizen out (client), then leave | KO'd citizen stays KO host-side, others react, body can be dragged | log: `DRAG started dragging citizen …` | |
| CRIM-10 | Client drags a body into a building and dumps it | Drag + drop position synced; trespass/crime judged on host | `DRAG … put down` | |
| CRIM-11 | Client murders a citizen | Death + murder record replay; news later mentions it | log: `DEATH citizen … — full replay`, `NEWS paper from host applied` | |
| CRIM-12 | Gun fired near witnesses (either side) | Enforcer dispatch is host-only; both get the pursuit HUD | no `CIT blocked` on host, yes on client | |
| CRIM-13 | Fine payment (kiosk/police, either side) | Fines are per player — one player's fine list doesn't pay the other's | `STATUS fine … is P?'s` | |
| CRIM-14 | Wanted timer expires in a building | Both HUDs clear per player | `STATUS wanted in building … for …` | |

## 9. Combat, KO, death (FIGHT)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| FIGHT-1 | Host-side citizen attacks the client | Damage applied on client (health bar, hit feel); attacker visible with swing | host: `NET hit … health … ko=`; client: `NET damage … from citizen …` | |
| FIGHT-2 | Client hits a host-driven citizen | Damage on host, victim reacts there | client: `NET hit citizen … ; sent to the host` | |
| FIGHT-3 | Client hits *the host's own body*? | Record actual behavior (should behave like hitting a citizen, host applies) | ~ observation | |
| FIGHT-4 | Throw bottle/flashbomb: arc and spin | Partner sees the same trajectory | eyes | |
| FIGHT-5 | **Client gets KO'd** | Ragdoll on host puppet; client: KO screen + third-person view of partner while recovering; recovery wakes properly (animator back) | log: `KO body=… val=True`, `KO view: watching the partner while recovering`, `KO view: back to first person` | |
| FIGHT-6 | Host gets KO'd | Client sees puppet collapse, no third-person forced on client | partner screen | |
| FIGHT-7 | KO'd while partner is awake | 20 s real-time recovery (TIME-8) | `REST KO: waiting …` | |
| FIGHT-8 | Both KO'd | Time skip allowed; both wake in sync | — | |
| FIGHT-9 | Player killed (citizen finishes you) | Record behavior: no crash, session survives, partner can carry on; death handling per game — write down exactly what happens | DEFECT-worthy if game-over nukes the session | |
| FIGHT-10 | Handcuff a citizen (client) | Restrained host-side, visible for both; escapable as solo rules | log: `GADGET … handcuffed` | |
| FIGHT-11 | Flash bomb / incapacitator / grenade used by client | Effect replays host-side; citizens stunned on both sides | `GADGET {from}'s gadget #… went off` | |
| FIGHT-12 | Enforcers pursue the client through the city | Path/chase streamed while in radius; on release the citizen re-plans (no freeze) | eyes | |
| FIGHT-13 | Puppet body's weapon (drawn gun) | Partner's puppet holds the gun, no leftover citizen props | log: `body armed with …`, `dropped the citizen's hand props` | |

## 10. Security systems (SEC)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| SEC-1 | Client triggers an address alarm | Host raises it authoritative; lockdown/timers identical | log: `WORLD P1 set off the …` | |
| SEC-2 | Camera spots the client | Security investigates host-side; client's own camera sim blocked | no double alarm logic client-side | |
| SEC-3 | Flip the breaker (either player) | Cameras/security dead on both; breaker state persists | WORLD extra-state log | |
| SEC-4 | Sabotage a security system (client, hacker route) | Applied on host | `WORLD …` lines | |
| SEC-5 | Client's own far citizens trigger a "local alarm" | Suppressed — only host events count | client logs show blocks only | |
| SEC-6 | Sentry gun/turret encounter (if you have one in the city) | Fires host-side only, bullets/damage shared | ~ record if none available | |
| SEC-7 | Alarm expires while one player is in a menu | State still syncs | — | |

## 11. World systems (WORLD)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| WORLD-1 | Open/close/lock doors, both players | Same ajar/lock state both sides within ~1 s | DoorSync log | |
| WORLD-2 | Walk back to a door you visited 10 min ago that a citizen re-locked while you were away | Lock resynced on approach (0.9.30) | eyes | |
| WORLD-3 | Pick up / drop / throw / pocket items both directions | Same id, same resting place | `ITEM took #… removed for everyone` | |
| WORLD-4 | Citizen (host-driven) moves an item next to the client | Client sees it move/delete | ~2.5 events/s normal | |
| WORLD-5 | Flip light switches (client) | Applied on host incl. the room's linked lights; mismatches snap back | `ITEM switch #… from P1 applied` / `P1 set it to X, the host has Y; resyncing` | |
| WORLD-6 | Citizen flips switches/lights/TV near the client | Streams; client's own local switching suppressed there | host `WORLD` switch lines | |
| WORLD-7 | Elevator: client presses call + rides | Host drives the car; floor/position/doors streamed; drift snaps | `LIFT … called to floor …`, `car snapped to the host's height` | |
| WORLD-8 | Two players in one car between floors | Both ride together; exiting mid-floor sane | eyes | |
| WORLD-9 | Client rides up, host reboots... leave: no floating in shaft after leave | Clean release | — | |
| WORLD-10 | Weather change (rain → clear) | Both machines same weather within seconds | `WORLD weather from host: rain …` | |
| WORLD-11 | Fight blood / wound spatter | Host blood → client; client's local blood near own player → host; stray local blood removed | `BLOOD …` lines | |
| WORLD-12 | Trash cleanup day passes | Client never deletes items the host kept (clients' cleanup off) | no ghost-item removals client-side | |
| WORLD-13 | Newspaper day change | Same headline/murder on both | `NEWS paper from host applied: …` | |
| WORLD-14 | Runtime spawns: dropped/generated items (newspaper, flyers, case items) | Same id everywhere; client spawns live in the client's id range | `SPAWN applied … #… from P…` | |
| WORLD-15 | Take building blueprints / share a key (either side) | Shared to the other player | `MAP … took the blueprints`, `MAP … shared a key to …` | |
| WORLD-16 | Reveal blueprint of a room on the map | Both maps reveal it | `MAP blueprint reveal failed` = bug | |
| WORLD-17 | Hotel room: rent (client), sleep | Money per player; bed/rest rules as TIME-7 | eyes | |
| WORLD-18 | Bank interior exists in city (optional) | Just wander: doors/items/security per WORLD rules; robbing = CRIM+SEC combo, heavy — record only | ~ | |
| WORLD-19 | Long walk across the city as client (street level) | Continuous world matches host where they overlap; far streets = own sim, allowed to differ | ~ boundary check | |

## 12. Money, profile, progression (MONEY)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| MONEY-1 | Client buys items at a shop | Client's own money decreases; host unaffected | eyes | |
| MONEY-2 | Case hand-in reward (either player) | Rule: everyone gets full reward (`PayEveryone`) — both wallets grow | MONEY check both | |
| MONEY-3 | Leave and rejoin within the same session | Money/inventory/lockpicks intact | `PROFILE saved …` / `restored from …` | |
| MONEY-4 | Rejoin days later (profile file persisted) | Same: keyed by host SteamID + city, survives game restarts | `BepInEx/sodcoop_profiles/` | |
| MONEY-5 | Join a *different* host/city | Separate profile; never sees host A's earnings | profile file names | |
| MONEY-6 | First-ever join ("no profile yet") | Starts with the save's money, **no** sync disks; log says so | `PROFILE no profile for this host world yet: …` | |
| MONEY-7 | Client buys lockpicks, uses 5 | Counts per player; host's lockpicks unchanged | eyes | |
| MONEY-8 | Client's apartment/upgrades from own single-player | Do not leak into host world or vice versa | ~ observation | |
| MONEY-9 | Social credit changes (client fined) | Per player; the host's credit untouched | STATUS lines | |
| MONEY-10 | Inventory across join: client held items in own save | Moved to host puppet at join, client inventory emptied cleanly | `SNAPSHOT emptied … slot(s)`, `handed to the host's body` | |

## 13. Notifications (NOTIFY)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| NOTIFY-1 | Partner finds key evidence / takes a job / gets KO'd / case solved | Game-message toasts appear for the other player | log: `NOTIFY …` | |
| NOTIFY-2 | Spam test: partner opens/closes board repeatedly | No toast spam flooding the HUD | ~ | |
| NOTIFY-3 | Toast text names the right player (Steam name) | Correct names, no `P1`-style ids in UI | eyes | |

## 14. Robustness and disconnects (ROB)

| ID | Step | Expected | Check | Res |
|---|---|---|---|---|
| ROB-1 | Client Ctrl+Delete mid-everything (board open, dialog open, dragging item, riding lift) | Clean solo state every time; no stuck pause, no stranded puppet | do each once | |
| ROB-2 | Host Ctrl+Delete | Client: session reset — own body back, puppets released, host-pause lifted, save menu usable again | client log: `NET host timed out` only if killed | |
| ROB-3 | **Kill the host** (process/Steam quit) | Client times out (~10 s) and returns to playable solo | `NET host timed out` | |
| ROB-4 | Kill the client | Host removes body/marker, world continues, no exception | `NET … dropped` | |
| ROB-5 | Steam relay failure during session (toggle network path if possible) | Either survives on relay changeover or fails with a clean message | `NET steam session with …` transitions | |
| ROB-6 | Join abort mid-snapshot (Ctrl+Delete at 50% transfer) | Both sides clean; retry succeeds | JOIN-1 rerun | |
| ROB-7 | Host alt-tab / Steam overlay popups during transfer | Transfer completes | JOIN-1 | |
| ROB-8 | Rejoin x5 in a row (CONN+JOIN loop) | No ghost bodies, no duplicate markers, no leak in overlay peer list | watch eyes + `DOUBLE` lines | |
| ROB-9 | Two hosts in the friends list simultaneously | Deterministic pick (search finds N lobbies — log), no crash; document which is chosen | `NET found … friend lobby(s)` | |
| ROB-10 | Client Save/Load menu during session | Hard-blocked | `MENU blocked: clients can't …` | |
| ROB-11 | Host quicksave (F5) during session | Still works, own save only, no client impact | — | |
| ROB-12 | Host loads a different save during a session (if menu reachable) | Record behavior; ideally clients get a fresh snapshot or a clean disconnect | ~ record | |
| ROB-13 | High latency (both machines on Wi-Fi/relay ≥ 150 ms) | Playable; puppet interpolation smooth-ish; document | ~ | |
| ROB-14 | Packet loss (if you can netem 5–10% UDP) | Reliable messages survive; poses drop gracefully | ~ optional | |
| ROB-15 | 2 h continuous session (LONG) | No drift accumulation: spot-check 20 doors/items/case state; memory steady; logs rotate | LONG-1..3 | |
| ROB-16 | Log size growth | No runaway spam at default `VerboseLog=false` | `ls -l sodcoop.log` | |
| ROB-17 | Uninstall after testing (friend build) | Delete `winhttp.dll` → vanilla game, no other residue | FRIEND_README uninstall | |

## 15. Regressions (REG) — fixed bugs, check them each release

| ID | Fixed in | Bug | Check | Res |
|---|---|---|---|---|
| REG-01 | 0.9.38+ | Partner renders as only head/hands/feet (outfit not loaded after jump/outfit-swap) | Rejoin, elevator jump >10 m, day-night outfit swap; watch `{Owner} body outfit … renderers n/n`, `reloaded (far/empty)` | |
| REG-02 | 0.9.27 | Client freeze at "loading the host's world" (IL2CPP Boehm GC `SuspendThread loop failed`) | Join 5+ min after menu; join twice; GC toggling must log on and off | `SNAPSHOT garbage collector off/on again` | |
| REG-03 | 0.9.3/0.9.10 | Client's premature objective completion wipes the crime-scene street | CASE-8/9; compare to solo behavior | |
| REG-04 | 0.9.26 | Client can't walk after closing the case board | Search a victim → board → close → walk; also with a transition finishing behind the board | `PAUSE movement …` watchdog lines | |
| REG-05 | 0.9.11/0.9.14 | Client light-switch flips lost to the reach check; second click sound | WORLD-5, incl. a switch whose position is metres away (ceiling light) | |
| REG-06 | 0.9.12/0.9.13 | Host's repeated case sends rebuild the client's board mid-edit and eat pins | CASE-12/13 | |
| REG-07 | 0.8.0 | Case id collision: client case overwrote the murder case | CASE-18 | |
| REG-08 | 0.7.4 | Doppelgänger: own stand-in visible | BODY-12/13 | |
| REG-09 | 0.9.30 | Door lock desync after absence | WORLD-2 | |
| REG-10 | 0.9.28/0.9.29 | Broken windows / police tape / barged doors / breakers only existing on one side | CRIM-4/6, SEC-3 | |
| REG-11 | 0.7.0 | Victim corpse appears in the client's wrong location | Compare corpse room in a fresh murder on both | `DEATH citizen … moved to the host's crime scene` | |
| REG-12 | 0.9.19/0.9.24 | Menu header/footer lines sit wrong with the extra buttons (per resolution) | SET-7 incl. resolution change mid-run | |
| REG-13 | 0.9.32/0.9.37 | Vanilla menu background leaks (splash vs menu scene) | SET-6, cold boot both machines | |
| REG-14 | 0.9.0 | KO recovery stuck when partner is awake | FIGHT-7 (20 s exactly-ish) | |
| REG-15 | 0.4.0 | Pause: world froze when one player opened a menu; paused player got stuck (`ResumeGame` guard) | TIME-2/3/4/5 | |
| REG-16 | 0.9.0 | Story mode hosting | Warning shown when hosting non-sandbox; document that it's not blocked | `{msg} [sandboxMode=False]` |
| REG-17 | 0.7.0 | Murder director picks a player body as victim | Multi-hour session; no `murder` on puppet ids | BODY-14 |
| REG-18 | 0.9.26+ | "Nothing holding it" watchdogs firing spuriously (movement/look/pause forced back on) | Normal play for an hour: watchdog lines should be absent | `PAUSE … nothing holding it` |
| REG-19 | 0.6.x | Held item on puppet stuck after partner pockets it | BODY-5 rapid draw/pocket | |
| REG-20 | 0.8.0 | Carried-object physics: thrown object waits/twitches before settling | FIGHT-4 | |

## 16. Known drift (DRIFT) — must not crash, divergence is accepted

| ID | Area | What to do | Expected = quiet divergence | Res |
|---|---|---|---|---|
| DRIFT-1 | Own apartment, furniture, decor | Client visits own apartment (pre-existing save) | Visible only client-side; nothing breaks; entering own bed ok | |
| DRIFT-2 | Witness memory ("have you seen X?") | Both ask the same citizen about the partner | Answers can differ; never hang/crash | |
| DRIFT-3 | Dialog memory / random answers | Same citizen, same topic, both machines | Differ OK | |
| DRIFT-4 | Story mode | Host a story chapter deliberately once | Warning shown; if anything catastrophic, that's the reason it's unsupported — record, don't fix-play | |
| DRIFT-5 | Passcodes found by one player | Client finds a code, types it on a keypad the host never saw | Works locally (trust) — knowledge NOT shared; decision still open | |
| DRIFT-6 | Achievements / progression | Both play normally | Host gets them; client's Steam achievements — record what happens | |
| DRIFT-7 | TV schedules / radio | Watch TV both sides same channel | May differ; OK | |
| DRIFT-8 | v-mail / message threads | Own phone messages | Per machine; OK | |
| DRIFT-9 | After leaving a session | Client left in the host's world snapshot | Save menu state sane, can quit to menu, own saves untouched — record if the leftover world can be saved (should not pollute) | |
| DRIFT-10 | Far-away city simulation | Both players live in different districts for hours | Far streets differ (own sims) — that's by design; only the near radius must match | |

## 17. Long-session / world-progression (LONG)

| ID | Step | Expected | Res |
|---|---|---|---|
| LONG-1 | 2+ h session, normal play | Perf steady (`PERF` spot checks), no memory bloat, no log spam | |
| LONG-2 | Cross day rollover | News sync (WORLD-13), new cases (CASE-20), killer may strike again — murder + death sync (FIGHT-11/CASE-11) | |
| LONG-3 | Full in-game day: citizens' schedules | Routines visible on client at radius (commute, work, night-out) | |
| LONG-4 | 3 in-game days, both players earn | Money profiles stable (MONEY-3/4) | |
| LONG-5 | Desync audit at end: pick 10 random doors, 10 items, case board, weather, newspaper | All identical in overlapping areas | |

---

## Smoke round (~15 minutes, run after every build)

SET-2/3 → CONN-1/2 → JOIN-1/2/8 → BODY-1/5/12 → TIME-2/4 → CASE-11/12 → CRIM-1 → FIGHT-1/5 →
WORLD-1/5/7 → REG-01/02/04 → NOTIFY-1 → ROB-13 (if over relay).

Everything green → full area rounds as time allows. Suggested full-round order after a
release: 1 Connection → 2 Join → 5 Case loop → 8 Crime → 11 World → the rest.

## Defect entry template

```
### DEFECT-NN — short title
- Test: <ID>   Round: <name>   Date: <date>
- Build: mod <ver> / protocol v<N> / game <ver>   Host OS: <..>  Client OS: <..>  link: direct|relay, ping <ms>
- City / scenario:
- Steps (numbered, reproducible):
- Expected:            Observed:
- First bad build (if bisected):
- Log excerpts — host:   client:   (quote ±10 lines around the failure)
- Files attached: sodcoop.log + prev + LogOutput from BOTH machines, screenshots
- Notes / suspected area: <Net/XSync.cs, Patches/…>
```

## Log-line cheat sheet (prefix → meaning)

| Prefix | Meaning |
|---|---|
| `NET …` | session lifecycle, peers, damage, illegal-status |
| `SNAPSHOT …` | join transfer, world load, GC guard, temp save |
| `PROFILE …` | per-player money/inventory/disks persistence |
| `OBJ / CASE / NOTIFY` | objective facts, case records & board, toasts |
| `JOB / PHONE / DIALOG` | side jobs, telephony, conversation choices |
| `TALK` | conversation lock (one citizen, one talker) |
| `ITEM / SPAWN / FORENSIC / BLOOD / NEWS` | object lifecycle & world content |
| `DOORS / WORLD / LIFT` | doors, weather/windows/alarms/breakers, elevators |
| `CIT / TRESPASS / SEES-ILLEGAL / PURSUED-BY / STATUS` | citizen AI, crime, wanted & fines |
| `PAUSE / TIME / REST` | pause rule, clock sync, sleep/wait/KO time |
| `KO / DAMAGE / ATTACK / DRAG / GADGET` | combat |
| `MENU / MAP` | main-menu UI, markers/blueprints/keys |
| `ROOMS / DOUBLE / POSE` | room loading, stand-in hiding, pose pipeline |
| `PATCH / PERF / DIAG / TRACE` | diagnostics (DIAG/TRACE need `[Debug] VerboseLog=true`) |
