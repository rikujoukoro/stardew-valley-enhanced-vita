#!/usr/bin/env bash
# Builds fnatest.vpk (FNAG00001): the Vita Mono runtime running an FNA Game, with SDL3, FNA3D and
# FAudio served to [DllImport] from generated tables. Copies the VPK to $1 if given. Run inside WSL as
# root, after build-sdl3.sh, build-fna3d.sh, build-faudio.sh, build-mono-host.sh and build-mono-vita.sh.
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST=/root/vita/mono-host
PREFIX=/root/vita/prefix
FNA_DLL="$HERE/../../../FNA/bin/Release/net4.0/FNA.dll"
BUILD=/root/vita/build-fnatest
STAGE="$BUILD/stage"
TABLES="$BUILD/tables"

rm -rf "$BUILD"
mkdir -p "$STAGE/lib/mono/4.5" "$STAGE/etc/mono/4.5" "$TABLES"

# Managed side: FNA.dll (the net4.0 build, without its Linux dllmap config) and the test game.
cp "$FNA_DLL" "$STAGE/FNA.dll"
cp "$HERE/fnatest/FnaTest.cs" "$BUILD/"
"$HOST/bin/mcs" -nologo -optimize -out:"$STAGE/fnatest.exe" -r:"$STAGE/FNA.dll" "$BUILD/FnaTest.cs"
for dll in mscorlib System System.Core; do
	cp -L "$HOST/lib/mono/4.5/$dll.dll" "$STAGE/lib/mono/4.5/"
done
mkdir -p "$STAGE/Content"
echo "content from app0" > "$STAGE/Content/test.txt"
cp "$HOST/etc/mono/config" "$STAGE/etc/mono/config"
cp "$HOST/etc/mono/4.5/machine.config" "$STAGE/etc/mono/4.5/machine.config"

# Native side: what FNA's [DllImport("SDL3")], [DllImport("FNA3D")] and [DllImport("FAudio")]
# resolve to.
bash "$HERE/gen-native-table.sh" sdl3 "$TABLES/vita_sdl3_table.c" '^SDL_' "$HERE/fna-vita/sdl3.overrides" "$PREFIX/lib/libSDL3.a"
bash "$HERE/gen-native-table.sh" fna3d "$TABLES/vita_fna3d_table.c" '^(FNA3D_|MOJOSHADER_)' - \
	"$PREFIX/lib/libFNA3D.a" "$PREFIX/lib/libmojoshader.a"
bash "$HERE/gen-native-table.sh" faudio "$TABLES/vita_faudio_table.c" '^(F3DAudio|FACT|FAPO|FAudio|CreateFAPO|XNA_|qoa_|ReverbConvert)' - \
	"$PREFIX/lib/libFAudio.a"

cmake -S "$HERE/fnatest" -B "$BUILD" -G Ninja \
	-DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" \
	-DCMAKE_BUILD_TYPE=Release
cmake --build "$BUILD" --parallel
ls -la "$BUILD/fnatest.vpk"
if [ -n "${1:-}" ]; then
	cp "$BUILD/fnatest.vpk" "$1/fnatest.vpk"
	echo "copied to $1/fnatest.vpk"
fi
