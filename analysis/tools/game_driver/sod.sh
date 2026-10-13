#!/usr/bin/env bash
# Drive the game window with uinput (dotool) + grim screenshots.
#   shot                      screenshot -> view.png (50% scale)
#   click X Y [left|right]    absolute click, full-res screen pixels (3440x1440)
#   do "<dotool cmds>"        raw dotool commands, e.g. "keydown w" "keyup w" "key esc"
#   hold KEY MS               hold a key for MS milliseconds
#   look DX DY                relative mouse motion (camera)
S="${SOD_DRIVER_OUT:-/tmp/sod_driver}"; mkdir -p "$S"; D="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DC=/nix/store/534l408ldgd6iih9z7ak434zqfnnhcmj-dotool-1.6/bin/dotoolc
focus() { niri msg action focus-window --id "$(niri msg -j windows | python3 -I -c "import sys,json;print(next(w[\"id\"] for w in json.load(sys.stdin) if w[\"app_id\"]==\"steam_app_986130\" and w[\"title\"]==\"Shadows of Doubt\"))")"; sleep 0.3; }
d() { printf '%s\n' "$@" | $DC; }
case "$1" in
  focus) focus ;;
  shot) focus; grim -o DP-1 $S/full.png && magick $S/full.png -resize 50% $S/view.png ;;
  click) focus; d "mouseto $(awk "BEGIN{print $2/3440}") $(awk "BEGIN{print $3/1440}")"; sleep 0.15; d "click ${4:-left}" ;;
  do) focus; shift; d "$@" ;;
  hold) focus; d "keydown $2"; sleep $(awk "BEGIN{print $3/1000}"); d "keyup $2" ;;
  look) focus; d "mousemove $2 $3" ;;
esac
