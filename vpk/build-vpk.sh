#!/usr/bin/env bash
# Second half of the build, inside WSL (or Linux/Docker) as root: AOT-compiles the game assemblies the Windows
# builder made and links them with the launcher into stardew.vpk.
#
#   bash vpk/build-vpk.sh <work folder of the builder, e.g. /mnt/c/.../vita-builder/work>
#
# Needs VitaSDK in /usr/local/vitasdk and the toolchain (vpk/pack-toolchain.sh) in /root/vita: prefix (SDL3, vitaGL,
# FNA3D, FAudio), mono-vita (runtime), mono-cross (AOT compiler), mono-host (class libraries).
# SCE_SYS=<folder> overrides the LiveArea/icon/manual folder (default: <work>/out/sce_sys, drawn by the builder from
# the player's Content). The trophy pack is made here from its pic0.png (make-trophies.py, needs python3-pil).
# Output: <work>/out/ux0/stardew.vpk next to <work>/out/ux0/data/StardewValley.
set -euo pipefail

WORK="$(realpath "${1:?usage: build-vpk.sh <builder work folder>}")"
REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SCE_SYS="${SCE_SYS:-$WORK/out/sce_sys}"
VITA="$WORK/src/SV_PC_source/Tools/Vita"
GAME="$WORK/out/ux0/data/StardewValley"
# Our own build folders, so a maintainer's /root/vita/build-aot and build-stardew stay untouched.
AOT=/root/vita/builder-aot
BUILD=/root/vita/builder-stardew

fail() { echo "ERROR: $*" >&2; exit 1; }
[ -x /usr/local/vitasdk/bin/arm-vita-eabi-gcc ] || fail "VitaSDK not found in /usr/local/vitasdk"
for f in prefix/lib/libvitaGL.a prefix/lib/libFNA3D.a mono-vita/include/mono-2.0 mono-cross/bin/mono-sgen mono-host/lib/mono/4.5/mscorlib.dll; do
	[ -e "/root/vita/$f" ] || fail "toolchain incomplete: /root/vita/$f missing (extract the toolchain archive into /)"
done
[ -f "$GAME/Stardew Valley.exe" ] || fail "no builder output in $GAME: run the Windows builder first"
[ -f "$WORK/src/FNA/bin/Release/net4.0/FNA.dll" ] || fail "no FNA build in $WORK/src/FNA"
[ -f "$SCE_SYS/icon0.png" ] && [ -f "$SCE_SYS/livearea/contents/template.xml" ] || fail "no LiveArea art in $SCE_SYS"
command -v cmake >/dev/null && command -v ninja >/dev/null && python3 -c "import PIL" 2>/dev/null || fail "needs cmake, ninja, python3 and python3-pil"

rm -rf "$VITA/stardew/sce_sys"
cp -r "$SCE_SYS" "$VITA/stardew/sce_sys"
[ -f "$VITA/stardew/sce_sys/trophy/STDV00001_00/TROPHY.TRP" ] || python3 "$VITA/stardew/trophies/make-trophies.py" > "$WORK/out/trophies.log" 2>&1 || { cat "$WORK/out/trophies.log"; fail "trophy pack failed"; }
sed -i "s#/root/vita/build-aot#$AOT#g" "$VITA/aot-stardew.sh"
sed -i "s#/root/vita/build-stardew#$BUILD#g" "$VITA/build-stardew.sh"

echo "== AOT (log: $WORK/out/aot.log)"
AOT_PROFILE="$REPO/assets/stardew-1.aotprofile" PROFILE_ONLY="System:System.Xml:Stardew Valley" \
	bash "$VITA/aot-stardew.sh" "$GAME" > "$WORK/out/aot.log" 2>&1 || { tail -20 "$WORK/out/aot.log"; fail "AOT failed"; }
tail -9 "$WORK/out/aot.log"
echo "== VPK (log: $WORK/out/vpk.log)"
AOT_DIR="$AOT" bash "$VITA/build-stardew.sh" "$WORK/out" > "$WORK/out/vpk.log" 2>&1 || { tail -20 "$WORK/out/vpk.log"; fail "VPK build failed"; }
mv "$WORK/out/stardew.vpk" "$WORK/out/ux0/stardew.vpk"
sha256sum "$WORK/out/ux0/stardew.vpk"
echo "== Done: copy everything in out/ux0 to the Vita's ux0: and install ux0:stardew.vpk with VitaShell"
