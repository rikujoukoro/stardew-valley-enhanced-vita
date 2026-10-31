#!/usr/bin/env bash
# Builds SDL3 from source for the PS Vita with VitaSDK, installing into $WORK/prefix (not into the SDK,
# so the vdpm-provided sdl3 package stays untouched). Run inside WSL Ubuntu as root.
#   bash build-sdl3.sh [tag]     default tag: release-3.4.16 (same version as the vdpm sdl3 package)
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
TAG="${1:-release-3.4.16}"
# Before the cd below: $0 may be relative to the caller's directory.
HERE="$(dirname "$(readlink -f "$0")")"
WORK=/root/vita
PREFIX="$WORK/prefix"
mkdir -p "$WORK" "$PREFIX"
cd "$WORK"

if [ ! -d SDL ]; then
	git clone --depth 1 --branch "$TAG" https://github.com/libsdl-org/SDL SDL
fi

# Local port edits: audio timing and the batched gamepad snapshot used by FNA on Vita.
python3 "$HERE/sdl-vita/patch-sdl-audio.py" SDL
python3 "$HERE/sdl-vita/patch-sdl-gamepad.py" SDL

# -fno-short-enums: arm-vita-eabi defaults to 1-byte enums (SDL skips its enum-size check on the
# Vita), but SDL3-CS marshals every enum, in structs too, as a 4-byte int.
rm -rf SDL/build-vita
cmake -S SDL -B SDL/build-vita -G Ninja \
	-DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" \
	-DCMAKE_BUILD_TYPE=Release \
	-DCMAKE_INSTALL_PREFIX="$PREFIX" \
	-DCMAKE_C_FLAGS="-fno-short-enums" \
	-DSDL_SHARED=OFF -DSDL_STATIC=ON -DSDL_TESTS=OFF -DSDL_EXAMPLES=OFF
cmake --build SDL/build-vita --parallel
cmake --install SDL/build-vita

ls -la "$PREFIX/lib/libSDL3.a"
git -C SDL describe --tags --always
