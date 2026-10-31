#!/usr/bin/env bash
# Builds stardew.vpk (STDV00001): the Stardew Valley launcher (Vita Mono runtime, SDL3, FNA3D, FAudio)
# with Mono's class libraries and FNA.dll. The game itself is not in the VPK: stage-stardew.ps1 builds
# the folder the player copies to ux0:data/StardewValley from their own game files.
# Copies the VPK to $1 if given. Run inside WSL as root, after build-sdl3.sh, build-fna3d.sh,
# build-faudio.sh, build-mono-host.sh and build-mono-vita.sh. With AOT_DIR=/root/vita/build-aot (from
# aot-stardew.sh), the eboot carries precompiled code.
set -euo pipefail

export VITASDK=/usr/local/vitasdk
export PATH="$VITASDK/bin:$PATH"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
HOST=/root/vita/mono-host
PREFIX=/root/vita/prefix
FNA_DLL="$HERE/../../../FNA/bin/Release/net4.0/FNA.dll"
BUILD=/root/vita/build-stardew
STAGE="$BUILD/stage"
TABLES="$BUILD/tables"

rm -rf "$BUILD"
mkdir -p "$STAGE/lib/mono/4.5" "$STAGE/lib/fna" "$STAGE/etc/mono/4.5" "$TABLES"

# Class libraries the game and its DLLs reference (System.Xml pulls in System.Configuration, which
# pulls in System.Security; System references Mono.Security, which an AOT image of System records as
# a dependency), and FNA.dll (the net4.0 build, without its Linux dllmap config).
for dll in mscorlib System System.Core System.Xml System.Numerics System.Configuration System.Security Mono.Security; do
	cp -L "$HOST/lib/mono/4.5/$dll.dll" "$STAGE/lib/mono/4.5/"
done
cp "$FNA_DLL" "$STAGE/lib/fna/FNA.dll"
cp "$HOST/etc/mono/config" "$STAGE/etc/mono/config"
cp "$HOST/etc/mono/4.5/machine.config" "$STAGE/etc/mono/4.5/machine.config"
# LiveArea: bubble icon, background, LOAD gate and pages (stardew/sce_sys, made from the game's own art).
cp -r "$HERE/stardew/sce_sys" "$STAGE/"

# Native side: what FNA's [DllImport("SDL3")], [DllImport("FNA3D")] and [DllImport("FAudio")]
# resolve to.
bash "$HERE/gen-native-table.sh" sdl3 "$TABLES/vita_sdl3_table.c" '^SDL_' "$HERE/fna-vita/sdl3.overrides" "$PREFIX/lib/libSDL3.a"
bash "$HERE/gen-native-table.sh" fna3d "$TABLES/vita_fna3d_table.c" '^(FNA3D_|MOJOSHADER_)' - \
	"$PREFIX/lib/libFNA3D.a" "$PREFIX/lib/libmojoshader.a"
bash "$HERE/gen-native-table.sh" faudio "$TABLES/vita_faudio_table.c" '^(F3DAudio|FACT|FAPO|FAudio|CreateFAPO|XNA_|qoa_|ReverbConvert)' - \
	"$PREFIX/lib/libFAudio.a"

# AOT_DIR (aot-stardew.sh's output) links precompiled code for the assemblies compiled there.
cmake -S "$HERE/stardew" -B "$BUILD" -G Ninja \
	-DCMAKE_TOOLCHAIN_FILE="$VITASDK/share/vita.toolchain.cmake" \
	-DCMAKE_BUILD_TYPE=Release \
	-DAOT_DIR="${AOT_DIR:-}"
cmake --build "$BUILD" --parallel
ls -la "$BUILD/stardew.vpk"
if [ -n "${1:-}" ]; then
	cp "$BUILD/stardew.vpk" "$1/stardew.vpk"
	echo "copied to $1/stardew.vpk"
fi
