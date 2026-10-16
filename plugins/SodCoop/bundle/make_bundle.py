"""Zip everything a friend needs to join a SodCoop session: BepInEx 6 IL2CPP exactly as installed here,
SodCoop.dll and a config set to join over Steam. The friend unzips it into their "Shadows of Doubt" folder.

usage: python3 -I make_bundle.py [--with-interop] [GAME_DIR] [OUT_DIR]
  GAME_DIR        default ~/.local/share/Steam/steamapps/common/Shadows of Doubt (must have BepInEx + SodCoop built)
  OUT_DIR         default <repo>/dist
  --with-interop  also ship BepInEx/interop. It is generated from the game's own code, so by default it is left
                  out and the friend's BepInEx regenerates it on the first start (a few extra minutes).

Build the plugin first: nix shell nixpkgs#dotnet-sdk_8 -c dotnet build -c Release (in plugins/SodCoop).
"""
import os
import re
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_DIR = os.path.dirname(HERE)
REPO = os.path.dirname(os.path.dirname(PLUGIN_DIR))

with_interop = "--with-interop" in sys.argv
args = [a for a in sys.argv[1:] if not a.startswith("--")]
game = args[0] if args else os.path.expanduser("~/.local/share/Steam/steamapps/common/Shadows of Doubt")
out_dir = args[1] if len(args) > 1 else os.path.join(REPO, "dist")

# Game-folder files and folders that make up BepInEx (doorstop loader, .NET runtime, BepInEx itself).
TOP_FILES = ["winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt"]
TOP_DIRS = ["dotnet"]
# unity-libs is downloaded by BepInEx on the first start; it is shipped only if the game folder has it.
BEPINEX_DIRS = ["core", "unity-libs", "patchers"]  # not: plugins (only ours), cache, logs, config (ours); interop on request

BEPINEX_CFG = """[Logging.Console]

## No extra console window next to the game.
Enabled = false
"""

CONFIG = """## SodCoop (mp_sod) settings. F11 in game uses [Net] Mode.

[Net]

## HostSteam: create a friends-only lobby. JoinSteam: join JoinLobby, or with 0 the lobby of a friend who is hosting.
## HostUdp/JoinUdp: plain UDP (LAN tests).
# Setting type: NetMode
# Acceptable values: HostSteam, JoinSteam, HostUdp, JoinUdp
Mode = JoinSteam

UdpPort = 47615

JoinAddress = 127.0.0.1:47615

## 0 = find a friend who is hosting.
JoinLobby = 0

## Name shown to others; empty = your Steam name.
PlayerName =

[Session]

MaxPlayers = 2

[Debug]

VerboseLog = false
"""


def mod_version():
    src = open(os.path.join(PLUGIN_DIR, "Net", "NetSession.cs"), encoding="utf-8").read()
    return re.search(r'ModVersion = "([^"]+)"', src).group(1)


def main():
    dll = os.path.join(PLUGIN_DIR, "bin", "Release", "net6.0", "SodCoop.dll")
    for need in [dll, os.path.join(game, "winhttp.dll"), os.path.join(game, "BepInEx", "interop", "Assembly-CSharp.dll")]:
        if not os.path.exists(need):
            sys.exit(f"missing {need} (build the plugin / start the game once with BepInEx first)")

    version = mod_version()
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, f"SodCoop-{version}-friend.zip")
    count = 0
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        def add(path, arc):
            nonlocal count
            z.write(path, arc)
            count += 1

        for f in TOP_FILES:
            p = os.path.join(game, f)
            if os.path.exists(p):
                add(p, f)
        for d in TOP_DIRS + [os.path.join("BepInEx", b) for b in BEPINEX_DIRS + (["interop"] if with_interop else [])]:
            root = os.path.join(game, d)
            if not os.path.isdir(root):
                continue
            z.writestr(d.replace(os.sep, "/") + "/", "")  # keep empty folders (patchers)
            for dirpath, _, files in os.walk(root):
                for f in files:
                    p = os.path.join(dirpath, f)
                    add(p, os.path.relpath(p, game))
        # Only the settings that differ from BepInEx's defaults; BepInEx fills in the rest on the first start.
        z.writestr("BepInEx/config/BepInEx.cfg", BEPINEX_CFG)
        z.writestr("BepInEx/config/mp_sod.coop.cfg", CONFIG)
        add(dll, "BepInEx/plugins/SodCoop.dll")
        add(os.path.join(HERE, "FRIEND_README.txt"), "SodCoop-README.txt")

    print(f"{out}: {count} files, {os.path.getsize(out) / 1e6:.1f} MB (SodCoop {version}, interop {'included' if with_interop else 'regenerated on first start'})")


if __name__ == "__main__":
    main()
