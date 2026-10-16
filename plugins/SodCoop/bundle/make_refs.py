"""Zip the game's BepInEx interop DLLs that SodCoop compiles against, for the GitHub release workflow.

They are generated from the game's own code, so they are not in the repository. Upload the zip somewhere
private and put its download URL in the repository secret GAME_REFS_URL (see .github/workflows/release.yml).
Make a new one whenever the game updates.

usage: python3 -I make_refs.py [GAME_DIR] [OUT_ZIP]
  GAME_DIR  default ~/.local/share/Steam/steamapps/common/Shadows of Doubt (BepInEx must have run once)
  OUT_ZIP   default <repo>/dist/sodcoop-game-refs.zip
"""
import os
import re
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_DIR = os.path.dirname(HERE)
REPO = os.path.dirname(os.path.dirname(PLUGIN_DIR))

game = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/.local/share/Steam/steamapps/common/Shadows of Doubt")
out = sys.argv[2] if len(sys.argv) > 2 else os.path.join(REPO, "dist", "sodcoop-game-refs.zip")


def main():
    csproj = open(os.path.join(PLUGIN_DIR, "SodCoop.csproj"), encoding="utf-8").read()
    names = re.findall(r'Include="\$\(GameDir\)/BepInEx/interop/([^"]+)"', csproj)
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
        for n in names:
            p = os.path.join(game, "BepInEx", "interop", n)
            if not os.path.exists(p):
                sys.exit(f"missing {p} (start the game once with BepInEx first)")
            z.write(p, n)
    print(f"{out}: {len(names)} DLLs, {os.path.getsize(out) / 1e6:.1f} MB. Keep it private.")


if __name__ == "__main__":
    main()
