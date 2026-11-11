"""Steam Workshop package for Doubt Together (Co-op): SodCoop.dll plus a bundled BepInEx loader and a bootstrap
script per OS, so subscribing + one Steam launch option installs everything. The game's own workshop loader only
reads ModSettings.txt and never runs code - the bootstrap scripts here do that (ITEM_PAGE.bbcode is the honest
description users get).

usage: python3 -I make_workshop.py [--item-id N] [--install-local] [GAME_DIR] [OUT_DIR]
  GAME_DIR       source of the BepInEx loader to bundle; default: the Steam install (start it once first)
  OUT_DIR        default <repo>/dist
  --item-id N    bake the published item id into the launch lines; before the first upload the id is unknown,
                 and the output carries a <ITEM_ID> placeholder to replace by hand
  --install-local  also place the assembled folder where the in-game Mods menu finds it, ready for its upload
                   button. Updates stay manual: re-upload from the game (workshop has no CI story).

The interop assemblies stay out of this public package: they are generated from the game's own code and every
player's loader regenerates them on the first start. For the mod-manager route use make_thunderstore.py, for the
no-mod-manager GitHub release zip use make_bundle.py - all three take the same built plugin.
"""
import os
import shutil
import sys
import zipfile

HERE = os.path.dirname(os.path.abspath(__file__))
PLUGIN_DIR = os.path.dirname(HERE)
REPO = os.path.dirname(os.path.dirname(PLUGIN_DIR))
TPL_DIR = os.path.join(HERE, "workshop")

# Reuse make_bundle's loader file lists and mod_version() without running its zip build (it parses argv at import).
sys.path.insert(0, HERE)
_saved_argv = sys.argv
sys.argv = [_saved_argv[0]]
import make_bundle as loader_files  # noqa: E402

sys.argv = _saved_argv

MODS_DIR_LOCALLOW = ("~/.local/share/Steam/steamapps/compatdata/986130/pfx/drive_c/users/"
                     "steamuser/AppData/LocalLow/ColePowered Games/Shadows of Doubt/Mods")
ITEM_NAME = "DoubtTogether"


def parse_args():
    argv = sys.argv[1:]
    item_id = "<ITEM_ID>"
    if "--item-id" in argv:
        try:
            item_id = argv[argv.index("--item-id") + 1]
        except IndexError:
            sys.exit("--item-id wants the number from the workshop page URL")
    install_local = "--install-local" in argv
    pos = [a for i, a in enumerate(argv) if not a.startswith("--") and (i == 0 or argv[i - 1] != "--item-id")]
    game = pos[0] if pos else loader_files.game
    out_dir = pos[1] if len(pos) > 1 else os.path.join(REPO, "dist")
    return item_id, install_local, game, out_dir


def render(name, item_id, version, eol="\n"):
    """A workshop text template with __VERSION__ and __ITEM_ID__ filled in and the wanted line endings."""
    text = open(os.path.join(TPL_DIR, name), encoding="utf-8").read()
    text = text.replace("__VERSION__", version).replace("__ITEM_ID__", item_id)
    return text.replace("\r\n", "\n").replace("\n", eol)


