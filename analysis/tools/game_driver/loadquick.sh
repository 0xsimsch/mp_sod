#!/usr/bin/env bash
# From a fresh launch: title -> Load Game -> Quick Save -> Start, then wait for the city.
S="${SOD_DRIVER_OUT:-/tmp/sod_driver}"; mkdir -p "$S"; D="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
sleep 25
$D/sod.sh click 1720 1300; sleep 10
$D/sod.sh click 612 836; sleep 4
$D/sod.sh click 2268 1158; sleep 75
$D/sod.sh shot
