#!/usr/bin/env bash
# Builds exectest/ (kubridge executable-memory experiment) into exectest.vpk and copies it to $1 if
# given. Run inside WSL as root.
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BUILD=/root/vita/build-exectest
rm -rf "$BUILD"
cmake -S "$HERE/exectest" -B "$BUILD" -G Ninja -DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" -DCMAKE_BUILD_TYPE=Release
cmake --build "$BUILD"
if [ -n "${1:-}" ]; then
	cp "$BUILD/exectest.vpk" "$1/exectest.vpk"
	echo "copied to $1/exectest.vpk"
fi
