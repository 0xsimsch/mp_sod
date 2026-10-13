#!/usr/bin/env bash
# Kill the game, relaunch it, load the quick save.
S="${SOD_DRIVER_OUT:-/tmp/sod_driver}"; mkdir -p "$S"; D="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
for p in $(pgrep -f '^S:.steamapps.common.Shadows of Doubt.Shadows of Doubt.exe'); do kill $p; done
for i in $(seq 1 30); do niri msg -j windows | grep -q steam_app_986130 || break; sleep 2; done
sleep 5
(steam steam://rungameid/986130 >/dev/null 2>&1 &)
for i in $(seq 1 60); do niri msg -j windows | grep -q steam_app_986130 && break; sleep 3; done
$D/loadquick.sh
