# SodCoop: co-op for Shadows of Doubt

An unofficial two-player co-op mod for [Shadows of Doubt](https://store.steampowered.com/app/986130/Shadows_of_Doubt/) (game version v42.04). One player hosts their own sandbox world and a friend joins over Steam. Both play in the same city, work the same case and see each other. Published to players as **Doubt Together (Co-op)**; code, plugin GUID and file names stay `SodCoop`. Licensed under [GPL-3.0-or-later](LICENSE).

**Status: early test build.** Many systems are synced, but most were never tested in a real two-player session. Expect rough edges.

Not affiliated with ColePowered Games or Fireshine Games. This repository contains no game files or decompiled game code. `lib/refs` holds stripped reference copies of the game's BepInEx interop assemblies (type and method signatures only, every method body removed), so the mod can be compiled without a game install.

## What is synced

The host is authoritative: it runs the city, citizens, murders and side jobs. Clients load a snapshot of the host's world when they join, then receive updates.

- **Players:** position, look direction, held item, crouching, sitting/lying, activities (lockpicking, typing, phone, …), flashlight, map markers. The world pauses only when every player is paused.
- **Case work:** the case and case board (pins, strings, colors, notes, positions), evidence and facts, objectives, hand-in and resolution, side jobs.
- **Citizens:** posture, KO, damage in both directions, speech, attacks, awareness. The host simulates the citizens near every player.
- **World:** doors, items (pick-up, move, throw, delete), switches, weather, windows, alarms, cameras, breakers, elevators, phones, blood, newspapers.
- **Per player:** money and inventory are kept per player, keyed by SteamID.
- **UI:** "Host Co-op" button and a list of friends who are hosting ("Join <name>") in the main menu, a connection overlay, and a third-person view of your partner while you recover from a KO.

Story mode isn't supported (a warning is shown). Apartments and decor, witness memory and dialog memory are still per machine. The list of what's done and what's open is in [`analysis/SYNC_PRIORITIES.md`](analysis/SYNC_PRIORITIES.md).

## Install and play

Three channels, same mod in all of them. **Both players need the same version** (checked when they connect), and the game needs to be version v42.04 (bottom right of the main menu). Each channel brings the mod runtime (BepInEx 6 IL2CPP) itself.

1. **Steam Workshop** — subscribe, paste one launch line; a bootstrap installs the bundled loader on every start (only where nothing is there yet, so an existing mod setup survives). Windows and Linux/Steam Deck. Item page: *(link once uploaded)*.
2. **Thunderstore** — install **Doubt Together (Co-op)** with a mod manager from [thunderstore.io/c/shadows-of-doubt](https://thunderstore.io/c/shadows-of-doubt/); it pulls BepInExPack_IL2CPP in as a dependency.
3. **GitHub release, no mod manager** — download `DoubtTogether-<version>-with-bepinex.zip` from [Releases](https://github.com/0xsimsch/mp_sod/releases/latest) and unzip it INTO the game folder (Steam > right click > Manage > Browse local files), so that `winhttp.dll` ends up next to "Shadows of Doubt.exe"; **BepInEx is inside the zip**. On Linux/Steam Deck (Proton) also set the launch option `WINEDLLOVERRIDES="winhttp=n,b" %command%`. The first start takes a few minutes longer while the loader prepares itself. The full player instructions are in `DoubtTogether-README.txt` inside the zip.

Then: the host loads a sandbox save and clicks **Host Co-op** (or presses F11). The friend sees the host in the main menu and clicks **Join <name>**, or uses "Join game" in the Steam friends list.

### Packaging (dev)

Three scripts cut the three channels from the same build:

- `python3 -I plugins/SodCoop/bundle/make_bundle.py` → the manual-install zip of channel 3 (this is what the GitHub release attaches); its player instructions are [`plugins/SodCoop/bundle/PLAYER_README.txt`](plugins/SodCoop/bundle/PLAYER_README.txt).
- `python3 -I plugins/SodCoop/bundle/make_thunderstore.py` → the Thunderstore package (manifest with BepInExPack as dependency, page text `THUNDERSTORE_README.md`).
- `python3 -I plugins/SodCoop/bundle/make_workshop.py` → the Steam Workshop item (bundled loader + per-OS bootstrap scripts, `ModSettings.txt`, page text `plugins/SodCoop/bundle/workshop/ITEM_PAGE.bbcode`; `--install-local` stages the folder for the in-game upload button).

Uploads to the stores stay manual; store icon: [`plugins/SodCoop/Assets/doubt-together-icon-256.png`](plugins/SodCoop/Assets/doubt-together-icon-256.png).

Dev-machine rule in the workshop era: keep your own workshop item **unsubscribed** while developing, or the bootstrap reinstalls the subscribed (old) DLL over your `game/BepInEx/plugins` copy on every start and you chase ghosts.

## Build

Requires the .NET 8 SDK and the game with BepInEx installed and run once, so that `BepInEx/interop` exists. The project looks for the game in `~/.local/share/Steam/steamapps/common/Shadows of Doubt`. To use another location, pass `-p:GameDir=...`.

```
cd plugins/SodCoop
dotnet build -c Release          # also copies SodCoop.dll into the game's BepInEx/plugins
cd ../SodCoop.Tests && dotnet run   # protocol and transport tests, no game needed
```

## Releases

`.github/workflows/release.yml` builds the mod, runs the tests and packages the manual-install zip on GitHub. Pushing a tag `vX.Y.Z` that matches `ModVersion` in `plugins/SodCoop/Net/NetSession.cs` publishes a GitHub release with `DoubtTogether-<version>-with-bepinex.zip` (BepInEx included, channel 3 above). Running the workflow by hand only uploads the zip as a build artifact.

It compiles against `lib/refs`, so it needs no game install and no secrets. When the game updates, or `SodCoop.csproj` gets a new game reference, regenerate the references and commit them:

```
python3 -I plugins/SodCoop/bundle/make_refs.py   # strips the game's interop DLLs into lib/refs (plugins/RefStrip, Mono.Cecil)
```

To build locally without the game installed: `dotnet build plugins/SodCoop -c Release -p:InteropDir=$PWD/lib/refs -p:CopyToGame=false`. You also need BepInEx's core DLLs, set with `-p:GameDir=`.

## Repository layout

| Path | Contents |
|---|---|
| `plugins/SodCoop/` | the mod. `Net/` holds the session, transports and one `*Sync.cs` per game system; `Patches/` holds Harmony patches; `Puppet/` holds remote player bodies. Development log: [`README.md`](plugins/SodCoop/README.md) |
| `plugins/SodCoop.Tests/` | console tests for the wire protocol and the UDP transport |
| `plugins/RefStrip/` | strips method bodies from assemblies, used to make `lib/refs` |
| `lib/refs/` | stripped game references for building without the game |
| `plugins/SodProbe/` | the measurement plugin used for the early feasibility tests |
| `analysis/` | design notes, research results, and tools: a disassembly helper, fake UDP host/client, and a game driver for unattended tests on Linux |
| `determinism_test/`, `probe_results/` | results of the early tests (city generation determinism, AI tick rate, room loading) |

The analysis tools expect a decompiled copy of the game in `decompiled/` and the game files in `game/`. Neither is part of the repository.
