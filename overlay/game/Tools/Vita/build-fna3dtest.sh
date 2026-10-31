#!/usr/bin/env bash
# Builds Tools/Vita/fna3dtest into fna3dtest.vpk (FNAT00001, extended memory) and copies it to $1 if
# given. Run inside WSL Ubuntu as root, after build-sdl3.sh and build-fna3d.sh.
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD=/root/vita/build-fna3dtest
rm -rf "$BUILD"
mkdir -p "$BUILD"
cp "$HERE/../../../FNA/src/Graphics/Effect/StockEffects/FXB/SpriteEffect.fxb" "$BUILD/SpriteEffect.fxb"
cmake -S "$HERE/fna3dtest" -B "$BUILD" -G Ninja \
	-DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" \
	-DCMAKE_BUILD_TYPE=Release
cmake --build "$BUILD" --parallel
ls -la "$BUILD/fna3dtest.vpk"
if [ -n "${1:-}" ]; then
	cp "$BUILD/fna3dtest.vpk" "$1/fna3dtest.vpk"
	echo "copied to $1/fna3dtest.vpk"
fi