def main():
    item_id, install_local, game, out_dir = parse_args()
    version = loader_files.mod_version()
    dll = os.path.join(PLUGIN_DIR, "bin", "Release", "net6.0", "SodCoop.dll")
    if not os.path.exists(dll):  # build-local.sh deploys straight into the game
        dll = os.path.join(game, "BepInEx", "plugins", "SodCoop.dll")
    needs = [dll, os.path.join(game, "winhttp.dll"), os.path.join(game, "doorstop_config.ini"),
             os.path.join(game, ".doorstop_version"), os.path.join(game, "BepInEx", "core", "BepInEx.Core.dll")]
    for need in needs:
        if not os.path.exists(need):
            sys.exit(f"missing {need} (build the plugin / start the game once with BepInEx first)")

    item_dir = os.path.join(out_dir, "workshop", ITEM_NAME)
    if os.path.exists(item_dir):
        shutil.rmtree(item_dir)
    os.makedirs(item_dir)

    # Text templates to the item root. .cmd must leave as CRLF: cmd.exe and findstr get picky otherwise.
    for name, eol in [("ModSettings.txt", "\n"), ("SoDCoop.sh", "\n"), ("SoDCoop.cmd", "\r\n"),
                      ("LAUNCH.txt", "\r\n"), ("README-PLAYERS.txt", "\r\n")]:
        with open(os.path.join(item_dir, name), "w", encoding="utf-8", newline="") as f:
            f.write(render(name, item_id, version, eol))
    # The Steam item description is page text, not part of the upload; next to the zip for the paste.
    page = os.path.join(out_dir, "workshop", "ITEM_PAGE.bbcode")
    with open(page, "w", encoding="utf-8", newline="") as f:
        f.write(render("ITEM_PAGE.bbcode", item_id, version))

    # The loader payload: the same tree make_bundle ships, with configs written below and interop left out.
    count = 0

    def add_file(path, arc):
        nonlocal count
        target = os.path.join(item_dir, arc)
        os.makedirs(os.path.dirname(target), exist_ok=True)
        shutil.copy2(path, target)
        count += 1

    def add_tree(rel):
        root = os.path.join(game, rel)
        if not os.path.isdir(root):
            return
        for dirpath, _, files in os.walk(root):
            for f in files:
                p = os.path.join(dirpath, f)
                add_file(p, os.path.relpath(p, game))

    for f in loader_files.TOP_FILES:
        p = os.path.join(game, f)
        if os.path.exists(p):
            add_file(p, f)
    for d in loader_files.TOP_DIRS + [os.path.join("BepInEx", b) for b in loader_files.BEPINEX_DIRS]:
        add_tree(d)
    # patchers is empty but the scripts expect the folder; a placeholder keeps it through zip + workshop.
    os.makedirs(os.path.join(item_dir, "BepInEx", "patchers"), exist_ok=True)
    with open(os.path.join(item_dir, "BepInEx", "patchers", ".keep"), "w", encoding="utf-8") as f:
        f.write("empty on purpose: BepInEx fills this in\n")
    # Only the settings that differ from BepInEx's defaults; the game's first start fills in the rest.
    os.makedirs(os.path.join(item_dir, "BepInEx", "config"), exist_ok=True)
    with open(os.path.join(item_dir, "BepInEx", "config", "BepInEx.cfg"), "w", encoding="utf-8", newline="") as f:
        f.write(loader_files.BEPINEX_CFG)
    with open(os.path.join(item_dir, "BepInEx", "config", "mp_sod.coop.cfg"), "w", encoding="utf-8", newline="") as f:
        f.write(loader_files.CONFIG)
    add_file(dll, os.path.join("BepInEx", "plugins", "SodCoop.dll"))
    shutil.copytree(os.path.join(TPL_DIR, "ThirdParty"), os.path.join(item_dir, "ThirdParty"))
    count += sum(len(files) for _, _, files in os.walk(os.path.join(TPL_DIR, "ThirdParty")))

    # A zip with the same tree: release attachment, and proof of what a subscription contains.
    os.makedirs(out_dir, exist_ok=True)
    out_zip = os.path.join(out_dir, f"{ITEM_NAME}-{version}-workshop.zip")
    with zipfile.ZipFile(out_zip, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as z:
        for dirpath, _, files in os.walk(item_dir):
            for f in files:
                p = os.path.join(dirpath, f)
                z.write(p, os.path.relpath(p, item_dir))

    print(f"{item_dir}: {count} files")
    print(f"{out_zip}: {os.path.getsize(out_zip) / 1e6:.1f} MB (Doubt Together {version}, item id {'baked: ' + item_id if item_id != '<ITEM_ID>' else 'placeholder'})")
    print(f"item page text: {page}")
    if install_local:
        mods = os.path.expanduser(MODS_DIR_LOCALLOW)
        if not os.path.isdir(mods):
            sys.exit(f"--install-local: not a Steam/Proton install: {mods}")
        target = os.path.join(mods, ITEM_NAME)
        if os.path.exists(target):
            shutil.rmtree(target)
        shutil.copytree(item_dir, target)
        print(f"installed for upload: {target}")
        print("next: start the game > Mods menu > Doubt Together (Co-op) > Upload; put the item id it gives you")
        print("into the next build (--item-id) so LAUNCH.txt and the item page carry the real number.")


if __name__ == "__main__":
    main()
