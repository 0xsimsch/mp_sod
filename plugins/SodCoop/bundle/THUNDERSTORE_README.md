# Doubt Together (Co-op)

Two-player co-op for Shadows of Doubt over Steam: one player hosts their sandbox world, a friend joins it,
and both work the same cases in the same city — the same evidence, the same citizens, the same clock.

## What is shared

- **Case work**: evidence and facts, the case board (pins, strings, notes), objectives, hand-in and verdict.
  What one player finds counts for both.
- **The world**: doors, locks, switches, items (pick up, drop, throw), alarms, blood, weather, deaths.
- **The city**: the host simulates it, so both see the same people in the same places. Citizens react to
  both players; fights and damage go both ways.
- **Time**: one clock. It speeds up only when everyone rests; a knocked-out partner never skips your time.
- **Your partner**: a full character in the world — you see them, hear their footsteps, and can watch them
  in third person while you recover from a knock-out.
- Money and inventory stay per player.

## Requirements

- Game version v42.04 (shown at the bottom right of the main menu).
- **Both players need this mod, in the same version.**
- BepInEx: the mod manager installs it automatically (BepInExPack_IL2CPP dependency).

## Install

With a mod manager: install "Doubt Together (Co-op)" and start the game through the manager.
Manually: take `DoubtTogether-<version>-with-bepinex.zip` from the [GitHub releases](https://github.com/0xsimsch/mp_sod/releases)
(BepInEx + the mod inside) into the game folder; on Linux/Steam Deck also set the launch option
`WINEDLLOVERRIDES="winhttp=n,b" %command%`.

The first start after installing takes a few minutes longer while BepInEx prepares itself.

## Play

1. The host loads (or starts) a **sandbox** save and presses F11 — or uses the "Host Co-op" button in the
   main menu.
2. The friend sees the host in their main menu as "Join <name>" and clicks it (F11 joins the first host
   found), or right-clicks them in the Steam friends list > "Join game".
3. Wait a minute: the host's world is transferred and loads by itself. You keep your own money and inventory.

Ctrl+Delete leaves the session. Story mode is not supported (a warning appears). Lobbies are friends-only.

## Troubleshooting

`BepInEx/sodcoop.log` in the game folder tells both players what happened. Please attach it to bug reports:
[github.com/0xsimsch/mp_sod/issues](https://github.com/0xsimsch/mp_sod/issues).

## Privacy

Everything runs peer-to-peer between the two of you over Steam — no servers. Your partner sees your Steam
name (or the name in the mod's config), your character and your world actions.

## License

This project is open source: [github.com/0xsimsch/mp_sod](https://github.com/0xsimsch/mp_sod).
**Contributors:** 0xsimsch, Hunter99

GPL-3.0-or-later. Unofficial fan mod; not affiliated
with ColePowered Games or Fireshine Games.
