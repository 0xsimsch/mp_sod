# Game driver (Linux / niri / Proton)

Scripts used to run in-game tests unattended (night of 2026-10-12).

- Needs `dotoold` running (`/nix/store/...-dotool-1.6/bin/dotoold`, uinput access) and the niri compositor; screenshots via `grim` on output `DP-1` (3440×1440), scaled with ImageMagick.
- `sod.sh shot` → `$SOD_DRIVER_OUT/view.png` (50 %); `sod.sh do "key f5"`; `sod.sh click X Y` (full-res pixels); `sod.sh look DX DY`; `sod.sh hold KEY MS`.
- `restart.sh`: kill the game, relaunch via `steam steam://rungameid/986130`, then `loadquick.sh` (title → Load Game → first save → Start, waits ~75 s).
- Plugin output: `BepInEx/sodcoop.log` in the game folder.
