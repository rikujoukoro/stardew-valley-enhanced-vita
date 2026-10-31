#!/usr/bin/env bash
# Builds FAudio (XAudio2/XACT reimplementation, FNA's audio) as a static Vita library on SDL3's audio,
# into /root/vita/prefix. Run inside WSL Ubuntu as root, after build-sdl3.sh.
#
# The FNA checkout is left untouched: the sources are copied to /root/vita/src/FAudio first (CMake
# flags cannot carry the checkout's path with spaces).
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
FAUDIO_SRC="$HERE/../../../FNA/lib/FAudio"
WORK=/root/vita/src/FAudio
BUILD=/root/vita/build-faudio
PREFIX=/root/vita/prefix

rm -rf "$WORK" "$BUILD"
mkdir -p "$WORK"
cp -r "$FAUDIO_SRC/CMakeLists.txt" "$FAUDIO_SRC/include" "$FAUDIO_SRC/src" "$FAUDIO_SRC/cmake" "$WORK/"
find "$WORK" -type f \( -name '*.c' -o -name '*.h' -o -name '*.txt' -o -name '*.cmake' -o -name '*.in' \) -exec sed -i 's/\r$//' {} +
# Local port edits (fna-vita/patch-faudio.py): O(1) Cue list appends.
python3 "$HERE/fna-vita/patch-faudio.py" "$WORK"

# -fno-short-enums: FNA's C# side marshals FAudio/FACT enums (in structs too) as 4-byte ints, while
# arm-vita-eabi defaults to 1-byte enums. FAudio's SIMD paths need AArch64 NEON, so the 32-bit Vita
# uses the scalar converters and resamplers.
cmake -S "$WORK" -B "$BUILD" -G Ninja \
	-DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" \
	-DCMAKE_BUILD_TYPE=Release \
	-DCMAKE_PREFIX_PATH="$PREFIX" \
	-DBUILD_SHARED_LIBS=OFF \
	-DBUILD_SDL3=ON \
	-DXNASONG=ON \
	-DFAUDIO_INSTALL=OFF \
	-DCMAKE_C_FLAGS="-fno-short-enums"
cmake --build "$BUILD" --parallel

mkdir -p "$PREFIX/lib" "$PREFIX/include/FAudio"
cp "$BUILD/libFAudio.a" "$PREFIX/lib/"
cp "$WORK/include/"*.h "$PREFIX/include/FAudio/"
ls -la "$PREFIX/lib/libFAudio.a"
