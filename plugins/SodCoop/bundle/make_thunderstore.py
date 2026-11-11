"""Thunderstore package for Doubt Together (Co-op): the plugin DLL plus a manifest, so a mod manager installs
it next to BepInExPack automatically. The full BepInEx bundle for manual installs stays in make_bundle.py.

usage: python3 -I make_thunderstore.py [OUT_DIR]
Needs the built plugin: dotnet build -c Release in plugins/SodCoop (or a ./build-local.sh deploy).
Upload at thunderstore.io > Upload package (the icon is picked there, not from the zip); pick the Mods category
and let the uploader validate manifest.json.
"""
import json
import os
import re
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_DIR = os.path.dirname(HERE)
REPO = os.path.dirname(os.path.dirname(PLUGIN_DIR))
out_dir = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, "dist")

NAME = "DoubtTogether"  # thunderstore names are letters, digits and underscores only
PRETTY = "Doubt Together (Co-op)"
WEBSITE = "https://github.com/0xsimsch/mp_sod"
DESCRIPTION = "Two-player co-op over Steam: one player hosts their sandbox world, a friend joins, and both " \
              "work the same cases in the same city."
# BepInExPack for Shadows of Doubt: the manager installs it before this. If the uploader rejects the
# owner/name strings it wants package UUIDs instead — thunderstore.io/c/shadows-of-doubt/p/BepInEx/BepInExPack_IL2CPP.
DEPENDENCIES = ["BepInEx/BepInExPack_IL2CPP"]


def mod_version():
    src = open(os.path.join(PLUGIN_DIR, "Net", "NetSession.cs"), encoding="utf-8").read()
    return re.search(r'ModVersion = "([^"]+)"', src).group(1)


def main():
    version = mod_version()
    dll = os.path.join(PLUGIN_DIR, "bin", "Release", "net6.0", "SodCoop.dll")
    if not os.path.exists(dll):  # build-local.sh deploys straight into the game
        dll = os.path.expanduser("~/.local/share/Steam/steamapps/common/Shadows of Doubt/BepInEx/plugins/SodCoop.dll")
    readme, license = os.path.join(HERE, "THUNDERSTORE_README.md"), os.path.join(REPO, "LICENSE")
    for need in (dll, readme, license):
        if not os.path.exists(need):
            sys.exit(f"missing {need} (build the plugin first)")

    # One version everywhere: the manifest mirrors ModVersion, because both players need the same mod version.
    manifest = {
        "name": NAME,
        "pretty_name": PRETTY,
        "version": version,
        "description": DESCRIPTION,
        "website": WEBSITE,
        "game_url": "https://thunderstore.io/c/shadows-of-doubt/",
        "dependencies": DEPENDENCIES,
    }
    os.makedirs(out_dir, exist_ok=True)
    out = os.path.join(out_dir, f"{NAME}-{version}.zip")
    with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        z.writestr("manifest.json", json.dumps(manifest, indent=2) + "\n")
        z.write(dll, "BepInEx/plugins/SodCoop.dll")  # the manager unzips this onto the game folder
        z.write(readme, "README.md")  # becomes the package page text
        z.write(license, "LICENSE.txt")
    print(f"{out}: {os.path.getsize(out) / 1e6:.1f} MB ({PRETTY} {version})")


if __name__ == "__main__":
    main()
