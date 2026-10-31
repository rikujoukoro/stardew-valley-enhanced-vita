#!/usr/bin/env bash
# Builds Tools/Vita/sdl3test into two VPKs against the SDL3 built by build-sdl3.sh:
#   sdl3test.vpk         (SDLT00001, default memory budget)
#   sdl3test-extmem.vpk  (SDLT00002, param.sfo ATTRIBUTE2=12)
# Run inside WSL Ubuntu as root. Both are copied to the directory $1 (a Windows path under /mnt/c) if given.
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/sdl3test"

build_variant() {
	local name="$1" extended="$2"
	local build="/root/vita/build-$name"
	rm -rf "$build"
	cmake -S "$SRC" -B "$build" -G Ninja \
		-DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" \
		-DCMAKE_BUILD_TYPE=Release \
		-DCMAKE_PREFIX_PATH=/root/vita/prefix \
		-DEXTENDED_MEMORY="$extended"
	cmake --build "$build" --parallel
	ls -la "$build/sdl3test.vpk"
	if [ -n "${OUT_DIR:-}" ]; then
		cp "$build/sdl3test.vpk" "$OUT_DIR/$name.vpk"
		echo "copied to $OUT_DIR/$name.vpk"
	fi
}

OUT_DIR="${1:-}"
build_variant sdl3test OFF
build_variant sdl3test-extmem ON
