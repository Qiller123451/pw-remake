#!/bin/sh
# Regenerates the map goldens (tests/golden/maps/*) from the JS remake: see maps_dump.mjs. Paths of this container;
# override with REMAKE_SRC, UPLOADS (the staged Data folder of the installation), BRUSHES.
set -e
cd "$(dirname "$0")/../.."
SRC=${REMAKE_SRC:-/home/claude/pw-exporter/remake/src}
U=${UPLOADS:-/mnt/user-data/uploads/Paraworld/Data}
BR=${BRUSHES:-/mnt/user-data/uploads/Paraworld/export/remake/375f3d3a/assets/terrain/objbrush.json}
B=$(ls $U/Base/Maps/Cpn_single_001/*.ula $U/BoosterPack1/Maps/Cpn_single_001/*.ula $U/Base/Maps/Multiplayer/*.ula | tr '\n' ',' | sed 's/,$//')
M="$U/MIRAGE/Maps/Cpn_single_001/single_b41_seas.ula,$U/MIRAGE/Maps/Cpn_single_001/single_seas_02.ula,$U/MIRAGE/Maps/Cpn_single_001/single_05.ula"
node --no-warnings --import ./tests/golden/node_hooks.mjs tests/golden/maps_dump.mjs "$SRC" tests/golden/maps "$BR" "/opt/data/base=$B" "/opt/data/mirage=$M"
