#!/bin/sh
# Doubt Together (Co-op) bootstrap for Linux / Steam Play (Proton).
#
# Steam runs this from the game folder with its real launch command appended (Steam > Properties > Launch options):
#   sh "../../workshop/content/986130/__ITEM_ID__/SoDCoop.sh" %command%
#
# What it does, in order:
#  - if the in-game Mods menu switched this item off: remove the loader files it had installed,
#    then exec Steam's real command so the plain game still starts;
#  - otherwise copy the bundled BepInEx loader into the game folder (only where nothing is there yet,
#    so an existing mod-manager setup is never overwritten), always refresh SodCoop.dll,
#    add the WINEDLLOVERRIDES needed under Proton, and exec Steam's real launch command ("$@").
#
# Unofficial co-op mod. Source (GPL-3.0-or-later): https://github.com/0xsimsch/mp_sod

game="$PWD"
mod="$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)"  # this item's workshop folder

# Every action is logged so a remote bug report has something to attach: Steam shows
# none of the launch option's output. Prefer the game folder, fall back to temp.
logf="${TMPDIR:-/tmp}/sodcoop_bootstrap.log"
if [ -f "$game/Shadows of Doubt.exe" ]; then
    mkdir -p "$game/BepInEx" 2>/dev/null
    [ -d "$game/BepInEx" ] && logf="$game/BepInEx/sodcoop_bootstrap.log"
fi
log() { echo "$(date '+%F %T') $*" >> "$logf" 2>/dev/null; }
log "start: game=$game mod=$mod"

# Safety: only ever write into a real Shadows of Doubt folder.
if [ ! -f "$game/Shadows of Doubt.exe" ]; then
    log "NOT a Shadows of Doubt folder, doing nothing. Was the Windows line pasted here by mistake? The Linux line starts with: sh"
    echo "[DoubtTogether] not in the game folder (cwd is $game), doing nothing"
    if [ "$#" -gt 0 ]; then exec "$@"; fi  # still launch the plain game
    exit 0
fi

# The in-game Mods menu writes its Enabled checkbox back into this file.
if grep -Eq '"enabled"[[:space:]]*:[[:space:]]*false' "$mod/ModSettings.txt" 2>/dev/null; then
    # Remove the mod itself. The loader (winhttp.dll, doorstop_config.ini, dotnet/) only goes too if it is
    # the copy this item installed (same .doorstop_version), never some other mod manager's loader.
    rm -f "$game/BepInEx/plugins/SodCoop.dll"
    if [ -f "$game/.doorstop_version" ] && cmp -s "$game/.doorstop_version" "$mod/.doorstop_version"; then
        rm -f "$game/winhttp.dll" "$game/doorstop_config.ini" "$game/.doorstop_version"
        rm -rf "$game/dotnet"
        log "switched off in the Mods menu, loader removed"
    else
        log "switched off, mod dll removed, loader kept because it is not ours"
    fi
    echo "[DoubtTogether] switched off in the Mods menu, loader removed"
    if [ "$#" -gt 0 ]; then exec "$@"; fi  # still launch the plain game, without our env changes
    exit 0
fi

mkdir -p "$game/BepInEx/plugins"

install_if_missing() {
    if [ ! -e "$game/$1" ]; then
        cp -a "$mod/$1" "$game/$1" 2>>"$logf" || log "FAILED to install $1: the game folder is not writable"
    fi
}
install_if_missing winhttp.dll
install_if_missing doorstop_config.ini
install_if_missing .doorstop_version
[ -d "$game/dotnet" ] || { cp -a "$mod/dotnet" "$game/dotnet" 2>>"$logf" || log "FAILED to install dotnet: the game folder is not writable"; }
for d in core patchers config unity-libs; do
    if [ ! -d "$game/BepInEx/$d" ] && [ -d "$mod/BepInEx/$d" ]; then
        cp -a "$mod/BepInEx/$d" "$game/BepInEx/$d" 2>>"$logf" || log "FAILED to install BepInEx/$d: the game folder is not writable"
    fi
done
# The mod itself: always the version that was subscribed.
cp -f "$mod/BepInEx/plugins/SodCoop.dll" "$game/BepInEx/plugins/SodCoop.dll" 2>>"$logf" \
    || log "FAILED to copy SodCoop.dll into BepInEx/plugins: the game folder is not writable"

# Proton ships its own winhttp; the "n,b" override is what makes the game load the bundled loader.
# Keep any overrides that are already set (Steam's own, the user's).
case "$WINEDLLOVERRIDES" in
    *winhttp*) ;;
    *) WINEDLLOVERRIDES="winhttp=n,b${WINEDLLOVERRIDES:+;$WINEDLLOVERRIDES}" ;;
esac
export WINEDLLOVERRIDES

if [ "$#" -eq 0 ]; then
    log "loader installed, but Steam passed no game command: the launch option still has the old form with the semicolon, WINEDLLOVERRIDES cannot reach the game that way. Use: sh ...SoDCoop.sh %command%"
    echo "[DoubtTogether] loader installed into $game"
    echo "[DoubtTogether] start the game from Steam (the launch option passes Steam's real command to this script)"
    exit 0
fi
log "loader installed, starting the game (WINEDLLOVERRIDES=$WINEDLLOVERRIDES)"
exec "$@"
