"""Write lib/refs: stripped reference copies of the game's BepInEx interop DLLs that SodCoop compiles against.

They keep only types and signatures, no method bodies, so the GitHub release workflow can build
without a game install. Run it again (and commit lib/refs) whenever the game updates or SodCoop.csproj gets
a new interop reference.

Uses plugins/RefStrip (Mono.Cecil): every method body becomes "throw null"; types and signatures stay.
Needs the .NET 8 SDK.

usage: python3 -I make_refs.py [GAME_DIR]
  GAME_DIR  default ~/.local/share/Steam/steamapps/common/Shadows of Doubt (BepInEx must have run once)
"""
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_DIR = os.path.dirname(HERE)
REPO = os.path.dirname(os.path.dirname(PLUGIN_DIR))
OUT = os.path.join(REPO, "lib", "refs")


def main():
    args = sys.argv[1:]
    game = args[0] if args else os.path.expanduser("~/.local/share/Steam/steamapps/common/Shadows of Doubt")

    csproj = open(os.path.join(PLUGIN_DIR, "SodCoop.csproj"), encoding="utf-8").read()
    names = re.findall(r'Include="\$\(InteropDir\)/([^"]+)"', csproj)
    sources = [os.path.join(game, "BepInEx", "interop", n) for n in names]
    for p in sources:
        if not os.path.exists(p):
            sys.exit(f"missing {p} (start the game once with BepInEx first)")

    shutil.rmtree(OUT, ignore_errors=True)
    os.makedirs(OUT)
    strip = os.path.join(os.path.dirname(PLUGIN_DIR), "RefStrip")
    subprocess.run(["dotnet", "run", "-c", "Release", "--project", strip, "--", OUT, *sources], check=True)
    made = sorted(os.listdir(OUT))
    if made != sorted(names):
        sys.exit(f"RefStrip wrote {made}, expected {sorted(names)}")
    size = sum(os.path.getsize(os.path.join(OUT, n)) for n in made)
    print(f"{OUT}: {len(made)} reference DLLs, {size / 1e6:.1f} MB")


if __name__ == "__main__":
    main()
