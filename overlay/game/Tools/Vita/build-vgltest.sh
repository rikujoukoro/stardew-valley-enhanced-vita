#!/usr/bin/env bash
# Builds Tools/Vita/vgltest into vgltest.vpk (VGLT00001, extended memory) and copies it to $1 if given.
# Run inside WSL Ubuntu as root.
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/vgltest"
BUILD=/root/vita/build-vgltest
rm -rf "$BUILD"
cmake -S "$SRC" -B "$BUILD" -G Ninja \
	-DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" \
	-DCMAKE_BUILD_TYPE=Release
cmake --build "$BUILD" --parallel
ls -la "$BUILD/vgltest.vpk"
if [ -n "${1:-}" ]; then
	cp "$BUILD/vgltest.vpk" "$1/vgltest.vpk"
	echo "copied to $1/vgltest.vpk"
fi
